using System;
using System.Windows;
using System.Windows.Controls;

namespace PdfReader.Views
{
    /// <summary>
    /// 工具栏按钮的弹窗内容：打开 / 翻页 / 导出 / 缩放 / 关闭。
    /// 所有文案在代码里从 <see cref="Strings"/> 赋值，XAML 不含硬编码文本。
    /// </summary>
    public partial class ReaderPopupContent : UserControl
    {
        private readonly PdfReaderPlugin _plugin;

        /// <summary>RefreshState 代码赋值 ComboBox 选中项时抑制事件触发，避免循环。</summary>
        private bool _suppressModeEvent;

        /// <summary>质量档位下拉的同类抑制标志。</summary>
        private bool _suppressQualityEvent;

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
            ResetZoomButton.Content = Strings.ResetZoom;

            DisplayModeCombo.Items.Clear();
            DisplayModeCombo.Items.Add(new ComboBoxItem { Content = Strings.SinglePage, Tag = PdfDisplayMode.SinglePage });
            DisplayModeCombo.Items.Add(new ComboBoxItem { Content = Strings.DoublePage, Tag = PdfDisplayMode.DoublePage });
            DisplayModeCombo.Items.Add(new ComboBoxItem { Content = Strings.ContinuousScroll, Tag = PdfDisplayMode.ContinuousScroll });

            QualityCombo.Items.Clear();
            QualityCombo.Items.Add(new ComboBoxItem { Content = Strings.QualityPerformance, Tag = RenderQualityMode.Performance });
            QualityCombo.Items.Add(new ComboBoxItem { Content = Strings.QualityBalanced, Tag = RenderQualityMode.Balanced });
            QualityCombo.Items.Add(new ComboBoxItem { Content = Strings.QualityQuality, Tag = RenderQualityMode.Quality });
        }

        /// <summary>按当前会话状态刷新页码与按钮可用性。</summary>
        internal void RefreshState()
        {
            bool open = _plugin?.IsDocumentOpen == true;
            int page = _plugin?.CurrentPage ?? 0;
            int total = _plugin?.PageCount ?? 0;

            PageText.Text = open ? string.Format(Strings.PageOfFormat, page + 1, total) : "—";

            double scale = _plugin?.ViewScale ?? 1.0;
            ZoomText.Text = open ? string.Format(Strings.ZoomFormat, (int)Math.Round(scale * 100)) : "—";
            ResetZoomButton.IsEnabled = open && Math.Abs(scale - 1.0) > 0.001;

            PreviousButton.IsEnabled = open && page > 0;
            NextButton.IsEnabled = open && page < total - 1;
            ExportButton.IsEnabled = open;
            CloseButton.IsEnabled = open;
            DisplayModeCombo.IsEnabled = open;

            // 同步模式选择，抑制事件循环。
            var mode = _plugin?.DisplayMode ?? PdfDisplayMode.SinglePage;
            foreach (var obj in DisplayModeCombo.Items)
            {
                if (obj is ComboBoxItem item && item.Tag is PdfDisplayMode m && m == mode)
                {
                    if (!ReferenceEquals(DisplayModeCombo.SelectedItem, item))
                    {
                        _suppressModeEvent = true;
                        DisplayModeCombo.SelectedItem = item;
                        _suppressModeEvent = false;
                    }
                    return;
                }
            }

            // 同步渲染质量档位（设置页改过的话弹窗跟着变）。
            var quality = _plugin?.Config?.RenderQuality ?? RenderQualityMode.Balanced;
            foreach (var obj in QualityCombo.Items)
            {
                if (obj is ComboBoxItem item && item.Tag is RenderQualityMode q && q == quality)
                {
                    if (!ReferenceEquals(QualityCombo.SelectedItem, item))
                    {
                        _suppressQualityEvent = true;
                        QualityCombo.SelectedItem = item;
                        _suppressQualityEvent = false;
                    }
                    return;
                }
            }
        }

        private async void DisplayModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressModeEvent || _plugin == null) return;
            if (DisplayModeCombo.SelectedItem is ComboBoxItem item && item.Tag is PdfDisplayMode mode)
                await SafeRun(() => _plugin.SetDisplayModeAsync(mode));
        }

        private void QualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressQualityEvent || _plugin == null) return;
            if (QualityCombo.SelectedItem is ComboBoxItem item && item.Tag is RenderQualityMode mode)
                _plugin.SetRenderQuality(mode);
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

        private async void ResetZoomButton_Click(object sender, RoutedEventArgs e)
        {
            await SafeRun(() => _plugin.ResetZoomAsync());
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
