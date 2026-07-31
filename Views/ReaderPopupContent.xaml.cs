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
        }

        /// <summary>按当前会话状态刷新页码与按钮可用性。</summary>
        internal void RefreshState()
        {
            bool open = _plugin?.IsDocumentOpen == true;
            int page = _plugin?.CurrentPage ?? 0;
            int total = _plugin?.PageCount ?? 0;

            PageText.Text = open ? string.Format(Strings.PageOfFormat, page + 1, total) : "—";
            StatusText.Text = _plugin?.StatusText ?? string.Empty;

            PreviousButton.IsEnabled = open && page > 0;
            NextButton.IsEnabled = open && page < total - 1;
            ExportButton.IsEnabled = open;
            CloseButton.IsEnabled = open;
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
