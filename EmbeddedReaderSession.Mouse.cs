using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PdfReader
{
    internal sealed partial class EmbeddedReaderSession
    {
        private Window _mouseHost;
        private Point? _mouseDragOrigin;
        private Point _mouseDragLast;
        private bool _mouseDragging;

        private bool CanUseMouseNavigation => !IsPenMode || _navigationCanvas?.EditingMode == InkCanvasEditingMode.Select;

        private void AttachMouseNavigation(Window host)
        {
            _mouseHost = host;
            host.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(NavigationMouseDown), true);
            host.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(NavigationMouseMove), true);
            host.AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(NavigationMouseUp), true);
            host.LostMouseCapture += NavigationMouseLost;
            host.Deactivated += NavigationMouseDeactivated;
        }

        private void DetachMouseNavigation()
        {
            if (_mouseHost == null) return;
            if (!_mouseHost.Dispatcher.CheckAccess()) { _mouseHost.Dispatcher.Invoke(DetachMouseNavigation); return; }
            EndMouseNavigation();
            _mouseHost.RemoveHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(NavigationMouseDown));
            _mouseHost.RemoveHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(NavigationMouseMove));
            _mouseHost.RemoveHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(NavigationMouseUp));
            _mouseHost.LostMouseCapture -= NavigationMouseLost;
            _mouseHost.Deactivated -= NavigationMouseDeactivated;
            _mouseHost = null;
        }

        // Background layers are not hit-testable. Accept the ink surface above
        // the PDF, but leave toolbar controls, catalogue lists and adorners alone.
        private bool IsDocumentNavigationSource(DependencyObject source)
        {
            for (; source != null; source = source is Visual
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
                if (source is Control && source is not InkCanvas && source is not Window
                    && !ReferenceEquals(source, _backgroundView)) return false;
            return true;
        }

        internal bool BeginMouseNavigation(Point position, DependencyObject source)
        {
            var view = _backgroundView;
            if (!IsOpen || _modeSwitching || view?.IsVisible != true || !CanUseMouseNavigation
                || _navigationTouches.Count > 0 || _mouseDragOrigin != null || !IsDocumentNavigationSource(source)
                || !double.IsFinite(position.X) || !double.IsFinite(position.Y)
                || position.X < 0 || position.Y < 0 || position.X > view.ActualWidth || position.Y > view.ActualHeight) return false;
            _mouseDragOrigin = position; _mouseDragLast = position;
            return true;
        }

        internal bool UpdateMouseNavigation(Point position)
        {
            if (_mouseDragOrigin is not Point origin) return false;
            if (!CanUseMouseNavigation || !IsOpen || _modeSwitching || _backgroundView?.IsVisible != true)
            { EndMouseNavigation(); return false; }
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y)) return false;
            if (!_mouseDragging && Math.Abs(position.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(position.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return true;
            _mouseDragging = true; _gestureActive = true;
            var increment = Matrix.Identity;
            increment.Translate(position.X - _mouseDragLast.X, position.Y - _mouseDragLast.Y);
            _mouseDragLast = position;
            ApplyViewTransform(increment);
            return true;
        }

        internal void EndMouseNavigation()
        {
            if (_mouseHost != null && !_mouseHost.Dispatcher.CheckAccess())
            { _mouseHost.Dispatcher.Invoke(EndMouseNavigation); return; }
            if (_mouseDragOrigin == null) return;
            _mouseDragOrigin = null;
            bool moved = _mouseDragging; _mouseDragging = false;
            if (_mouseHost != null && ReferenceEquals(Mouse.Captured, _mouseHost)) Mouse.Capture(null);
            if (moved && IsOpen && _backgroundView?.IsVisible == true) OnCanvasGestureCompleted(null);
            else if (moved) _gestureActive = false;
        }

        private void NavigationMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.StylusDevice != null || e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.None
                || Mouse.Captured != null || _backgroundView == null) return;
            if (!BeginMouseNavigation(e.GetPosition(_backgroundView), e.OriginalSource as DependencyObject)) return;
            if (!Mouse.Capture(_mouseHost, CaptureMode.Element)) { EndMouseNavigation(); return; }
            e.Handled = true;
        }

        private void NavigationMouseMove(object sender, MouseEventArgs e)
        {
            if (_mouseDragOrigin == null) return;
            if (e.StylusDevice != null || e.LeftButton != MouseButtonState.Pressed) { EndMouseNavigation(); return; }
            UpdateMouseNavigation(e.GetPosition(_backgroundView)); e.Handled = true;
        }

        private void NavigationMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || _mouseDragOrigin == null) return;
            EndMouseNavigation(); e.Handled = true;
        }

        private void NavigationMouseLost(object sender, MouseEventArgs e)
        { if (!ReferenceEquals(Mouse.Captured, _mouseHost)) EndMouseNavigation(); }
        private void NavigationMouseDeactivated(object sender, EventArgs e) => EndMouseNavigation();
    }
}
