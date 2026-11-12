using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.IO;
using Linguacode.Application;

namespace Linguacode.Desktop;

public partial class MainWindow
{
    private bool scrubbing;
    private string? scrubRecordingId;
    private CancellationTokenSource? waveformCancellation;
    private Task waveformTask = Task.CompletedTask;
    private string? waveformRecordingId, waveformIdentity;
    private bool viewportUpdating;
    private static string PlaybackTime(double seconds) => TranscriptRow.TimeLabel(seconds);

    private void UpdateTimelineLabels()
    {
        if (PlaybackElapsed is null) return;
        PlaybackElapsed.Text = PlaybackTime(PlaybackPosition.Value);
        PlaybackDuration.Text = PlaybackTime(Selected?.Capture.DurationSeconds ?? 0);
        UpdateWaveformPosition();
    }
    private void RefreshWaveform()
    {
        if (Selected is not { } call)
        {
            waveformCancellation?.Cancel(); waveformRecordingId = waveformIdentity = null;
            TimelineMarkers.Data = null; TimelineMarkers.Viewport.Reset(0); ApplyViewport(); return;
        }
        try
        {
            if (call.Capture.State is "Starting" or "Recording" or "Finalizing") return;
            var capture = recorder.ReadCapture(call.Capture.Id);
            var directory = recorder.CaptureDirectory(call.Capture.Id);
            var identity = RecordingWaveforms.Identity(directory, capture);
            if (waveformRecordingId == capture.Id && waveformIdentity == identity) return;
            waveformCancellation?.Cancel();
            waveformCancellation = new();
            if (waveformRecordingId != capture.Id || TimelineMarkers.Viewport.Duration != capture.DurationSeconds)
                TimelineMarkers.Viewport.Reset(capture.DurationSeconds);
            waveformRecordingId = capture.Id; waveformIdentity = identity;
            TimelineMarkers.Data = null; ApplyViewport();
            WaveformStatus.Text = "Loading waveform…";
            waveformTask = LoadWaveform(directory, capture, waveformCancellation);
        }
        catch (Exception ex) { WaveformStatus.Text = "Waveform unavailable: " + ex.Message; }
    }
    private async Task LoadWaveform(string directory, Linguacode.Audio.CaptureManifest capture, CancellationTokenSource cancellation)
    {
        try
        {
            var progress = new Progress<double>(fraction => {
                if (waveformCancellation == cancellation && !cancellation.IsCancellationRequested && Selected?.Capture.Id == capture.Id)
                    WaveformStatus.Text = $"Loading waveform… {fraction:P0}";
            });
            var data = await RecordingWaveforms.LoadAsync(directory, capture, Path.Combine(recorder.DataDirectory, "desktop", "cache", "waveforms"), cancellation.Token, progress);
            if (cancellation.IsCancellationRequested || Selected?.Capture.Id != capture.Id) return;
            TimelineMarkers.Data = data;
            WaveformStatus.Text = data.Coverage.Any(c => c.Kind is not ("IntentionalMute" or "InferredSilence")) ? "Audio warnings · hover for details" : "";
            TimelineMarkers.InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested && Selected?.Capture.Id == capture.Id)
            { waveformIdentity = null; WaveformStatus.Text = "Waveform unavailable: " + ex.Message; TimelineMarkers.InvalidateVisual(); }
        }
        finally { if (waveformCancellation == cancellation) waveformCancellation = null; cancellation.Dispose(); }
    }
    private void ApplyViewport()
    {
        if (TimelineScroll is null) return;
        var view = TimelineMarkers.Viewport;
        TimelineMarkers.ViewStart = view.Start; TimelineMarkers.ViewEnd = view.End;
        viewportUpdating = true;
        TimelineScroll.Maximum = Math.Max(0, view.Duration - view.Span);
        TimelineScroll.ViewportSize = view.Span; TimelineScroll.LargeChange = view.Span * .8;
        TimelineScroll.SmallChange = Math.Max(.1, view.Span / 10); TimelineScroll.Value = view.Start;
        TimelineScroll.Visibility = view.Zoomed ? Visibility.Visible : Visibility.Hidden;
        viewportUpdating = false;
        ZoomInButton.IsEnabled = view.Span > Math.Min(5, view.Duration);
        ZoomOutButton.IsEnabled = view.Zoomed;
        TimelineMarkers.PlayheadVisible = PlaybackPosition.Value >= view.Start && PlaybackPosition.Value <= view.End;
        TimelineMarkers.InvalidateVisual();
    }
    private void UpdateWaveformPosition()
    {
        if (TimelineMarkers is null || PlaybackPosition is null) return;
        TimelineMarkers.Position = PlaybackPosition.Value;
        TimelineMarkers.SelectedPassage = (TranscriptList?.SelectedItem as TranscriptRow)?.Line;
        if (!scrubbing && playback?.Playing == true && FollowPlayback.IsChecked == true)
            TimelineMarkers.Viewport.Reveal(PlaybackPosition.Value);
        ApplyViewport();
    }
    private void ZoomInClick(object sender, RoutedEventArgs e) { TimelineMarkers.Viewport.Zoom(.5, PlaybackPosition.Value); ApplyViewport(); }
    private void ZoomOutClick(object sender, RoutedEventArgs e) { TimelineMarkers.Viewport.Zoom(2, PlaybackPosition.Value); ApplyViewport(); }
    private void FitTimelineClick(object sender, RoutedEventArgs e) { TimelineMarkers.Viewport.Fit(); ApplyViewport(); }
    private void TimelineScrollChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!viewportUpdating) { TimelineMarkers.Viewport.Pan(e.NewValue); ApplyViewport(); } }
    private Task CommitPlaybackPosition(double seconds)
    {
        EnsurePlayback();
        if (playback is not null)
        {
            playback.Seek(seconds);
            sliderUpdating = true; PlaybackPosition.Value = playback.Position; sliderUpdating = false;
        }
        TimelineMarkers.Viewport.Reveal(seconds);
        UpdateTimelineLabels(); UpdateTranscriptPosition();
        return Task.CompletedTask;
    }
    private void PlaybackDragStarted(object sender, DragStartedEventArgs e)
    { scrubbing = true; scrubRecordingId = Selected?.Capture.Id; }
    private async void PlaybackDragCompleted(object sender, DragCompletedEventArgs e)
    {
        scrubbing = false;
        if (scrubRecordingId != Selected?.Capture.Id || activeId is not null) return;
        if (e.Canceled)
        {
            sliderUpdating = true; PlaybackPosition.Value = playback?.Position ?? 0; sliderUpdating = false;
            UpdateTimelineLabels(); UpdateTranscriptPosition();
            return;
        }
        await Guard(() => CommitPlaybackPosition(PlaybackPosition.Value));
    }
    private async void SkipBackClick(object sender, RoutedEventArgs e) => await Guard(() =>
        CommitPlaybackPosition(Math.Max(0, (playback?.Position ?? PlaybackPosition.Value) - 10)));
    private async void SkipForwardClick(object sender, RoutedEventArgs e) => await Guard(() =>
        CommitPlaybackPosition(Math.Min(Selected?.Capture.DurationSeconds ?? 0, (playback?.Position ?? PlaybackPosition.Value) + 10)));
    private void PlaybackMouseMove(object sender, MouseEventArgs e)
    {
        if (PlaybackPosition.Template.FindName("PART_Track", PlaybackPosition) is not Track track) return;
        var seconds = track.ValueFromPoint(e.GetPosition(track));
        if (double.IsFinite(seconds)) PlaybackPosition.ToolTip = TimelineMarkers.HoverText(new Point(TimelineMarkers.X(Math.Clamp(seconds, 0, PlaybackPosition.Maximum)), 40));
    }
}
