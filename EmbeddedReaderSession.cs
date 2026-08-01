using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ink_Canvas.Plugins;
using PdfReader.Views;

namespace PdfReader
{
    /// <summary>
    /// 嵌入式 PDF 会话：把 PDF 作为连续滚动长条注入宿主画布下方，墨迹由宿主自己的 InkCanvas 承载。
    /// 滚动时背景层平移、宿主同步平移画布墨迹（实时跟随）；滚动停止后按视口内可见页切分/恢复墨迹。
    /// 导出交给宿主的 <see cref="ICanvasCompositionService.ExportWithInkAsync"/>。
    /// 同时实现 <see cref="IPluginCanvasGestureHandler"/>：接管宿主转发的双指捏合缩放/平移，
    /// 把缩放/平移作用于背景层视图矩阵，并实时同步画布墨迹。
    /// </summary>
    internal sealed class EmbeddedReaderSession : IDisposable, IPluginCanvasGestureHandler
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

        /// <summary>
        /// 展示模式切换/文档重载进行中：屏蔽 SizeChanged/去抖触发的无参可见页同步，
        /// 避免用中间态矩形误清画布墨迹（切换瞬间画布仍是旧坐标系）。
        /// </summary>
        private bool _modeSwitching;

        /// <summary>
        /// 视图矩阵（缩放/平移），与背景层根节点的 RenderTransform 一一对应。
        /// 双指手势/滚轮缩放更新它后，通过 <see cref="PdfBackgroundView.SetViewMatrix"/> 应用到背景层，
        /// 并用宿主 <see cref="ICanvasCompositionService.TransformInkAsync"/> 以同一增量矩阵实时变换墨迹。
        /// 宿主对墨迹的按页存取自动包含该矩阵（TransformToVisual），因此墨迹始终锚定页面内容。
        /// </summary>
        private Matrix _viewMatrix = Matrix.Identity;

        /// <summary>双指手势进行中；手势结束（<see cref="OnCanvasGestureCompleted"/>）时复位。</summary>
        private bool _gestureActive;

        /// <summary>当前展示模式。</summary>
        public PdfDisplayMode DisplayMode
        {
            get { lock (_gate) return _displayMode; }
        }

        /// <summary>当前视图缩放比例（矩阵 M11，均一缩放）。</summary>
        internal double ViewScale
        {
            get { lock (_gate) return _viewMatrix.M11; }
        }

        /// <summary>视图矩阵（缩放/平移）变化时触发，供弹窗刷新缩放百分比显示。</summary>
        public event Action ViewTransformChanged;

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
            _composition.SetCanvasGestureHandler(this);

            _composition.ConfigurePages((uint)session.PageCount, (uint)_currentPage, RenderPageForExportAsync);

            _modeSwitching = true;
            try
            {
                await ApplyInitialDisplayAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _modeSwitching = false;
            }

            await BeginPresentationAsync((int)session.PageCount, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>按当前模式初始化背景层显示。</summary>
        private async Task ApplyInitialDisplayAsync(CancellationToken cancellationToken)
        {
            var view = _backgroundView;
            if (view == null) return;

            // 重载文档时背景视图是新建的，内部模式仍为默认单页；先与当前模式对齐，
            // 否则连续滚动下 Strip 保持折叠，ResetStrip 在 0 尺寸上布局，页面全空。
            if (view.Mode != _displayMode)
            {
                if (view.Dispatcher.CheckAccess()) view.SetDisplayMode(_displayMode);
                else await view.Dispatcher.InvokeAsync(() => view.SetDisplayMode(_displayMode));
            }

            if (_displayMode == PdfDisplayMode.ContinuousScroll)
            {
                await ResetStripAsync(cancellationToken).ConfigureAwait(false);

                // 长条的页面 Image 尺寸要到下一帧布局才真正就位（ActualWidth/Height 有效）。
                // 此刻立即滚动/同步会拿到 0 尺寸矩形（stripH=0）→ 可见页为空、墨迹被清，
                // 或落入宿主单页分支被误存到 _pluginCurrentPageIndex（「墨迹刷到第一页」）。
                // 因此把「跳当前页顶部 + 按可见页归位墨迹」推迟到 Loaded 优先级布局完成后执行。
                if (view.Dispatcher.CheckAccess())
                {
                    _ = view.Dispatcher.BeginInvoke(
                        new Action(() => InitializeScrollDisplayAsync(view, cancellationToken)),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else
                {
                    await view.Dispatcher.InvokeAsync(() =>
                        _ = view.Dispatcher.BeginInvoke(
                            new Action(() => InitializeScrollDisplayAsync(view, cancellationToken)),
                            System.Windows.Threading.DispatcherPriority.Loaded));
                }
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

        /// <summary>
        /// 连续滚动模式的完整初始化：长条布局就绪后跳到当前页顶部，再按可见页归位墨迹。
        /// 切换瞬间不实时平移墨迹（会把墨迹按整页高度甩出视口），而是跳到位后
        /// 按滚动后的长条矩形整体重放（先按旧可见页保存，再按新位置恢复）。
        /// </summary>
        private async void InitializeScrollDisplayAsync(PdfBackgroundView view, CancellationToken cancellationToken)
        {
            if (view == null || !IsOpen || _displayMode != PdfDisplayMode.ContinuousScroll) return;

            try
            {
                await ScrollToPageTopAsync(_currentPage, cancellationToken, jump: true).ConfigureAwait(false);
                await SyncVisiblePagesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logError?.Invoke("连续滚动模式初始化失败", ex);
            }
        }

        /// <summary>切换展示模式，重排背景层并恢复墨迹。</summary>
        public async Task SetDisplayModeAsync(PdfDisplayMode mode, CancellationToken cancellationToken)
        {
            _logError?.Invoke($"SetDisplayMode 请求={mode} 当前={_displayMode} IsOpen={IsOpen}", null);
            if (_displayMode == mode) return;

            var view = _backgroundView;
            if (view == null) return;

            // 切换期间屏蔽无参可见页同步（SizeChanged/去抖触发），避免用中间态矩形误清画布墨迹。
            _modeSwitching = true;
            try
            {
                // 先按旧模式保存当前画布墨迹到对应页（此时 _displayMode 还是旧值，
                // SyncVisiblePagesAsync 会用旧模式矩形正确保存），再切换模式。
                if (IsOpen)
                    await SyncVisiblePagesAsync(cancellationToken).ConfigureAwait(false);

                // 记录新模式：即使文档未打开（IsOpen false）也要保存，
                // 这样用户先选模式再打开 PDF 时，OpenAsync 能按所选模式初始化。
                lock (_gate) { _displayMode = mode; }

                if (!IsOpen) return;

                if (view.Dispatcher.CheckAccess()) view.SetDisplayMode(mode);
                else await view.Dispatcher.InvokeAsync(() => view.SetDisplayMode(mode));

                await ApplyInitialDisplayAsync(cancellationToken).ConfigureAwait(false);
                _logError?.Invoke($"SetDisplayMode 完成={mode}", null);
            }
            finally
            {
                _modeSwitching = false;
            }
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

        /// <summary>宿主翻页条触发，与弹窗上/下一页一致：按当前模式分派。</summary>
        private async Task<int> HandleHostNavigationAsync(PresentationNavigation direction,
            CancellationToken cancellationToken)
        {
            if (!IsOpen) return 0;

            int target = CurrentPage + (direction == PresentationNavigation.Next ? 1 : -1);
            if (target < 0 || target >= PageCount) return 0;

            if (_displayMode == PdfDisplayMode.ContinuousScroll)
                await ScrollToPageTopAsync(target, cancellationToken).ConfigureAwait(false);
            else
                await GoToPageAsync(target, cancellationToken).ConfigureAwait(false);

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

            // 告诉宿主墨迹换算的目标是「内容层」（缩放/平移容器），而非固定背景根。
            // 这样墨迹的按页存取自动包含内容层的缩放，缩放后翻页/切模式墨迹不错位。
            if (created != null)
            {
                try { _composition.SetCanvasContentAnchor(created.ContentAnchor); }
                catch (Exception ex) { _logError?.Invoke("设置 PDF 内容锚点失败", ex); }
            }
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

            // Ctrl+滚轮：以光标为锚缩放（给无触摸屏环境提供缩放入口，与 PDF 查看器惯例一致）。
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (HandleZoom(e.Delta > 0, local)) { e.Handled = true; return; }
            }

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
                _ = GoToPageAsync(target, CancellationToken.None);
            }
            return true;
        }

        #region 双指手势与缩放

        /// <summary>当前视图矩阵的缩放比例（均一缩放）。</summary>
        private double CurrentScale => _viewMatrix.M11;

        public bool OnCanvasGestureStarting(ManipulationStartingEventArgs e)
        {
            if (!IsOpen || _backgroundView == null) return false;
            if ((e.Manipulators?.Count() ?? 0) < 2) return false;

            // 只声明缩放 + 平移：双指捏合缩放 / 双指平移。不加旋转避免误旋转。
            e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
            _gestureActive = true;
            return true;
        }

        public bool OnCanvasGestureDelta(ManipulationDeltaEventArgs e)
        {
            if (!IsOpen || _backgroundView == null) return false;
            if ((e.Manipulators?.Count() ?? 0) < 2) return false;

            try
            {
                var delta = e.DeltaManipulation;
                double tx = delta.Translation.X;
                double ty = delta.Translation.Y;
                double factor = delta.Scale.Length > 0 ? (delta.Scale.X + delta.Scale.Y) / 2.0 : 1.0;

                double oldScale = CurrentScale;
                double newScale = Math.Max(ZoomModel.MinScale, Math.Min(ZoomModel.MaxScale, oldScale * factor));
                double ratio = newScale / oldScale;

                if (Math.Abs(tx) < 0.001 && Math.Abs(ty) < 0.001 && Math.Abs(ratio - 1.0) < 0.0001)
                    return true;

                // 增量矩阵 = 以手势中心为锚缩放 + 平移，作用于画布坐标。
                Point origin = e.ManipulationOrigin;
                var inc = new Matrix();
                inc.ScaleAt(ratio, ratio, origin.X, origin.Y);
                inc.Translate(tx, ty);

                ApplyViewTransform(inc);
                return true;
            }
            catch (Exception ex)
            {
                _logError?.Invoke("PDF 双指手势处理失败", ex);
                return true;
            }
        }

        public void OnCanvasGestureCompleted(ManipulationCompletedEventArgs e)
        {
            if (!_gestureActive) return;
            _gestureActive = false;

            // 手势期间墨迹已用同一增量矩阵实时跟随；结束时按最终视图矩阵把墨迹重放归位到各页。
            SyncVisiblePagesAsync();
            RaiseViewTransformChanged();
        }

        /// <summary>应用增量矩阵：更新视图矩阵、同步墨迹，并调度滚动停止归位。</summary>
        private void ApplyViewTransform(Matrix inc)
        {
            var view = _backgroundView;
            if (view == null) return;

            _viewMatrix = _viewMatrix * inc;

            if (view.Dispatcher.CheckAccess()) view.SetViewMatrix(_viewMatrix);
            else view.Dispatcher.Invoke(() => view.SetViewMatrix(_viewMatrix));

            // 墨迹实时跟随（同一增量矩阵，画布坐标）。手势事件在 UI 线程回调，
            // 宿主的 TransformInkAsync 在 UI 线程内联执行，因此按增量即时生效。
            try
            {
                _ = _composition.TransformInkAsync(inc, CancellationToken.None)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted) _logError?.Invoke("双指手势墨迹跟随失败", t.Exception);
                    }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                _logError?.Invoke("双指手势墨迹跟随失败", ex);
            }

            // 手势期间不重分页（避免频繁清/填画布），结束后去抖一次按最终矩阵归位。
            ScheduleSettleSync();
            RaiseViewTransformChanged();
        }

        /// <summary>
        /// 缩放感知的墨迹跟随：缩放后同样的滚动增量对应更大的画布位移（×当前缩放比例）。
        /// 仍走宿主 <see cref="ICanvasCompositionService.ScrollOffsetAsync"/>，宿主记账
        /// 在每次可见页同步时归零，因此缩放前后一致，无需宿主感知缩放本身。
        /// </summary>
        private Task ScrollInkFollowAsync(double actualDelta, CancellationToken cancellationToken = default)
        {
            double s = CurrentScale;
            return _composition.ScrollOffsetAsync(actualDelta * s, cancellationToken);
        }

        /// <summary>Ctrl+滚轮缩放：以光标（背景层局部坐标）为锚步进缩放。</summary>
        private bool HandleZoom(bool zoomIn, Point anchorInView)
        {
            if (!IsOpen) return false;

            double oldScale = CurrentScale;
            double target = zoomIn ? ZoomModel.StepUp(oldScale) : ZoomModel.StepDown(oldScale);
            if (Math.Abs(target - oldScale) < 1e-9) return false;
            double ratio = target / oldScale;

            // 光标在背景层局部坐标；缩放锚点须换算到画布坐标（与墨迹/视图矩阵同一坐标系）。
            Point anchor = _viewMatrix.Transform(anchorInView);

            var inc = new Matrix();
            inc.ScaleAt(ratio, ratio, anchor.X, anchor.Y);

            ApplyViewTransform(inc);
            return true;
        }

        /// <summary>重置缩放：视图矩阵归零，墨迹按逆矩阵复位，再按页重放。</summary>
        internal async Task ResetZoomAsync()
        {
            if (_viewMatrix.IsIdentity) return;

            Matrix inverse = _viewMatrix;
            if (inverse.HasInverse) inverse.Invert();
            else inverse = Matrix.Identity;

            _viewMatrix = Matrix.Identity;

            var view = _backgroundView;
            if (view != null)
            {
                if (view.Dispatcher.CheckAccess()) view.SetViewMatrix(_viewMatrix);
                else await view.Dispatcher.InvokeAsync(() => view.SetViewMatrix(_viewMatrix)).Task;
            }

            try
            {
                await _composition.TransformInkAsync(inverse, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logError?.Invoke("重置缩放墨迹失败", ex);
            }

            await SyncVisiblePagesAsync(CancellationToken.None).ConfigureAwait(false);
            RaiseViewTransformChanged();
        }

        private void RaiseViewTransformChanged()
        {
            try { ViewTransformChanged?.Invoke(); }
            catch (Exception ex) { _logError?.Invoke("视图变换通知失败", ex); }
        }

        #endregion

        /// <summary>按增量滚动，墨迹实时跟随。</summary>
        public async Task ScrollByAsync(double deltaY)
        {
            if (deltaY == 0) return;

            var view = _backgroundView;
            if (view == null) return;

            // 缩放后视口内可见的局部高度变小（缩放 s 后只看到 1/s 的内容），滚动边界随之缩小。
            double viewportH = view.EffectiveViewportHeight;
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

            // 2. 宿主实时平移墨迹（缩放感知：缩放后同样的滚动增量对应更大的画布位移）。
            try
            {
                await ScrollInkFollowAsync(actualDelta).ConfigureAwait(false);
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
        public async Task ScrollToPageTopAsync(int pageIndex, CancellationToken cancellationToken, bool jump = false)
        {
            var view = _backgroundView;
            if (view == null || !IsOpen) return;

            int target = ClampPage(pageIndex, PageCount);
            double viewportH = view.EffectiveViewportHeight;

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

            await ScrollToOffsetAsync(newOffset, cancellationToken, jump).ConfigureAwait(false);
        }

        /// <summary>滚动到绝对偏移。</summary>
        private async Task ScrollToOffsetAsync(double newOffset, CancellationToken cancellationToken, bool jump = false)
        {
            var view = _backgroundView;
            if (view == null) return;

            double delta = newOffset - view.ScrollOffset;
            bool moved = Math.Abs(delta) >= 0.5;

            if (moved)
            {
                if (view.Dispatcher.CheckAccess()) view.SetScrollOffset(newOffset);
                else await view.Dispatcher.InvokeAsync(() => view.SetScrollOffset(newOffset));

                // 实时平移墨迹仅在常规滚动时做。jump 为真表示模式切换/初始定位：
                // 画布墨迹还是旧坐标系，平移会把它甩出视口，改为随后的
                // SyncVisiblePagesAsync 按新长条矩形整体重放。
                if (!jump)
                {
                    try
                    {
                        await ScrollInkFollowAsync(delta, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logError?.Invoke("滚动墨迹跟随失败", ex);
                    }
                }
            }

            UpdateCurrentPageFromScroll();
            // 即使没实际滚动（首次切滚动 offset 已为目标值）也要调度墨迹同步，
            // 否则切到滚动模式后画布墨迹不会按长条矩形恢复。jump 跳转由调用方显式同步。
            if (!jump) ScheduleSettleSync();
        }

        /// <summary>根据滚动偏移计算视口顶部页，并通知页码变化。</summary>
        private void UpdateCurrentPageFromScroll()
        {
            var view = _backgroundView;
            if (view == null) return;

            // 缩放围绕非原点锚点时会引入视图矩阵平移，视口顶边对应的长条内容偏移
            // 偏离 ScrollOffset，需按矩阵修正。
            double offset = view.GetViewportTopScrollOffset();

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

            // 同步宿主翻页条的页码：滚动模式翻页条/弹窗触发的滚动也要刷新。
            if (_presentationActive)
            {
                try { _ = _presentation.UpdatePageAsync(_currentPage + 1); }
                catch (Exception ex) { _logError?.Invoke("同步放映模式页码失败", ex); }
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
                // 180ms 内有新滚动：token 被取消、delay 未完成，跳过本次归位。
                // 只有滚动真正停止 180ms 后才按可见页矩形归位墨迹。
                if (t.IsCanceled) return;
                SyncVisiblePagesAsync();
            }, token);
        }

        /// <summary>把当前可见页集合提交给宿主（按模式取矩形，切分/恢复墨迹）。</summary>
        private void SyncVisiblePagesAsync()
        {
            // 模式切换/重载进行中：画布墨迹仍是旧坐标系，用中间态矩形同步会误清画布。
            // 切换流程结束时会显式同步一次，这里直接跳过。
            if (_modeSwitching) return;

            var view = _backgroundView;
            if (view == null) return;

            // ScheduleSettleSync 在去抖后从线程池回调，必须回到 UI 线程再访问 WPF 对象
            //（Canvas.GetTop 等依赖属性），否则跨线程异常让滚动停止后的归位同步从未生效。
            if (!view.Dispatcher.CheckAccess())
            {
                view.Dispatcher.BeginInvoke(new Action(SyncVisiblePagesAsync));
                return;
            }

            try
            {
                IReadOnlyList<PluginVisiblePage> pages;
                if (_displayMode == PdfDisplayMode.ContinuousScroll)
                    pages = view.GetStripVisiblePageRects();
                else
                    pages = view.GetPagerVisiblePageRects(CurrentPage);

                // 诊断：确认滚动模式可见页同步是否被调用。
                _logError?.Invoke($"SyncVisiblePages 模式={_displayMode} 页数={pages?.Count ?? -1} " +
                    $"scrollOffset={view.ScrollOffset:F1} stripH={view.StripHeight:F1} viewH={view.ActualHeight:F1}", null);

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
                    if (view.Dispatcher.CheckAccess()) pages = view.GetStripVisiblePageRects();
                    else pages = await view.Dispatcher.InvokeAsync(view.GetStripVisiblePageRects).Task;
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

            try { _composition.SetCanvasGestureHandler(null); }
            catch (Exception ex) { _logError?.Invoke("注销 PDF 画布手势处理器失败", ex); }

            try { _composition.SetCanvasContentAnchor(null); }
            catch (Exception ex) { _logError?.Invoke("清除 PDF 内容锚点失败", ex); }

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
