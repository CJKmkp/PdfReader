using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ink_Canvas.Plugins;

namespace PdfReader.Views
{
    /// <summary>展示模式。</summary>
    public enum PdfDisplayMode
    {
        /// <summary>单页翻页。</summary>
        SinglePage,

        /// <summary>双页翻页（左右并排，按页对翻）。</summary>
        DoublePage,

        /// <summary>连续滚动（所有页垂直长条）。</summary>
        ContinuousScroll
    }

    /// <summary>
    /// 注入到宿主 InkCanvas 下方的 PDF 背景视图，支持三种展示模式：
    /// 单页翻页、双页翻页（翻页容器 <see cref="Pager"/>）、连续滚动（长条 <see cref="Strip"/>）。
    /// 只负责显示页面位图与计算页矩形，不含任何交互与墨迹：
    /// 宿主注入时会把它设为 IsHitTestVisible = false，书写事件全部落到宿主画布上。
    /// </summary>
    public partial class PdfBackgroundView : UserControl
    {
        /// <summary>页与页之间的空白带高度（DIP）。</summary>
        private const double PageGutter = 32;

        /// <summary>双页模式左右页之间的间隙（DIP）。</summary>
        private const double DoublePageGutter = 12;

        /// <summary>翻页滑动过渡时长。</summary>
        private static readonly Duration SlideDuration = new Duration(TimeSpan.FromMilliseconds(180));

        /// <summary>连续滚动长条里的页 Image 列表。</summary>
        private readonly List<Image> _pageImages = new List<Image>();

        /// <summary>长条里每页的源。</summary>
        private readonly List<ImageSource> _pageSources = new List<ImageSource>();

        /// <summary>当前滚动偏移（DIP）。</summary>
        private double _scrollOffset;

        /// <summary>当前展示模式。</summary>
        private PdfDisplayMode _mode = PdfDisplayMode.SinglePage;

        public PdfBackgroundView()
        {
            InitializeComponent();
        }

        /// <summary>当前展示模式。</summary>
        public PdfDisplayMode Mode => _mode;

        /// <summary>总页数（长条模式下有效）。</summary>
        public int PageCount => _pageImages.Count;

        /// <summary>当前滚动偏移（DIP）。</summary>
        public double ScrollOffset => _scrollOffset;

        /// <summary>长条总高度（含页间空白带）。</summary>
        public double StripHeight { get; private set; }

        #region 模式切换

        /// <summary>切换展示模式并显示对应容器。</summary>
        public void SetDisplayMode(PdfDisplayMode mode)
        {
            _mode = mode;

            switch (mode)
            {
                case PdfDisplayMode.SinglePage:
                case PdfDisplayMode.DoublePage:
                    Pager.Visibility = Visibility.Visible;
                    Strip.Visibility = Visibility.Collapsed;
                    ApplyPagerLayout(mode == PdfDisplayMode.DoublePage);
                    break;
                case PdfDisplayMode.ContinuousScroll:
                    Pager.Visibility = Visibility.Collapsed;
                    Strip.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void ApplyPagerLayout(bool doublePage)
        {
            if (doublePage)
            {
                RightColumn.Width = new GridLength(1, GridUnitType.Star);
                RightImage.Visibility = Visibility.Visible;
            }
            else
            {
                RightColumn.Width = new GridLength(0);
                RightImage.Visibility = Visibility.Collapsed;
            }
        }

        #endregion

        #region 翻页模式（单/双页）

        /// <summary>设置单页显示。</summary>
        public void SetSinglePage(ImageSource image)
        {
            SetDisplayMode(PdfDisplayMode.SinglePage);
            LeftImage.Source = image;
        }

        /// <summary>设置双页显示；<paramref name="right"/> 为 null 时右页留空。</summary>
        public void SetDoublePage(ImageSource left, ImageSource right)
        {
            SetDisplayMode(PdfDisplayMode.DoublePage);
            LeftImage.Source = left;
            RightImage.Source = right;
        }

        /// <summary>带滑动过渡地设置单页。</summary>
        public void SetSinglePageWithSlide(ImageSource image, bool forward)
        {
            if (image == null)
            {
                SetSinglePage(null);
                return;
            }

            SetSinglePage(image);
            SlideImage(LeftTransform, forward);
        }

        /// <summary>带滑动过渡地设置双页。</summary>
        public void SetDoublePageWithSlide(ImageSource left, ImageSource right, bool forward)
        {
            SetDoublePage(left, right);
            SlideImage(LeftTransform, forward);
            SlideImage(RightTransform, forward);
        }

        private void SlideImage(TranslateTransform transform, bool forward)
        {
            double height = ActualHeight;
            if (height <= 0) return;

            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = forward ? height : -height;

            var slide = new DoubleAnimation
            {
                From = transform.Y,
                To = 0,
                Duration = SlideDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            slide.Completed += (_, __) =>
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                transform.Y = 0;
            };
            transform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        /// <summary>翻页模式（单/双页）下当前可见页矩形。</summary>
        public IReadOnlyList<PluginVisiblePage> GetPagerVisiblePageRects(int leftPageIndex)
        {
            var list = new List<PluginVisiblePage>(2);
            double viewW = ActualWidth;
            double viewH = ActualHeight;
            if (viewW <= 0 || viewH <= 0) return list;

            if (_mode == PdfDisplayMode.DoublePage)
            {
                double half = viewW / 2;
                var leftRect = ComputeUniformRect(LeftImage.Source,
                    new Rect(0, 0, half - DoublePageGutter / 2, viewH));
                if (!leftRect.IsEmpty)
                    list.Add(new PluginVisiblePage { PageIndex = (uint)leftPageIndex, ContentRect = leftRect });

                var rightRect = ComputeUniformRect(RightImage.Source,
                    new Rect(half + DoublePageGutter / 2, 0, half - DoublePageGutter / 2, viewH));
                if (!rightRect.IsEmpty)
                    list.Add(new PluginVisiblePage { PageIndex = (uint)(leftPageIndex + 1), ContentRect = rightRect });
            }
            else
            {
                var rect = ComputeUniformRect(LeftImage.Source,
                    new Rect(0, 0, viewW, viewH));
                if (!rect.IsEmpty)
                    list.Add(new PluginVisiblePage { PageIndex = (uint)leftPageIndex, ContentRect = rect });
            }

            return list;
        }

        #endregion

        #region 连续滚动模式（长条）

        /// <summary>重置长条为指定页数（连续滚动用）。</summary>
        public void ResetStrip(int pageCount)
        {
            Strip.Children.Clear();
            _pageImages.Clear();
            _pageSources.Clear();

            double top = 0;
            for (int i = 0; i < pageCount; i++)
            {
                var img = new Image
                {
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Width = ActualWidth,
                    SnapsToDevicePixels = true
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Canvas.SetTop(img, top);
                Canvas.SetLeft(img, 0);
                Strip.Children.Add(img);
                _pageImages.Add(img);
                _pageSources.Add(null);
                top += img.Width * (img.Source?.Height / (img.Source?.Width ?? 1) ?? 1.414) + PageGutter;
            }
            StripHeight = top - PageGutter;
            RecomputeStripLayout();
        }

        /// <summary>设置长条里指定页的位图源。</summary>
        public void SetStripPageSource(int pageIndex, ImageSource source)
        {
            if (pageIndex < 0 || pageIndex >= _pageImages.Count) return;
            _pageImages[pageIndex].Source = source;
            _pageSources[pageIndex] = source;
            RecomputeStripLayout();
        }

        /// <summary>设置滚动偏移并平移长条。</summary>
        public void SetScrollOffset(double offset)
        {
            _scrollOffset = offset;
            StripTransform.Y = -_scrollOffset;
        }

        /// <summary>重新布局长条（页面 Uniform 居中）。</summary>
        private void RecomputeStripLayout()
        {
            double viewW = ActualWidth;
            if (viewW <= 0) return;

            double top = 0;
            for (int i = 0; i < _pageImages.Count; i++)
            {
                var img = _pageImages[i];
                var source = _pageSources[i];
                double imgW = source?.Width ?? 612;
                double imgH = source?.Height ?? 792;
                double w = Math.Min(viewW, imgW);
                double h = w * (imgH / imgW);
                img.Width = w;
                Canvas.SetLeft(img, (viewW - w) / 2);
                Canvas.SetTop(img, top);
                top += h + PageGutter;
            }
            StripHeight = top - PageGutter;
            Strip.Height = StripHeight;
        }

        /// <summary>长条模式下视口内可见页矩形（长条坐标）。</summary>
        public IReadOnlyList<PluginVisiblePage> GetStripVisiblePageRects(double viewportHeight)
        {
            var list = new List<PluginVisiblePage>();
            double viewW = ActualWidth;
            if (viewW <= 0 || viewportHeight <= 0) return list;

            for (int i = 0; i < _pageImages.Count; i++)
            {
                var img = _pageImages[i];
                double w = img.ActualWidth > 0 ? img.ActualWidth : img.Width;
                double h = img.ActualHeight > 0 ? img.ActualHeight : img.Height;
                double pageTop = Canvas.GetTop(img);
                double pageBottom = pageTop + h;

                if (pageBottom > _scrollOffset && pageTop < _scrollOffset + viewportHeight)
                {
                    list.Add(new PluginVisiblePage
                    {
                        PageIndex = (uint)i,
                        ContentRect = new Rect((viewW - w) / 2, pageTop, w, h)
                    });
                }
            }

            return list;
        }

        /// <summary>长条模式下指定页的 Image。</summary>
        public Image GetStripPageImage(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _pageImages.Count) return null;
            return _pageImages[pageIndex];
        }

        /// <summary>长条模式下给定 Y 坐标所属页。</summary>
        public int GetPageIndexAtStripOffset(double y)
        {
            for (int i = 0; i < _pageImages.Count; i++)
            {
                double top = Canvas.GetTop(_pageImages[i]);
                double h = _pageImages[i].ActualHeight > 0 ? _pageImages[i].ActualHeight : _pageImages[i].Height;
                if (y >= top && y < top + h) return i;
            }
            if (y < 0) return 0;
            return Math.Max(0, _pageImages.Count - 1);
        }

        #endregion

        #region 通用矩形

        /// <summary>指定页在背景层里的矩形（用于导出定位墨迹）。</summary>
        public Rect? GetPageRect(int pageIndex)
        {
            if (_mode == PdfDisplayMode.ContinuousScroll)
            {
                if (pageIndex < 0 || pageIndex >= _pageImages.Count) return null;
                var img = _pageImages[pageIndex];
                double w = img.ActualWidth > 0 ? img.ActualWidth : img.Width;
                double h = img.ActualHeight > 0 ? img.ActualHeight : img.Height;
                double top = Canvas.GetTop(img);
                return new Rect((ActualWidth - w) / 2, top, w, h);
            }

            // 翻页模式：当前页/页对的矩形。
            var pages = GetPagerVisiblePageRects(pageIndex);
            if (pages != null && pages.Count > 0) return pages[0].ContentRect;
            return null;
        }

        /// <summary>计算图片 Uniform 缩放后铺入视口的矩形。</summary>
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

        #endregion
    }
}
