using System.Windows;
using Linguacode.Application;

namespace Linguacode.Desktop;

public partial class MainWindow
{
    private async Task CheckManualTranscription()
    {
        if (Selected is not { } call) throw new InvalidOperationException("Manual transcription check requires a saved fixture.");
        var metadata = recorder.Metadata(call.Capture.Id);
        if (!RetranscribeButton.IsEnabled) throw new InvalidOperationException("Saved analysis has no Re-transcribe action.");
        var targetDialog = new LanguageSelectionDialog(true, "hr", "Synthetic fixture; no provider request.");
        if (targetDialog.SelectedLanguage != "hr") throw new InvalidOperationException("Translation target chooser lost its selection.");
        targetDialog.LanguageInput.SelectedValue = "de";
        if (targetDialog.SelectedLanguage != "de") throw new InvalidOperationException("Translation target chooser did not change language.");
        targetDialog.Close();
        var stop = stopRecording; var prepare = prepareTranscription; var prepared = transcriptionPrepared;
        try
        {
            if (!ReferenceEquals(FindTranscriptTab(RetranscribeButton), DetailsTab) || !TranscriptSourceLanguage.IsEnabled)
                throw new InvalidOperationException("Re-transcribe and its editable source language are not in Details.");
            if (!ReferenceEquals(FindTranscriptTab(RedetectSpeakersButton), DetailsTab) || !ReferenceEquals(FindTranscriptTab(SpeakerHints), DetailsTab))
                throw new InvalidOperationException("Speaker re-detection and spelling hints are not separate Details controls.");
            TranscriptLanguageObservation[] detection = [new("call", "sl", .91)];
            ApplyTranscriptLanguages("smoke-detected", "auto", detection);
            if (SelectedTranscriptionLanguage() != "sl" || !TranscriptSourceLanguage.Text.Contains("91"))
                throw new InvalidOperationException("Source selector did not default to the detected language and score.");
            WorkspaceTabs.SelectedItem = DetailsTab;
            WriteSmokePreview("detected-language-details.png", new Size(984, 681));
            var languages = TranscriptSourceLanguage.Items.Cast<LanguageChoice>().ToArray();
            if (languages.Length != 100 || languages.Select(c => c.Code).Distinct().Count() != 100 ||
                !languages.Any(c => c.Code == "ja") || !languages.Any(c => c.Code == "sw") ||
                languages.Any(c => c.Code == "yue" || c.Name == c.Code))
                throw new InvalidOperationException("Source selector did not expose the complete named Whisper small language list.");
            foreach (var choice in languages) Linguacode.Audio.RecordingAnalysis.ValidateOptions(new(Language: choice.Code));
            var exportY = ExportButton.TranslatePoint(new Point(), RecordingFileActions).Y;
            if (new[] { RawExportButton, DeleteRecordingButton, DeleteAnalysisButton }.Any(button =>
                Math.Abs(button.TranslatePoint(new Point(), RecordingFileActions).Y - exportY) > 1))
                throw new InvalidOperationException("Export and delete actions are not on the same row.");
            TranscriptSourceLanguage.SelectedValue = "hr";
            ApplyTranscriptLanguages("smoke-detected", "auto", detection);
            if (SelectedTranscriptionLanguage() != "hr") throw new InvalidOperationException("Refreshing overwrote the user's next transcription language.");
            TranscriptSourceLanguage.Text = "de";
            ApplyTranscriptLanguages("smoke-detected", "auto", detection);
            if (SelectedTranscriptionLanguage() != "de") throw new InvalidOperationException("Typed source-language code was not retained.");
            ApplyTranscriptLanguages("smoke-other", "auto", detection);
            if (SelectedTranscriptionLanguage() != "sl") throw new InvalidOperationException("Language choice leaked between recordings.");
            RefreshTranscriptSettings();
            var pending = recorder.Metadata(call.Capture.Id); pending.AnalysisId = null; pending.Language = "auto";
            recorder.SaveMetadata(call.Capture.Id, pending);
            activeId = call.Capture.Id; ownsCapture = true;
            stopRecording = _ => Task.FromResult(call.Capture);
            transcriptionPrepared = false;
            prepareTranscription = (_, _) => throw new InvalidOperationException("Stop attempted automatic transcription setup.");
            await Stop();
            if (recorder.Metadata(call.Capture.Id).AnalysisId is not null || activeId is not null || WorkspaceTabs.SelectedItem != ReviewTab ||
                !TranscribeButton.IsEnabled || !TranscriptSourceLanguage.IsEnabled)
                throw new InvalidOperationException("Stopping did not leave saved audio pending in the Transcript tab.");
            if (!ReferenceEquals(FindTranscriptTab(TranscribeButton), ReviewTab))
                throw new InvalidOperationException("Transcribe is not in the Transcript tab.");
            if (!ReferenceEquals(FindTranscriptTab(TranscriptSourceLanguage), DetailsTab))
                throw new InvalidOperationException("Source language settings are not in Details.");
            TranscriptSourceLanguage.SelectedValue = "hr";
            recorder.SetTranscriptionLanguage(call.Capture.Id, SelectedTranscriptionLanguage());
            RefreshLibrary(call.Capture.Id);
            if ((string?)TranscriptSourceLanguage.SelectedValue != "hr") throw new InvalidOperationException("Source language selection was not retained.");
            WriteSmokePreview("manual-transcription.png", new Size(984, 681));
            WorkspaceTabs.SelectedItem = DetailsTab;
            WriteSmokePreview("transcription-language-details.png", new Size(984, 681));
            var description = DescribeDetectedLanguages([new("call", "en", .78), new("mic", "hr", .86)]);
            if (!description.Contains("Call output: English") || !description.Contains("Microphone: Croatian"))
                throw new InvalidOperationException("Detected languages lost source identity.");
        }
        finally
        {
            transcriptionLanguageDrafts.Remove("smoke-detected"); transcriptionLanguageDrafts.Remove("smoke-other");
            stopRecording = stop; prepareTranscription = prepare; transcriptionPrepared = prepared;
            activeId = null; ownsCapture = false;
            recorder.SaveMetadata(call.Capture.Id, metadata); RefreshLibrary(call.Capture.Id);
        }
    }

    private static System.Windows.Controls.TabItem? FindTranscriptTab(DependencyObject current)
    {
        while (current is not null)
        {
            if (current is System.Windows.Controls.TabItem tab) return tab;
            current = LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
