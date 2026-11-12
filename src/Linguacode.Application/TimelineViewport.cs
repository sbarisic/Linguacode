namespace Linguacode.Application;

/// <summary>Time-domain viewport, independent of audio and WPF layout.</summary>
public sealed class TimelineViewport
{
    public double Duration { get; private set; }
    public double Start { get; private set; }
    public double Span { get; private set; }
    public double End => Start + Span;
    public bool Zoomed => Span < Duration;
    public void Reset(double duration) { Duration = Math.Max(0, duration); Fit(); }
    public void Fit() { Start = 0; Span = Duration; }
    public void Pan(double start) => Start = Math.Clamp(start, 0, Math.Max(0, Duration - Span));
    public void Zoom(double factor, double position)
    {
        var center = position >= Start && position <= End ? position : Start + Span / 2;
        Span = Math.Clamp(Span * factor, Math.Min(5, Duration), Duration);
        Pan(center - Span / 2);
    }
    public void Reveal(double position)
    { if (position < Start || position > End) Pan(position - Span / 2); }
    public double TimeAt(double pixel, double width) => Start + Math.Clamp(pixel / Math.Max(1, width), 0, 1) * Span;
    public double TickStep(double width)
    {
        var minimum = Span * 70 / Math.Max(1, width);
        foreach (var step in new double[] { .1, .2, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800, 86400 })
            if (step >= minimum) return step;
        return Math.Ceiling(minimum / 86400) * 86400;
    }
}
