using System;
using System.Globalization;
using System.IO;
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
        private EmbeddedReaderSession _session;
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
                        _session = new EmbeddedReaderSession(_composition, _presentation, _config, LogError);
                        _session.PageChanged += Session_PageChanged;
                        _session.Closed += Session_Closed;
                    }
                    session = _session;
                }

                await session.OpenAsync(path, initialPage, CancellationToken.None).ConfigureAwait(false);

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
                try { session.Dispose(); } catch { }
            }
        }
    }
}
