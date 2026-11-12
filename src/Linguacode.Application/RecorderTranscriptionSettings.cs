using System.Text.Json;
using Linguacode.Core;
using Linguacode.Audio;

namespace Linguacode.Application;

public sealed record TranscriptLanguageObservation(string Track, string Language, double? Score);

public sealed partial class CallRecorder
{
    public void SetTranscriptionLanguage(string captureId, string language)
    {
        RecordingAnalysis.ValidateOptions(new(Language: language));
        var metadata = Metadata(captureId);
        if (metadata.AnalysisId is { } id && File.Exists(Analysis.Store.FileFor(id, "job.json")))
            throw new UserFacingException("This transcription already has saved work. Its source language is fixed for resume. Use Re-transcribe to start again with another language.");
        metadata.Language = language;
        SaveMetadata(captureId, metadata);
    }

    public void ResetTranscription(string captureId, string language, bool useSpeakerHints = false)
    {
        RecordingAnalysis.ValidateOptions(new(Language: language));
        var metadata = Metadata(captureId);
        var id = metadata.AnalysisId ?? throw new UserFacingException("Transcribe this recording first.");
        var hints = useSpeakerHints ? SpeakerSpellingHints(metadata) : null;
        Analysis.RestartComponent(id, "transcription", language, hints);
        metadata.Language = language;
        SaveMetadata(captureId, metadata);
    }

    public void ResetSpeakers(string captureId)
    {
        var metadata = Metadata(captureId);
        var id = metadata.AnalysisId ?? throw new UserFacingException("Transcribe this recording first.");
        // Clear labels before resetting: on interruption, old names cannot attach to new speaker IDs.
        // The microphone label describes a known source and remains valid.
        var original = new Dictionary<string, string>(metadata.SpeakerNames);
        var revision = Analysis.Store.Read(id).Revision;
        metadata.SpeakerNames = original.Where(p => p.Key == "You").ToDictionary(p => p.Key, p => p.Value);
        SaveMetadata(captureId, metadata);
        try { Analysis.RestartComponent(id, "diarization", metadata.Language); }
        catch
        {
            var job = Analysis.Store.Read(id);
            if (job.Revision == revision)
            { metadata.SpeakerNames = original; SaveMetadata(captureId, metadata); }
            throw;
        }
    }

    public static string? SpeakerSpellingHints(CallMetadata metadata)
    {
        var names = new List<string>(); var length = 0;
        foreach (var name in metadata.SpeakerNames.Values.Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0 || name.Length > 100 || name.Any(char.IsControl)) continue;
            if (length + name.Length + (names.Count == 0 ? 0 : 2) > 1000) break;
            names.Add(name); length += name.Length + (names.Count == 1 ? 0 : 2);
        }
        return names.Count == 0 ? null : string.Join(", ", names);
    }

    // Language detection is chunk-level evidence, not a claim about every sentence.
    public TranscriptLanguageObservation[] TranscriptionLanguages(string captureId)
    {
        var metadata = Metadata(captureId);
        if (metadata.AnalysisId is not { } id || !File.Exists(Analysis.Store.FileFor(id, "job.json"))) return [];
        var job = Analysis.Store.Read(id);
        if (job.Options.Language != "auto") return []; // A selected language's 1.0 is not detection confidence.
        var observations = new List<TranscriptLanguageObservation>();
        foreach (var chunk in job.Chunks)
        {
            var path = Analysis.Store.FileFor(id, $"evidence-{chunk.Index:D6}.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("transcription", out var result) || result.ValueKind != JsonValueKind.Object ||
                    !result.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "succeeded" ||
                    !result.TryGetProperty("language", out var language) || language.ValueKind != JsonValueKind.String ||
                    result.TryGetProperty("noSpeech", out var silence) && silence.ValueKind == JsonValueKind.True) continue;
                double? score = result.TryGetProperty("languageScore", out var value) && value.ValueKind == JsonValueKind.Number &&
                    value.TryGetDouble(out var number) && double.IsFinite(number) && number is >= 0 and <= 1 ? number : null;
                observations.Add(new(chunk.TrackId, language.GetString()!, score));
            }
            catch (Exception ex) when (ex is IOException or JsonException) { /* Partial evidence must not block review. */ }
        }
        return observations.ToArray();
    }
}
