using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Linguacode.Application;

namespace Linguacode.Desktop;

public sealed class TranscriptRow : INotifyPropertyChanged
{
    public TranscriptLine Line { get; }
    public TranscriptRow(TranscriptLine line, string disclosure) { Line = line; TranslationDisclosure = disclosure; }
    public string Id => Line.Id;
    public double Start => Line.Start;
    public double End => Line.End;
    public string SpeakerId => Line.SpeakerId;
    public string Speaker => Line.Speaker;
    public string Text => Line.Text;
    public string Track => Line.Track;
    public string DisplayTime => "[" + TimeLabel(Start) + "]";
    public string Details => $"{TimeLabel(Start)} – {TimeLabel(End)} · {End - Start:0.000} s · {(Track == "mic" ? "Microphone" : Track == "call" ? "Call output" : "Audio")}";
    public string TranslationDisclosure { get; }
    public string TranslationMenuLabel => "Translate to…";
    public string TranslationIdentity { get; init; } = "";
    public static string TimeLabel(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"hh\:mm\:ss\.fff" : @"mm\:ss\.fff");
    private bool playing;
    public bool IsPlaying { get => playing; set { if (playing == value) return; playing = value; PropertyChanged?.Invoke(this, new(nameof(IsPlaying))); } }
    private string translation = "";
    public string Translation { get => translation; set { translation = value; PropertyChanged?.Invoke(this, new(nameof(Translation))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public static bool Contains(TranscriptLine line, double position, int track) => position >= line.Start && position < line.End &&
        (track == 0 || string.IsNullOrEmpty(line.Track) || line.Track == (track == 1 ? "call" : "mic"));
}

public partial class MainWindow
{
    private TranscriptRow[] transcriptRows = [];
    private readonly System.Windows.Threading.DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private string? followedSegment;
    private static bool InsideTranscriptButton(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase) return true;
        return false;
    }
    private void TranscriptTextFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: TranscriptRow row }) TranscriptList.SelectedItem = row;
    }
    private async void CopyPassageClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TranscriptRow row })
            await CopyTranscriptText(row.Text);
    }
    private async void CopyTextClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu { PlacementTarget: TextBox text })
            await CopyTranscriptText(text.Text);
    }
    private Task CopyTranscriptText(string text) => Guard(() =>
    {
        if (!string.IsNullOrEmpty(text)) { Clipboard.SetText(text); StatusText.Text = "Text copied."; }
        return Task.CompletedTask;
    });
    private string FullTranscriptText() => string.Join(Environment.NewLine,
        transcript.Select(row => $"[{TranscriptRow.TimeLabel(row.Start)}] {row.Speaker}: {row.Text}"));
    private void UpdateTranscriptPosition()
    {
        if (TimelineMarkers is null || PlaybackTrack is null || FollowPlayback is null) return;
        var position = PlaybackPosition.Value;
        foreach (var row in transcriptRows) row.IsPlaying = TranscriptRow.Contains(row.Line, position, PlaybackTrack.SelectedIndex);
        var first = transcriptRows.FirstOrDefault(r => r.IsPlaying);
        if (first?.Id != followedSegment)
        {
            followedSegment = first?.Id;
            if (first is not null && playback?.Playing == true && FollowPlayback.IsChecked == true && TranscriptList.Items.Contains(first)) TranscriptList.ScrollIntoView(first);
        }
        TimelineMarkers.Position = position; TimelineMarkers.Track = PlaybackTrack.SelectedIndex; TimelineMarkers.InvalidateVisual();
    }
    private async void TranslateClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (busy || Selected is not { } call || sender is not FrameworkElement { DataContext: TranscriptRow row }) return;
        var dialog = new LanguageSelectionDialog(true, recorder.Preferences.SummaryLanguage,
            $"Sends this passage's transcript text to {recorder.ModelDescription()}. Audio stays local.") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var language = dialog.SelectedLanguage;
        var id = row.Id;
        var result = "";
        row.Translation = "Translating…";
        await Guard(async () =>
        {
            try { await Run(async ct => result = await recorder.Translate(call.Capture.Id, id, language, ct)); }
            catch (Exception ex) { result = ex is OperationCanceledException ? "Translation cancelled." : "Translation failed: " + ex.Message; ErrorSettingsButton.Visibility = Visibility.Visible; }
            if (Selected?.Capture.Id == call.Capture.Id && transcriptRows.FirstOrDefault(r => r.Id == id) is { } current)
            { current.Translation = result; TranscriptList.SelectedItem = current; }
        });
    }
}
