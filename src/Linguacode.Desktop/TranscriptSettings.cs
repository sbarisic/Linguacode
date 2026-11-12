using System.Windows;
using Linguacode.Application;

namespace Linguacode.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<string, string> transcriptionLanguageDrafts = [];
    private bool refreshingTranscriptLanguage;
    private string? transcriptLanguageKey;

    private static List<LanguageChoice> SourceLanguageChoices() =>
        [new("auto", "Auto detect"), .. Linguacode.Audio.AudioTool.WhisperSmallLanguages
            .Select(code => new LanguageChoice(code, SourceLanguageName(code))).OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase)];

    private static string SourceLanguageName(string code) => code switch
    {
        "jw" => "Javanese", "tl" => "Tagalog", "nn" => "Norwegian Nynorsk", "no" => "Norwegian",
        "ht" => "Haitian Creole", "zh" => "Chinese", _ => CallRecorder.TranslationLanguageName(code)
    };

    private void RefreshTranscriptSettings()
    {
        var language = Selected?.Analysis?.Options.Language ?? Selected?.Metadata.Language ?? recorder.Preferences.Language;
        var observations = Selected is { } call ? recorder.TranscriptionLanguages(call.Capture.Id) : [];
        ApplyTranscriptLanguages(Selected is { } selected ? selected.Capture.Id + ":" + selected.Analysis?.Id : null, language, observations);
    }

    private void ApplyTranscriptLanguages(string? key, string language, TranscriptLanguageObservation[] observations)
    {
        var choices = SourceLanguageChoices();
        var detected = observations.GroupBy(o => o.Language).OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Average(o => o.Score ?? 0)).ThenBy(g => g.Key).ToArray();
        foreach (var group in detected)
        {
            var choice = new LanguageChoice(group.Key, SourceLanguageName(group.Key) + DetectionScore(group));
            var index = choices.FindIndex(c => c.Code == group.Key);
            if (index < 0) choices.Add(choice); else choices[index] = choice;
        }
        var selected = key is not null && transcriptionLanguageDrafts.TryGetValue(key, out var draft) ? draft
            : language == "auto" && detected.Length > 0 ? detected[0].Key : language;
        if (!choices.Any(c => c.Code == selected)) choices.Add(new(selected, CallRecorder.TranslationLanguageName(selected)));
        refreshingTranscriptLanguage = true;
        try
        {
            transcriptLanguageKey = key;
            TranscriptSourceLanguage.ItemsSource = choices;
            TranscriptSourceLanguage.SelectedValue = selected;
            TranscriptSourceLanguage.ToolTip = "Used by Transcribe or Re-transcribe. Resume keeps the original job language. Select Auto detect to detect again, or type a supported language code. " +
                (observations.Length > 0 ? "Detected from saved audio chunks: " + DescribeDetectedLanguages(observations) +
                    ". Percentages are language-detection scores, not word accuracy. With multiple languages, the most frequent detected language is suggested."
                    : language != "auto" ? "The previous language was selected manually; no detection probability is available." : "No detected language is available yet.");
        }
        finally { refreshingTranscriptLanguage = false; }
    }

    private void TranscriptLanguageChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!refreshingTranscriptLanguage && transcriptLanguageKey is { } key)
            transcriptionLanguageDrafts[key] = SelectedTranscriptionLanguage();
    }

    private static string DetectionScore(IEnumerable<TranscriptLanguageObservation> observations)
    {
        var scores = observations.Where(o => o.Score.HasValue).Select(o => o.Score!.Value).ToArray();
        return scores.Length == 0 ? " (detected)" : scores.Min() == scores.Max() ? $" ({scores[0]:P0})" : $" ({scores.Min():P0}–{scores.Max():P0})";
    }
    private static string DescribeDetectedLanguages(TranscriptLanguageObservation[] observations)
    {
        if (observations.Length == 0) return "Language will be detected when transcription runs.";
        return string.Join(" · ", observations.GroupBy(o => (o.Track, o.Language)).Select(group =>
        {
            var source = group.Key.Track == "mic" ? "Microphone" : group.Key.Track == "call" ? "Call output" : "Audio";
            return $"{source}: {CallRecorder.TranslationLanguageName(group.Key.Language)}{DetectionScore(group)}";
        }));
    }

    private string SelectedTranscriptionLanguage() => TranscriptSourceLanguage.SelectedItem is LanguageChoice choice && TranscriptSourceLanguage.Text == choice.Name
        ? choice.Code : TranscriptSourceLanguage.Text.Trim().ToLowerInvariant();
}
