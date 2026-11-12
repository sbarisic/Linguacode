using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Linguacode.Desktop;

// Keep Slider keyboard/automation behavior, but let the entire waveform start a scrub.
public sealed class TimelineSeekSlider : Slider
{
    private bool seeking;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (!IsEnabled || !CaptureMouse()) return;
        Focus();
        BeginSeek(ValueAt(e));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (seeking) PreviewSeek(ValueAt(e));
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!seeking) return;
        PreviewSeek(ValueAt(e));
        FinishSeek(false);
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (seeking && e.Key == Key.Escape) { FinishSeek(true); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (seeking) FinishSeek(true);
        base.OnLostMouseCapture(e);
    }

    private double ValueAt(MouseEventArgs e) => Template.FindName("PART_Track", this) is Track track
        ? track.ValueFromPoint(e.GetPosition(track)) : Value;

    internal void BeginSeek(double value)
    {
        seeking = true;
        RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        PreviewSeek(value);
    }

    internal void PreviewSeek(double value)
    {
        if (!seeking || !double.IsFinite(value)) return;
        var track = Template.FindName("PART_Track", this) as Track;
        SetCurrentValue(ValueProperty, Math.Clamp(value, track?.Minimum ?? Minimum, track?.Maximum ?? Maximum));
    }

    internal void FinishSeek(bool canceled)
    {
        if (!seeking) return;
        seeking = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        RaiseEvent(new DragCompletedEventArgs(0, 0, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
    }
}
