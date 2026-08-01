using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Ink_Canvas.Plugins;
using PdfReader.Views;

namespace PdfReader
{
    /// <summary>
    /// 嵌入式 PDF 会话：把 PDF 作为连续滚动长条注入宿主画布下方，墨迹由宿主自己的 InkCanvas 承载。
    /// 滚动时背景层平移、宿主同步平移画布墨迹（实时跟随）；滚动停止后按视口内可见页切分/恢复墨迹。
    /// 导出交给宿主的 <see cref="ICanvasCompositionService.ExportWithInkAsync"/>。
    /// </summary>
    internal sealed class EmbeddedReaderSession : IDisposable
    {
        /// <summary>本插件在宿主放映模式里的演示源标识。</summary>
        private const string PresentationSourceId = "com.icc.pdf-reader";

        private readonly ICanvasCompositionService _composition;

        /// <summary>外部演示源服务；宿主版本较旧时为 null，此时退化为「不进入放映模式」。</summary>
        private readonly IPresentationSourceService _presentation;

        private readonly ReaderConfig _config;
        private readonly Action<string, Exception> _logError;

        private readonly object _gate = new object();
        private PdfDocumentSession _document;
        private PageRenderCache _cache;
        private PdfBackgroundView _backgroundView;
        private CancellationTokenSource _renderCts;
        private int _currentPage;
        private bool _disposed;

        /// <summary>已挂上 PreviewMouseWheel 的宿主窗口。</summary>
        private Window _wheelHost;

        /// <summary>是否已成功进入宿主放映模式；退出时据此决定是否调用 EndAsync。</summary>
        private bool _presentationActive;

        /// <summary>滚动停止去抖计时器状态。</summary>
        private CancellationTokenSource _scrollSettleCts;

        /// <summary>正在翻页/滚动中，防止滚轮连发导致并发交错。</summary>
        private int _navigating;

        /// <summary>当前展示模式。</summary>
        private PdfDisplayMode _displayMode = PdfDisplayMode.SinglePage;

        /// <summary>当前展示模式。</summary>
        public PdfDisplayMode DisplayMode
        {
            get { lock (_gate) return _displayMode; }
        }

        public EmbeddedReaderSession(ICanvasCompositionService composition,
            IPresentationSourceService presentation, ReaderConfig config,
            Action<string, Exception> logError)
        {
            _composition = composition ?? throw new ArgumentNullException(nameof(composition));
            _presentation = presentation;
            _config = config ?? new ReaderConfig();
            _logError = logError;
            _cache = new PageRenderCache(8, _config.CacheBudgetBytes);
        }

        public bool IsOpen
        {
            get { lock (_gate) return _document != null; }
        }

        public string FilePath
        {
            get { lock (_gate) return _document?.FilePath; }
        }

        public int PageCount
        {
            get { lock (_gate) return _document == null ? 0 : (int)_document.PageCount; }
        }

        /// <summary>当前视口顶部对应的页（0-based），用于页码显示与翻页条。</summary>
        public int CurrentPage
        {
            get { lock (_gate) return _currentPage; }
        }

        /// <summary>打开文档、按当前展示模式初始化背景层。</summary>
        public async Task OpenAsync(string path, int initialPage, CancellationToken cancellationToken)
        {
            var session = await PdfDocumentSession.OpenAsync(path, cancellationToken).ConfigureAwait(false);

            PdfDocumentSession previous;
            lock (_gate)
            {
                previous = _document;
                _document = session;
                _cache = new PageRenderCache(8, _config.CacheBudgetBytes);
                _currentPage = ClampPage(initialPage, (int)session.PageCount);
            }
            previous?.Dispose();

            EnsureBackgroundLayer();

            _composition.ConfigurePages((uint)session.PageCount, (uint)_currentPage, RenderPageForExportAsync);

            await ApplyInitialDisplayAsync(cancellationToken).ConfigureAwait(false);

            await BeginPresentationAsync((int)session.PageCount, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>按当前模式初始化背景层显示。</summary>
        private async Task ApplyInitialDisplayAsync(CancellationToken cancellationToken)
        {
            var view = _backgroundView;
            if (view == null) return;

            if (_displayMode == PdfDisplayMode.ContinuousScroll)
            {
                await ResetStripAsync(cancellationToken).ConfigureAwait(false);
                await ScrollToPageTopAsync(_currentPage, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // 翻页模式：渲染当前页（双页渲染页对）。
                int page = CurrentPage;
                var left = await RenderPageAsync(page, cancellationToken).ConfigureAwait(false);
                BitmapSource right = null;
                if (_displayMode == PdfDisplayMode.DoublePage && page + 1 < PageCount)
                    right = await RenderPageAsync(page + 1, cancellationToken).ConfigureAwait(false);

                ApplyBackground(left, right);

                await SyncVisiblePagesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>切换展示模式，重排背景层并恢复墨迹。</summary>
        public async Task SetDisplayModeAsync(PdfDisplayMode mode, CancellationToken cancellationToken)
        {
            if (_displayMode == mode) return;

            var view = _backgroundView;
            if (view == null || !IsOpen) return;

            // 切换前先按旧模式保存当前画布墨迹到对应页，
            // 否则宿主可见页矩形还没变，墨迹仍按旧坐标系，切到新模式后错位。
            await SyncVisiblePagesAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate) { _displayMode = mode; }

            if (view.Dispatcher.CheckAccess()) view.SetDisplayMode(mode);
            else await view.Dispatcher.InvokeAsync(() => view.SetDisplayMode(mode));

            await ApplyInitialDisplayAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>重建长条：重置所有页占位并逐个渲染。</summary>
        private async Task ResetStripAsync(CancellationToken cancellationToken)
        {
            var view = _backgroundView;
            if (view == null) return;

            int count = PageCount;
            if (view.Dispatcher.CheckAccess()) view.ResetStrip(count);
            else view.Dispatcher.Invoke(() => view.ResetStrip(count));

            // 视口附近先渲染（前 3 页），其余延迟渲染。
            int initialWindow = Math.Min(count, 3);
            for (int i = 0; i < initialWindow; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bitmap = await RenderPageAsync(i, cancellationToken).ConfigureAwait(false);
                ApplyPageSource(i, bitmap);
            }

            // 其余页后台渲染（不阻塞）。
            _ = Task.Run(() => RenderRemainingPagesAsync(initialWindow, cancellationToken), cancellationToken);
        }

        private async Task RenderRemainingPagesAsync(int startIndex, CancellationToken cancellationToken)
        {
            try
            {
                for (int i = startIndex; i < PageCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bitmap = await RenderPageAsync(i, cancellationToken).ConfigureAwait(false);
                    ApplyPageSource(i, bitmap);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logError?.Invoke("渲染 PDF 页失败", ex);
            }
        }

        private void ApplyPageSource(int pageIndex, BitmapSource bitmap)
        {
            var view = _backgroundView;
            if (view == null || bitmap == null) return;

            if (view.Dispatcher.CheckAccess()) view.SetStripPageSource(pageIndex, bitmap);
            else view.Dispatcher.Invoke(() => view.SetStripPageSource(pageIndex, bitmap));
        }

        /// <summary>进入宿主放映模式。</summary>
        private async Task BeginPresentationAsync(int pageCount, CancellationToken cancellationToken)
        {
            if (_presentation == null || pageCount <= 0) return;

            try
            {
                var descriptor = new PresentationSourceDescriptor
                {
                    Id = PresentationSourceId,
                    Name = Strings.PluginName,
                    PageCount = pageCount,
                    CurrentPage = CurrentPage + 1,
                    NavigateAsync = HandleHostNavigationAsync,
                    AllowPageNumberClick = false
                };

                await _presentation.BeginAsync(descriptor, cancellationToken).ConfigureAwait(false);

                if (_presentation.IsActive)
                {
                    _presentationActive = true;
                    try { _presentation.Ended += OnPresentationEnded; }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logError?.Invoke("进入放映模式失败", ex);
            }
        }

        /// <summary>宿主结束外部演示源时触发，插件随之关闭 PDF。</summary>
        private void OnPresentationEnded(string sourceId)
        {
            if (sourceId != PresentationSourceId) return;

            _presentationActive = false;
            try { _presentation.Ended -= OnPresentationEnded; }
            catch { }
            Close();

            // 通知插件刷新 UI（弹窗状态、页码等）。
            try { Closed?.Invoke(); }
            catch (Exception ex) { _logError?.Invoke("PDF 关闭通知失败", ex); }
        }

        /// <summary>文档被关闭（含宿主强制结束演示源）时触发，供插件刷新 UI。</summary>
        public event Action Closed;

        /// <summary>宿主翻页条触发的滚动到下一/上一页顶部。</summary>
        private async Task<int> HandleHostNavigationAsync(PresentationNavigation direction,
            CancellationToken cancellationToken)
        {
            if (!IsOpen) return 0;

            int target = CurrentPage + (direction == PresentationNavigation.Next ? 1 : -1);
            if (target < 0 || target >= PageCount) return 0;

            await ScrollToPageTopAsync(target, cancellationToken).ConfigureAwait(false);
            return CurrentPage + 1;
        }

        /// <summary>退出宿主放映模式。</summary>
        private void EndPresentation()
        {
            if (_presentation == null || !_presentationActive) return;
            _presentationActive = false;

            try { _presentation.Ended -= OnPresentationEnded; }
            catch { }

            try
            {
                _ = _presentation.EndAsync(PresentationSourceId);
            }
            catch (Exception ex)
            {
                _logError?.Invoke("退出放映模式失败", ex);
            }
        }

        private void EnsureBackgroundLayer()
        {
            if (_backgroundView != null && _composition.HasBackgroundLayer) return;

            PdfBackgroundView created = null;
            _composition.InjectBackgroundLayer(() =>
            {
                created = new PdfBackgroundView();
                created.Loaded += BackgroundView_Loaded;
                created.SizeChanged += BackgroundView_SizeChanged;
                return created;
            });
            _backgroundView = created;
        }

        private void BackgroundView_Loaded(object sender, RoutedEventArgs e)
        {
            var view = sender as PdfBackgroundView;
            AttachWheelHandler(view);
            if (view != null) SyncVisiblePagesAsync();
        }

        private void BackgroundView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is PdfBackgroundView view) SyncVisiblePagesAsync();
        }

        private void AttachWheelHandler(PdfBackgroundView view)
        {
            if (view == null) return;

            var window = Window.GetWindow(view);
            if (window == null || ReferenceEquals(window, _wheelHost)) return;

            DetachWheelHandler();
            window.PreviewMouseWheel += Host_PreviewMouseWheel;
            _wheelHost = window;
        }

        private void DetachWheelHandler()
        {
            var host = _wheelHost;
            if (host == null) return;

            try { host.PreviewMouseWheel -= Host_PreviewMouseWheel; }
            catch { }
            _wheelHost = null;
        }

        private void Host_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!IsOpen) return;

            var view = _backgroundView;
            if (view == null || !view.IsVisible) return;

            Point local;
            try { local = e.GetPosition(view); }
            catch { return; }

            if (local.X < 0 || local.Y < 0 || local.X > view.ActualWidth || local.Y > view.ActualHeight)
                return;

            if (HandleScroll(e.Delta)) e.Handled = true;
        }

        /// <summary>页码变化通知。</summary>
        public event Action<int> PageChanged;

        /// <summary>滚轮滚动（连续滚动模式）或翻页（翻页模式）。返回 true 表示事件已被接管。</summary>
        public bool HandleScroll(int delta)
        {
            if (!IsOpen || delta == 0) return false;

            if (_displayMode == PdfDisplayMode.ContinuousScroll)
            {
                double step = delta > 0 ? 60 : -60;
                _ = ScrollByAsync(step);
            }
            else
            {
                // 翻页模式：滚轮翻页。双页按页对翻（+2 / -2），单页按 1。
                int step = _displayMode == PdfDisplayMode.DoublePage ? 2 : 1;
                int target = CurrentPage + (delta > 0 ? -step : step);
                _logError?.Invoke($"滚轮翻页 模式={_displayMode} 当前页={CurrentPage} 目标页={target} delta={delta}", null);
                _ = GoToPageAsync(target, CancellationToken.None);
            }
            return true;
        }

        /// <summary>按增量滚动，墨迹实时跟随。</summary>
        public async Task ScrollByAsync(double deltaY)
        {
            if (deltaY == 0) return;

            var view = _backgroundView;
            if (view == null) return;

            double viewportH = view.ActualHeight;
            double maxOffset = Math.Max(0, view.StripHeight - viewportH);

            // 计算新偏移并夹取边界。
            double newOffset = view.ScrollOffset + deltaY;
            if (newOffset < 0) newOffset = 0;
            if (newOffset > maxOffset) newOffset = maxOffset;
            double actualDelta = newOffset - view.ScrollOffset;
            if (actualDelta == 0) return;

            // 1. 平移背景长条。
            if (view.Dispatcher.CheckAccess()) view.SetScrollOffset(newOffset);
            else await view.Dispatcher.InvokeAsync(() => view.SetScrollOffset(newOffset));

            // 2. 宿主实时平移墨迹。
            try
            {
                await _composition.ScrollOffsetAsync(actualDelta, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logError?.Invoke("滚动墨迹跟随失败", ex);
            }

            // 3. 更新当前页（视口顶部页）。
            UpdateCurrentPageFromScroll();

            // 4. 去抖后重建可见页墨迹。
            ScheduleSettleSync();
        }

        /// <summary>上一页（翻页模式翻到上一页，滚动模式滚到上一页顶部）。</summary>
        public async Task PreviousPageAsync(CancellationToken cancellationToken)
        {
            if (_displayMode == PdfDisplayMode.ContinuousScroll)
                await ScrollToPageTopAsync(CurrentPage - 1, cancellationToken).ConfigureAwait(false);
            else
                await GoToPageAsync(CurrentPage - 1, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>下一页（翻页模式翻到下一页，滚动模式滚到下一页顶部）。</summary>
        public async Task NextPageAsync(CancellationToken cancellationToken)
        {
            if (_displayMode == PdfDisplayMode.ContinuousScroll)
                await ScrollToPageTopAsync(CurrentPage + 1, cancellationToken).ConfigureAwait(false);
            else
                await GoToPageAsync(CurrentPage + 1, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>滚动到指定页顶部。</summary>
        public async Task ScrollToPageTopAsync(int pageIndex, CancellationToken cancellationToken)
        {
            var view = _backgroundView;
            if (view == null || !IsOpen) return;

            int target = ClampPage(pageIndex, PageCount);
            double viewportH = view.ActualHeight;

            // 长条可能尚未初始化（切到连续滚动模式但页还没渲染），此时 GetStripPageImage 返回 null。
            double pageTop;
            if (view.Dispatcher.CheckAccess())
            {
                var img = view.GetStripPageImage(target);
                if (img == null) return;
                pageTop = Canvas.GetTop(img);
            }
            else
            {
                pageTop = await view.Dispatcher.InvokeAsync(() =>
                {
                    var img = view.GetStripPageImage(target);
                    if (img == null) return double.NaN;
                    return Canvas.GetTop(img);
                }).Task;
                if (double.IsNaN(pageTop)) return;
            }

            double maxOffset = Math.Max(0, view.StripHeight - viewportH);
            double newOffset = Math.Max(0, Math.Min(pageTop, maxOffset));

            await ScrollToOffsetAsync(newOffset, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>滚动到绝对偏移。</summary>
        private async Task ScrollToOffsetAsync(double newOffset, CancellationToken cancellationToken)
        {
            var view = _backgroundView;
            if (view == null) return;

            double delta = newOffset - view.ScrollOffset;
            if (Math.Abs(delta) < 0.5) return;

            if (view.Dispatcher.CheckAccess()) view.SetScrollOffset(newOffset);
            else await view.Dispatcher.InvokeAsync(() => view.SetScrollOffset(newOffset));

            try
            {
                await _composition.ScrollOffsetAsync(delta, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logError?.Invoke("滚动墨迹跟随失败", ex);
            }

            UpdateCurrentPageFromScroll();
            ScheduleSettleSync();
        }

        /// <summary>根据滚动偏移计算视口顶部页，并通知页码变化。</summary>
        private void UpdateCurrentPageFromScroll()
        {
            var view = _backgroundView;
            if (view == null) return;

            double offset = view.ScrollOffset;
            double viewportH = view.ActualHeight;

            // 找视口顶部覆盖的页。
            int topPage = 0;
            if (view.Dispatcher.CheckAccess())
            {
                topPage = view.GetPageIndexAtStripOffset(offset + 1);
            }

            lock (_gate)
            {
                _currentPage = ClampPage(topPage, PageCount);
                int page = _currentPage;
                _config.LastPageIndex = page;
                try { PageChanged?.Invoke(page); }
                catch (Exception ex) { _logError?.Invoke("PDF 页码变化通知失败", ex); }
            }
        }

        /// <summary>滚动停止去抖后重建可见页集合，让宿主把墨迹切分/恢复到位。</summary>
        private void ScheduleSettleSync()
        {
            _scrollSettleCts?.Cancel();
            _scrollSettleCts?.Dispose();
            _scrollSettleCts = new CancellationTokenSource();
            var token = _scrollSettleCts.Token;

            _ = Task.Delay(180, token).ContinueWith(t =>
            {
                if (t.IsCanceled || !token.IsCancellationRequested) return;
                SyncVisiblePagesAsync();
            }, token);
        }

        /// <summary>把当前可见页集合提交给宿主（按模式取矩形，切分/恢复墨迹）。</summary>
        private void SyncVisiblePagesAsync()
        {
            var view = _backgroundView;
            if (view == null) return;

            try
            {
                IReadOnlyList<PluginVisiblePage> pages;
                if (_displayMode == PdfDisplayMode.ContinuousScroll)
                    pages = view.GetStripVisiblePageRects(view.ActualHeight);
                else
                    pages = view.GetPagerVisiblePageRects(CurrentPage);

                if (pages == null || pages.Count == 0) return;

                _composition.SetVisiblePagesAsync(pages);
            }
            catch (Exception ex)
            {
                _logError?.Invoke("同步 PDF 可见页失败", ex);
            }
        }

        /// <summary>带取消的可见页同步（供翻页模式用）。</summary>
        private async Task SyncVisiblePagesAsync(CancellationToken cancellationToken)
        {
            var view = _backgroundView;
            if (view == null) return;

            try
            {
                IReadOnlyList<PluginVisiblePage> pages;
                if (_displayMode == PdfDisplayMode.ContinuousScroll)
                {
                    if (view.Dispatcher.CheckAccess()) pages = view.GetStripVisiblePageRects(view.ActualHeight);
                    else pages = await view.Dispatcher.InvokeAsync(() => view.GetStripVisiblePageRects(view.ActualHeight)).Task;
                }
                else
                {
                    if (view.Dispatcher.CheckAccess()) pages = view.GetPagerVisiblePageRects(CurrentPage);
                    else pages = await view.Dispatcher.InvokeAsync(() => view.GetPagerVisiblePageRects(CurrentPage)).Task;
                }

                if (pages == null || pages.Count == 0) return;

                await _composition.SetVisiblePagesAsync(pages, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logError?.Invoke("同步 PDF 可见页失败", ex);
            }
        }

        /// <summary>翻页忙时暂存的最新目标页，当前翻页完成后继续翻。</summary>
        private int _pendingPage = -1;

        /// <summary>翻页模式：翻到指定页（双页翻页对）。快速连发时合并到最新目标页。</summary>
        public async Task GoToPageAsync(int pageIndex, CancellationToken cancellationToken)
        {
            if (_displayMode == PdfDisplayMode.ContinuousScroll)
            {
                await ScrollToPageTopAsync(pageIndex, cancellationToken).ConfigureAwait(false);
                return;
            }

            // 忙时暂存最新目标页，当前翻页完成后继续翻；避免滚轮连发事件被丢弃。
            if (Interlocked.CompareExchange(ref _navigating, 1, 0) != 0)
            {
                Interlocked.Exchange(ref _pendingPage, pageIndex);
                return;
            }

            try
            {
                int pending = pageIndex;
                do
                {
                    await GoToPageCoreAsync(pending, cancellationToken).ConfigureAwait(false);
                    pending = Interlocked.Exchange(ref _pendingPage, -1);
                } while (pending >= 0);
            }
            finally
            {
                Interlocked.Exchange(ref _pendingPage, -1);
                Interlocked.Exchange(ref _navigating, 0);
            }
        }

        private async Task GoToPageCoreAsync(int pageIndex, CancellationToken cancellationToken)
        {
            int total;
            lock (_gate)
            {
                if (_document == null) return;
                total = (int)_document.PageCount;
            }

            int target = ClampPage(pageIndex, total);
            int from = CurrentPage;
            if (target == from) return;

            lock (_gate) { _currentPage = target; }

            // 双页对齐页对起点。
            if (_displayMode == PdfDisplayMode.DoublePage)
            {
                int left = target / 2 * 2;
                if (left != target)
                {
                    target = left;
                    lock (_gate) { _currentPage = target; }
                }
            }

            await RenderCurrentPageAsync(cancellationToken).ConfigureAwait(false);

            await SyncVisiblePagesAsync(cancellationToken).ConfigureAwait(false);

            _config.LastPageIndex = target;

            if (_presentationActive)
            {
                try { _ = _presentation.UpdatePageAsync(target + 1); }
                catch (Exception ex) { _logError?.Invoke("同步放映模式页码失败", ex); }
            }

            try { PageChanged?.Invoke(target); }
            catch (Exception ex) { _logError?.Invoke("PDF 页码变化通知失败", ex); }
        }

        /// <summary>渲染当前页（双页渲染页对）并应用到背景层。</summary>
        private async Task RenderCurrentPageAsync(CancellationToken cancellationToken)
        {
            int page = CurrentPage;
            var left = await RenderPageAsync(page, cancellationToken).ConfigureAwait(false);
            BitmapSource right = null;
            if (_displayMode == PdfDisplayMode.DoublePage && page + 1 < PageCount)
                right = await RenderPageAsync(page + 1, cancellationToken).ConfigureAwait(false);

            ApplyBackground(left, right);
        }

        /// <summary>把当前页位图应用到翻页容器。</summary>
        private void ApplyBackground(BitmapSource left, BitmapSource right)
        {
            var view = _backgroundView;
            if (view == null) return;

            Action apply = () =>
            {
                if (_displayMode == PdfDisplayMode.DoublePage)
                    view.SetDoublePage(left, right);
                else
                    view.SetSinglePage(left);
            };

            if (view.Dispatcher.CheckAccess()) apply();
            else view.Dispatcher.Invoke(apply);
        }

        /// <summary>渲染指定页，命中缓存则直接返回。</summary>
        private async Task<BitmapSource> RenderPageAsync(int pageIndex, CancellationToken cancellationToken)
        {
            PdfDocumentSession document;
            PageRenderCache cache;
            lock (_gate)
            {
                document = _document;
                cache = _cache;
            }
            if (document == null) return null;

            int width = ComputeRenderWidth(document, pageIndex);
            int bucket = width / ZoomModel.WidthBucket;

            if (cache.TryGet(pageIndex, bucket, out var cached)) return cached;

            var bitmap = await document.RenderAsync(pageIndex, width, cancellationToken).ConfigureAwait(false);
            if (bitmap != null) cache.Put(pageIndex, bucket, bitmap);
            return bitmap;
        }

        /// <summary>交给宿主的离屏渲染回调。</summary>
        private Task<BitmapSource> RenderPageForExportAsync(uint pageIndex, CancellationToken cancellationToken)
            => RenderPageAsync((int)pageIndex, cancellationToken);

        /// <summary>按页面物理尺寸与配置倍率计算渲染宽度。</summary>
        private int ComputeRenderWidth(PdfDocumentSession document, int pageIndex)
        {
            double pageWidth;
            double pageHeight;
            try
            {
                var size = document.GetPageSize(pageIndex);
                pageWidth = size.Width;
                pageHeight = size.Height;
            }
            catch
            {
                pageWidth = 612;
                pageHeight = 792;
            }

            return ZoomModel.ComputeRenderWidth(_config.NormalizedRenderScale, pageWidth, pageHeight, 1.0);
        }

        /// <summary>把「背景 + 墨迹」导出为新的 PDF：完整文档，从第一页到末页。</summary>
        public Task<string> ExportAsync(string outputPath, CancellationToken cancellationToken)
        {
            if (!IsOpen) throw new InvalidOperationException(Strings.ErrorNoDocument);

            int count = PageCount;
            if (count <= 0) throw new InvalidOperationException(Strings.ErrorNoDocument);

            if (!_composition.HasBackgroundLayer || _composition.PageCount != (uint)count)
            {
                EnsureBackgroundLayer();
                _composition.ConfigurePages((uint)count, (uint)CurrentPage, RenderPageForExportAsync);
            }

            return _composition.ExportWithInkAsync(outputPath, 0u, cancellationToken);
        }

        /// <summary>关闭文档并移除背景层。</summary>
        public void Close()
        {
            EndPresentation();

            _scrollSettleCts?.Cancel();
            _scrollSettleCts?.Dispose();
            _scrollSettleCts = null;

            PdfDocumentSession document;
            lock (_gate)
            {
                _renderCts?.Cancel();
                _renderCts?.Dispose();
                _renderCts = null;
                document = _document;
                _document = null;
                _currentPage = 0;
                _cache?.Clear();
            }

            document?.Dispose();

            DetachWheelHandler();

            var view = _backgroundView;
            if (view != null)
            {
                try
                {
                    view.Loaded -= BackgroundView_Loaded;
                    view.SizeChanged -= BackgroundView_SizeChanged;
                }
                catch { }
            }

            try { _composition.RemoveBackgroundLayer(); }
            catch (Exception ex) { _logError?.Invoke("移除 PDF 背景层失败", ex); }

            _backgroundView = null;
        }

        private static int ClampPage(int pageIndex, int pageCount)
        {
            if (pageCount <= 0) return 0;
            if (pageIndex < 0) return 0;
            if (pageIndex >= pageCount) return pageCount - 1;
            return pageIndex;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Close(); } catch { }
        }
    }
}
