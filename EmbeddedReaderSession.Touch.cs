using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PdfReader
{
    internal sealed partial class EmbeddedReaderSession
    {
        private Window _touchHost;
        private InkCanvas _navigationCanvas;
        private readonly Dictionary<int, Point> _navigationTouches = new();
        private readonly Dictionary<int, TouchDevice> _capturedTouches = new();
        internal bool CanUseSingleTouchNavigation => TouchGesturesEnabled
            && (!IsPenMode || _navigationCanvas?.EditingMode == InkCanvasEditingMode.Select);

        private void AttachTouchNavigation(Window host)
        {
            _touchHost = host;
            _navigationCanvas = host.FindName("inkCanvas") as InkCanvas ?? FindCanvas(host);
            // Older hosts only forward two-contact Manipulation events. Capture
            // read/select touch input before InkCanvas turns it into a lasso.
            host.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(NavigationTouchDown), true);
            host.AddHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(NavigationTouchMove), true);
            host.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(NavigationTouchUp), true);
            host.AddHandler(UIElement.LostTouchCaptureEvent, new EventHandler<TouchEventArgs>(NavigationTouchLost), true);
            host.Deactivated += NavigationHostDeactivated;
        }

        private void DetachTouchNavigation()
        {
            if (_touchHost != null && !_touchHost.Dispatcher.CheckAccess())
            { _touchHost.Dispatcher.Invoke(DetachTouchNavigation); return; }
            ResetNavigationTouches();
            if (_touchHost == null) return;
            _touchHost.RemoveHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(NavigationTouchDown));
            _touchHost.RemoveHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(NavigationTouchMove));
            _touchHost.RemoveHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(NavigationTouchUp));
            _touchHost.RemoveHandler(UIElement.LostTouchCaptureEvent, new EventHandler<TouchEventArgs>(NavigationTouchLost));
            _touchHost.Deactivated -= NavigationHostDeactivated;
            _touchHost = null; _navigationCanvas = null;
        }

        private static InkCanvas FindCanvas(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is InkCanvas canvas) return canvas;
                var nested = FindCanvas(child); if (nested != null) return nested;
            }
            return null;
        }

        internal bool BeginTouchNavigation(int id, Point position)
        {
            if (!IsOpen || _modeSwitching || _backgroundView?.IsVisible != true || !CanUseSingleTouchNavigation
                || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return false;
            _navigationTouches[id] = position; _gestureActive = true; return true;
        }

        internal bool UpdateTouchNavigation(int id, Point position)
        {
            if (!_navigationTouches.ContainsKey(id) || !CanUseSingleTouchNavigation
                || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return false;
            var before = _navigationTouches.OrderBy(pair => pair.Key).Take(2).Select(pair => pair.Value).ToArray();
            _navigationTouches[id] = position;
            var after = _navigationTouches.OrderBy(pair => pair.Key).Take(2).Select(pair => pair.Value).ToArray();
            Point oldCenter = before.Length == 1 ? before[0] : new Point((before[0].X + before[1].X) / 2, (before[0].Y + before[1].Y) / 2);
            Point newCenter = after.Length == 1 ? after[0] : new Point((after[0].X + after[1].X) / 2, (after[0].Y + after[1].Y) / 2);
            double factor = 1;
            if (before.Length == 2)
            {
                double oldSpan = (before[0] - before[1]).Length, newSpan = (after[0] - after[1]).Length;
                if (oldSpan >= 12 && newSpan >= 12) factor = newSpan / oldSpan;
            }
            return ApplyTouchNavigation(after.Length, newCenter - oldCenter, factor, oldCenter);
        }

        internal bool ApplyTouchNavigation(int contacts, Vector translation, double factor, Point origin)
        {
            if (!IsOpen || _backgroundView?.IsVisible != true || contacts < 1 || _modeSwitching) return false;
            if (contacts == 1 && !CanUseSingleTouchNavigation) return false;
            if (!TouchGesturesEnabled && !IsPenMode) return true;
            if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(translation.X) || !double.IsFinite(translation.Y)
                || !double.IsFinite(origin.X) || !double.IsFinite(origin.Y)) return true;
            double scale = ZoomModel.Clamp(CurrentScale * factor);
            double ratio = scale / CurrentScale;
            var increment = Matrix.Identity;
            increment.ScaleAt(ratio, ratio, origin.X, origin.Y);
            increment.Translate(translation.X, translation.Y);
            _gestureActive = true;
            ApplyViewTransform(increment);
            return true;
        }

        internal void EndTouchNavigation(int id)
        {
            if (!_navigationTouches.Remove(id)) return;
            if (_capturedTouches.Remove(id, out var device) && ReferenceEquals(device.Captured, _touchHost)) device.Capture(null);
            if (_navigationTouches.Count != 0 || !_gestureActive) return;
            if (IsOpen && _backgroundView?.IsVisible == true) OnCanvasGestureCompleted(null);
            else _gestureActive = false;
        }

        private void ResetNavigationTouches()
        {
            if (_touchHost != null && !_touchHost.Dispatcher.CheckAccess())
            { _touchHost.Dispatcher.Invoke(ResetNavigationTouches); return; }
            foreach (int id in _navigationTouches.Keys.ToArray()) EndTouchNavigation(id);
        }

        private void NavigationHostDeactivated(object sender, EventArgs e) => ResetNavigationTouches();

        private void NavigationTouchDown(object sender, TouchEventArgs e)
        {
            var view = _backgroundView;
            if (view == null || !CanUseSingleTouchNavigation) return;
            for (var source = e.OriginalSource as DependencyObject; source != null;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
                if (source is Control && source is not InkCanvas && !ReferenceEquals(source, view) && source is not Window) return;
            var point = e.GetTouchPoint(view).Position;
            if (point.X < 0 || point.Y < 0 || point.X > view.ActualWidth || point.Y > view.ActualHeight) return;
            if (!BeginTouchNavigation(e.TouchDevice.Id, point)) return;
            // The injected background is deliberately not hit-testable. Capture
            // to the host window so WPF does not revoke capture on the next frame.
            if (!e.TouchDevice.Capture(_touchHost)) { EndTouchNavigation(e.TouchDevice.Id); return; }
            _capturedTouches[e.TouchDevice.Id] = e.TouchDevice;
            e.Handled = true;
        }

        private void NavigationTouchMove(object sender, TouchEventArgs e)
        {
            if (!_navigationTouches.ContainsKey(e.TouchDevice.Id)) return;
            if (!UpdateTouchNavigation(e.TouchDevice.Id, e.GetTouchPoint(_backgroundView).Position)) EndTouchNavigation(e.TouchDevice.Id);
            e.Handled = true;
        }

        private void NavigationTouchUp(object sender, TouchEventArgs e)
        {
            if (!_navigationTouches.ContainsKey(e.TouchDevice.Id)) return;
            EndTouchNavigation(e.TouchDevice.Id); e.Handled = true;
        }

        private void NavigationTouchLost(object sender, TouchEventArgs e)
        {
            if (!ReferenceEquals(e.TouchDevice.Captured, _touchHost)) EndTouchNavigation(e.TouchDevice.Id);
        }
    }
}
