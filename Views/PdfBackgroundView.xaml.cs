using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ink_Canvas.Plugins;

namespace PdfReader.Views
{
    /// <summary>
    /// 注入到宿主 InkCanvas 下方的 PDF 背景视图。
    /// 支持单页（一张图铺满）与双页（左右并排）两种展示。
    /// 只负责显示页面位图，不含任何交互与墨迹：宿主注入时会把它设为 IsHitTestVisible = false，
    /// 书写事件全部落到宿主画布上。页面坐标系即本元素的 ActualWidth/ActualHeight（DIP），
    /// 宿主按此换算墨迹坐标。
    /// </summary>
    public partial class PdfBackgroundView : UserControl
    {
        /// <summary>翻页滑动过渡时长。</summary>
        private static readonly Duration SlideDuration = new Duration(TimeSpan.FromMilliseconds(180));

        /// <summary>双页模式左右页之间的间隙（DIP）。</summary>
        private const double DoublePageGutter = 12;

        public PdfBackgroundView()
        {
            InitializeComponent();
        }

        /// <summary>当前是否双页模式。</summary>
        public bool IsDoublePage { get; private set; }

        /// <summary>设置单页显示。</summary>
        public void SetSinglePage(ImageSource image)
        {
            IsDoublePage = false;
            RightColumn.Width = new GridLength(0);
            RightImage.Visibility = Visibility.Collapsed;
            LeftImage.Source = image;
        }

        /// <summary>设置双页显示；<paramref name="right"/> 为 null 时右页留空。</summary>
        public void SetDoublePage(ImageSource left, ImageSource right)
        {
            IsDoublePage = true;
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
            RightImage.Visibility = Visibility.Visible;
            LeftImage.Source = left;
            RightImage.Source = right;
        }

        /// <summary>
        /// 计算当前可见页的矩形列表（背景元素坐标系，DIP）。
        /// 返回 (pageIndex, rect)：单页时 1 项；双页时左、右 2 项。
        /// 供宿主按矩形切分墨迹。
        /// </summary>
        public IReadOnlyList<PluginVisiblePage> GetVisiblePageRects(int leftPageIndex)
        {
            var list = new List<PluginVisiblePage>(2);

            double viewW = ActualWidth;
            double viewH = ActualHeight;
            if (viewW <= 0 || viewH <= 0) return list;

            if (IsDoublePage)
            {
                double half = viewW / 2;
                var leftRect = ComputeUniformRect(LeftImage.Source,
                    new Rect(0, 0, half - DoublePageGutter / 2, viewH));
                if (!leftRect.IsEmpty)
                {
                    list.Add(new PluginVisiblePage
                    {
                        PageIndex = (uint)leftPageIndex,
                        ContentRect = leftRect
                    });
                }

                var rightRect = ComputeUniformRect(RightImage.Source,
                    new Rect(half + DoublePageGutter / 2, 0, half - DoublePageGutter / 2, viewH));
                if (!rightRect.IsEmpty)
                {
                    list.Add(new PluginVisiblePage
                    {
                        PageIndex = (uint)(leftPageIndex + 1),
                        ContentRect = rightRect
                    });
                }
            }
            else
            {
                var rect = ComputeUniformRect(LeftImage.Source,
                    new Rect(0, 0, viewW, viewH));
                if (!rect.IsEmpty)
                {
                    list.Add(new PluginVisiblePage
                    {
                        PageIndex = (uint)leftPageIndex,
                        ContentRect = rect
                    });
                }
            }

            return list;
        }

        /// <summary>计算图片 Uniform 缩放后在视口内占据的矩形。</summary>
        private static Rect ComputeUniformRect(ImageSource source, Rect viewport)
        {
            if (source == null || viewport.IsEmpty) return Rect.Empty;

            double imgW = source.Width;
            double imgH = source.Height;
            if (imgW <= 0 || imgH <= 0 || viewport.Width <= 0 || viewport.Height <= 0) return Rect.Empty;

            double scale = Math.Min(viewport.Width / imgW, viewport.Height / imgH);
            double w = imgW * scale;
            double h = imgH * scale;
            return new Rect(
                viewport.X + (viewport.Width - w) / 2,
                viewport.Y + (viewport.Height - h) / 2,
                w, h);
        }

        /// <summary>带滑动过渡地设置单页（向后=下滑入，向前=上滑入）。</summary>
        public void SetSinglePageWithSlide(ImageSource image, bool forward)
        {
            if (image == null)
            {
                SetSinglePage(null);
                return;
            }

            SetSinglePage(image);

            double height = ActualHeight;
            if (height <= 0) return;

            LeftTransform.BeginAnimation(TranslateTransform.YProperty, null);
            LeftTransform.Y = forward ? height : -height;

            var slide = new DoubleAnimation
            {
                From = LeftTransform.Y,
                To = 0,
                Duration = SlideDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            slide.Completed += (_, __) =>
            {
                LeftTransform.BeginAnimation(TranslateTransform.YProperty, null);
                LeftTransform.Y = 0;
            };
            LeftTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        /// <summary>带滑动过渡地设置双页（整体下滑入 / 上滑入）。</summary>
        public void SetDoublePageWithSlide(ImageSource left, ImageSource right, bool forward)
        {
            SetDoublePage(left, right);

            double height = ActualHeight;
            if (height <= 0) return;

            var target = new TranslateTransform();
            foreach (var (t, name) in new[] { (LeftTransform, nameof(LeftTransform)), (RightTransform, nameof(RightTransform)) })
            {
                t.BeginAnimation(TranslateTransform.YProperty, null);
                t.Y = forward ? height : -height;
                var slide = new DoubleAnimation
                {
                    From = t.Y,
                    To = 0,
                    Duration = SlideDuration,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                };
                slide.Completed += (_, __) =>
                {
                    t.BeginAnimation(TranslateTransform.YProperty, null);
                    t.Y = 0;
                };
                t.BeginAnimation(TranslateTransform.YProperty, slide);
            }
            _ = target; // 保留占位，避免误删的变量告警
        }
    }
}
