using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Ink_Canvas.Plugins;
using PdfReader.Views;

namespace PdfReader
{
    /// <summary>
    /// 嵌入式 PDF 会话：把 PDF 作为背景层注入宿主画布下方，墨迹由宿主自己的 InkCanvas 承载。
    /// 翻页时通过 <see cref="ICanvasCompositionService.SetCurrentPageAsync"/> 让宿主按页存取墨迹，
    /// 导出交给宿主的 <see cref="ICanvasCompositionService.ExportWithInkAsync"/>（PdfSharp 组装）。
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

        /// <summary>正在翻页中，用于丢弃同一次滚轮手势里的连发事件。</summary>
        private int _turning;

        /// <summary>已挂上 PreviewMouseWheel 的宿主窗口。</summary>
        private Window _wheelHost;

        /// <summary>是否已成功进入宿主放映模式；退出时据此决定是否调用 EndAsync。</summary>
        private bool _presentationActive;

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

        public int CurrentPage
        {
            get { lock (_gate) return _currentPage; }
        }

        /// <summary>打开文档、注入背景层并渲染首页。</summary>
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

            // 先告知总页数与当前页，并交出离屏渲染回调（导出非当前页时宿主会回调它）。
            _composition.ConfigurePages((uint)session.PageCount, (uint)_currentPage, RenderPageForExportAsync);

            await RenderCurrentPageAsync(cancellationToken).ConfigureAwait(false);

            await BeginPresentationAsync((int)session.PageCount, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 进入宿主放映模式，让宿主的四个翻页条接管翻页。
        /// 宿主未提供该服务（旧版本）时静默跳过，PDF 仍可用弹窗与滚轮翻页。
        /// </summary>
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
                    // 宿主页码是 1-based，内部索引是 0-based。
                    CurrentPage = CurrentPage + 1,
                    NavigateAsync = HandleHostNavigationAsync,
                    // PDF 没有缩略图跳页 UI，禁用页码点击。
                    AllowPageNumberClick = false
                };

                await _presentation.BeginAsync(descriptor, cancellationToken).ConfigureAwait(false);

                // BeginAsync 返回 true 才算真正进入；被真实 PPT 放映拒绝时保持未激活。
                if (_presentation.IsActive)
                {
                    _presentationActive = true;

                    // 宿主侧强制结束（用户点退出按钮、真实 PPT 开始放映等）时，
                    // 插件要随之关闭文档并移除背景层，否则会残留放映布局。
                    try { _presentation.Ended += OnPresentationEnded; }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logError?.Invoke("进入放映模式失败", ex);
            }
        }

        /// <summary>宿主结束外部演示源时触发，插件随之关闭 PDF（等价于"退出=关闭"）。</summary>
        private void OnPresentationEnded(string sourceId)
        {
            if (sourceId != PresentationSourceId) return;

            _presentationActive = false;
            try { _presentation.Ended -= OnPresentationEnded; }
            catch { }

            // 宿主已在 UI 线程触发本事件；Close 是同步清理，直接调用。
            Close();
        }

        /// <summary>
        /// 宿主翻页条触发的翻页。返回新页码（1-based），到边界返回 0 让宿主忽略。
        /// </summary>
        private async Task<int> HandleHostNavigationAsync(PresentationNavigation direction,
            CancellationToken cancellationToken)
        {
            if (!IsOpen) return 0;

            int target = CurrentPage + (direction == PresentationNavigation.Next ? 1 : -1);
            if (target < 0 || target >= PageCount) return 0;

            await GoToPageAsync(target, cancellationToken).ConfigureAwait(false);
            return CurrentPage + 1;
        }

        /// <summary>
        /// 退出宿主放映模式。仅在确实进入过（服务存在且文档已打开）时才调用，
        /// 避免对未激活的演示源发起无谓的结束请求。不 await：关闭是同步路径。
        /// </summary>
        private void EndPresentation()
        {
            if (_presentation == null || !_presentationActive) return;
            _presentationActive = false;

            // 主动结束时先退订：否则宿主 EndAsync 触发 Ended 事件又会调 OnPresentationEnded → Close，
            // 造成递归关闭。
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

            // 工厂在宿主的 UI 线程被同步调用（RunOnUiThread 内），返回前 _backgroundView 必已赋值，
            // 因此本方法返回后即可安全地向它推送位图。
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

            // 布局完成后才有确切的 ActualWidth/Height，此时同步一次内容矩形。
            if (view != null) SyncPageContentRect(view);
        }

        /// <summary>画布尺寸变化会改变 Uniform 后的页面矩形，需要重新同步给宿主。</summary>
        private void BackgroundView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is PdfBackgroundView view) SyncPageContentRect(view);
        }

        /// <summary>
        /// 背景层 IsHitTestVisible = false，收不到滚轮事件，因此挂到宿主窗口的
        /// PreviewMouseWheel（隧道事件，窗口先于任何子元素收到）。
        /// </summary>
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

            // 仅在指针位于背景层范围内时接管，避免抢走工具栏、弹窗里的滚动。
            var view = _backgroundView;
            if (view == null || !view.IsVisible) return;

            Point local;
            try { local = e.GetPosition(view); }
            catch { return; }

            if (local.X < 0 || local.Y < 0 || local.X > view.ActualWidth || local.Y > view.ActualHeight)
                return;

            if (HandleMouseWheel(e.Delta)) e.Handled = true;
        }

        /// <summary>页码变化通知（含滚轮翻页），供插件刷新弹窗与保存配置。</summary>
        public event Action<int> PageChanged;

        /// <summary>滚轮向下翻到下一页，向上翻到上一页。返回 true 表示事件已被 PDF 会话接管。</summary>
        public bool HandleMouseWheel(int delta)
        {
            if (!IsOpen || delta == 0) return false;

            // 一个滚轮刻度通常会产生多个 PreviewMouseWheel；一次翻页动画未完成前丢弃后续事件。
            if (Interlocked.CompareExchange(ref _turning, 1, 0) != 0) return true;

            _ = TurnFromWheelAsync(delta < 0);
            return true;
        }

        private async Task TurnFromWheelAsync(bool forward)
        {
            try
            {
                int target = CurrentPage + (forward ? 1 : -1);
                if (target < 0 || target >= PageCount) return;
                await GoToPageAsync(target, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                // 留出很短的间隔，避免触控板惯性在一页完成后立即连翻多页。
                await Task.Delay(90).ConfigureAwait(false);
                Interlocked.Exchange(ref _turning, 0);
            }
        }

        /// <summary>翻到指定页：先渲染背景，再让宿主切换该页墨迹。</summary>
        public async Task GoToPageAsync(int pageIndex, CancellationToken cancellationToken)
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

            await RenderCurrentPageAsync(cancellationToken, animate: true, forward: target > from)
                .ConfigureAwait(false);

            // 背景已经是新页，此时再交给宿主换墨迹，避免出现「旧墨迹压新页」的一帧。
            await _composition.SetCurrentPageAsync((uint)target, cancellationToken).ConfigureAwait(false);

            _config.LastPageIndex = target;

            try { PageChanged?.Invoke(target); }
            catch (Exception ex) { _logError?.Invoke("PDF 页码变化通知失败", ex); }
        }

        public Task NextPageAsync(CancellationToken cancellationToken)
            => GoToPageAsync(CurrentPage + 1, cancellationToken);

        public Task PreviousPageAsync(CancellationToken cancellationToken)
            => GoToPageAsync(CurrentPage - 1, cancellationToken);

        private async Task RenderCurrentPageAsync(CancellationToken cancellationToken,
            bool animate = false, bool forward = true)
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                _renderCts?.Cancel();
                _renderCts?.Dispose();
                _renderCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts = _renderCts;
            }

            int page = CurrentPage;
            try
            {
                var bitmap = await RenderPageAsync(page, cts.Token).ConfigureAwait(false);
                if (bitmap == null || cts.IsCancellationRequested) return;
                ApplyBackground(bitmap, animate, forward);
            }
            catch (OperationCanceledException)
            {
                // 被后续翻页取代，属正常路径。
            }
            catch (Exception ex)
            {
                _logError?.Invoke(string.Format(Strings.ErrorRenderFailedFormat, page + 1), ex);
            }
        }

        private void ApplyBackground(BitmapSource bitmap, bool animate = false, bool forward = true)
        {
            var view = _backgroundView;
            if (view == null) return;

            Action apply = () =>
            {
                if (animate) view.SetPageWithSlide(bitmap, forward);
                else view.SetPage(bitmap);

                // 页面按 Uniform 居中留边，导出必须知道真正的页面区域，
                // 否则会被拉伸成整块画布的比例（16:9），墨迹也跟着错位。
                SyncPageContentRect(view);
            };

            if (view.Dispatcher.CheckAccess()) apply();
            else view.Dispatcher.Invoke(apply);
        }

        /// <summary>把当前页在背景层内的实际矩形同步给宿主，供导出裁剪与墨迹换算。</summary>
        private void SyncPageContentRect(PdfBackgroundView view)
        {
            try
            {
                // 刚换图时布局可能未更新，先强制测量一次再取矩形。
                view.UpdateLayout();
                _composition.SetPageContentRect(view.GetPageContentRect());
            }
            catch (Exception ex)
            {
                _logError?.Invoke("同步 PDF 页面内容矩形失败", ex);
            }
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

        /// <summary>
        /// 交给宿主的离屏渲染回调：导出时逐页调用（含非当前页）。
        /// 返回已 Freeze 的位图，宿主据其像素宽度决定合成倍率。
        /// </summary>
        private Task<BitmapSource> RenderPageForExportAsync(uint pageIndex, CancellationToken cancellationToken)
            => RenderPageAsync((int)pageIndex, cancellationToken);

        /// <summary>按页面物理尺寸与配置倍率计算渲染宽度，并受 ZoomModel 上限约束。</summary>
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

            // 宿主切换黑板/清屏等操作可能移除背景层并清空分页状态；导出前重新配置，
            // 确保宿主拿到当前文档的页数与离屏渲染回调。
            if (!_composition.HasBackgroundLayer || _composition.PageCount != (uint)count)
            {
                EnsureBackgroundLayer();
                _composition.ConfigurePages((uint)count, (uint)CurrentPage, RenderPageForExportAsync);
            }

            // 宿主的语义是「从给定页导到末页」，因此固定传 0 以导出整个文档，
            // 而不是当前浏览到的那一页。
            return _composition.ExportWithInkAsync(outputPath, 0u, cancellationToken);
        }

        /// <summary>关闭文档并移除背景层（宿主会同时清空按页墨迹缓存）。</summary>
        public void Close()
        {
            // 先退出放映模式：否则关掉 PDF 后宿主仍停留在放映布局、翻页条还挂着，
            // 而翻页请求已经没有文档可翻。这里不 await，Close 是同步 API。
            EndPresentation();

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
