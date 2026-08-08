using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
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
        /// <summary>
        /// 页与页之间的空白带高度（DIP）。0 = 两页连续拼接，接缝处以低透明度灰色虚线分隔
        /// （参考 Adobe 连续滚动视图），不再有深色空隙。
        /// </summary>
        private const double PageGutter = 0;

        /// <summary>页接缝分隔线厚度（DIP）。</summary>
        private const double PageSeparatorThickness = 1.0;

        /// <summary>双页模式左右页之间的间隙（DIP）。</summary>
        private const double DoublePageGutter = 12;

        /// <summary>翻页滑动过渡时长。</summary>
        private static readonly Duration SlideDuration = new Duration(TimeSpan.FromMilliseconds(180));

        /// <summary>连续滚动长条里的页 Image 列表。</summary>
        private readonly List<Image> _pageImages = new List<Image>();

        /// <summary>长条里每页的源。</summary>
        private readonly List<ImageSource> _pageSources = new List<ImageSource>();

        /// <summary>页接缝的分隔线（低透明度灰色虚线），与页一一对应（第 i 条 = 第 i 页下边界）。</summary>
        private readonly List<Line> _pageSeparators = new List<Line>();

        /// <summary>当前滚动偏移（DIP）。</summary>
        private double _scrollOffset;

        /// <summary>
        /// 视图矩阵（缩放/平移），作用于整个背景层（含长条与翻页容器）。
        /// 宿主通过 <c>inkCanvas.TransformToVisual(本视图)</c> 把该矩阵自动纳入墨迹换算，
        /// 因此墨迹的按页保存/恢复天然对齐缩放后的页面，无需在矩形上同步。
        /// </summary>
        private Matrix _viewMatrix = Matrix.Identity;

        /// <summary>当前展示模式。</summary>
        private PdfDisplayMode _mode = PdfDisplayMode.SinglePage;

        public PdfBackgroundView()
        {
            InitializeComponent();
            // 尺寸变化（背景层完成布局，ActualWidth 从 0 变非 0）时重算长条页面尺寸，
            // 否则 RecomputeStripLayout 因 viewW<=0 提前返回，长条显示全空。
            SizeChanged += OnViewSizeChanged;
        }

        private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_mode == PdfDisplayMode.ContinuousScroll && _pageImages.Count > 0)
                RecomputeStripLayout();
        }

        /// <summary>当前展示模式。</summary>
        public PdfDisplayMode Mode => _mode;

        /// <summary>总页数（长条模式下有效）。</summary>
        public int PageCount => _pageImages.Count;

        /// <summary>当前滚动偏移（DIP）。</summary>
        public double ScrollOffset => _scrollOffset;

        /// <summary>长条总高度（含页间空白带）。</summary>
        public double StripHeight { get; private set; }

        /// <summary>缩放后的可见视口高度（背景层局部坐标）。用于滚动边界与可见页判定。</summary>
        public double EffectiveViewportHeight
        {
            get
            {
                double s = _viewMatrix.M11;
                return ActualHeight / (s > 0 && !double.IsNaN(s) && !double.IsInfinity(s) ? s : 1.0);
            }
        }

        /// <summary>
        /// 设置视图矩阵（缩放/平移）并应用到「页面内容」容器（<see cref="ContentHost"/>）。
        /// 根节点的深色画布背景保持铺满不动；宿主的墨迹换算锚点指向 <see cref="ContentHost"/>
        /// （见 <see cref="ContentAnchor"/>），因此墨迹仍与缩放后的页面内容正确对齐。
        /// </summary>
        public void SetViewMatrix(Matrix matrix)
        {
            _viewMatrix = matrix;
            // 矩阵已显式编码缩放锚点，ContentHostTransform 的 CenterX/CenterY 保持默认 (0,0)。
            ContentHostTransform.Matrix = matrix;
        }

        /// <summary>
        /// 内容锚点：承载页面内容、会被缩放/平移的容器。宿主墨迹换算
        /// （inkCanvas.TransformToVisual 该锚点）会自动纳入它的缩放/平移变换。
        /// </summary>
        public FrameworkElement ContentAnchor => ContentHost;

        /// <summary>
        /// 视口顶边对应的长条滚动偏移（含视图矩阵平移修正）。
        /// 缩放围绕非原点锚点时，视图矩阵含平移分量，视口顶边对应的内容位置会偏离
        /// <see cref="ScrollOffset"/>，当前页判定需要据此修正。
        /// </summary>
        public double GetViewportTopScrollOffset()
        {
            double s = _viewMatrix.M11;
            if (s <= 0 || double.IsNaN(s) || double.IsInfinity(s)) return _scrollOffset;
            return _scrollOffset - _viewMatrix.OffsetY / s;
        }

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
                    // 进入滚动模式时从顶部开始，随后由会话滚动到当前页顶部；
                    // 否则复用会话时残留旧偏移，长条起步位置不可预期。
                    SetScrollOffset(0);
                    // Strip 刚变为可见时 ActualWidth 可能还没布局好，延迟到下一帧重算长条，
                    // 否则页面 Image 尺寸为 0，长条显示全空。
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_pageImages.Count > 0) RecomputeStripLayout();
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
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
            _pageSeparators.Clear();

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
            // 强制布局，确保 ActualWidth 非 0，否则 RecomputeStripLayout 会因 viewW<=0 提前返回，
            // 页面 Image 尺寸保持 0，长条显示全灰/空白。
            UpdateLayout();
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

        /// <summary>
        /// 重新布局长条（页面 Uniform 居中）。页面宽度用与单页模式相同的
        /// <see cref="ComputeUniformRect"/> 逻辑（scale = min(viewW/imgW, viewH/imgH)），
        /// 保证同一页在单页与长条模式下宽度一致，避免模式切换时墨迹缩放错位。
        /// </summary>
        private void RecomputeStripLayout()
        {
            double viewW = ActualWidth;
            double viewH = ActualHeight;
            if (viewW <= 0) return;

            // 分隔线数量 = 页数 - 1（页与页之间的接缝）。
            int separatorCount = Math.Max(0, _pageImages.Count - 1);
            while (_pageSeparators.Count < separatorCount)
            {
                var line = new Line
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(96, 128, 128, 128)), // 低透明度灰色
                    StrokeThickness = PageSeparatorThickness,
                    StrokeDashArray = new DoubleCollection { 2.0, 2.0 },
                    IsHitTestVisible = false
                };
                _pageSeparators.Add(line);
                Strip.Children.Add(line);
            }

            double top = 0;
            for (int i = 0; i < _pageImages.Count; i++)
            {
                var img = _pageImages[i];
                var source = _pageSources[i];
                double imgW = source?.Width ?? 612;
                double imgH = source?.Height ?? 792;

                // 与单页模式 ComputeUniformRect 相同的 Uniform 计算。
                double scale = Math.Min(viewW / imgW, viewH > 0 ? viewH / imgH : 1.0);
                double w = imgW * scale;
                double h = imgH * scale;

                img.Width = w;
                img.Height = h;
                double left = (viewW - w) / 2;
                Canvas.SetLeft(img, left);
                Canvas.SetTop(img, top);

                // 页接缝处画分隔线：跨当前页宽度，压在边界上（1px 线各占两页 0.5px）。
                if (i < separatorCount)
                {
                    var sep = _pageSeparators[i];
                    sep.X1 = left;
                    sep.X2 = left + w;
                    sep.Y1 = top + h;
                    sep.Y2 = top + h;
                }

                top += h + PageGutter;
            }
            StripHeight = top - PageGutter;
            Strip.Height = StripHeight;
        }

        /// <summary>
        /// 长条模式下视口内可见页矩形（背景层局部坐标，已含滚动偏移）。
        /// 宿主用它在画布坐标系裁剪/恢复墨迹，因此必须与墨迹坐标系一致。
        /// 可见窗口由视口映射回背景局部坐标得出，缩放/平移后的视图矩阵会改变该窗口，
        /// 据此判定哪些页真正在视口内（缩放后可见范围缩小、平移后窗口偏移）。
        /// </summary>
        public IReadOnlyList<PluginVisiblePage> GetStripVisiblePageRects()
        {
            var list = new List<PluginVisiblePage>();
            double viewW = ActualWidth;
            double viewH = ActualHeight;
            if (viewW <= 0 || viewH <= 0) return list;

            double s = _viewMatrix.M11;
            if (s <= 0 || double.IsNaN(s) || double.IsInfinity(s)) s = 1.0;

            // 画布视口 [0, viewH] 经视图矩阵逆映射回背景局部坐标的可见窗口。
            // OffsetY 是缩放锚点平移产生的分量：锚点不在原点时，缩放也会移动窗口。
            double winTop = -_viewMatrix.OffsetY / s;
            double winBottom = winTop + viewH / s;

            for (int i = 0; i < _pageImages.Count; i++)
            {
                var img = _pageImages[i];
                double w = img.ActualWidth > 0 ? img.ActualWidth : img.Width;
                double h = img.ActualHeight > 0 ? img.ActualHeight : img.Height;
                double pageTop = Canvas.GetTop(img) - _scrollOffset;
                double pageBottom = pageTop + h;

                if (pageBottom > winTop && pageTop < winBottom)
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
                double top = Canvas.GetTop(img) - _scrollOffset;
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
