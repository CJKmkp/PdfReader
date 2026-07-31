using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PdfReader.Views
{
    /// <summary>
    /// PDF 阅读器设置页。所有可见文案在代码里从 <see cref="Strings"/> 赋值，XAML 不含硬编码文本。
    /// 直接读写插件持有的 <see cref="ReaderConfig"/>，改动即时保存。
    /// </summary>
    public partial class SettingsView : UserControl
    {
        private readonly PdfReaderPlugin _plugin;
        private readonly ReaderConfig _config;
        private bool _loading;

        internal SettingsView(PdfReaderPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            _config = plugin?.Config ?? new ReaderConfig();
            ApplyLocalizedText();
            LoadFromConfig();
        }

        private void ApplyLocalizedText()
        {
            HeaderText.Text = Strings.SettingsHeader;
            IntroText.Text = Strings.SettingsIntro;
            OpenNowButton.Content = Strings.SettingsOpenNow;

            RenderScaleLabel.Text = Strings.SettingsRenderScale;
            RememberLabel.Text = Strings.SettingsRememberLast;
            CacheBudgetLabel.Text = Strings.SettingsCacheBudget;

            ShortcutsHeader.Text = Strings.SettingsShortcutsHeader;
            ShortcutsText.Text = Strings.SettingsShortcuts;
            NotesHeader.Text = Strings.SettingsNotesHeader;
            NotesText.Text = Strings.SettingsNotes;
        }

        private void LoadFromConfig()
        {
            _loading = true;
            try
            {
                RenderScaleSlider.Value = _config.NormalizedRenderScale;
                RenderScaleValue.Text = FormatScale(_config.NormalizedRenderScale);
                RememberToggle.IsOn = _config.RememberLastDocument;
                CacheBudgetSlider.Value = _config.NormalizedCacheBudgetMb;
                CacheBudgetValue.Text = _config.NormalizedCacheBudgetMb.ToString(CultureInfo.CurrentCulture);
            }
            finally
            {
                _loading = false;
            }
        }

        private static string FormatScale(double scale)
            => scale.ToString("0.0", CultureInfo.CurrentCulture) + "×";

        private async void OpenNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;

            IsEnabled = false;
            try { await _plugin.PickAndOpenAsync(); }
            finally { IsEnabled = true; }
        }

        private void RenderScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (RenderScaleValue != null)
                RenderScaleValue.Text = FormatScale(e.NewValue);
            if (_loading) return;

            _config.RenderScale = e.NewValue;
            _plugin?.SaveConfig();
        }

        private void RememberToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _config.RememberLastDocument = RememberToggle.IsOn;
            _plugin?.SaveConfig();
        }

        private void CacheBudgetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int mb = (int)System.Math.Round(e.NewValue);
            if (CacheBudgetValue != null)
                CacheBudgetValue.Text = mb.ToString(CultureInfo.CurrentCulture);
            if (_loading) return;

            _config.CacheBudgetMb = mb;
            _plugin?.SaveConfig();
        }
    }
}
