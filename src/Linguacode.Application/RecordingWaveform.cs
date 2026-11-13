using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Linguacode.Audio;

namespace Linguacode.Application;

public readonly record struct WaveformPeak(float Min, float Max);
public sealed record WaveformCoverage(double Start, double End, string Kind, string Track);
public sealed record RecordingWaveform(double Duration, ImmutableArray<ImmutableArray<WaveformPeak>> Channels,
    ImmutableArray<WaveformCoverage> Coverage, string Identity)
{
    public const double BinSeconds = .02;
    // Same selector order as CallPlayback: mix, output, microphone.
    public ImmutableArray<WaveformPeak> ForTrack(int track) => Channels[Math.Clamp(track, 0, Channels.Length - 1)];
}

/// <summary>Disposable, local peak cache. Audio is read on the playback clock, never by the UI thread.</summary>
public static class RecordingWaveforms
{
    private const int Version = 2;
    public static Task<RecordingWaveform> LoadAsync(string audioDirectory, CaptureManifest capture, string cacheDirectory,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null) => Task.Run(() => Load(audioDirectory, capture, cacheDirectory, cancellationToken, progress), cancellationToken);

    public static string Identity(string directory, CaptureManifest capture)
    {
        var files = capture.AllChunks().Select(c => {
            var info = new FileInfo(Path.Combine(directory, c.File));
            return new { c.File, Exists = info.Exists, Length = info.Exists ? info.Length : -1, Modified = info.Exists ? info.LastWriteTimeUtc.Ticks : 0 };
        }).ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Version, Capture = capture, Files = files })));
    }

    private static RecordingWaveform Load(string directory, CaptureManifest capture, string cacheDirectory, CancellationToken ct, IProgress<double>? progress)
    {
        ct.ThrowIfCancellationRequested();
        var identity = Identity(directory, capture);
        var sources = (capture.Version == 2 ? capture.Tracks : [capture]).ToArray();
        if (sources.Length is < 1 or > 2 || !double.IsFinite(capture.DurationSeconds) || capture.DurationSeconds < 0)
            throw new IOException("Invalid waveform source.");
        var count = checked((int)Math.Ceiling(capture.DurationSeconds / RecordingWaveform.BinSeconds));
        var coverage = BuildCoverage(sources, capture.DurationSeconds);
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capture.Id))) + ".peaks");
        try
        {
            using var input = new BinaryReader(File.OpenRead(path));
            if (input.ReadInt32() == Version && input.ReadString() == identity && input.ReadInt32() == count)
            {
                var channels = ImmutableArray.CreateBuilder<ImmutableArray<WaveformPeak>>(3);
                for (var channel = 0; channel < 3; channel++)
                {
                    var peaks = ImmutableArray.CreateBuilder<WaveformPeak>(count);
                    for (var i = 0; i < count; i++)
                    {
                        if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                        var peak = new WaveformPeak(input.ReadSingle(), input.ReadSingle());
                        if (!float.IsFinite(peak.Min) || !float.IsFinite(peak.Max) || peak.Min < -1 || peak.Max > 1 || peak.Min > peak.Max)
                            throw new IOException("Invalid peak cache.");
                        peaks.Add(peak);
                    }
                    channels.Add(peaks.MoveToImmutable());
                }
                if (input.BaseStream.Position != input.BaseStream.Length) throw new IOException("Invalid peak cache length.");
                return new(capture.DurationSeconds, channels.MoveToImmutable(), coverage, identity);
            }
        }
        catch (IOException) { /* Missing, truncated or stale caches are rebuilt from retained audio. */ }

        var readers = new List<CaptureAudioReader>();
        var data = new[] { new WaveformPeak[count], new WaveformPeak[count], new WaveformPeak[count] };
        try
        {
            foreach (var source in sources) readers.Add(new(directory, source));
            // Empty regions can be skipped without losing a peak. Include the adjacent frame for interpolation.
            var ranges = sources.Select(s => s.Chunks.Select(c => (Start: FrameTime(s, c.StartFrame - 1), End: FrameTime(s, c.StartFrame + c.Frames + 1))).ToArray()).ToArray();
            var rangeIndices = new int[sources.Length];
            var totalFrames = (long)(capture.DurationSeconds * 48000); // Identical end boundary to playback.
            for (var bin = 0; bin < count; bin++)
            {
                ct.ThrowIfCancellationRequested();
                if (bin > 0 && bin % 50 == 0) progress?.Report((double)bin / count);
                var start = bin * RecordingWaveform.BinSeconds;
                var end = start + RecordingWaveform.BinSeconds;
                var any = false;
                for (var t = 0; t < sources.Length; t++)
                {
                    while (rangeIndices[t] < ranges[t].Length && ranges[t][rangeIndices[t]].End <= start) rangeIndices[t]++;
                    any |= rangeIndices[t] < ranges[t].Length && ranges[t][rangeIndices[t]].Start < end;
                }
                if (!any) continue;
                var min0 = float.PositiveInfinity; var max0 = float.NegativeInfinity;
                var min1 = min0; var max1 = max0; var min2 = min0; var max2 = max0;
                for (long frame = (long)bin * 960; frame < Math.Min(totalFrames, (long)(bin + 1) * 960); frame++)
                {
                    var time = frame / 48000d;
                    var first = readers[0].Sample(time);
                    var second = readers.Count > 1 ? readers[1].Sample(time) : 0;
                    var mixed = readers.Count > 1 ? (first + second) / 2 : first;
                    if (capture.Version == 1 && capture.Device.Kind == "microphone") (first, second) = (second, first);
                    min0 = Math.Min(min0, mixed); max0 = Math.Max(max0, mixed);
                    min1 = Math.Min(min1, first); max1 = Math.Max(max1, first);
                    min2 = Math.Min(min2, second); max2 = Math.Max(max2, second);
                }
                if (float.IsFinite(min0))
                { data[0][bin] = new(min0, max0); data[1][bin] = new(min1, max1); data[2][bin] = new(min2, max2); }
            }
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
        ct.ThrowIfCancellationRequested();
        if (Identity(directory, capture) != identity) throw new IOException("Recording changed while loading its waveform. Select it again to retry.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new BinaryWriter(File.Create(temporary)))
            {
                output.Write(Version); output.Write(identity); output.Write(count);
                foreach (var channel in data)
                foreach (var peak in channel) { output.Write(peak.Min); output.Write(peak.Max); }
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(capture.DurationSeconds, data.Select(ImmutableArray.CreateRange).ToImmutableArray(), coverage, identity);
    }

    private static string SourceName(CaptureManifest source, int index) => string.IsNullOrEmpty(source.TrackId) ? source.Device.Kind == "microphone" ? "mic" : "call" : source.TrackId;

    private static ImmutableArray<WaveformCoverage> BuildCoverage(CaptureManifest[] sources, double duration)
    {
        var result = new List<WaveformCoverage>();
        for (var index = 0; index < sources.Length; index++)
        {
            var source = sources[index]; var name = SourceName(source, index);
            var known = source.Coverage.Select(c => new WaveformCoverage(Math.Clamp(FrameTime(source, c.StartFrame), 0, duration),
                Math.Clamp(FrameTime(source, c.StartFrame + c.Frames), 0, duration), c.Kind, name)).OrderBy(c => c.Start).ToArray();
            result.AddRange(known);
            void Missing(double start, double end)
            {
                start = Math.Clamp(start, 0, duration); end = Math.Clamp(end, 0, duration);
                foreach (var interval in known)
                {
                    if (interval.End <= start || interval.Start >= end) continue;
                    if (interval.Start > start) result.Add(new(start, interval.Start, "Missing", name));
                    start = Math.Max(start, interval.End);
                }
                if (start < end) result.Add(new(start, end, "Missing", name));
            }
            // Chunk holes and delayed starts must not appear as ordinary silence even in legacy captures.
            var previous = 0L;
            Missing(0, FrameTime(source, 0));
            foreach (var chunk in source.Chunks)
            {
                if (chunk.StartFrame > previous) Missing(FrameTime(source, previous), FrameTime(source, chunk.StartFrame));
                previous = chunk.StartFrame + chunk.Frames;
            }
            Missing(Math.Min(source.DurationSeconds, FrameTime(source, previous)), duration);
        }
        return result.ToImmutableArray();
    }

    public static double FrameTime(CaptureManifest source, long frame)
    {
        var anchors = source.Anchors;
        if (anchors.Count == 0) return (double)frame / source.SampleRate;
        var lo = 0; var hi = anchors.Count - 1;
        while (lo < hi) { var mid = (lo + hi + 1) / 2; if (anchors[mid].Frame <= frame) lo = mid; else hi = mid - 1; }
        var a = anchors[lo];
        var qpc = lo + 1 < anchors.Count && frame >= a.Frame
            ? a.Qpc + (frame - a.Frame) * (double)(anchors[lo + 1].Qpc - a.Qpc) / (anchors[lo + 1].Frame - a.Frame)
            : a.Qpc + (frame - a.Frame) * 10_000_000d / source.SampleRate;
        return (qpc - source.StartQpc) / 10_000_000;
    }
}
