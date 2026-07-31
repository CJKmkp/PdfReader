using System;

namespace PdfReader
{
    internal enum ZoomMode
    {
        FitWidth = 0,
        FitPage = 1,
        Custom = 2
    }

    /// <summary>
    /// 缩放计算与渲染像素宽度推导。纯逻辑，不依赖 WPF 之外的东西，便于单独验证边界值。
    /// </summary>
    internal static class ZoomModel
    {
        /// <summary>最小缩放比例。</summary>
        public const double MinScale = 0.1;

        /// <summary>最大缩放比例。</summary>
        public const double MaxScale = 8.0;

        /// <summary>单张位图的最大边长（像素）。超过会占用过多显存且 BitmapImage 解码变慢。</summary>
        public const int MaxPixelDimension = 4096;

        /// <summary>单张位图的最大总像素数（40 MP，约 160MB BGRA）。</summary>
        public const long MaxPixelCount = 40L * 1000 * 1000;

        /// <summary>渲染宽度按此粒度取整，避免连续缩放时每一像素都产生新的缓存条目。</summary>
        public const int WidthBucket = 64;

        private static readonly double[] ZoomSteps =
        {
            0.10, 0.15, 0.25, 0.33, 0.50, 0.67, 0.75, 1.00,
            1.25, 1.50, 2.00, 2.50, 3.00, 4.00, 6.00, 8.00
        };

        public static double Clamp(double scale)
        {
            if (double.IsNaN(scale) || double.IsInfinity(scale)) return 1.0;
            if (scale < MinScale) return MinScale;
            if (scale > MaxScale) return MaxScale;
            return scale;
        }

        /// <summary>返回比当前值大的下一档缩放。</summary>
        public static double StepUp(double scale)
        {
            for (int i = 0; i < ZoomSteps.Length; i++)
            {
                if (ZoomSteps[i] > scale + 1e-6) return ZoomSteps[i];
            }
            return MaxScale;
        }

        /// <summary>返回比当前值小的上一档缩放。</summary>
        public static double StepDown(double scale)
        {
            for (int i = ZoomSteps.Length - 1; i >= 0; i--)
            {
                if (ZoomSteps[i] < scale - 1e-6) return ZoomSteps[i];
            }
            return MinScale;
        }

        /// <summary>
        /// 由视口尺寸与页面尺寸计算 FitWidth / FitPage 对应的缩放比例。
        /// </summary>
        /// <param name="mode">缩放模式；Custom 时直接返回 <paramref name="customScale"/>。</param>
        /// <param name="viewportWidth">视口宽度（设备无关像素，已扣除边距与滚动条）。</param>
        /// <param name="viewportHeight">视口高度（设备无关像素）。</param>
        /// <param name="pageWidth">页面宽度（PDF 点，即 1/72 英寸）。</param>
        /// <param name="pageHeight">页面高度（PDF 点）。</param>
        public static double ComputeScale(ZoomMode mode, double viewportWidth, double viewportHeight,
            double pageWidth, double pageHeight, double customScale)
        {
            if (pageWidth <= 0 || pageHeight <= 0) return Clamp(customScale);

            switch (mode)
            {
                case ZoomMode.FitWidth:
                    if (viewportWidth <= 0) return 1.0;
                    return Clamp(viewportWidth / pageWidth);
                case ZoomMode.FitPage:
                    if (viewportWidth <= 0 || viewportHeight <= 0) return 1.0;
                    return Clamp(Math.Min(viewportWidth / pageWidth, viewportHeight / pageHeight));
                default:
                    return Clamp(customScale);
            }
        }

        /// <summary>
        /// 由缩放比例与页面尺寸推导实际渲染的位图像素宽度。
        /// 会考虑 DPI 缩放、按 <see cref="WidthBucket"/> 取整，并施加尺寸/总像素上限。
        /// </summary>
        /// <param name="scale">显示缩放比例（相对页面原始尺寸）。</param>
        /// <param name="pageWidth">页面宽度（PDF 点）。</param>
        /// <param name="pageHeight">页面高度（PDF 点）。</param>
        /// <param name="dpiScale">显示器 DPI 缩放（96 DPI 为 1.0）。</param>
        public static int ComputeRenderWidth(double scale, double pageWidth, double pageHeight, double dpiScale)
        {
            if (pageWidth <= 0 || pageHeight <= 0) return WidthBucket;
            if (dpiScale <= 0 || double.IsNaN(dpiScale) || double.IsInfinity(dpiScale)) dpiScale = 1.0;

            double raw = pageWidth * Clamp(scale) * dpiScale;
            if (raw < 1) raw = 1;

            // 向上取整到桶边界，保证渲染分辨率不低于显示分辨率
            int width = (int)Math.Ceiling(raw / WidthBucket) * WidthBucket;

            // 边长上限
            double aspect = pageHeight / pageWidth;
            if (width > MaxPixelDimension) width = MaxPixelDimension;
            int height = (int)Math.Ceiling(width * aspect);
            if (height > MaxPixelDimension)
            {
                width = (int)Math.Floor(MaxPixelDimension / aspect);
                height = MaxPixelDimension;
            }

            // 总像素上限
            if ((long)width * height > MaxPixelCount)
            {
                width = (int)Math.Floor(Math.Sqrt(MaxPixelCount / aspect));
            }

            if (width < 1) width = 1;
            return width;
        }
    }
}
