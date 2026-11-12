using System.Text.Json.Nodes;
using Linguacode.Core;

namespace Linguacode.Audio;

public sealed partial class RecordingAnalysis
{
    public void RestartComponent(string id, string component, string language, string? spellingHints = null)
    {
        if (component is not ("transcription" or "diarization")) throw new ArgumentException("Unsupported restart component.");
        using var lease = Store.Lease(id);
        var job = Store.Read(id);
        if (job.ResetPending) ApplyComponentReset(job);
        if (!job.Prepared) throw new UserFacingException("Finish preparing this recording before re-running analysis.");
        var options = job.Options with { NoSummary = true, Language = component == "transcription" ? language : job.Options.Language,
            TranscriptionHints = component == "transcription" ? spellingHints : job.Options.TranscriptionHints };
        ValidateOptions(options);
        var preparation = RecordingAnalysisStore.ReadJson<JsonObject>(Store.FileFor(id, "preparation.json"));
        if (RecordingAnalysisStore.Hash(Store.FileFor(id, "normalized.f32")) != preparation["hash"]?.ToString())
            throw new IOException("Prepared audio changed; cannot re-run analysis.");
        var installation = backend.Status(data);
        if (component == "transcription" ? !installation.Transcription : !installation.Diarization)
            throw new UserFacingException("Prepare the local audio engines before re-running analysis.");
        using var installationLease = LocalAudioInstallation.Use(installation.Location);
        var helper = Path.Combine(installation.Location, "helper");
        var manifest = RecordingAnalysisStore.ReadJson<JsonObject>(Path.Combine(helper, "downloads.json"));
        if (!string.IsNullOrEmpty(options.TranscriptionHints) && component == "transcription" && manifest["transcriptionHints"]?.GetValue<int>() != 1)
            throw new UserFacingException("The local transcription helper needs updating to use spelling hints. Restart the recorder to prepare it.");
        // Save intent first. A crash during reset resumes this idempotent cleanup before any inference.
        job.ResetPreviousInstallation = job.Installation; job.ResetPreviousFingerprint = job.Fingerprint;
        job.Installation = Path.GetFileName(installation.Location); job.Fingerprint = LocalAudioInstallation.Fingerprint(helper);
        job.Options = options; job.ComponentScope = component; job.Version = 3; job.Revision++;
        if (component == "diarization") job.SpeakerNames = job.SpeakerNames.Where(p => p.Key == "You").ToDictionary(p => p.Key, p => p.Value);
        job.ResetPending = true; job.State = "Paused"; job.Stage = "Reset"; job.Error = null;
        Store.Save(job);
        ApplyComponentReset(job);
    }

    private void ApplyComponentReset(RecordingAnalysisJob job)
    {
        var component = job.ComponentScope;
        if (component is not ("transcription" or "diarization")) throw new IOException("Invalid pending component reset.");
        foreach (var chunk in job.Chunks)
        {
            var path = Store.FileFor(job.Id, $"evidence-{chunk.Index:D6}.json");
            if (File.Exists(path))
            {
                var evidence = RecordingAnalysisStore.ReadJson<JsonObject>(path);
                var provenance = evidence["componentSources"] as JsonObject ?? new JsonObject();
                foreach (var retained in chunk.Components.Keys.Where(k => k != component))
                    if (evidence[retained] is not null && provenance[retained] is null)
                        provenance[retained] = new JsonObject { ["installation"] = job.ResetPreviousInstallation, ["fingerprint"] = job.ResetPreviousFingerprint };
                evidence.Remove(component); provenance.Remove(component); evidence["componentSources"] = provenance.DeepClone();
                RecordingAnalysisStore.WriteJson(path, evidence);
            }
            chunk.Components[component] = "Pending";
        }
        // No raw or normalized audio is deleted; retained components stay in their original chunks.
        foreach (var path in Directory.EnumerateFiles(Store.DirectoryFor(job.Id)))
        {
            var name = Path.GetFileName(path);
            if (name == "summary.md" || name.StartsWith("summary-", StringComparison.Ordinal) || name.StartsWith("translation-", StringComparison.Ordinal) ||
                component == "transcription" && name == "transcript.jsonl" || component == "diarization" && name == "speakers.jsonl")
            { RecordingAnalysisStore.CheckPath(path); File.Delete(path); }
        }
        job.SummaryRevision = job.SummaryModel = job.CompletedSummaryModel = null;
        job.ResetPending = false; job.Stage = "Inference"; Store.Save(job);
    }
}
