using System.Text.Json.Nodes;
using Linguacode.Application;
using Linguacode.Audio;
using Xunit;

namespace Linguacode.Tests;

public sealed class RecordingAnalysisRestartTests
{
    private sealed class Backend : ILocalAudioBackend
    {
        public string Installation = "";
        public List<JsonObject> Requests = [];
        public bool FailSpeakers;
        public int CancelAt = -1;
        public AudioReadiness Status(string data) => new(true, true, Installation, "fixture", true);
        public Task<JsonObject> Run(string installation, JsonObject request, Action<string> progress, CancellationToken ct)
        {
            Requests.Add((JsonObject)request.DeepClone());
            if (Requests.Count == CancelAt) throw new OperationCanceledException();
            return Task.FromResult(JsonNode.Parse("""
                {"transcription":{"status":"succeeded","language":"sl","languageScore":0.9,"segments":[{"startSeconds":1,"endSeconds":2,"text":"hello","words":[{"startSeconds":1,"endSeconds":2,"text":"hello"}]}]},
                 "diarization":{"status":"succeeded","turns":[{"startSeconds":0.5,"endSeconds":2.5,"speaker":"local"}],"embeddings":[{"speaker":"local","seconds":4,"vector":[1,0]}]}}
                """)!.AsObject().Also(result => { if (FailSpeakers) result["diarization"] = new JsonObject { ["status"] = "failed" }; }));
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory directory = new();
        public Backend Backend { get; } = new();
        public CallRecorder Recorder { get; }
        public string Capture { get; } = Guid.NewGuid().ToString("N");
        public RecordingAnalysisJob Job { get; }
        public string File(string name) => Recorder.Analysis.Store.FileFor(Job.Id, name);
        public Fixture()
        {
            Backend.Installation = Path.Combine(directory.Path, "audio", "installations", Guid.NewGuid().ToString("N"));
            var helper = Path.Combine(Backend.Installation, "helper"); Directory.CreateDirectory(helper);
            System.IO.File.WriteAllText(Path.Combine(helper, "downloads.json"), """{"transcriptionHints":1}""");
            Recorder = new(directory.Path, Path.Combine(directory.Path, "Linguacode.AudioHost.exe"), audioBackend: Backend);
            Job = new() { Prepared = true, Version = 4, TrackCount = 2, Frames = 160000, Installation = Path.GetFileName(Backend.Installation),
                Fingerprint = LocalAudioInstallation.Fingerprint(helper), Options = new(NoSummary: true),
                DecoderHash = LocalAudioInstallation.Fingerprint(LocalAudioInstallation.Bundle) };
            foreach (var track in new[] { "call", "mic" })
            {
                var chunk = RecordingAnalysis.PlanChunks(Job.Frames, false).Single();
                chunk.Index = Job.Chunks.Count; chunk.TrackId = track; chunk.SourceOffset = chunk.Index * Job.Frames; Job.Chunks.Add(chunk);
            }
            Directory.CreateDirectory(Recorder.Analysis.Store.DirectoryFor(Job.Id));
            using (var normalized = System.IO.File.Create(File("normalized.f32"))) normalized.SetLength(Job.Frames * 8);
            RecordingAnalysisStore.WriteJson(File("preparation.json"), new { hash = RecordingAnalysisStore.Hash(File("normalized.f32")) });
            Recorder.Analysis.Store.Save(Job);
            Recorder.SaveMetadata(Capture, new() { Title = "Retained call", AnalysisId = Job.Id, SpeakerNames = new() { ["Speaker 1"] = "Matič", ["You"] = "Saša" } });
        }
        public Task<RecordingAnalysisJob> Run(bool retry = false) => Recorder.Analysis.Run(Job.Id, false, retry, _ => { }, null, null, default);
        public string[] Requested(int index) => Backend.Requests[index]["components"]!.AsArray().Select(c => c!.ToString()).ToArray();
        public void Dispose() { Recorder.Dispose(); directory.Dispose(); }
    }

    [Fact]
    public async Task RetranscriptionKeepsSpeakerEvidenceNamesAudioAndUsesOptionalHints()
    {
        using var f = new Fixture(); await f.Run();
        var speakers = System.IO.File.ReadAllText(f.File("speakers.jsonl"));
        var audioHash = RecordingAnalysisStore.Hash(f.File("normalized.f32"));
        var diarization = RecordingAnalysisStore.ReadJson<JsonObject>(f.File("evidence-000000.json"))["diarization"]!.ToJsonString();
        System.IO.File.WriteAllText(f.File("summary.md"), "old summary");
        System.IO.File.WriteAllText(f.File("translation-test.json"), "{}");
        f.Recorder.ResetTranscription(f.Capture, "sl", true);
        Assert.Equal("Matič", f.Recorder.Metadata(f.Capture).SpeakerNames["Speaker 1"]);
        Assert.Equal(f.Job.Id, f.Recorder.Metadata(f.Capture).AnalysisId);
        Assert.False(System.IO.File.Exists(f.File("summary.md")));
        Assert.False(System.IO.File.Exists(f.File("translation-test.json")));
        Assert.Equal("Completed", (await f.Run()).State);
        Assert.Equal(speakers, System.IO.File.ReadAllText(f.File("speakers.jsonl")));
        Assert.Equal(diarization, RecordingAnalysisStore.ReadJson<JsonObject>(f.File("evidence-000000.json"))["diarization"]!.ToJsonString());
        Assert.Equal(audioHash, RecordingAnalysisStore.Hash(f.File("normalized.f32")));
        Assert.All(f.Backend.Requests.Skip(2), r => {
            Assert.Equal(new[] { "transcription" }, r["components"]!.AsArray().Select(c => c!.ToString()));
            Assert.Equal("Matič, Saša", r["transcriptionHints"]!.ToString()); Assert.Equal("sl", r["language"]!.ToString());
        });
        Assert.Equal(new[] { "Matič", "Saša" }, f.Recorder.Transcript(f.Capture).Select(r => r.Speaker));
        f.Recorder.ResetTranscription(f.Capture, "en", false);
        await f.Run(); Assert.Null(f.Backend.Requests[^1]["transcriptionHints"]);
    }

    [Fact]
    public async Task SpeakerRedetectionKeepsWordsAndOnlyClearsRemoteNames()
    {
        using var f = new Fixture(); await f.Run();
        var words = RecordingAnalysisStore.ReadJson<JsonObject>(f.File("evidence-000000.json"))["transcription"]!.ToJsonString();
        f.Recorder.ResetSpeakers(f.Capture);
        Assert.Equal("Saša", Assert.Single(f.Recorder.Metadata(f.Capture).SpeakerNames).Value);
        await f.Run();
        Assert.Equal(new[] { "diarization" }, f.Requested(2)); Assert.Equal(new[] { "diarization" }, f.Requested(3));
        Assert.Equal(words, RecordingAnalysisStore.ReadJson<JsonObject>(f.File("evidence-000000.json"))["transcription"]!.ToJsonString());
    }

    [Fact]
    public async Task CancellationResumesOnlyUncertainTranscriptionAndNeverRunsFailedSpeakers()
    {
        using var f = new Fixture(); f.Backend.FailSpeakers = true; await f.Run();
        f.Recorder.ResetTranscription(f.Capture, "sl"); f.Backend.CancelAt = 4;
        Assert.Equal("Paused", (await f.Run()).State);
        Assert.Equal("CompletedWithGaps", (await f.Run()).State); Assert.Equal(4, f.Backend.Requests.Count);
        await f.Run(true); Assert.Equal(5, f.Backend.Requests.Count);
        Assert.All(f.Backend.Requests.Skip(2), r => Assert.Equal(new[] { "transcription" }, r["components"]!.AsArray().Select(c => c!.ToString())));
    }

    [Fact]
    public async Task InterruptedResetCompletesBeforeInferenceAndKeepsRetainedEvidence()
    {
        using var f = new Fixture(); await f.Run();
        var job = f.Recorder.Analysis.Store.Read(f.Job.Id);
        job.Version = 4; job.Revision++; job.ComponentScope = "transcription"; job.ResetPending = true; job.State = "Paused";
        f.Recorder.Analysis.Store.Save(job);
        // A reset crashed after removing only the first chunk's selected component.
        var evidence = RecordingAnalysisStore.ReadJson<JsonObject>(f.File("evidence-000000.json")); evidence.Remove("transcription");
        RecordingAnalysisStore.WriteJson(f.File("evidence-000000.json"), evidence);
        var result = await f.Run(); Assert.False(result.ResetPending); Assert.Equal("Completed", result.State);
        Assert.Equal(4, f.Backend.Requests.Count); Assert.Equal(new[] { "transcription" }, f.Requested(3));
        Assert.Equal("Matič", f.Recorder.Transcript(f.Capture)[0].Speaker);
    }
}
