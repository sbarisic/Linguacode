using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Linguacode.Desktop;

public partial class MainWindow
{
    private async Task CheckWaveformPresentation()
    {
        var view = (FrameworkElement)Content;
        var size = new Size(984, 681);
        view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
        // The context menu must use the clicked passage, even if another row is selected.
        static T? Descendant<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T candidate && match(candidate)) return candidate;
                if (Descendant(child, match) is { } found) return found;
            }
            return null;
        }
        var menuHost = Descendant<StackPanel>(TranscriptList, panel => panel.ContextMenu is not null)
            ?? throw new InvalidOperationException("Transcript has no rename context menu.");
        var menu = menuHost.ContextMenu;
        menu.PlacementTarget = menuHost;
        if (menu.Items[0] is not MenuItem { DataContext: TranscriptRow menuRow } ||
            !ReferenceEquals(menuRow, menuHost.DataContext))
            throw new InvalidOperationException("Rename menu did not target its passage.");
        var rename = new RenameSpeakerDialog(menuRow.Speaker);
        if (rename.SpeakerName != menuRow.Speaker || !rename.SaveButton.IsEnabled)
            throw new InvalidOperationException("Rename dialog lost the current display name.");
        rename.NameInput.Text = " ";
        if (rename.SaveButton.IsEnabled) throw new InvalidOperationException("Rename allowed an empty name.");
        rename.Close();
        var passageText = Descendant<TextBox>(TranscriptList, text => text.DataContext is TranscriptRow row && text.Text == row.Text)
            ?? throw new InvalidOperationException("Transcript passage is not selectable text.");
        if (!passageText.IsReadOnly || !InsideTranscriptButton(passageText))
            throw new InvalidOperationException("Selectable transcript text is editable or starts playback on double-click.");
        passageText.Select(0, Math.Min(9, passageText.Text.Length));
        if (passageText.SelectedText != passageText.Text[..passageText.SelectionLength] || passageText.SelectionLength == 0)
            throw new InvalidOperationException("Transcript text selection failed.");
        var textMenu = passageText.ContextMenu;
        textMenu.PlacementTarget = passageText;
        if (textMenu.Items[0] is not MenuItem { Command: { } copy, CommandTarget: { } copyTarget } ||
            copy != System.Windows.Input.ApplicationCommands.Copy || !ReferenceEquals(copyTarget, passageText) || !System.Windows.Input.ApplicationCommands.Copy.CanExecute(null, copyTarget))
            throw new InvalidOperationException("Transcript Copy command did not target selected text.");
        if (!FullTranscriptText().Contains("[00:01.000] Speaker 1: Synthetic", StringComparison.Ordinal) ||
            !FullTranscriptText().Contains("[00:03.000] You:", StringComparison.Ordinal))
            throw new InvalidOperationException("Copy transcript lost timestamps or display names.");
        SetControls();
        if (!CopySummaryButton.IsEnabled || CopySummaryButton.Content.ToString() != "Copy transcript")
            throw new InvalidOperationException("Transcript tab did not expose Copy transcript.");
        passageText.Select(0, 0);
        var scrollBar = Descendant<ScrollBar>(TranscriptList, bar => bar.Orientation == Orientation.Vertical);
        if (scrollBar?.Style?.BasedOn != System.Windows.Application.Current.FindResource("DefaultScrollBarStyle"))
            throw new InvalidOperationException("Transcript scrollbar fell back to a classic template.");
        TimelineMarkers.Viewport.Zoom(.5, 5); ApplyViewport(); view.UpdateLayout();
        PlaybackPosition.ApplyTemplate();
        if (PlaybackPosition.Template.FindName("PART_Track", PlaybackPosition) is not Track track ||
            track.Minimum != TimelineMarkers.Viewport.Start || track.Maximum != TimelineMarkers.Viewport.End)
            throw new InvalidOperationException("Native seek track does not use the visible time range.");
        var mapped = track.ValueFromPoint(new Point(track.ActualWidth / 2, 10));
        if (Math.Abs(mapped - (TimelineMarkers.Viewport.Start + TimelineMarkers.Viewport.Span / 2)) > .001)
            throw new InvalidOperationException("Waveform seeking does not match its ruler.");
        var peer = new SliderAutomationPeer(PlaybackPosition);
        var range = (IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)!;
        if (range.Maximum != Selected!.Capture.DurationSeconds || range.SmallChange != .1 || peer.GetName() != "Playback position")
            throw new InvalidOperationException("Accessible seek range lost the full call duration.");
        range.SetValue(4.5);
        if (PlaybackPosition.Value != 4.5 || PlaybackElapsed.Text != "00:04.500") throw new InvalidOperationException("Accessible seeking failed.");
        await CommitPlaybackPosition(5);
        var start = TimelineMarkers.Viewport.Start;
        if (TimelineMarkers.HitPassage(new Point(TimelineMarkers.X(3.5), 86))?.Track != "call" ||
            TimelineMarkers.HitPassage(new Point(TimelineMarkers.X(3.5), 96))?.Track != "mic")
            throw new InvalidOperationException("Overlapping passages cannot be selected independently.");
        var rows = TimelineMarkers.Rows;
        try
        {
            TimelineMarkers.Rows = [.. rows, rows[0] with { Id = "overlapping-remote", Speaker = "Second remote speaker" }];
            if (TimelineMarkers.HitPassages(new Point(TimelineMarkers.X(3.5), 86)).Length != 2)
                throw new InvalidOperationException("Same-source overlap hid a speaker passage.");
        }
        finally { TimelineMarkers.Rows = rows; }
        // Synthetic fixtures only: no microphone, playback hardware, desktop focus or model requests.
        foreach (var minimum in new[] { false, true })
        foreach (var scale in new[] { 1d, 1.5, 2d })
        {
            var dimensions = minimum ? new Size(884, 601) : new Size(1264, 861);
            WriteSmokePreview($"waveform-light-{(minimum ? "minimum" : "normal")}-{scale * 100:0}.png", dimensions, scale);
            if (minimum)
            {
                if (TranscriptList.ActualHeight < 60) throw new InvalidOperationException("Minimum-height layout hid the transcript.");
                ReviewScroll.ScrollToBottom();
                WriteSmokePreview($"waveform-light-minimum-bottom-{scale * 100:0}.png", dimensions, scale);
                ReviewScroll.ScrollToTop();
            }
        }
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var original = dictionaries[0];
        try
        {
            dictionaries[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml") };
            RecorderTheme.Apply(true);
            foreach (var minimum in new[] { false, true })
            foreach (var scale in new[] { 1d, 1.5, 2d })
                WriteSmokePreview($"waveform-dark-{(minimum ? "minimum" : "normal")}-{scale * 100:0}.png", minimum ? new Size(884, 601) : new Size(1264, 861), scale);
            var app = System.Windows.Application.Current;
            var keys = new[] { "Recorder.Background", "Recorder.Surface", "Recorder.Text", "Recorder.Muted", "Recorder.Border", "Recorder.Icon", "Recorder.Accent", "Recorder.Selection", "Recorder.SeekTrack" };
            var before = keys.ToDictionary(k => k, k => app.Resources[k]);
            try
            {
                foreach (var key in keys) app.Resources[key] = key is "Recorder.Background" or "Recorder.Surface" or "Recorder.Selection" ? Brushes.Black : key is "Recorder.Icon" or "Recorder.Accent" ? Brushes.Yellow : Brushes.White;
                foreach (var minimum in new[] { false, true })
                foreach (var scale in new[] { 1d, 1.5, 2d })
                    WriteSmokePreview($"waveform-contrast-{(minimum ? "minimum" : "normal")}-{scale * 100:0}.png", minimum ? new Size(884, 601) : new Size(1264, 861), scale);
            }
            finally { foreach (var pair in before) app.Resources[pair.Key] = pair.Value; }
        }
        finally { dictionaries[0] = original; RecorderTheme.Apply(); }
        if (TimelineMarkers.Viewport.Start != start) throw new InvalidOperationException("Resizing or theme changes reset the viewport.");
        FitTimelineClick(this, new RoutedEventArgs());
    }
}
