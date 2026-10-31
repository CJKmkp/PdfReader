using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

internal sealed class TestTouchDevice : TouchDevice, IDisposable
{
    private readonly Visual _coordinates;
    private Point _position;
    private TouchAction _action;
    internal TestTouchDevice(Visual coordinates, int id) : base(id)
    { _coordinates = coordinates; SetActiveSource(PresentationSource.FromVisual(coordinates)); Activate(); }
    public override TouchPoint GetTouchPoint(IInputElement relativeTo)
    {
        Point position = relativeTo is Visual target ? _coordinates.TransformToVisual(target).Transform(_position) : _position;
        return new TouchPoint(this, position, new Rect(position, new Size(4, 4)), _action);
    }
    public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo) => new() { GetTouchPoint(relativeTo) };
    internal TouchEventArgs Send(UIElement host, RoutedEvent routedEvent, Point position, TouchAction action, object source = null)
    {
        _position = position; _action = action;
        var args = new TouchEventArgs(this, Environment.TickCount) { RoutedEvent = routedEvent, Source = source ?? host };
        host.RaiseEvent(args); return args;
    }
    public void Dispose() { Capture(null); Deactivate(); }
}
