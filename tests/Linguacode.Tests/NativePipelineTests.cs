using System.Diagnostics;
using System.Text.Json.Nodes;
using Linguacode.Audio;
using Xunit;

namespace Linguacode.Tests;

public sealed class NativeAudioFactAttribute : FactAttribute
{
    public NativeAudioFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LINGUACODE_NATIVE_TEST_DATA")))
            Skip = "Set LINGUACODE_NATIVE_TEST_DATA to an isolated data directory after running audio setup.";
    }
}

[Collection("AudioService")]
public sealed class NativePipelineTests
{
    private static string Installation()
    {
        var status = LocalAudioInstallation.Status(Environment.GetEnvironmentVariable("LINGUACODE_NATIVE_TEST_DATA")!);
        Assert.True(status.Transcription && status.Classification && status.Diarization, status.Message); return status.Location;
    }
    private static JsonObject SelfTest(string installation, string component) => new()
    { ["operation"] = "selftest", ["models"] = Path.Combine(installation, "models"), ["components"] = new JsonArray(component) };

    [NativeAudioFact]
    public async Task NativeEngineCancellationDiscardsWorkerAndEachEngineCanRestart()
    {
        var installation = Installation(); using var worker = new LocalAudioHost();
        foreach (var component in new[] { "transcription", "classification", "diarization" })
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.Infer(installation, SelfTest(installation, component),
                stage => { if (stage == component) cancellation.Cancel(); }, cancellation.Token));
            Assert.Null(worker.ProcessId);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await worker.Infer(installation, SelfTest(installation, component), _ => { }, deadline.Token);
            Assert.Equal("succeeded", result[component]!["status"]!.ToString());
        }
    }

    [NativeAudioFact]
    public async Task NativeWorkerReusesModelsRecoversFromProcessDeathAndUnloadsWhenIdle()
    {
        var installation = Installation(); using var worker = new LocalAudioHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await worker.Infer(installation, SelfTest(installation, "diarization"), _ => { }, deadline.Token);
        var first = worker.ProcessId;
        await worker.Infer(installation, SelfTest(installation, "diarization"), _ => { }, deadline.Token);
        Assert.Equal(first, worker.ProcessId);
        using (var process = Process.GetProcessById(first!.Value)) { process.Kill(); await process.WaitForExitAsync(deadline.Token); }
        var exitTimer = Stopwatch.StartNew();
        while (worker.ProcessId is not null) { Assert.True(exitTimer.Elapsed < TimeSpan.FromSeconds(10)); await Task.Delay(25, deadline.Token); }
        var result = await worker.Infer(installation, SelfTest(installation, "diarization"), _ => { }, deadline.Token);
        Assert.Equal("succeeded", result["diarization"]!["status"]!.ToString()); Assert.NotEqual(first, worker.ProcessId);
        worker.IdleTimeout = TimeSpan.FromMilliseconds(100);
        await worker.Infer(installation, SelfTest(installation, "diarization"), _ => { }, deadline.Token);
        var timer = Stopwatch.StartNew();
        while (worker.ProcessId is not null) { Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10)); await Task.Delay(25, deadline.Token); }
        Assert.Equal("unloaded", worker.State);
    }

    [NativeAudioFact]
    public async Task RealModelsRunThroughServicePreparationAndRecordingAnalysis()
    {
        var data = Environment.GetEnvironmentVariable("LINGUACODE_NATIVE_TEST_DATA")!;
        var status = LocalAudioInstallation.Status(data);
        Assert.True(status.Transcription && status.Classification && status.Diarization, status.Message);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Linguacode.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var source = Path.Combine(root.FullName, "tests", "fixtures", "speech", "en_us-0.wav");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var host = new AudioServiceHost(data); var server = host.Run(cancellation.Token);
        try
        {
            var timer = Stopwatch.StartNew();
            while (!(await AudioServiceControl.Status(data, cancellation.Token)).Available)
            { Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10)); await Task.Delay(50, cancellation.Token); }
            using var analysis = new RecordingAnalysis(data);
            var created = analysis.Create("file", source, new(Sounds: true, Language: "en", NoSummary: true));
            var completed = await analysis.Run(created.Id, false, false, _ => { }, null, null, cancellation.Token);
            Assert.Equal("Completed", completed.State); Assert.True(completed.Prepared); Assert.True(completed.Frames > 0);
            Assert.All(completed.Chunks, chunk => Assert.All(chunk.Components.Values, value => Assert.Equal("Succeeded", value)));
            var transcript = File.ReadAllText(analysis.Store.FileFor(created.Id, "transcript.jsonl"));
            Assert.Contains("communication", transcript, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(analysis.Store.FileFor(created.Id, "decoder.py")));
            var again = await analysis.Run(created.Id, false, false, _ => { }, null, null, cancellation.Token);
            Assert.Equal(completed.Updated, again.Updated);
        }
        finally
        {
            cancellation.Cancel();
            try { await server.WaitAsync(TimeSpan.FromSeconds(15)); } catch (OperationCanceledException) { }
        }
    }
}
