using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Ink_Canvas.Plugins;

namespace PdfReader
{
    internal sealed record ViewportTile(int Page, Rect Destination, Rect Source, int PixelWidth, int PixelHeight);

    internal static class ViewportRenderPlan
    {
        internal static IReadOnlyList<ViewportTile> Create(IReadOnlyList<PluginVisiblePage> pages, Matrix matrix,
            Size viewport, double dpi, long byteBudget)
        {
            if (!matrix.HasInverse || viewport.Width <= 0 || viewport.Height <= 0) return Array.Empty<ViewportTile>();
            double scale = matrix.M11;
            if (!double.IsFinite(scale) || scale <= 0) return Array.Empty<ViewportTile>();
            if (!double.IsFinite(dpi) || dpi <= 0) dpi = 1;
            var inverse = matrix; inverse.Invert();
            // A small overscan avoids a seam at the edge of the screen.
            var window = new Rect(-16, -16, viewport.Width + 32, viewport.Height + 32);
            window.Transform(inverse);
            var regions = pages.Select(page => (Page: page, Area: Rect.Intersect(page.ContentRect, window)))
                .Where(item => !item.Area.IsEmpty && item.Area.Width > 0 && item.Area.Height > 0)
                .OrderByDescending(item => item.Area.Width * item.Area.Height).Take(8).ToArray();
            long pixels = Math.Max(65536, byteBudget / 4 / Math.Max(1, regions.Length));
            return regions.Select(item =>
            {
                var page = item.Page.ContentRect; var area = item.Area;
                double width = Math.Ceiling(area.Width * scale * dpi), height = Math.Ceiling(area.Height * scale * dpi);
                double ratio = Math.Min(1, Math.Min(Math.Sqrt(pixels / Math.Max(1, width * height)),
                    4096 / Math.Max(width, height)));
                var source = new Rect((area.X - page.X) / page.Width, (area.Y - page.Y) / page.Height,
                    area.Width / page.Width, area.Height / page.Height);
                return new ViewportTile((int)item.Page.PageIndex, area, source,
                    Math.Max(1, (int)(width * ratio)), Math.Max(1, (int)(height * ratio)));
            }).ToArray();
        }
    }
}
