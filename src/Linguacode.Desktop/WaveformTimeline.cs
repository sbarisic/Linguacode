using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using Linguacode.Application;

namespace Linguacode.Desktop;

/// <summary>Only the data visualization is drawn. Transport, range input and scrolling use WPF controls.</summary>
public sealed class WaveformTimeline : FrameworkElement
{
    public static readonly DependencyProperty ViewStartProperty = DependencyProperty.Register(nameof(ViewStart), typeof(double), typeof(WaveformTimeline), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewEndProperty = DependencyProperty.Register(nameof(ViewEnd), typeof(double), typeof(WaveformTimeline), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double ViewStart { get => (double)GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewEnd { get => (double)GetValue(ViewEndProperty); set => SetValue(ViewEndProperty, value); }
    public static readonly DependencyProperty PlayheadVisibleProperty = DependencyProperty.Register(nameof(PlayheadVisible), typeof(bool), typeof(WaveformTimeline), new PropertyMetadata(true));
    public bool PlayheadVisible { get => (bool)GetValue(PlayheadVisibleProperty); set => SetValue(PlayheadVisibleProperty, value); }
    public TimelineViewport Viewport { get; } = new();
    public RecordingWaveform? Data { get; set; }
    public TranscriptLine[] Rows { get; set; } = [];
    public double Duration { get; set; }
    public double Position { get; set; }
    public int Track { get; set; }
    public TranscriptLine? SelectedPassage { get; set; }
    public Action<TranscriptLine>? Selected { get; set; }
    public Action<double>? Seek { get; set; }
    private Geometry? wave, markers, missing, muted;
    private RecordingWaveform? amplitudeData;
    private int amplitudeTrack = -1;
    private double amplitudeScale = 1;
    private (RecordingWaveform? Data, TranscriptLine[] Rows, double Start, double End, double Width, int Track) cached;
    private const double Inset = 9, WaveTop = 25, WaveBottom = 77, LaneTop = 84, RulerTop = 108;
    private double PlotWidth => Math.Max(1, ActualWidth - Inset * 2);
    public double X(double time) => Inset + (time - ViewStart) / Math.Max(.000001, ViewEnd - ViewStart) * PlotWidth;
    public double TimeAt(double x) => Viewport.TimeAt(x - Inset, PlotWidth);
    private Brush Brush(string key) => (Brush)FindResource(key);
    private FormattedText Text(string value, double size = 11) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), size, Brush("Recorder.Text"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private bool Included(string track) => Track == 0 || string.IsNullOrEmpty(track) || track == (Track == 1 ? "call" : "mic");
    private static string Source(string track) => track == "mic" ? "Microphone" : "Call output";

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth <= 18 || Duration <= 0) return;
        if (cached != (Data, Rows, ViewStart, ViewEnd, ActualWidth, Track)) BuildGeometry();
        var center = (WaveTop + WaveBottom) / 2;
        dc.PushClip(new RectangleGeometry(new Rect(Inset, WaveTop, PlotWidth, WaveBottom - WaveTop)));
        if (SelectedPassage is { } selected && Included(selected.Track))
            dc.DrawRoundedRectangle(Brush("Recorder.Selection"), null,
                new Rect(X(selected.Start), WaveTop, Math.Max(1, X(selected.End) - X(selected.Start)), WaveBottom - WaveTop), 5, 5);
        dc.DrawLine(new Pen(Brush("Recorder.SeekTrack"), 1), new(Inset, center), new(ActualWidth - Inset, center));
        if (wave is not null)
        {
            dc.DrawGeometry(null, new Pen(Brush("Recorder.Muted"), 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, wave);
            dc.PushClip(new RectangleGeometry(new Rect(Inset, WaveTop, Math.Clamp(X(Position) - Inset, 0, PlotWidth), WaveBottom - WaveTop)));
            dc.DrawGeometry(null, new Pen(Brush("Recorder.Icon"), 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, wave);
            dc.Pop();
        }
        dc.Pop();
        dc.DrawGeometry(Brush("Recorder.Border"), null, markers);
        dc.DrawGeometry(Brush("Recorder.Muted"), null, muted);
        dc.DrawGeometry(Brush("Recorder.Warning"), null, missing);
        var captionRight = Inset;
        if (SelectedPassage is { } passage && Included(passage.Track) && passage.End > ViewStart && passage.Start < ViewEnd)
        {
            var label = Text(passage.Speaker + " · " + Source(passage.Track));
            label.MaxTextWidth = Math.Max(1, PlotWidth * .5); label.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(label, new(Inset, 3));
            captionRight = Inset + label.WidthIncludingTrailingWhitespace + 8;
            var left = Math.Max(Inset, X(passage.Start)); var right = Math.Min(ActualWidth - Inset, X(passage.End));
            dc.DrawRoundedRectangle(Brush("Recorder.Icon"), null, new Rect(left, passage.Track == "mic" ? LaneTop + 10 : LaneTop, Math.Max(1, right - left), 5), 2, 2);
        }
        DrawRuler(dc);
        if (Position >= ViewStart && Position <= ViewEnd)
        {
            var x = X(Position);
            dc.DrawLine(new Pen(Brush("Recorder.Icon"), 2), new(x, WaveTop), new(x, WaveBottom));
            var label = Text(TranscriptRow.TimeLabel(Position), 12);
            label.SetForegroundBrush(Brush("Recorder.Accent"));
            // Keep both labels readable when the playhead is over the speaker caption.
            var boxX = Math.Clamp(x - label.Width / 2 - 5, Inset, Math.Max(Inset, ActualWidth - Inset - label.Width - 10));
            if (boxX < captionRight) boxX = Math.Max(boxX, ActualWidth - Inset - label.Width - 10);
            dc.DrawRoundedRectangle(Brush("Recorder.Selection"), null, new Rect(boxX, 0, label.Width + 10, 23), 4, 4);
            dc.DrawText(label, new(boxX + 5, 3));
        }
    }

    private void DrawRuler(DrawingContext dc)
    {
        var pen = new Pen(Brush("Recorder.Muted"), 1);
        dc.DrawLine(pen, new(Inset, RulerTop), new(ActualWidth - Inset, RulerTop));
        var step = Viewport.TickStep(PlotWidth);
        var lastRight = double.NegativeInfinity;
        for (var time = Math.Ceiling(ViewStart / step) * step; time <= ViewEnd + .000001; time += step)
        {
            var x = X(time); dc.DrawLine(pen, new(x, RulerTop), new(x, RulerTop + 4));
            var span = TimeSpan.FromSeconds(time);
            var label = Text(span.ToString(time >= 3600 ? @"hh\:mm\:ss" : step < 1 ? @"mm\:ss\.f" : @"mm\:ss"));
            var left = Math.Clamp(x - label.Width / 2, Inset, Math.Max(Inset, ActualWidth - Inset - label.Width));
            if (left >= lastRight + 8) { dc.DrawText(label, new(left, RulerTop + 5)); lastRight = left + label.Width; }
        }
    }

    private void BuildGeometry()
    {
        cached = (Data, Rows, ViewStart, ViewEnd, ActualWidth, Track);
        var waves = new StreamGeometry(); var annotations = new StreamGeometry();
        var gaps = new StreamGeometry(); var mutes = new StreamGeometry();
        using (var ctx = waves.Open())
        {
            if (Data is not null)
            {
                var bins = Data.ForTrack(Track);
                if (amplitudeData != Data || amplitudeTrack != Track)
                {
                    amplitudeData = Data; amplitudeTrack = Track;
                    amplitudeScale = 24 / Math.Max(.01, bins.IsEmpty ? 0 : bins.Max(p => Math.Max(Math.Abs(p.Min), Math.Abs(p.Max))));
                }
                var columns = Math.Max(1, (int)(PlotWidth / 3));
                for (var column = 0; column < columns; column++)
                {
                    var start = (int)Math.Floor((ViewStart + (ViewEnd - ViewStart) * column / columns) / RecordingWaveform.BinSeconds);
                    var end = (int)Math.Ceiling((ViewStart + (ViewEnd - ViewStart) * (column + 1) / columns) / RecordingWaveform.BinSeconds);
                    float min = 0, max = 0;
                    for (var bin = Math.Max(0, start); bin < Math.Min(bins.Length, end); bin++)
                    { min = Math.Min(min, bins[bin].Min); max = Math.Max(max, bins[bin].Max); }
                    var x = Inset + (column + .5) * PlotWidth / columns;
                    var center = (WaveTop + WaveBottom) / 2;
                    ctx.BeginFigure(new(x, center - max * amplitudeScale), false, false);
                    ctx.LineTo(new(x, center - min * amplitudeScale + .5), true, false);
                }
            }
        }
        // Rasterize interval occupancy at display resolution: repaint cost is independent of call length.
        var width = Math.Max(1, (int)Math.Ceiling(PlotWidth));
        var lanes = new bool[2, width]; var gapLanes = new bool[2, width]; var muteLanes = new bool[2, width];
        void Mark(bool[,] target, double start, double end, string track)
        {
            if (!Included(track) || end <= ViewStart || start >= ViewEnd) return;
            var left = Math.Clamp((int)Math.Floor(X(start) - Inset), 0, width - 1);
            var right = Math.Clamp((int)Math.Ceiling(X(end) - Inset), left + 1, width);
            for (var i = left; i < right; i++) target[track == "mic" ? 1 : 0, i] = true;
        }
        foreach (var row in Rows) Mark(lanes, row.Start, row.End, row.Track);
        if (Data is not null)
            foreach (var item in Data.Coverage)
                if (item.Kind != "InferredSilence") Mark(item.Kind == "IntentionalMute" ? muteLanes : gapLanes, item.Start, item.End, item.Track);
        void DrawRuns(StreamGeometry geometry, bool[,] pixels, bool coverage)
        {
            using var ctx = geometry.Open();
            for (var lane = 0; lane < 2; lane++)
            for (var i = 0; i < width; i++)
            {
                if (!pixels[lane, i]) continue;
                var start = i; while (i + 1 < width && pixels[lane, i + 1]) i++;
                var top = coverage ? WaveBottom + 3 + lane * 2 : LaneTop + lane * 10;
                var height = coverage ? 1.5 : 5;
                ctx.BeginFigure(new(Inset + start, top), true, true);
                ctx.LineTo(new(Inset + i + 1, top), true, false); ctx.LineTo(new(Inset + i + 1, top + height), true, false); ctx.LineTo(new(Inset + start, top + height), true, false);
            }
        }
        DrawRuns(annotations, lanes, false); DrawRuns(gaps, gapLanes, true); DrawRuns(mutes, muteLanes, true);
        foreach (var g in new[] { waves, annotations, gaps, mutes }) g.Freeze();
        wave = waves; markers = annotations; missing = gaps; muted = mutes;
    }
    internal TranscriptLine[] HitPassages(Point point) => point.Y < LaneTop || point.Y > LaneTop + 17 ? [] : Rows
        .Where(r => Included(r.Track) && (r.Track == "mic") == (point.Y >= LaneTop + 8) && TimeAt(point.X) >= r.Start && TimeAt(point.X) < r.End)
        .OrderBy(r => r.End - r.Start).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
    internal TranscriptLine? HitPassage(Point point) => HitPassages(point).FirstOrDefault();
    public string HoverText(Point point)
    {
        var passages = HitPassages(point);
        if (passages.Length > 0) return string.Join("\n\n", passages.Select(row => $"{row.Speaker} · {Source(row.Track)}\n{TranscriptRow.TimeLabel(row.Start)} – {TranscriptRow.TimeLabel(row.End)}\n{row.Text}"));
        var time = TimeAt(point.X);
        var coverage = Data?.Coverage.Where(c => Included(c.Track) && time >= c.Start && time < c.End)
            .Select(c => Source(c.Track) + ": " + (c.Kind == "InferredSilence" ? "Inferred silence" : c.Kind == "IntentionalMute" ? "Recorder microphone muted" : "Missing or uncertain audio (" + c.Kind + ")"));
        return "Seek to " + TranscriptRow.TimeLabel(time) + (coverage is null ? "" : "\n" + string.Join("\n", coverage));
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); ToolTip = HoverText(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var rows = HitPassages(e.GetPosition(this));
        if (rows.Length == 1) Selected?.Invoke(rows[0]);
        else if (rows.Length > 1)
        {
            // Simultaneous speakers on the same source remain individually reachable.
            var menu = new ContextMenu { PlacementTarget = this };
            foreach (var row in rows)
            {
                var item = new MenuItem { Header = $"{row.Speaker} · {TranscriptRow.TimeLabel(row.Start)} – {TranscriptRow.TimeLabel(row.End)}", ToolTip = row.Text };
                item.Click += (_, _) => Selected?.Invoke(row); menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }
        else Seek?.Invoke(TimeAt(e.GetPosition(this).X));
        e.Handled = true;
    }
}
