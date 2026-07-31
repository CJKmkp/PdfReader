using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PdfReader.Views
{
    /// <summary>
    /// 注入到宿主 InkCanvas 下方的 PDF 背景视图。
    /// 只负责显示当前页位图并铺满画布，不含任何交互与墨迹：
    /// 宿主注入时会把它设为 IsHitTestVisible = false，书写事件全部落到宿主画布上。
    /// 页面坐标系即本元素的 ActualWidth/ActualHeight（DIP），宿主按此换算墨迹坐标。
    /// </summary>
    public partial class PdfBackgroundView : UserControl
    {
        /// <summary>翻页滑动过渡时长。</summary>
        private static readonly Duration SlideDuration = new Duration(TimeSpan.FromMilliseconds(180));

        public PdfBackgroundView()
        {
            InitializeComponent();
        }

        /// <summary>设置当前页位图；传入 null 时清空显示。</summary>
        public void SetPage(ImageSource image)
        {
            PageImage.Source = image;
        }

        /// <summary>
        /// 计算 Uniform 缩放后页面实际占据的矩形（本元素坐标系，DIP）。
        /// 导出时用它裁出页面区域，保持原始宽高比；无图或未布局时返回 null。
        /// </summary>
        public Rect? GetPageContentRect()
        {
            var source = PageImage.Source;
            if (source == null) return null;

            double viewW = ActualWidth;
            double viewH = ActualHeight;
            if (viewW <= 0 || viewH <= 0) return null;

            double imgW = source.Width;
            double imgH = source.Height;
            if (imgW <= 0 || imgH <= 0) return null;

            // Uniform：取较小的缩放比，另一方向居中留边。
            double scale = Math.Min(viewW / imgW, viewH / imgH);
            double w = imgW * scale;
            double h = imgH * scale;
            return new Rect((viewW - w) / 2, (viewH - h) / 2, w, h);
        }

        /// <summary>
        /// 带滑动过渡地切换到新页面。
        /// <paramref name="forward"/> 为 true 时新页自下方滑入（下一页），false 时自上方滑入（上一页）。
        /// </summary>
        public void SetPageWithSlide(ImageSource image, bool forward)
        {
            if (image == null)
            {
                SetPage(null);
                return;
            }

            double height = ActualHeight;
            if (height <= 0)
            {
                // 尚未布局完成，退化为直接替换，避免用错误的偏移量做动画。
                SetPage(image);
                return;
            }

            PageImage.Source = image;

            // 新页先偏移到视口外，再滑回原位；方向与滚轮方向一致。
            PageTransform.BeginAnimation(TranslateTransform.YProperty, null);
            PageTransform.Y = forward ? height : -height;

            var slide = new DoubleAnimation
            {
                From = PageTransform.Y,
                To = 0,
                Duration = SlideDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            slide.Completed += (_, __) =>
            {
                PageTransform.BeginAnimation(TranslateTransform.YProperty, null);
                PageTransform.Y = 0;
            };

            PageTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }
}
