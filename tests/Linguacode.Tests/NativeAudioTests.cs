using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Linguacode.Audio;
using Xunit;

namespace Linguacode.Tests;

public sealed class NativeAudioTests
{
    internal static void Wave(string path, int seconds = 3, int rate = 48000, int channels = 2, int bits = 16, bool floating = false)
    {
        using var output = new BinaryWriter(File.Create(path)); var length = seconds * rate * channels * bits / 8;
        output.Write("RIFF"u8); output.Write(length + 36); output.Write("WAVEfmt "u8); output.Write(16);
        output.Write((short)(floating ? 3 : 1)); output.Write((short)channels); output.Write(rate); output.Write(rate * channels * bits / 8);
        output.Write((short)(channels * bits / 8)); output.Write((short)bits); output.Write("data"u8); output.Write(length);
        for (var i = 0; i < seconds * rate; i++)
            for (var channel = 0; channel < channels; channel++)
            {
                var sample = .3 * Math.Sin(i * 2 * Math.PI * 440 / rate);
                if (floating) { if (bits == 32) output.Write((float)sample); else output.Write(sample); }
                else if (bits == 8) output.Write((byte)(128 + sample * 127));
                else if (bits == 16) output.Write((short)(sample * short.MaxValue));
                else if (bits == 24) { var value = (int)(sample * 8388607); output.Write((byte)value); output.Write((byte)(value >> 8)); output.Write((byte)(value >> 16)); }
                else output.Write((int)(sample * int.MaxValue));
            }
    }
    internal static async Task<JsonObject> Worker(JsonObject request, bool prepare = true, CancellationToken cancellation = default)
    {
        var text = await LocalAudioProcess.Run(LocalAudioInstallation.Worker, prepare ? ["--prepare"] : [],
            AppContext.BaseDirectory, request.ToJsonString(), cancellation);
        if (prepare) return JsonNode.Parse(text)!.AsObject();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonNode.Parse(s)!).Single(n => n["type"]!.ToString() == "result")["result"]!.AsObject();
    }
    private static JsonObject Request(string source, string destination, long budget = 100000000) => new()
    { ["inputs"] = new JsonArray(new JsonObject { ["path"] = source }), ["destination"] = destination, ["maxBytes"] = budget };
    [Theory]
    [InlineData(8, false)] [InlineData(16, false)] [InlineData(24, false)] [InlineData(32, false)] [InlineData(32, true)] [InlineData(64, true)]
    public async Task PcmFormatsResampleAndDownmix(int bits, bool floating)
    {
        using var directory = new TestDirectory(); var path = Path.Combine(directory.Path, "input.wav"); var target = Path.Combine(directory.Path, "normalized.f32");
        Wave(path, bits: bits, floating: floating);
        var result = await Worker(Request(path, target)); Assert.Equal(48000, result["frames"]!.GetValue<long>());
        var bytes = File.ReadAllBytes(target); var samples = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        Assert.All(samples, sample => Assert.True(float.IsFinite(sample) && Math.Abs(sample) <= 1));
        Assert.InRange(samples.Max(), .29f, .5f);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result["hash"]!.ToString());
    }
    [Fact]
    public async Task CompleteRecordingStreamsBeyond120SecondsAndClipIsCapped()
    {
        using var directory = new TestDirectory(); var path = Path.Combine(directory.Path, "input.wav"); var target = Path.Combine(directory.Path, "normalized.f32");
        Wave(path, seconds: 241, rate: 8000, channels: 1);
        var result = await Worker(Request(path, target)); Assert.Equal(241 * 16000, result["frames"]!.GetValue<long>());
        var clip = await Worker(new JsonObject { ["protocol"] = 3, ["operation"] = "audio_classify", ["models"] = directory.Path,
            ["path"] = path, ["startSeconds"] = 0, ["durationSeconds"] = 0 }, false);
        Assert.Equal(120, clip["durationSeconds"]!.GetValue<double>()); Assert.True(clip["partial"]!.GetValue<bool>());
        Assert.Equal("failed", clip["classification"]!["status"]!.ToString());
    }
    [Fact]
    public async Task GapsAndMappingsPreserveAbsoluteFrames()
    {
        using var directory = new TestDirectory(); var path = Path.Combine(directory.Path, "input.wav"); var target = Path.Combine(directory.Path, "normalized.f32");
        Wave(path, 1, 16000, 1);
        var request = Request(path, target); request["inputs"] = new JsonArray(
            new JsonObject { ["path"] = path, ["start"] = 0, ["frames"] = 16000 },
            new JsonObject { ["path"] = path, ["start"] = 32000, ["frames"] = 16000 });
        var result = await Worker(request); Assert.Equal(48000, result["frames"]!.GetValue<long>());
        Assert.Equal(32000, result["mappings"]![1]!["start"]!.GetValue<long>());
        Assert.All(File.ReadAllBytes(target).Skip(16000 * 4).Take(16000 * 4), b => Assert.Equal(0, b));
    }
    [Theory]
    [InlineData("budget")] [InlineData("encoding")] [InlineData("length")] [InlineData("playlist")] [InlineData("missing")]
    public async Task InvalidSourcesNeverPromoteOutput(string failure)
    {
        using var directory = new TestDirectory(); var path = Path.Combine(directory.Path, "input.wav"); var target = Path.Combine(directory.Path, "normalized.f32");
        Wave(path, 1); var request = Request(path, target, failure == "budget" ? 1000 : 1000000);
        if (failure == "encoding") { request["inputs"]![0]!["bitsPerSample"] = 32; request["inputs"]![0]!["floatingPoint"] = true; }
        if (failure == "length") request["inputs"]![0]!["frames"] = 32000;
        if (failure == "playlist") File.WriteAllText(path, "#EXTM3U\nhttps://example.com/audio.wav\n");
        if (failure == "missing") File.Delete(path);
        await Assert.ThrowsAnyAsync<Exception>(() => Worker(request));
        Assert.False(File.Exists(target)); Assert.False(File.Exists(Path.ChangeExtension(target, ".partial")));
    }
    [Fact]
    public void LegacyAnalysisCannotBeLoadedOrExecuted()
    {
        using var directory = new TestDirectory(); var store = new RecordingAnalysisStore(directory.Path);
        var job = new RecordingAnalysisJob { Version = 3 }; Directory.CreateDirectory(store.DirectoryFor(job.Id)); store.Save(job);
        File.WriteAllText(store.FileFor(job.Id, "decoder.py"), "raise Exception('must never execute')");
        Assert.Throws<Linguacode.Core.UserFacingException>(() => store.Read(job.Id));
    }
    [Fact]
    public async Task VbrMp3DelayAndSelectedRangeAgreeWithFullDecode()
    {
        using var directory = new TestDirectory(); var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "decoder", "tone-vbr.mp3");
        var target = Path.Combine(directory.Path, "normalized.f32");
        var complete = await Worker(Request(path, target)); Assert.Equal(4 * 16000, complete["frames"]!.GetValue<long>());
        var clip = await Worker(new JsonObject { ["protocol"] = 3, ["operation"] = "audio_classify", ["models"] = directory.Path,
            ["path"] = path, ["startSeconds"] = 1, ["durationSeconds"] = 1 }, false);
        var range = File.ReadAllBytes(target).AsSpan(16000 * 4, 16000 * 4).ToArray();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(range)).ToLowerInvariant(), clip["clipHash"]!.ToString());
        Assert.Equal(1, clip["startSeconds"]!.GetValue<double>()); Assert.Equal(1, clip["durationSeconds"]!.GetValue<double>());
    }
    [Theory]
    [InlineData(-1, 1)] [InlineData(0, 121)] [InlineData(3, 1)] [InlineData(999999, 0)]
    public async Task InvalidOrEmptyRangesFail(int start, int duration)
    {
        using var directory = new TestDirectory(); var path = Path.Combine(directory.Path, "input.wav"); Wave(path);
        await Assert.ThrowsAnyAsync<Exception>(() => Worker(new JsonObject { ["protocol"] = 3, ["operation"] = "audio_classify",
            ["models"] = directory.Path, ["path"] = path, ["startSeconds"] = start, ["durationSeconds"] = duration }, false));
    }
    [Fact]
    public async Task PreparationDoesNotDeleteAnotherOwnersPartialFile()
    {
        using var directory = new TestDirectory(); var source = Path.Combine(directory.Path, "input.wav"); Wave(source);
        var target = Path.Combine(directory.Path, "normalized.f32"); var partial = Path.ChangeExtension(target, ".partial");
        File.WriteAllText(partial, "another owner's output");
        await Assert.ThrowsAnyAsync<Exception>(() => Worker(Request(source, target)));
        Assert.Equal("another owner's output", File.ReadAllText(partial)); Assert.False(File.Exists(target));
    }
}
