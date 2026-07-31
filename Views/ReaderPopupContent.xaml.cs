using System;
using System.Windows;
using System.Windows.Controls;

namespace PdfReader.Views
{
    /// <summary>
    /// 工具栏按钮的弹窗内容：打开 / 翻页 / 导出 / 关闭。
    /// 所有文案在代码里从 <see cref="Strings"/> 赋值，XAML 不含硬编码文本。
    /// </summary>
    public partial class ReaderPopupContent : UserControl
    {
        private readonly PdfReaderPlugin _plugin;

        /// <summary>RefreshState 代码赋值 IsChecked 时抑制事件触发，避免循环。</summary>
        private bool _suppressDoublePageEvent;

        internal ReaderPopupContent(PdfReaderPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            // 与宿主其它弹窗一致：内容交给 PopupShellContent 承载，标题走 Shell.Title。
            Shell.Title = Strings.PluginName;
            Shell.InnerContent = InnerContentHost.Content;
            Shell.Visibility = Visibility.Visible;

            ApplyLocalizedText();
            RefreshState();
        }

        private void ApplyLocalizedText()
        {
            OpenButton.Content = Strings.Open;
            PreviousButton.Content = Strings.PrevPage;
            NextButton.Content = Strings.NextPage;
            ExportButton.Content = Strings.Export;
            CloseButton.Content = Strings.Close;
            DoublePageButton.Content = Strings.DoublePage;
        }

        /// <summary>按当前会话状态刷新页码与按钮可用性。</summary>
        internal void RefreshState()
        {
            bool open = _plugin?.IsDocumentOpen == true;
            int page = _plugin?.CurrentPage ?? 0;
            int total = _plugin?.PageCount ?? 0;
            bool isDouble = _plugin?.IsDoublePage == true;

            PageText.Text = open ? string.Format(Strings.PageOfFormat, page + 1, total) : "—";
            StatusText.Text = _plugin?.StatusText ?? string.Empty;

            PreviousButton.IsEnabled = open && page > 0;
            NextButton.IsEnabled = open && page < total - (isDouble ? 2 : 1);
            ExportButton.IsEnabled = open;
            CloseButton.IsEnabled = open;
            DoublePageButton.IsEnabled = open;

            // 同步双页按钮状态，但抑制事件循环（代码赋值会触发 Checked/Unchecked）。
            if (DoublePageButton.IsChecked != isDouble)
            {
                _suppressDoublePageEvent = true;
                DoublePageButton.IsChecked = isDouble;
                _suppressDoublePageEvent = false;
            }
        }

        private async void DoublePageButton_Checked(object sender, RoutedEventArgs e)
        {
            await OnDoublePageToggle(true);
        }

        private async void DoublePageButton_Unchecked(object sender, RoutedEventArgs e)
        {
            await OnDoublePageToggle(false);
        }

        private async System.Threading.Tasks.Task OnDoublePageToggle(bool enabled)
        {
            if (_suppressDoublePageEvent || _plugin == null) return;
            await SafeRun(() => _plugin.SetDoublePageModeAsync(enabled));
        }

        private async void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            await SafeRun(() => _plugin.PickAndOpenAsync());
        }

        private async void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            await SafeRun(() => _plugin.PreviousPageAsync());
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            await SafeRun(() => _plugin.NextPageAsync());
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            await SafeRun(() => _plugin.PickAndExportAsync());
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin?.CloseDocument();
            RefreshState();

            // 关闭文档后把整个面板也收起：宿主在 ToolbarRegistry 给 Shell 的标题栏
            // 关闭按钮接了 popup.IsOpen = false，这里通过 RaiseEvent 触发同一接线。
            TryClosePanel();
        }

        /// <summary>触发标题栏关闭按钮的点击，让宿主收下面板。无副作用地失败。</summary>
        private void TryClosePanel()
        {
            try
            {
                var closeButton = Shell?.CloseButtonControl;
                if (closeButton == null) return;

                closeButton.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            catch
            {
                // 宿主未接线（旧版本）时静默：文档已关闭，面板留待用户手动收起。
            }
        }

        /// <summary>统一收敛 async void 事件里的异常，避免冒泡到宿主崩溃窗口。</summary>
        private async System.Threading.Tasks.Task SafeRun(Func<System.Threading.Tasks.Task> action)
        {
            if (_plugin == null) return;

            SetBusy(true);
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                // 用户取消，静默处理。
            }
            finally
            {
                SetBusy(false);
                RefreshState();
            }
        }

        private void SetBusy(bool busy)
        {
            IsEnabled = !busy;
        }
    }
}
