using System.Diagnostics;
using System.Text.Json.Nodes;
using Linguacode.Audio;
using Linguacode.Core;
using Xunit;

namespace Linguacode.Tests;

[Collection("AudioService")]
public sealed class BuildSmokeTests
{
    [Fact]
    public async Task DesktopAndServiceRunFromAssembledBuild()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Linguacode.slnx"))) root = root.Parent;
        Assert.NotNull(root);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var build = Path.Combine(root.FullName, "build", configuration);
        using var directory = new TestDirectory(); var capture = Fixture(directory.Path);
        async Task<string> Run(string executable, params string[] arguments)
        {
            var start = new ProcessStartInfo(Path.Combine(build, executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in arguments.Concat(new[] { "--data-dir", directory.Path })) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
            catch { process.Kill(true); throw; }
            var text = await stdout; var error = await stderr; Assert.True(process.ExitCode == 0, text + error); return text;
        }
        await Run("Linguacode.exe", "--smoke-test");
        var result = JsonNode.Parse(File.ReadAllText(Path.Combine(directory.Path, "desktop-smoke.json")))!;
        Assert.True(result["passed"]!.GetValue<bool>()); Assert.False(result["windowShown"]!.GetValue<bool>());
        try
        {
            var status = JsonNode.Parse(await Run("Linguacode.AudioHost.exe", "service", "start", "--json"))!;
            Assert.True(status["payload"]!["available"]!.GetValue<bool>());
            Assert.Equal(AudioWire.Version, status["payload"]!["protocol"]!.GetValue<int>());
            await Run("Linguacode.AudioHost.exe", "audio", "capture", "list", "1", "--json");
            await Run("Linguacode.AudioHost.exe", "audio", "capture", "export", capture, Path.Combine(directory.Path, "export"), "--json");
        }
        finally { await Run("Linguacode.AudioHost.exe", "service", "stop", "--json"); }
    }
    private static string Fixture(string root)
    {
        var captureId = Guid.NewGuid().ToString("N"); var analysisId = Guid.NewGuid().ToString("N");
        var capture = Path.Combine(root, "audio", "recordings", captureId); var analysis = Path.Combine(root, "audio", "analyses", analysisId);
        var metadata = Path.Combine(root, "desktop"); foreach (var path in new[] { capture, analysis, metadata }) Directory.CreateDirectory(path);
        void Save(string path, object value) => File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(value));
        Save(Path.Combine(root, "recorder-ui-fixture.json"), new { synthetic = true });
        var tracks = new List<CaptureManifest>();
        foreach (var name in new[] { "call", "mic" })
        {
            var rate = name == "call" ? 16000 : 48000; var file = name + "-chunk-000001.wav"; var path = Path.Combine(capture, file);
            NativeAudioTests.Wave(path, 12, rate, 1);
            tracks.Add(new CaptureManifest { Id = captureId, OperationId = captureId, TrackId = name, State = "Completed", Ended = DateTimeOffset.UtcNow,
                Device = new("fixture-" + name, "Synthetic " + name, false, name == "mic" ? "microphone" : "output"),
                SampleRate = rate, Channels = 1, BitsPerSample = 16, BlockAlign = 2, Format = $"16 bit PCM: {rate}Hz 1 channels", Frames = rate * 12,
                TimelineSeconds = 12, Bytes = new FileInfo(path).Length, StartQpc = 100000000,
                Anchors = [new(0, 100000000), new(rate * 11, 210000000)],
                Chunks = [new(file, 0, rate * 12, new FileInfo(path).Length, RecordingAnalysisStore.Hash(path))] });
        }
        Save(Path.Combine(capture, "manifest.json"), new CaptureManifest { Version = 2, Id = captureId, OperationId = captureId,
            State = "Completed", Ended = DateTimeOffset.UtcNow, Device = tracks[0].Device, Tracks = tracks, TimelineSeconds = 12, StartQpc = 100000000, Bytes = tracks.Sum(t => t.Bytes) });
        Save(Path.Combine(metadata, captureId + ".json"), new { Title = "Synthetic review fixture — not a real call", Language = "en", AnalysisId = analysisId });
        Save(Path.Combine(analysis, "job.json"), new RecordingAnalysisJob { Id = analysisId, SourceKind = "capture", Source = captureId, State = "Completed",
            Stage = "Done", Frames = 192000, TrackCount = 2, Options = new(NoSummary: true) });
        object[] rows = [new { id = "callc000000s00000", startSeconds = 1, endSeconds = 4, speaker = "Speaker 1", text = "Synthetic example: review the transcript and its timestamps.", track = "call" },
            new { id = "micc000001s00000", startSeconds = 3, endSeconds = 6, speaker = "You", text = "Primjer na hrvatskom: provjerimo zapis razgovora.", track = "mic" },
            new { id = "callc000000s00001", startSeconds = 7, endSeconds = 10, speaker = "Speaker 2", text = "Primer v slovenščini: preverimo zapis pogovora.", track = "call" }];
        File.WriteAllText(Path.Combine(analysis, "transcript.jsonl"), string.Join("\n", rows.Select(row => System.Text.Json.JsonSerializer.Serialize(row))) + "\n");
        Save(Path.Combine(analysis, "coverage.json"), new { note = "Synthetic UI fixture. No speech recognition was performed.", tracks = new[] { "call", "mic" }, durationSeconds = 12 });
        File.WriteAllText(Path.Combine(analysis, "summary.md"), "# Synthetic summary fixture\n\nThis text was authored for UI testing, not generated from a call.\n\n- Review the transcript. [callc000000s00000 00:01–00:04]\n");
        return captureId;
    }
}
