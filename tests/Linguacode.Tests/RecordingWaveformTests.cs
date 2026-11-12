using Linguacode.Application;
using Linguacode.Audio;
using NAudio.Wave;
using Xunit;

namespace Linguacode.Tests;

public sealed class RecordingWaveformTests
{
    private static CaptureManifest Track(string directory, string id, string name, int rate, double duration,
        params (double Start, float[] Samples)[] chunks)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
        var track = new CaptureManifest { Id = id, TrackId = name, State = "Completed", SampleRate = rate, Channels = 1,
            BitsPerSample = 32, BlockAlign = 4, FloatingPoint = true, Format = format.ToString(), TimelineSeconds = duration,
            Frames = (long)(duration * rate), StartQpc = 100_000_000 };
        foreach (var (start, samples) in chunks)
        {
            var filename = $"{name}-chunk-{track.Chunks.Count + 1:000000}.wav";
            var path = Path.Combine(directory, filename);
            using (var writer = new WaveFileWriter(path, format))
            {
                var bytes = new byte[samples.Length * 4]; Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length); writer.Write(bytes);
            }
            track.Chunks.Add(new(filename, (long)Math.Round(start * rate), samples.Length, new FileInfo(path).Length, RecordingAnalysisStore.Hash(path)));
        }
        return track;
    }
    private static Task<RecordingWaveform> Load(TestDirectory directory, CaptureManifest capture, CancellationToken ct = default) =>
        RecordingWaveforms.LoadAsync(directory.Path, capture, Path.Combine(directory.Path, "cache"), ct);

    [Fact]
    public async Task PeaksUseAllSamplesAndTheActualPlaybackMix()
    {
        using var d = new TestDirectory(); var id = Guid.NewGuid().ToString("N");
        var first = Enumerable.Range(0, 48000).Select(i => i % 2 == 0 ? .5f : -.5f).ToArray();
        var call = Track(d.Path, id, "call", 48000, 1, (0, first));
        var mic = Track(d.Path, id, "mic", 48000, 1, (0, first.Select(v => -v).ToArray()));
        var capture = new CaptureManifest { Id = id, Version = 2, TimelineSeconds = 1, Tracks = [call, mic] };
        var result = await Load(d, capture);
        Assert.Equal(50, result.ForTrack(0).Length);
        Assert.Equal(new WaveformPeak(0, 0), result.ForTrack(0)[10]);
        Assert.Equal(new WaveformPeak(-.5f, .5f), result.ForTrack(1)[10]);
        Assert.Equal(new WaveformPeak(-.5f, .5f), result.ForTrack(2)[10]);
    }

    [Fact]
    public async Task DifferentRatesDelayedStartsAndDriftUseTheSameMappingAsPlayback()
    {
        using var d = new TestDirectory(); var id = Guid.NewGuid().ToString("N");
        var call = Track(d.Path, id, "call", 16000, 2, (0, Enumerable.Repeat(.4f, 32000).ToArray()));
        var mic = Track(d.Path, id, "mic", 48000, 2, (0, Enumerable.Repeat(-.2f, 96000).ToArray()));
        mic.Anchors = [new(0, mic.StartQpc + 2_000_000), new(96000, mic.StartQpc + 22_010_000)];
        mic.Coverage = [new(0, 4800, "IntentionalMute")];
        var capture = new CaptureManifest { Id = id, Version = 2, TimelineSeconds = 2.3, Tracks = [call, mic] };
        var result = await Load(d, capture);
        Assert.Equal(new WaveformPeak(0, 0), result.ForTrack(2)[2]);
        Assert.InRange(result.Coverage.Single(c => c.Kind == "IntentionalMute").Start, .1999, .2001);
        Assert.Contains(result.Coverage, c => c.Track == "mic" && c.Kind == "Missing" && c.Start == 0 && c.End == .2);
        foreach (var bin in new[] { 10, 30, 90 })
        {
            using var r1 = new CaptureAudioReader(d.Path, call); using var r2 = new CaptureAudioReader(d.Path, mic);
            var values = Enumerable.Range(bin * 960, 960).Select(f => (r1.Sample(f / 48000d) + r2.Sample(f / 48000d)) / 2).ToArray();
            Assert.Equal(new WaveformPeak(values.Min(), values.Max()), result.ForTrack(0)[bin]);
        }
        Assert.InRange(RecordingWaveforms.FrameTime(mic, 48000), 1.2004, 1.2006);
    }

    [Fact]
    public async Task ChunkBoundariesSilenceAndCoverageRemainDistinct()
    {
        using var d = new TestDirectory(); var id = Guid.NewGuid().ToString("N");
        var track = Track(d.Path, id, "call", 48000, 1,
            (0, Enumerable.Repeat(.5f, 9600).ToArray()), (.2, new float[9600]), (.6, Enumerable.Repeat(-.5f, 19200).ToArray()));
        track.Coverage = [new(9600, 9600, "IntentionalMute"), new(19200, 9600, "Missing"), new(0, 480, "DeviceDiscontinuity")];
        var result = await Load(d, track);
        Assert.Equal(.5f, result.ForTrack(0)[5].Max);
        Assert.Equal(new WaveformPeak(0, 0), result.ForTrack(0)[15]);
        Assert.Equal(new WaveformPeak(0, 0), result.ForTrack(0)[25]);
        Assert.Equal(-.5f, result.ForTrack(0)[40].Min);
        Assert.Equal(new[] { "DeviceDiscontinuity", "IntentionalMute", "Missing" }, result.Coverage.Select(c => c.Kind));
        Assert.Equal(result.ForTrack(0).ToArray(), result.ForTrack(1).ToArray()); // Legacy single-source playback.
    }

    [Fact]
    public async Task CacheIsReusedInvalidatedAndRepairedWithoutTouchingAudio()
    {
        using var d = new TestDirectory(); var id = Guid.NewGuid().ToString("N");
        var track = Track(d.Path, id, "call", 48000, 1, (0, new float[48000]));
        var result = await Load(d, track);
        var cache = Directory.GetFiles(Path.Combine(d.Path, "cache"), "*.peaks").Single();
        var modified = File.GetLastWriteTimeUtc(cache);
        Assert.Equal(result.Identity, (await Load(d, track)).Identity);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(cache));
        track.Coverage.Add(new(0, 24000, "InferredSilence"));
        Assert.NotEqual(result.Identity, (await Load(d, track)).Identity);
        await File.WriteAllTextAsync(cache, "truncated");
        Assert.Equal(50, (await Load(d, track)).ForTrack(0).Length);
        var before = RecordingWaveforms.Identity(d.Path, track);
        File.SetLastWriteTimeUtc(Path.Combine(d.Path, track.Chunks[0].File), DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(before, (await Load(d, track)).Identity);
        Assert.Equal(track.Chunks[0].Hash, RecordingAnalysisStore.Hash(Path.Combine(d.Path, track.Chunks[0].File)));
    }

    [Fact]
    public async Task CancellationAndMissingAudioDoNotPublishPartialCaches()
    {
        using var d = new TestDirectory(); var id = Guid.NewGuid().ToString("N");
        var track = Track(d.Path, id, "call", 48000, 1, (0, new float[48000]));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Load(d, track, cts.Token));
        File.Delete(Path.Combine(d.Path, track.Chunks[0].File));
        await Assert.ThrowsAnyAsync<IOException>(() => Load(d, track));
        Assert.Empty(Directory.GetFiles(Path.Combine(d.Path, "cache")));
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    { public void Report(double value) => report(value); }

    [Fact]
    public async Task CancellationDuringPeakGenerationReleasesFilesAndLeavesNoCache()
    {
        using var d = new TestDirectory(); var id = Guid.NewGuid().ToString("N");
        var track = Track(d.Path, id, "call", 48000, 2, (0, new float[96000]));
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cts.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecordingWaveforms.LoadAsync(d.Path, track, Path.Combine(d.Path, "cache"), cts.Token, progress));
        Assert.Empty(Directory.GetFiles(Path.Combine(d.Path, "cache")));
        using var exclusive = new FileStream(Path.Combine(d.Path, track.Chunks[0].File), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanRead);
    }

    [Fact]
    public async Task TwoHourSparseFixtureRetainsBeginningMiddleEndWithBoundedPeakStorage()
    {
        using var d = new TestDirectory(); var id = Guid.NewGuid().ToString("N");
        var track = Track(d.Path, id, "call", 48000, 7200,
            (0, Enumerable.Repeat(.3f, 960).ToArray()), (3600, Enumerable.Repeat(.6f, 960).ToArray()), (7199.98, Enumerable.Repeat(.9f, 960).ToArray()));
        track.Anchors = [new(0, track.StartQpc), new(track.Frames, track.StartQpc + 72_000_000_000)];
        var result = await Load(d, track);
        Assert.Equal(360000, result.ForTrack(0).Length);
        Assert.Equal(.3f, result.ForTrack(0)[0].Max); Assert.Equal(.6f, result.ForTrack(0)[180000].Max); Assert.Equal(.9f, result.ForTrack(0)[^1].Max);
        Assert.InRange(new FileInfo(Directory.GetFiles(Path.Combine(d.Path, "cache"), "*.peaks").Single()).Length, 8_640_000, 8_641_000);
    }

    [Fact]
    public void ViewportZoomPanRevealAndRulerAreBounded()
    {
        var viewport = new TimelineViewport(); viewport.Reset(7200);
        viewport.Zoom(.5, 3600); Assert.Equal(1800, viewport.Start); Assert.Equal(5400, viewport.End);
        viewport.Zoom(.5, 0); Assert.Equal(2700, viewport.Start); // Off-screen playhead uses viewport midpoint.
        viewport.Reveal(7199); Assert.Equal(7200, viewport.End);
        viewport.Pan(-1); Assert.Equal(0, viewport.Start);
        for (var i = 0; i < 20; i++) viewport.Zoom(.5, 1);
        Assert.Equal(5, viewport.Span); Assert.Equal(0, viewport.TimeAt(-1, 100)); Assert.Equal(5, viewport.TimeAt(101, 100));
        Assert.True(viewport.TickStep(600) / viewport.Span * 600 >= 70);
        viewport.Fit(); Assert.Equal(7200, viewport.Span);
        viewport.Reset(2); viewport.Zoom(.5, 1); Assert.Equal(2, viewport.Span);
    }
}
