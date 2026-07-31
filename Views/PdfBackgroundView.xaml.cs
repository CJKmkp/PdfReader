using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ink_Canvas.Plugins;

namespace PdfReader.Views
{
    /// <summary>
    /// 注入到宿主 InkCanvas 下方的 PDF 背景视图：连续滚动长条。
    /// 所有页垂直排列在 <see cref="Strip"/> 里，页间留空白带，滚动通过 RenderTransform.TranslateY 实现。
    /// 只负责显示页面位图与计算页矩形，不含任何交互与墨迹：
    /// 宿主注入时会把它设为 IsHitTestVisible = false，书写事件全部落到宿主画布上。
    /// </summary>
    public partial class PdfBackgroundView : UserControl
    {
        /// <summary>页与页之间的空白带高度（DIP）。</summary>
        private const double PageGutter = 32;

        /// <summary>当前可见页的 Image 列表（按页序），与 <see cref="_pageImages"/> 一一对应。</summary>
        private readonly List<Image> _pageImages = new List<Image>();

        /// <summary>每页渲染用的源（长条坐标布局）。</summary>
        private readonly List<ImageSource> _pageSources = new List<ImageSource>();

        /// <summary>当前滚动偏移（DIP，长条内容向上滚为正）。</summary>
        private double _scrollOffset;

        /// <summary>长条当前实际高度（布局后）。</summary>
        private double _stripHeight;

        public PdfBackgroundView()
        {
            InitializeComponent();
        }

        /// <summary>总页数。</summary>
        public int PageCount => _pageImages.Count;

        /// <summary>当前滚动偏移（DIP）。</summary>
        public double ScrollOffset => _scrollOffset;

        /// <summary>长条总高度（含页间空白带）。</summary>
        public double StripHeight => _stripHeight;

        /// <summary>
        /// 重置为指定页数的长条。保留已有页的位图（复用缓存），新增页用 null 占位，
        /// 由会话层调用 <see cref="SetPageSource"/> 填充。
        /// </summary>
        public void ResetPages(int pageCount)
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
            _stripHeight = top - PageGutter;
            RecomputePageLayout();
        }

        /// <summary>设置指定页的位图源。</summary>
        public void SetPageSource(int pageIndex, ImageSource source)
        {
            if (pageIndex < 0 || pageIndex >= _pageImages.Count) return;
            _pageImages[pageIndex].Source = source;
            _pageSources[pageIndex] = source;
            RecomputePageLayout();
        }

        /// <summary>设置滚动偏移并平移长条。</summary>
        public void SetScrollOffset(double offset)
        {
            _scrollOffset = offset;
            StripTransform.Y = -_scrollOffset;
        }

        /// <summary>长条高度变化时重新布局（页面 Uniform 在画布内居中）。</summary>
        private void RecomputePageLayout()
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
                double scale = Math.Min(viewW / imgW, imgH > 0 ? 1.0 : 1.0);
                double w = imgW * scale;
                double h = imgH * scale;
                img.Width = w;
                Canvas.SetLeft(img, (viewW - w) / 2);
                Canvas.SetTop(img, top);
                top += h + PageGutter;
            }
            _stripHeight = top - PageGutter;
            Strip.Height = _stripHeight;
        }

        /// <summary>
        /// 计算视口 <c>[offset, offset + viewportHeight]</c> 内可见的页及各自在长条中的矩形。
        /// 返回 (pageIndex, 页在长条坐标的矩形)。
        /// </summary>
        public IReadOnlyList<PluginVisiblePage> GetVisiblePageRects(double viewportHeight)
        {
            var list = new List<PluginVisiblePage>();
            double viewW = ActualWidth;
            if (viewW <= 0 || viewportHeight <= 0) return list;

            double top = 0;
            for (int i = 0; i < _pageImages.Count; i++)
            {
                var img = _pageImages[i];
                double h = img.ActualHeight > 0 ? img.ActualHeight : img.Height;
                double w = img.ActualWidth > 0 ? img.ActualWidth : img.Width;
                double pageTop = top;
                double pageBottom = pageTop + h;

                // 页是否与视口相交。
                if (pageBottom > _scrollOffset && pageTop < _scrollOffset + viewportHeight)
                {
                    // 页在长条坐标的矩形（含页内空白，导出时按此裁剪墨迹）。
                    list.Add(new PluginVisiblePage
                    {
                        PageIndex = (uint)i,
                        ContentRect = new Rect(
                            (viewW - w) / 2,
                            pageTop,
                            w,
                            h)
                    });
                }

                top = pageBottom + PageGutter;
            }

            return list;
        }

        /// <summary>指定页在长条中的矩形（用于导出定位墨迹）。</summary>
        public Rect? GetPageRect(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _pageImages.Count) return null;
            double viewW = ActualWidth;
            var img = _pageImages[pageIndex];
            double w = img.ActualWidth > 0 ? img.ActualWidth : img.Width;
            double h = img.ActualHeight > 0 ? img.ActualHeight : img.Height;
            double top = Canvas.GetTop(img);
            return new Rect((viewW - w) / 2, top, w, h);
        }

        /// <summary>指定页对应的 Image 元素。</summary>
        public Image GetPageImage(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _pageImages.Count) return null;
            return _pageImages[pageIndex];
        }

        /// <summary>给定长条内的 Y 坐标，返回该位置所属的页索引。</summary>
        public int GetPageIndexAtOffset(double y)
        {
            for (int i = 0; i < _pageImages.Count; i++)
            {
                double top = Canvas.GetTop(_pageImages[i]);
                double h = _pageImages[i].ActualHeight > 0 ? _pageImages[i].ActualHeight : _pageImages[i].Height;
                if (y >= top && y < top + h) return i;
            }
            // 超出范围：最后一个可见的页。
            if (y < 0) return 0;
            return Math.Max(0, _pageImages.Count - 1);
        }
    }
}
