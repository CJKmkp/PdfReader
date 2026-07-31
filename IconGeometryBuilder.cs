using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Common.IconKeys;

namespace PdfReader
{
    /// <summary>
    /// 把 iNKORE.UI.WPF.Modern 的字体图标转成几何路径。
    /// <para>
    /// 宿主的 <c>ToolbarImageButton.Icon</c> 是 <see cref="GeometryDrawing"/>，只吃几何数据，
    /// 塞不进字体图标；而直接自绘按钮又会丢掉宿主按钮的按压动画、朝向适配与紧凑模式。
    /// 因此这里用 <see cref="FormattedText.BuildGeometry"/> 把字形轮廓取出来，
    /// 再等比缩放并居中到工具栏图标的 24x24 视口。
    /// </para>
    /// </summary>
    internal static class IconGeometryBuilder
    {
        /// <summary>工具栏图标视口尺寸，与 ToolbarImageButton 内 DrawingGroup 的 ClipGeometry 一致。</summary>
        private const double ViewportSize = 24.0;

        /// <summary>字形取轮廓时的字号。取大一些以减少轮廓量化误差，最终会被缩放。</summary>
        private const double SourceFontSize = 100.0;

        /// <summary>图标在视口内的占比，留出一点边距，视觉重量与宿主自带图标接近。</summary>
        private const double FillRatio = 0.86;

        /// <summary>
        /// 生成已缩放居中的字形几何。失败时返回 <c>null</c>，调用方应保留原有图标。
        /// </summary>
        public static Geometry FromFontIcon(FontIconData icon)
        {
            if (string.IsNullOrEmpty(icon.Glyph)) return null;

            try
            {
                var typeface = new Typeface(
                    icon.FontFamily ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontStyles.Normal,
                    FontWeights.Normal,
                    FontStretches.Normal);

                var formatted = new FormattedText(
                    icon.Glyph,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    SourceFontSize,
                    Brushes.Black,
                    new NumberSubstitution(),
                    TextFormattingMode.Ideal,
                    1.0);

                var geometry = formatted.BuildGeometry(new Point(0, 0));
                if (geometry == null || geometry.IsEmpty()) return null;

                return NormalizeToViewport(geometry);
            }
            catch
            {
                // 字体缺失或字形不存在时交回 null，由调用方回落到内置路径。
                return null;
            }
        }

        /// <summary>把任意尺寸的几何等比缩放并居中到 24x24 视口。</summary>
        private static Geometry NormalizeToViewport(Geometry geometry)
        {
            var bounds = geometry.Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;

            double target = ViewportSize * FillRatio;
            double scale = Math.Min(target / bounds.Width, target / bounds.Height);

            var transform = new TransformGroup();
            // 先把轮廓原点移到 (0,0)，再缩放，最后居中到视口。
            transform.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));
            transform.Children.Add(new ScaleTransform(scale, scale));
            transform.Children.Add(new TranslateTransform(
                (ViewportSize - bounds.Width * scale) / 2,
                (ViewportSize - bounds.Height * scale) / 2));

            var result = geometry.Clone();
            result.Transform = transform;

            // GetFlattenedPathGeometry 会把变换烘进路径数据，避免后续再被缩放一次。
            var flattened = result.GetFlattenedPathGeometry();
            if (flattened.CanFreeze) flattened.Freeze();
            return flattened;
        }
    }
}
