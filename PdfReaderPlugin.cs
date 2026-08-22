using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Common.IconKeys;
using Ink_Canvas.Controls;
using Ink_Canvas.Plugins;
using Microsoft.Extensions.DependencyInjection;
using PdfReader.Views;

namespace PdfReader
{
    /// <summary>
    /// PDF 阅读器插件入口。把 PDF 渲染成位图注入宿主画布下方作为背景，
    /// 批注仍由宿主自己的墨迹工具完成；翻页时宿主按页存取墨迹，导出时把「背景 + 墨迹」写入新 PDF。
    /// 依赖宿主的 <see cref="ICanvasCompositionService"/>，不自建 InkCanvas。
    /// </summary>
    [PluginEntrance]
    public class PdfReaderPlugin : PluginBase, IDisposable
    {
        /// <summary>
        /// 字体图标不可用时的回落路径（24x24 视口内的文档轮廓）。
        /// 正常情况下用的是 iNKORE.UI.WPF.Modern 的 SegoeFluentIcons.PDF。
        /// </summary>
        private const string FallbackIconGeometry =
            "F1 M24,24z M0,0z M6,2 L14,2 L20,8 L20,22 L6,22 z M14,2 L14,8 L20,8 " +
            "M9,13 L15,13 M9,16 L15,16 M9,19 L13,19";

        private readonly object _gate = new object();
        private string _configPath;
        private ReaderConfig _config;
        private ICanvasCompositionService _composition;

        /// <summary>外部演示源服务；宿主较旧时为 null，此时不进入放映模式，其余功能不受影响。</summary>
        private IPresentationSourceService _presentation;

        private INotificationService _notificationService;
        private IEventService _eventService;
        private IWindowService _windowService;
        private ICanvasInkService _canvasInkService;

        /// <summary>URI 深链接服务（文件关联双击 .pdf → icc://plugin/com.icc.pdf-reader/open）。</summary>
        private IPluginUriService _uriService;

        /// <summary>文件关联服务（设置页注册/注销/查看 .pdf 关联）。</summary>
        private IFileAssociationService _association;

        private EmbeddedReaderSession _session;

        /// <summary>当前是否处于白板模式（由 WhiteboardModeChanged 事件维护）。</summary>
        private bool _isWhiteboardMode;

        /// <summary>当前是否笔类模式（笔/荧光笔/橡皮/选择）；false = 纯鼠标模式。默认按笔处理更安全。</summary>
        private bool _isPenMode = true;

        /// <summary>本次 PDF 是从白板里打开的：关闭后要回到白板。</summary>
        private bool _openedFromWhiteboard;

        /// <summary>打开流程中我们自己退出白板的标志，避免误清 _openedFromWhiteboard。</summary>
        private bool _exitingWhiteboardForOpen;

        /// <summary>白板工具栏按钮承载的弹窗（关闭 PDF 时一并收起）。</summary>
        private System.Windows.Controls.Primitives.Popup _boardPopup;

        /// <summary>白板弹窗的独立内容实例（与浮动弹窗的 _popup 隔离，避免两个 Popup 争用同一内容）。</summary>
        private ReaderPopupContent _boardPopupContent;
        private SettingsView _settingsView;
        private ReaderPopupContent _popup;
        private string _statusText = string.Empty;
        private bool _disposed;

        public override void Initialize(IPluginHost host, IServiceCollection services)
        {
            base.Initialize(host, services);
            Log($"{Name} v{Version} 正在初始化...");

            _configPath = Path.Combine(PluginConfigFolder, "config.json");
            _config = ReaderConfig.Load(_configPath);

            if (!PdfSupport.Probe())
                Log("系统 PDF 组件不可用：" + (PdfSupport.UnavailableReason ?? "unknown"));

            try { _notificationService = GetService<INotificationService>(); }
            catch { _notificationService = null; }

            try { _composition = GetService<ICanvasCompositionService>(); }
            catch (Exception ex)
            {
                _composition = null;
                LogError("获取画布合成服务失败", ex);
            }

            if (_composition == null)
                Log("宿主未提供 ICanvasCompositionService，PDF 无法作为画布背景使用。");

            try { _presentation = GetService<IPresentationSourceService>(); }
            catch
            {
                _presentation = null;
                Log("宿主未提供演示源服务，PDF 将不进入放映模式（滚轮翻页与弹窗控制仍可用）。");
            }

            // 订阅白板模式切换：进入白板时隐藏 PDF 背景，退出时恢复（见 OnWhiteboardModeChanged）。
            try { _eventService = GetService<IEventService>(); }
            catch { _eventService = null; }

            if (_eventService != null)
            {
                try { _eventService.WhiteboardModeChanged += OnWhiteboardModeChanged; }
                catch { _eventService = null; }

                if (_eventService != null)
                {
                    // 笔/鼠标模式：控制鼠标模式下单指平移是否接管（见 OnPenModeChanged）。
                    try { _eventService.PenModeChanged += OnPenModeChanged; }
                    catch { }
                }
            }

            // 白板与 PDF 互操作（打开前退出白板、关闭后回到白板）需要的服务。
            try { _canvasInkService = GetService<ICanvasInkService>(); }
            catch { _canvasInkService = null; }

            try
            {
                _windowService = GetService<IWindowService>();
                _isWhiteboardMode = _windowService?.IsWhiteboardMode ?? false;
            }
            catch { _windowService = null; }

            // 文件关联深链接：宿主收到 .pdf 打开请求时派发 icc://plugin/<id>/open，这里接收并打开文档。
            try { _uriService = GetService<IPluginUriService>(); }
            catch { _uriService = null; }
            if (_uriService != null)
            {
                try { _uriService.RegisterHandler("open", OnOpenDocumentUri); }
                catch (Exception ex) { LogError("注册 PDF 打开深链接失败", ex); }
            }

            // 文件关联服务（设置页注册/注销 .pdf 关联用；宿主较旧为 null 时设置页提示不可用）。
            try { _association = GetService<IFileAssociationService>(); }
            catch { _association = null; }

            RegisterToolbarButton(host);
            Log("工具栏组件「" + Strings.PluginName + "」已注册。");
        }

        private void RegisterToolbarButton(IPluginHost host)
        {
            // 优先用 iNKORE.UI.WPF.Modern 官方的 PDF 图标，保证与宿主其它图标风格一致。
            var iconGeometry = IconGeometryBuilder.FromFontIcon(SegoeFluentIcons.PDF);
            var iconMarkup = iconGeometry?.ToString(CultureInfo.InvariantCulture) ?? FallbackIconGeometry;

            var item = new PluginToolbarItemInfo
            {
                Id = "pdf.reader",
                DisplayName = Strings.ToolbarButton,
                Description = Strings.ToolbarDescription,
                IconGeometry = iconMarkup,
                ViewFactory = () =>
                {
                    var button = new ToolbarImageButton { Label = Strings.ToolbarButton };
                    try
                    {
                        button.Icon.Geometry = iconGeometry ?? Geometry.Parse(FallbackIconGeometry);
                        button.SetResourceReference(ToolbarImageButton.IconBrushProperty, "IconForeground");
                    }
                    catch { }
                    NudgeIconAndLabel(button);
                    return button;
                },
                ApplyOrientation = (view, orientation) =>
                {
                    if (view is ToolbarImageButton button)
                        button.ApplyOrientation(orientation == Orientation.Vertical);
                },
                // 嵌入式模式需要打开/翻页/导出多个操作，交给宿主自动弹窗承载。
                PopupContentFactory = () =>
                {
                    _popup = new ReaderPopupContent(this);
                    return _popup;
                }
            };

            host.RegisterToolbarItem(item);
            RegisterBoardToolbarItem(host, item, iconMarkup);
        }

        /// <summary>
        /// 向白板工具栏注册同款 PDF 组件：白板模式下浮动栏隐藏，用户仍能从板工具栏打开 PDF 弹窗。
        /// 宿主板工具栏的插件包装器（PluginBoardToolbarItemWrapper）不接 PopupContentFactory，
        /// 因此这里自建一个承载同一份弹窗内容的 Popup，点击按钮开合。
        /// 宿主 SDK 较旧（无 RegisterBoardToolbarItem）时静默跳过，不影响浮动栏功能。
        /// </summary>
        private void RegisterBoardToolbarItem(IPluginHost host, PluginToolbarItemInfo item, string iconMarkup)
        {
            try
            {
                host.RegisterBoardToolbarItem(new PluginToolbarItemInfo
                {
                    Id = item.Id,
                    DisplayName = item.DisplayName,
                    Description = item.Description,
                    IconGeometry = item.IconGeometry,
                    PopupContentFactory = item.PopupContentFactory,
                    ViewFactory = () =>
                    {
                        // 用宿主自己的板工具栏按钮（BoardToolbarButton）：图标 20×20、文字 12 号、
                        // 无按压阴影，与宿主其它白板组件外观一致（浮动栏按钮是 24×24 + 13 号 + 按压反馈）。
                        var button = new BoardToolbarButton
                        {
                            Label = Strings.ToolbarButton,
                            IconGeometry = iconMarkup ?? FallbackIconGeometry
                        };

                        // 板工具栏的插件弹窗由插件自己承载：定位在按钮上方，点击按钮开合。
                        var popup = new System.Windows.Controls.Primitives.Popup
                        {
                            AllowsTransparency = true,
                            StaysOpen = true,
                            Focusable = true,
                            IsOpen = false,
                            PlacementTarget = button,
                            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom
                        };
                        popup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) => new[]
                        {
                            new System.Windows.Controls.Primitives.CustomPopupPlacement(
                                new Point(targetSize.Width / 2 - popupSize.Width / 2, -popupSize.Height - 8),
                                System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical)
                        };
                        _boardPopup = popup;

                        // 板弹窗用自己的内容实例，与浮动弹窗的 _popup 隔离：
                        // 共享实例会被两个 Popup 争用（reparent），且关闭按钮的 Tag 防重接线会互相冲突。
                        var popupContent = new ReaderPopupContent(this);
                        _boardPopupContent = popupContent;

                        // 标题栏 X 接线：宿主经验是弹窗未打开时嵌套 Shell 的视觉树可能不完整，
                        // 因此创建时接一次、Opened 后再补接一次（与宿主 ToolbarRegistry 同款做法）。
                        try
                        {
                            WireBoardPopupCloseButton(popupContent, popup);
                            popup.Opened += (s, e) => WireBoardPopupCloseButton(popupContent, popup);
                        }
                        catch { }

                        button.ButtonMouseUp += (s, e) =>
                        {
                            if (popup.IsOpen)
                            {
                                popup.IsOpen = false;
                            }
                            else
                            {
                                popup.Child = popupContent;
                                popup.IsOpen = true;
                            }
                        };
                        return button;
                    }
                });
            }
            catch (Exception ex)
            {
                LogError("注册白板工具栏 PDF 组件失败（宿主 SDK 可能较旧）", ex);
            }
        }

        /// <summary>
        /// 把弹窗内容里 PopupShellContent 的标题栏关闭按钮接到 popup 收起。
        /// 用 Tag 记录已接线的 popup，避免 Opened 补接时重复订阅。
        /// </summary>
        private static void WireBoardPopupCloseButton(ReaderPopupContent popupContent,
            System.Windows.Controls.Primitives.Popup popup)
        {
            if (popupContent == null || popup == null) return;

            var closeButton = popupContent.Shell?.CloseButtonControl;
            if (closeButton == null) return;
            if (ReferenceEquals(closeButton.Tag, popup)) return;

            closeButton.Tag = popup;
            closeButton.Click += (_, __) => popup.IsOpen = false;
        }

        /// <summary>
        /// 只对 PDF 按钮生效的微调：把图标与文字标签整体下移 1px、右移 1px。
        /// <see cref="ToolbarImageButton"/> 是宿主共享控件，改它的 Margin 会影响所有浮动栏按钮；
        /// 这里通过反射拿内部元素并加 <see cref="TranslateTransform"/>，只移动当前按钮的图标与文字。
        /// 用 RenderTransform 而不是 Margin，是因为宿主切换紧凑模式时会重置 Margin、不会动 RenderTransform。
        /// </summary>
        private static void NudgeIconAndLabel(ToolbarImageButton button)
        {
            try
            {
                const double shiftX = 1.0;
                const double shiftY = 1.0;
                var type = button.GetType();
                if (type.GetField("ButtonImage", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(button) is Image image)
                    image.RenderTransform = new TranslateTransform(shiftX, shiftY);
                if (type.GetField("LabelTextBlock", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(button) is TextBlock label)
                    label.RenderTransform = new TranslateTransform(shiftX, shiftY);
            }
            catch
            {
                // 宿主控件内部结构变化时静默跳过：仅该按钮不获得偏移，不影响其它功能。
            }
        }

        #region 供弹窗与设置页调用

        internal bool IsDocumentOpen
        {
            get { lock (_gate) return _session?.IsOpen == true; }
        }

        internal int CurrentPage
        {
            get { lock (_gate) return _session?.CurrentPage ?? 0; }
        }

        internal int PageCount
        {
            get { lock (_gate) return _session?.PageCount ?? 0; }
        }

        internal PdfDisplayMode DisplayMode
        {
            get { lock (_gate) return _session?.DisplayMode ?? PdfDisplayMode.SinglePage; }
        }

        internal async Task SetDisplayModeAsync(PdfDisplayMode mode)
        {
            EmbeddedReaderSession session;
            lock (_gate) session = _session;
            if (session?.IsOpen != true) return;

            await session.SetDisplayModeAsync(mode, CancellationToken.None).ConfigureAwait(false);
        }

        internal string StatusText => _statusText;

        /// <summary>弹出文件对话框并把选中的 PDF 加载为画布背景。</summary>
        internal async Task PickAndOpenAsync()
        {
            if (!EnsureUsable()) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Strings.DialogTitle,
                Filter = Strings.DialogFilter,
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true) return;

            // 白板模式下打开 PDF：先退出白板，让 PDF 正常成为画布背景；
            // 关闭 PDF 后由 AfterPdfClosed 回到白板。
            if (_isWhiteboardMode && _canvasInkService != null)
            {
                _exitingWhiteboardForOpen = true;
                try { _canvasInkService.ExitWhiteboard(); }
                catch (Exception ex) { LogError("打开 PDF 前退出白板失败", ex); }
                finally { _exitingWhiteboardForOpen = false; }
                _openedFromWhiteboard = true;
            }

            await OpenDocumentAsync(dialog.FileName, 0).ConfigureAwait(false);
        }

        internal async Task OpenDocumentAsync(string path, int initialPage)
        {
            if (!EnsureUsable()) return;

            SetStatus(Strings.Loading);
            try
            {
                EmbeddedReaderSession session;
                lock (_gate)
                {
                    if (_session == null)
                    {
                        _session = new EmbeddedReaderSession(_composition, _presentation, _config, LogError, Log);
                        _session.PageChanged += Session_PageChanged;
                        _session.Closed += Session_Closed;
                        _session.ViewTransformChanged += Session_ViewTransformChanged;
                    }
                    session = _session;
                }

                await session.OpenAsync(path, initialPage, CancellationToken.None).ConfigureAwait(false);

                // 打开 PDF 后自动开启双指缩放/移动：把当前的笔模式同步给会话（单指平移门控用）。
                session.IsPenMode = _isPenMode;

                _config.LastDocumentPath = path;
                _config.LastPageIndex = session.CurrentPage;
                SaveConfig();

                SetStatus(string.Format(Strings.OpenedFormat,
                    Path.GetFileName(path), session.PageCount));
            }
            catch (FileNotFoundException)
            {
                ShowError(Strings.ErrorFileNotFound);
            }
            catch (InvalidOperationException ex)
            {
                ShowError(ex.Message);
            }
            catch (Exception ex)
            {
                ShowError(IsLikelyFormatError(ex)
                    ? Strings.ErrorNotPdf
                    : string.Format(Strings.ErrorOpenFailedFormat, ex.Message));
            }
        }

        /// <summary>滚轮或按钮翻页后刷新状态并记忆页码。</summary>
        private void Session_PageChanged(int pageIndex)
        {
            _config.LastPageIndex = pageIndex;
            SaveConfig();

            int total;
            lock (_gate) total = _session?.PageCount ?? 0;
            SetStatus(string.Format(Strings.PageOfFormat, pageIndex + 1, total));
        }

        /// <summary>会话被关闭（含宿主强制结束演示源）时刷新弹窗状态。</summary>
        private void Session_Closed()
        {
            SetStatus(Strings.ClosedNotice);
            AfterPdfClosed();
        }

        /// <summary>PDF 关闭/放映结束后的收尾：收起板工具栏弹窗；从白板打开的则回到白板。</summary>
        private void AfterPdfClosed()
        {
            try
            {
                if (_boardPopup != null) _boardPopup.IsOpen = false;
            }
            catch { }

            if (!_openedFromWhiteboard) return;
            _openedFromWhiteboard = false;

            // 已在白板（用户中途手动进入）就不重复切换：宿主的 EnterWhiteboard 是取反切换。
            if (_windowService == null || _isWhiteboardMode) return;

            try { _windowService.EnterWhiteboard(); }
            catch (Exception ex) { LogError("关闭 PDF 后回到白板失败", ex); }
        }

        /// <summary>渲染质量档位变化：保存配置并让会话清缓存、按新档位重渲染当前视图。</summary>
        internal void SetRenderQuality(RenderQualityMode mode)
        {
            // 质量档内存占用大，用宿主通知提示一次（风格与宿主一致）。
            if (mode == RenderQualityMode.Quality)
                Notify(Strings.QualityWarning, NotificationLevel.Warning);

            if (_config != null)
            {
                _config.RenderQuality = mode;
                SaveConfig();
            }

            EmbeddedReaderSession session;
            lock (_gate) session = _session;
            if (session == null) return;

            try { _ = session.ReloadRenderQualityAsync(); }
            catch (Exception ex) { LogError("按新渲染质量重渲染失败", ex); }
        }

        /// <summary>笔/鼠标模式切换（true=笔类，false=鼠标）：同步给会话，控制单指平移是否接管。</summary>
        private void OnPenModeChanged(bool isPenMode)
        {
            _isPenMode = isPenMode;

            EmbeddedReaderSession session;
            lock (_gate) session = _session;
            if (session == null) return;
            session.IsPenMode = isPenMode;
        }

        /// <summary>
        /// 白板模式切换（true=进入，false=退出）时隐藏/恢复 PDF 背景层。
        /// 宿主对插件背景层不做白板处理：背景层注入在白板幕布（GridBackgroundCover）之上，
        /// 不处理的话进白板后 PDF 当前页会一直盖在幕布上。
        /// </summary>
        private void OnWhiteboardModeChanged(bool isWhiteboardMode)
        {
            _isWhiteboardMode = isWhiteboardMode;

            // 用户手动退出白板（非打开 PDF 触发的退出）：关闭 PDF 后不再自动回白板。
            if (!isWhiteboardMode && !_exitingWhiteboardForOpen)
                _openedFromWhiteboard = false;

            EmbeddedReaderSession session;
            lock (_gate) session = _session;
            if (session == null || !session.IsOpen) return;

            try
            {
                if (isWhiteboardMode) session.SuspendForWhiteboard();
                else session.ResumeAfterWhiteboard();
            }
            catch (Exception ex)
            {
                LogError("白板模式切换处理失败", ex);
            }
        }

        /// <summary>视图矩阵（缩放/平移）变化时刷新弹窗的缩放百分比。</summary>
        private void Session_ViewTransformChanged()
        {
            RefreshPopup();
        }

        /// <summary>当前视图缩放比例（1.0 = 100%）。</summary>
        internal double ViewScale
        {
            get { lock (_gate) return _session?.ViewScale ?? 1.0; }
        }

        /// <summary>重置缩放回 100%，墨迹随之复位。</summary>
        internal async Task ResetZoomAsync()
        {
            EmbeddedReaderSession session;
            lock (_gate) session = _session;
            if (session?.IsOpen != true) return;

            await session.ResetZoomAsync().ConfigureAwait(false);
        }

        internal async Task NextPageAsync()
        {
            EmbeddedReaderSession session;
            lock (_gate) session = _session;
            if (session?.IsOpen != true) return;

            // 走会话的按模式分派：翻页模式翻页，滚动模式滚到下一页顶部。
            await session.NextPageAsync(CancellationToken.None).ConfigureAwait(false);
        }

        internal async Task PreviousPageAsync()
        {
            EmbeddedReaderSession session;
            lock (_gate) session = _session;
            if (session?.IsOpen != true) return;

            await session.PreviousPageAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>弹出保存对话框并导出「背景 + 墨迹」。</summary>
        internal async Task PickAndExportAsync()
        {
            EmbeddedReaderSession session;
            lock (_gate) session = _session;
            if (session?.IsOpen != true)
            {
                ShowError(Strings.ErrorNoDocument);
                return;
            }

            string source = session.FilePath;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Strings.ExportDialogTitle,
                Filter = Strings.ExportDialogFilter,
                DefaultExt = ".pdf",
                AddExtension = true,
                FileName = BuildExportFileName(source)
            };
            if (!string.IsNullOrEmpty(source))
            {
                try { dialog.InitialDirectory = Path.GetDirectoryName(source); }
                catch { }
            }

            if (dialog.ShowDialog() != true) return;

            SetStatus(Strings.Exporting);
            try
            {
                string written = await session.ExportAsync(dialog.FileName, CancellationToken.None)
                    .ConfigureAwait(false);
                SetStatus(string.Format(Strings.ExportDoneFormat, Path.GetFileName(written)));
                Notify(string.Format(Strings.ExportDoneFormat, written), NotificationLevel.Success);
            }
            catch (OperationCanceledException)
            {
                SetStatus(string.Empty);
            }
            catch (Exception ex)
            {
                // 必须落日志：只弹提示的话，用户看到"导出失败"而日志里查不到任何线索。
                LogError($"PDF 导出失败（起始页 {session.CurrentPage + 1}/{session.PageCount}，目标 {dialog.FileName}）", ex);
                ShowError(string.Format(Strings.ErrorExportFailedFormat, ex.Message));
            }
        }

        private static string BuildExportFileName(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return "export" + Strings.ExportSuffix + ".pdf";
            try
            {
                return Path.GetFileNameWithoutExtension(sourcePath) + Strings.ExportSuffix + ".pdf";
            }
            catch
            {
                return "export" + Strings.ExportSuffix + ".pdf";
            }
        }

        /// <summary>关闭 PDF 并移除背景层。</summary>
        internal void CloseDocument()
        {
            EmbeddedReaderSession session;
            lock (_gate) session = _session;
            if (session == null) return;

            session.Close();
            SetStatus(Strings.ClosedNotice);
            AfterPdfClosed();
        }

        /// <summary>PDF 文件关联的 ProgId（HKCU\Software\Classes）。</summary>
        private const string PdfAssociationProgId = "ICCCommunity.PDF.Reader";

        /// <summary>处理 icc://plugin/com.icc.pdf-reader/open?path=&lt;urlencoded&gt;（文件关联双击 .pdf 触发）。</summary>
        private bool OnOpenDocumentUri(PluginUriRequest request)
        {
            try
            {
                if (request?.Query != null
                    && request.Query.TryGetValue("path", out string path)
                    && !string.IsNullOrWhiteSpace(path)
                    && File.Exists(path))
                {
                    // 深链接在 UI 线程回调；打开是异步流程，异常已在 OpenDocumentAsync 内收敛。
                    _ = OpenDocumentAsync(path, 0);
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogError("处理 PDF 打开深链接失败", ex);
            }
            return false;
        }

        internal (bool registered, string progId) GetAssociationStatus()
        {
            if (_association == null) return (false, PdfAssociationProgId);
            try
            {
                bool ok = _association.IsRegistered(".pdf");
                return (ok, PdfAssociationProgId);
            }
            catch (Exception ex)
            {
                LogError("检查 PDF 文件关联状态失败", ex);
                return (false, PdfAssociationProgId);
            }
        }

        /// <summary>该插件是否拿到宿主的文件关联服务（设置页据此显示可用性并禁用按钮）。</summary>
        internal bool IsAssociationSupported => _association != null;

        internal bool RegisterPdfAssociation()
        {
            if (_association == null)
            {
                ShowError(Strings.AssocUnavailable);
                return false;
            }
            try
            {
                // 传插件自身 ID：宿主据此把双击打开的 .pdf 文件派发回本插件的 "open" 处理器。
                bool ok = _association.Register(".pdf", PdfAssociationProgId, Strings.AssocDescription, pluginId: Id);
                if (ok) Notify(Strings.AssocRegistered, NotificationLevel.Success);
                else ShowError(Strings.AssocRegisterFailed);
                return ok;
            }
            catch (Exception ex)
            {
                LogError("注册 PDF 文件关联失败", ex);
                ShowError(Strings.AssocRegisterFailed);
                return false;
            }
        }

        internal bool UnregisterPdfAssociation()
        {
            if (_association == null)
            {
                ShowError(Strings.AssocUnavailable);
                return false;
            }
            try
            {
                bool ok = _association.Unregister(".pdf");
                if (ok) Notify(Strings.AssocUnregistered, NotificationLevel.Success);
                else ShowError(Strings.AssocUnregisterFailed);
                return ok;
            }
            catch (Exception ex)
            {
                LogError("注销 PDF 文件关联失败", ex);
                ShowError(Strings.AssocUnregisterFailed);
                return false;
            }
        }

        internal void SaveConfig()
        {
            try { _config?.Save(_configPath); }
            catch (Exception ex) { LogError("保存 PDF 阅读器配置失败", ex); }
        }

        internal ReaderConfig Config => _config;

        #endregion

        /// <summary>检查系统 PDF 组件与宿主合成服务是否都可用，不可用时给出本地化原因。</summary>
        private bool EnsureUsable()
        {
            if (!PdfSupport.IsAvailable)
            {
                string message = Strings.ErrorNoWinRtPdf;
                string reason = PdfSupport.UnavailableReason;
                if (!string.IsNullOrEmpty(reason))
                    message += string.Format(Strings.UnavailableSuffixFormat, reason);
                ShowError(message);
                return false;
            }

            if (_composition == null)
            {
                ShowError(Strings.ErrorNoComposition);
                return false;
            }

            return true;
        }

        private static bool IsLikelyFormatError(Exception ex)
        {
            // WinRT 对损坏/非 PDF 文件通常抛 COMException 或 ArgumentException。
            return ex is System.Runtime.InteropServices.COMException
                || ex is ArgumentException;
        }

        private void SetStatus(string text)
        {
            _statusText = text ?? string.Empty;
            RefreshPopup();
        }

        private void RefreshPopup()
        {
            var popup = _popup;
            if (popup == null) return;

            try
            {
                if (popup.Dispatcher.CheckAccess()) popup.RefreshState();
                else popup.Dispatcher.BeginInvoke(new Action(popup.RefreshState));
            }
            catch { }

            // 白板弹窗是独立实例，同样要同步页码/按钮可用状态。
            var boardPopup = _boardPopupContent;
            if (boardPopup == null || ReferenceEquals(boardPopup, popup)) return;

            try
            {
                if (boardPopup.Dispatcher.CheckAccess()) boardPopup.RefreshState();
                else boardPopup.Dispatcher.BeginInvoke(new Action(boardPopup.RefreshState));
            }
            catch { }
        }

        private void ShowError(string message)
        {
            SetStatus(message);
            Notify(message, NotificationLevel.Error);
        }

        private void Notify(string message, NotificationLevel level)
        {
            if (_notificationService != null)
            {
                try { _notificationService.Show(Strings.ErrorTitle, message, level); return; }
                catch { }
            }

            try
            {
                var image = level == NotificationLevel.Error ? MessageBoxImage.Warning : MessageBoxImage.Information;
                MessageBox.Show(message, Strings.ErrorTitle, MessageBoxButton.OK, image);
            }
            catch { }
        }

        public override object GetSettingsView()
        {
            if (_settingsView == null)
                _settingsView = new SettingsView(this);
            return _settingsView;
        }

        public override void Shutdown()
        {
            if (_eventService != null)
            {
                try { _eventService.WhiteboardModeChanged -= OnWhiteboardModeChanged; }
                catch { }
                try { _eventService.PenModeChanged -= OnPenModeChanged; }
                catch { }
            }
            DisposeSession();
            SaveConfig();
            Log($"{Name} 已关闭");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeSession();
        }

        private void DisposeSession()
        {
            EmbeddedReaderSession session;
            lock (_gate)
            {
                session = _session;
                _session = null;
            }

            if (session != null)
            {
                try { session.PageChanged -= Session_PageChanged; } catch { }
                try { session.Closed -= Session_Closed; } catch { }
                try { session.ViewTransformChanged -= Session_ViewTransformChanged; } catch { }
                try { session.Dispose(); } catch { }
            }
        }
    }
}
