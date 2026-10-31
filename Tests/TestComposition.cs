using Ink_Canvas.Plugins;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Only document rendering/navigation is exercised here. Ink remains the host's responsibility.
internal sealed class TestComposition : ICanvasCompositionService
{
    internal readonly Grid Root = new();
    internal readonly InkCanvas InputCanvas = new() { Background = Brushes.Transparent };
    internal FrameworkElement Background;
    internal IReadOnlyList<PluginVisiblePage> Visible = Array.Empty<PluginVisiblePage>();
    public bool HasBackgroundLayer => Background != null;
    public uint PageCount { get; private set; }
    public uint CurrentPageIndex { get; private set; }
    public void InjectBackgroundLayer(Func<FrameworkElement> factory)
        => Program.UI(() => { Background = factory(); Background.IsHitTestVisible = false; Root.Children.Clear(); Root.Children.Add(Background); Root.Children.Add(InputCanvas); });
    public void RemoveBackgroundLayer() => Program.UI(() => { Root.Children.Clear(); Background = null; });
    public void SetPageContentRect(Rect? rect) { }
    public void ConfigurePages(uint count, uint page, Func<uint, CancellationToken, Task<BitmapSource>> render)
    { PageCount = count; CurrentPageIndex = page; }
    public Task SetCurrentPageAsync(uint page, CancellationToken token = default) { CurrentPageIndex = page; return Task.CompletedTask; }
    public void SetCanvasGestureHandler(IPluginCanvasGestureHandler handler) { }
    public void SetCanvasContentAnchor(FrameworkElement anchor) { }
    public Task SetVisiblePagesAsync(IReadOnlyList<PluginVisiblePage> pages, CancellationToken token = default)
    { Visible = pages; if (pages.Count > 0) CurrentPageIndex = pages[0].PageIndex; return Task.CompletedTask; }
    public Task ScrollOffsetAsync(double deltaY, CancellationToken token = default) => Task.CompletedTask;
    public Task TransformInkAsync(Matrix matrix, CancellationToken token = default) => Task.CompletedTask;
    public Task<StrokeCollection> GetStrokesForPageAsync(uint page, CancellationToken token = default) => Task.FromResult(new StrokeCollection());
    public Task<string> ExportWithInkAsync(string path, uint page, CancellationToken token = default) => Task.FromResult(path);
}
