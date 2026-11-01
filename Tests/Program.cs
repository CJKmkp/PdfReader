using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfReader;
using PdfReader.Views;
using PdfSharp.Pdf;

internal static class Program
{
    private static int _checks;
    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; }
    internal static void UI(Action action)
    { var dispatcher = Application.Current.Dispatcher; if (dispatcher.CheckAccess()) action(); else dispatcher.Invoke(action); }
    internal static void Pump(int ms = 10)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Wait(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(25)) Pump();
        if (!task.IsCompleted) throw new TimeoutException("PDF test timeout"); task.GetAwaiter().GetResult();
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string root = Path.Combine(Path.GetTempPath(), "PdfReaderContinuous_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            WindowStateTests(); WheelStepTests(); RenderBudgetTests(); NativeResolutionTests(root); ConfigTests(root); LayoutTests(); SessionTests(root);
            Console.WriteLine($"PASS: {_checks} continuous PDF checks"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); }
    }

    private static void WindowStateTests()
    {
        bool fullscreen = false;
        int mouseCount = 0;
        var changes = new List<bool>();
        var state = new ReaderWindowState(() => fullscreen,
            value => { fullscreen = value; changes.Add(value); },
            () => { Check(!fullscreen, "Mouse selected before restoring desktop window"); mouseCount++; });
        state.End(true);
        Check(mouseCount == 0, "Closing an unused reader changed the user's tool");
        state.Begin(); state.Show(); state.Begin(); state.Show(); state.End(true); state.End(true);
        Check(!fullscreen && mouseCount == 1, "Repeated opens overwrote the original window state");
        state.Begin(); state.End(false);
        Check(mouseCount == 1, "Failed open changed the user's tool");
        fullscreen = true;
        var existingFullscreen = new ReaderWindowState(() => fullscreen, value => fullscreen = value, () => mouseCount++);
        existingFullscreen.Begin(); existingFullscreen.Show(); existingFullscreen.End(false);
        Check(fullscreen, "Reader restored a fullscreen window to the wrong state");
        var failingRestore = new ReaderWindowState(() => false,
            _ => throw new InvalidOperationException("restore fixture"), () => mouseCount++);
        failingRestore.Begin();
        try { failingRestore.End(true); } catch (InvalidOperationException) { }
        Check(mouseCount == 2, "Window restore failure prevented leaving annotation mode");
        Check(changes.SequenceEqual(new[] { true, true, false, false }), "Unexpected fullscreen lifecycle");
    }

    private static void RenderBudgetTests()
    {
        foreach (long budget in new[] { 32L << 20, 192L << 20 })
        {
            int normal = ZoomModel.ComputeBudgetedRenderWidth(2, 600, 800, budget);
            int enlarged = ZoomModel.ComputeBudgetedRenderWidth(8, 600, 800, budget);
            Check(enlarged > normal, "Zoomed resolution remained fixed");
            Check((long)enlarged * Math.Ceiling(enlarged * 800.0 / 600) * 4 <= budget,
                "Zoom refinement exceeded the pixel budget");
        }
    }

    private static void WheelStepTests()
    {
        Check(EmbeddedReaderSession.ComputeWheelScrollStep(-120, 3, 800, 1) == 144, "Wheel notch did not respect the three-line Windows preference");
        Check(EmbeddedReaderSession.ComputeWheelScrollStep(-15, 3, 800, 1) == 18, "High-resolution wheel delta was rounded");
        Check(EmbeddedReaderSession.ComputeWheelScrollStep(120, 3, 800, 2) == -72, "Zoomed wheel speed changed on screen");
        Check(EmbeddedReaderSession.ComputeWheelScrollStep(-120, -1, 800, 1) == 720, "Page-scroll preference was treated as a negative line count");
        Check(EmbeddedReaderSession.ComputeWheelScrollStep(120, 0, 800, 1) == 0, "Disabled Windows wheel scrolling was ignored");
    }

    private static void NativeResolutionTests(string root)
    {
        string path = Path.Combine(root, "wide.pdf");
        using (var pdf = new PdfDocument())
        {
            var page = pdf.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(4000);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(400);
            pdf.Save(path);
        }
        var opening = PdfDocumentSession.OpenAsync(path, CancellationToken.None);
        Wait(opening);
        using var document = opening.Result;
        var rendering = document.RenderAsync(0, 5120, CancellationToken.None);
        Wait(rendering);
        Check(rendering.Result.PixelWidth == 5120, "Native PDF renderer silently capped high-resolution requests at 4096px");
    }

    private static void ConfigTests(string root)
    {
        string path = Path.Combine(root, "config.json");
        Check(ReaderConfig.Load(path).DisplayMode == PdfDisplayMode.ContinuousScroll, "New installs do not default to continuous reading");
        File.WriteAllText(path, "{\"cacheBudgetMb\":64}");
        Check(ReaderConfig.Load(path).DisplayMode == PdfDisplayMode.ContinuousScroll, "Existing config migration changed cache preferences or missed continuous mode");
        var config = new ReaderConfig { DisplayMode = PdfDisplayMode.DoublePage }; config.Save(path);
        Check(ReaderConfig.Load(path).DisplayMode == PdfDisplayMode.DoublePage, "Reading mode was not persisted");
        File.WriteAllText(path, "{\"displayMode\":999}");
        Check(ReaderConfig.Load(path).DisplayMode == PdfDisplayMode.ContinuousScroll, "Invalid saved mode was accepted");
    }

    private static void LayoutTests()
    {
        var view = new PdfBackgroundView { Width = 900, Height = 700 };
        var window = new Window { Content = view, Width = 920, Height = 740, Left = -20000, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout(); view.SetDisplayMode(PdfDisplayMode.ContinuousScroll);
            view.ResetStrip(new[] { new Size(600, 800), new Size(1200, 600), new Size(500, 1000) });
            window.UpdateLayout();
            var before = Enumerable.Range(0, 3).Select(index => view.GetPageRect(index).Value).ToArray();
            var bitmap = BitmapSource.Create(800, 400, 96, 96, PixelFormats.Bgra32, null, new byte[800 * 400 * 4], 800 * 4); bitmap.Freeze();
            view.SetStripPageSource(1, bitmap); window.UpdateLayout();
            Check(Enumerable.Range(0, 3).All(index => view.GetPageRect(index) == before[index]), "Bitmap arrival changed page layout and moved annotations");
            Check(before[1].Height < before[0].Height && before[2].Y == before[1].Bottom, "Mixed page sizes did not form a contiguous strip");
            view.RetainStripSources(new HashSet<int>());
            Check(view.ResidentPageCount == 0 && view.GetPageRect(2) == before[2], "Evicting an image changed the document geometry");
            view.SetViewMatrix(new Matrix(.5, 0, 0, .5, 0, 0));
            Check(view.GetStripVisiblePageRects().Count >= 2 && !((Grid)view.FindName("ContentHost")).ClipToBounds,
                "Zooming out clipped adjacent continuous pages to the original viewport");
            view.SetViewMatrix(new Matrix(2, 0, 0, 2, -100, -300)); view.SetScrollOffset(-999999);
            Check(Math.Abs(view.GetViewportTopScrollOffset()) < .01, "Zoom/pan prevented scrolling back to the document start");
            view.SetScrollOffset(double.NaN);
            Check(double.IsFinite(view.ScrollOffset), "Non-finite scroll input corrupted the view");
            view.SetViewMatrix(Matrix.Identity); view.SetScrollOffset(0);
            view.ResetStrip(Enumerable.Repeat(new Size(600, 800), 5000).ToArray());
            for (int index = 0; index < 5000; index += 97)
                Check(view.GetPageIndexAtStripOffset(index * 700 + 1) == index, "Page lookup failed deep in a large document");
        }
        finally { window.Close(); }
    }

    private static void SessionTests(string root)
    {
        string path = Path.Combine(root, "mixed-pages.pdf"), other = Path.Combine(root, "short.pdf");
        using (var pdf = new PdfDocument())
        {
            for (int index = 0; index < 60; index++)
            { var page = pdf.AddPage(); page.Width = PdfSharp.Drawing.XUnit.FromPoint(index % 2 == 0 ? 600 : 1000); page.Height = PdfSharp.Drawing.XUnit.FromPoint(index % 2 == 0 ? 800 : 600); }
            pdf.Save(path);
        }
        using (var pdf = new PdfDocument())
        { var page = pdf.AddPage(); page.Width = PdfSharp.Drawing.XUnit.FromPoint(400); page.Height = PdfSharp.Drawing.XUnit.FromPoint(900); pdf.Save(other); }
        var composition = new TestComposition();
        var window = new Window { Content = composition.Root, Width = 1000, Height = 750, Left = -20000, ShowActivated = false };
        var errors = new List<string>(); var config = new ReaderConfig { CacheBudgetMb = 32 };
        using var session = new EmbeddedReaderSession(composition, null, config, (message, error) => { lock (errors) errors.Add(message + ": " + error); });
        try
        {
            window.Show(); window.UpdateLayout();
            var openingWatch = Stopwatch.StartNew();
            Wait(session.OpenAsync(path, 20, CancellationToken.None));
            var firstView = (PdfBackgroundView)composition.Background;
            Check(firstView.GetStripPageImage(20).Source is BitmapSource && Field<Task>(session, "_stripVisibleTask").IsCompleted,
                "Opening finished without the requested page's first paint");
            Console.WriteLine($"PDF open/first visible page: {openingWatch.ElapsedMilliseconds}ms (60-page fixture)");
            Pump(250);
            var view = (PdfBackgroundView)composition.Background;
            Check(session.DisplayMode == PdfDisplayMode.ContinuousScroll && session.CurrentPage == 20,
                "Opening a saved late page rendered the beginning of the document instead");
            Pump(300);
            var preview = session.RenderPreviewAsync(21, CancellationToken.None);
            Wait(preview);
            Check(preview.Result != null && preview.Result.IsFrozen && preview.Result.PixelWidth <= 240 && preview.Result.PixelHeight <= 201,
                "Catalogue thumbnail was missing, unfrozen, or rendered at reading resolution");
            var invalidPreview = session.RenderPreviewAsync(0, CancellationToken.None); Wait(invalidPreview);
            Check(invalidPreview.Result == null, "Catalogue accepted a zero-based page number");
            var jump = session.SelectPreviewPageAsync(38, CancellationToken.None); Wait(jump);
            Check(jump.Result == 38 && session.CurrentPage == 37, "Continuous catalogue jump selected the wrong page");
            var invalidJump = session.SelectPreviewPageAsync(61, CancellationToken.None); Wait(invalidJump);
            Check(invalidJump.Result == 0 && session.CurrentPage == 37, "Invalid catalogue jump changed the page");
            Wait(session.ScrollToPageTopAsync(20, CancellationToken.None)); Pump(300);
            Check(view.GetStripPageImage(20).Source is BitmapSource && view.GetStripPageImage(50).Source == null && view.ResidentPageCount <= 5,
                "Continuous mode rendered or retained offscreen pages");
            // These tests concern document touch navigation only, not ink storage.
            Wait(session.ScrollToPageTopAsync(0, CancellationToken.None)); Pump(180);
            MouseNavigationTests(session, composition, view);
            Wait(session.ResetZoomAsync());
            Wait(session.ScrollToPageTopAsync(0, CancellationToken.None)); Pump(180);
            composition.InputCanvas.EditingMode = InkCanvasEditingMode.Select; session.IsPenMode = true;
            using (var touch = new TestTouchDevice(view, 1001))
            {
                var down = touch.Send(window, UIElement.PreviewTouchDownEvent, new Point(300, 300), TouchAction.Down);
                Pump(20);
                Check(down.Handled && ReferenceEquals(touch.Captured, window),
                    "Selection-mode touch was not captured through the real WPF routed event hook");
                touch.Send(window, UIElement.PreviewTouchMoveEvent, new Point(300, -1600), TouchAction.Move);
                Check(session.CurrentPage > 1, "Captured selection-mode touch did not cross document pages");
                touch.Send(window, UIElement.PreviewTouchUpEvent, new Point(300, -1600), TouchAction.Up);
                Check(touch.Captured == null, "Touch release left the host window capturing later input");
            }
            Wait(session.ScrollToPageTopAsync(0, CancellationToken.None)); Pump(180);
            Check(session.CanUseSingleTouchNavigation && session.BeginTouchNavigation(1, new Point(300, 300)),
                "The host's selection tool prevented PDF touch navigation");
            Check(session.UpdateTouchNavigation(1, new Point(300, -1800)) && session.CurrentPage > 1,
                "Selection-mode touch panning remained trapped on one page");
            session.EndTouchNavigation(1);
            Wait(session.ScrollToPageTopAsync(0, CancellationToken.None)); Pump(180);
            Check(session.BeginTouchNavigation(1, new Point(250, 300)) && session.BeginTouchNavigation(2, new Point(650, 300)),
                "Selection mode did not accept two touch contacts");
            session.UpdateTouchNavigation(2, new Point(850, 300));
            Check(session.ViewScale > 1, "Two-contact selection-mode pinch did not zoom the PDF");
            session.EndTouchNavigation(2); session.EndTouchNavigation(1);
            composition.InputCanvas.EditingMode = InkCanvasEditingMode.Ink;
            Check(!session.BeginTouchNavigation(3, new Point(300, 300)), "Single-contact annotation input was stolen by document navigation");
            Check(session.ApplyTouchNavigation(2, new Vector(0, -2500), 1, new Point(400, 300)) && session.CurrentPage > 1,
                "Annotation-mode two-finger panning moved only the current page");
            session.ApplyTouchNavigation(2, new Vector(0, 1e6), 1, new Point(400, 300));
            Check(Math.Abs(view.GetViewportTopScrollOffset()) < .01, "Touch panning overshot the beginning of the document");
            session.ApplyTouchNavigation(2, new Vector(0, -1e6), 1, new Point(400, 300));
            Check(Math.Abs(view.GetViewportTopScrollOffset() + view.EffectiveViewportHeight - view.StripHeight) < .01,
                "Touch panning overshot the end of the document");
            session.OnCanvasGestureCompleted(null);
            Wait(session.ResetZoomAsync());
            Wait(session.ScrollToPageTopAsync(0, CancellationToken.None)); Pump(180);
            int notifications = 0; session.PageChanged += _ => notifications++;
            session.HandleScroll(-15); Pump(15);
            Check(Math.Abs(view.ScrollOffset - EmbeddedReaderSession.ComputeWheelScrollStep(-15, SystemParameters.WheelScrollLines, view.ActualHeight, 1)) < .01,
                "High-resolution wheel scrolling jumped a full notch");
            for (int i = 0; i < 8; i++) session.HandleScroll(-15);
            Pump(180);
            Check(notifications == 0, "Scrolling inside one page repeatedly wrote the remembered page/config");
            for (int round = 0; round < 5; round++)
            {
                foreach (int target in new[] { 45, 2, 38, 0 }) Wait(session.ScrollToPageTopAsync(target, CancellationToken.None));
                Pump(180);
                Check(session.CurrentPage == 0 && Math.Abs(view.GetViewportTopScrollOffset()) < .01,
                    "Fast reverse scrolling stopped at the wrong page or offset");
                Check(view.ResidentPageCount <= 6, "Repeated scrolling accumulated page bitmap references");
            }
            Wait(Task.Run(() => session.ScrollByAsync(30))); Pump(160);
            Check(Math.Abs(view.ScrollOffset - 30) < .01, "Background navigation accessed WPF outside its dispatcher");
            Wait(session.SetDisplayModeAsync(PdfDisplayMode.SinglePage, CancellationToken.None));
            var singleJump = session.SelectPreviewPageAsync(13, CancellationToken.None); Wait(singleJump);
            Check(singleJump.Result == 13 && session.CurrentPage == 12, "Single-page catalogue jump used a continuous offset");
            Wait(session.SetDisplayModeAsync(PdfDisplayMode.DoublePage, CancellationToken.None));
            var doubleJump = session.SelectPreviewPageAsync(14, CancellationToken.None); Wait(doubleJump);
            Check(doubleJump.Result == 13 && session.CurrentPage == 12, "Double-page catalogue jump did not align to the page pair");
            Wait(session.SetDisplayModeAsync(PdfDisplayMode.ContinuousScroll, CancellationToken.None)); Pump(180);
            Check(view.Mode == PdfDisplayMode.ContinuousScroll && view.GetStripVisiblePageRects().Count > 0,
                "Switching page/continuous mode left an empty reading view");
            typeof(EmbeddedReaderSession).GetMethod("HandleZoom", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(session, new object[] { true, new Point(400, 300) }); Pump(180);
            Wait(session.ScrollToPageTopAsync(0, CancellationToken.None)); Pump(180);
            Check(Math.Abs(view.GetViewportTopScrollOffset()) < .1, "Zoomed navigation could not reach page one");
            Wait(session.ScrollByAsync(500));
            for (int i = 0; i < 15; i++) session.HandleScroll(-120);
            session.IsPenMode = false;
            Check(session.BeginMouseNavigation(new Point(300, 300), composition.InputCanvas), "Replacement-document drag fixture could not start");
            Wait(session.OpenAsync(other, 0, CancellationToken.None)); Pump(350);
            Check(!session.UpdateMouseNavigation(new Point(300, 100)), "Replacing the PDF retained an old mouse drag");
            session.IsPenMode = true;
            Check(session.PageCount == 1 && view.ResidentPageCount <= 1 && view.GetStripPageImage(0).Source != null,
                "An old render task repainted a replacement document");
            var lastBitmap = (BitmapSource)view.GetStripPageImage(0).Source;
            Check(Math.Abs(lastBitmap.PixelWidth / (double)lastBitmap.PixelHeight - 400.0 / 900) < .001,
                "A stale bitmap from the old document was attached to the new page");
            foreach (var mode in new[] { PdfDisplayMode.ContinuousScroll, PdfDisplayMode.SinglePage, PdfDisplayMode.DoublePage })
            {
                Wait(session.ResetZoomAsync());
                Wait(session.SetDisplayModeAsync(mode, CancellationToken.None)); Pump(240);
                if (mode == PdfDisplayMode.ContinuousScroll) Wait(Field<Task>(session, "_stripRenderTask"));
                BitmapSource Bitmap() => mode == PdfDisplayMode.ContinuousScroll
                    ? (BitmapSource)view.GetStripPageImage(0).Source
                    : (BitmapSource)((Image)view.FindName("LeftImage")).Source;
                int baselineWidth = Bitmap().PixelWidth;
                Check(session.ApplyTouchNavigation(2, new Vector(), 6, new Point(400, 300)), "Zoom fixture was rejected");
                session.OnCanvasGestureCompleted(null); Pump(250);
                if (mode == PdfDisplayMode.ContinuousScroll) Wait(Field<Task>(session, "_stripRenderTask"));
                var timer = Stopwatch.StartNew();
                while (Bitmap().PixelWidth <= baselineWidth && timer.ElapsedMilliseconds < 5000) Pump(30);
                Check(Bitmap().PixelWidth > baselineWidth, $"{mode}: default performance mode did not refine the enlarged bitmap");
            }
            session.Close(); Pump(250);
            Check(!composition.HasBackgroundLayer, "Closed PDF retained its background view");
            Check(errors.Count == 0, "Continuous reader logged failures: " + string.Join("\n", errors));
        }
        finally { window.Close(); }
    }

    private static void MouseNavigationTests(EmbeddedReaderSession session, TestComposition composition, PdfBackgroundView view)
    {
        var origin = new Point(300, 300);
        composition.InputCanvas.EditingMode = InkCanvasEditingMode.Ink; session.IsPenMode = true;
        Check(!session.BeginMouseNavigation(origin, composition.InputCanvas), "Mouse drag stole left-button ink input");
        composition.InputCanvas.EditingMode = InkCanvasEditingMode.Select;
        Check(!session.BeginMouseNavigation(origin, new Button()) && !session.BeginMouseNavigation(origin, new ListBox()),
            "Mouse drag stole toolbar or catalogue interaction");
        Check(!session.BeginMouseNavigation(new Point(-1, 300), composition.InputCanvas), "Drag started outside the PDF viewport");
        session.TouchGesturesEnabled = false;
        Check(session.BeginMouseNavigation(origin, composition.InputCanvas), "Mouse selection drag incorrectly required touch gestures");
        Check(session.UpdateMouseNavigation(new Point(301, 300)) && Math.Abs(view.GetViewportTopScrollOffset()) < .01,
            "A click with slight pointer jitter moved the document");
        Check(session.UpdateMouseNavigation(new Point(300, -1800)) && session.CurrentPage > 1,
            "Mouse selection drag did not cross pages");
        Check(!session.BeginTouchNavigation(99, origin), "Touch navigation started during a mouse drag");
        session.EndMouseNavigation();
        Check(!session.UpdateMouseNavigation(origin), "Released mouse drag kept moving the document");
        Wait(session.ScrollToPageTopAsync(0, CancellationToken.None));
        Check(session.BeginMouseNavigation(origin, composition.InputCanvas), "A completed drag prevented the next drag");
        composition.InputCanvas.EditingMode = InkCanvasEditingMode.Ink;
        Check(!session.UpdateMouseNavigation(new Point(300, 200)), "Switching to ink left mouse navigation active");
        session.IsPenMode = false;
        Check(session.BeginMouseNavigation(origin, composition.InputCanvas), "Reading-mode mouse drag was unavailable");
        session.UpdateMouseNavigation(new Point(300, 100000));
        Check(Math.Abs(view.GetViewportTopScrollOffset()) < .01, "Mouse drag overshot the document start");
        var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseUpEvent };
        composition.InputCanvas.RaiseEvent(up);
        Check(up.Handled && !session.UpdateMouseNavigation(origin), "Routed mouse release did not end navigation");
        session.TouchGesturesEnabled = true; session.IsPenMode = true;
    }
}
