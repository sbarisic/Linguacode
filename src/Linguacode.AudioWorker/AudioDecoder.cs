using FFmpeg.AutoGen;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Linguacode.AudioWorker;

/// <summary>One bounded decoder for clips and complete recordings. Positions are relative to decoded audio, after skip samples.</summary>
public static unsafe class AudioDecoder
{
    public const int Rate = 16000;
    public static void Initialize(string directory) => ffmpeg.RootPath = directory;
    private static void Check(int result)
    {
        if (result >= 0) return;
        var text = stackalloc byte[1024]; ffmpeg.av_strerror(result, text, 1024);
        throw new IOException("Audio decoder: " + Marshal.PtrToStringUTF8((nint)text));
    }
    public static void Decode(string path, Func<float[], long, bool> consume, JsonObject? expected = null, Action<double?>? sourceDuration = null)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || !File.Exists(path))
            throw new IOException("Supply a local audio file.");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3")) throw new IOException("Only WAV and MP3 sources are supported.");
        AVFormatContext* format = null; AVCodecContext* codec = null; SwrContext* resampler = null;
        AVPacket* packet = null; AVFrame* frame = null; AVDictionary* options = null;
        try
        {
            Check(ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0));
            Check(ffmpeg.avformat_open_input(&format, path, ffmpeg.av_find_input_format(extension[1..]), &options));
            Check(ffmpeg.avformat_find_stream_info(format, null));
            sourceDuration?.Invoke(format->duration == ffmpeg.AV_NOPTS_VALUE ? null : format->duration / (double)ffmpeg.AV_TIME_BASE);
            var streamIndex = -1;
            for (var i = 0; i < format->nb_streams; i++)
                if (format->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                { if (streamIndex >= 0) throw new IOException("Expected exactly one audio stream."); streamIndex = i; }
            if (streamIndex < 0) throw new IOException("No audio stream.");
            var stream = format->streams[streamIndex]; var parameters = stream->codecpar;
            if (parameters->sample_rate is <= 0 or > 192000 || parameters->ch_layout.nb_channels is < 1 or > 8)
                throw new IOException("Unsupported source format.");
            if (expected is not null)
            {
                if (expected["sampleRate"] is { } rate && rate.GetValue<int>() != parameters->sample_rate ||
                    expected["channels"] is { } channels && channels.GetValue<int>() != parameters->ch_layout.nb_channels)
                    throw new IOException("Capture format disagrees with manifest.");
                if (expected["bitsPerSample"] is { } bitNode)
                {
                    var bits = bitNode.GetValue<int>(); var floating = expected["floatingPoint"]?.GetValue<bool>() == true;
                    var name = "pcm_" + (floating ? "f" : bits == 8 ? "u" : "s") + bits + (bits > 8 ? "le" : "");
                    if (ffmpeg.avcodec_get_name(parameters->codec_id) != name) throw new IOException("Capture sample encoding disagrees with manifest.");
                }
            }
            var decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
            if (decoder == null) throw new IOException("Audio codec unavailable.");
            codec = ffmpeg.avcodec_alloc_context3(decoder); if (codec == null) throw new OutOfMemoryException();
            Check(ffmpeg.avcodec_parameters_to_context(codec, parameters)); codec->thread_count = 1;
            // libavcodec needs this to adjust frame timestamps when trimming MP3 encoder delay.
            codec->pkt_timebase = stream->time_base;
            Check(ffmpeg.avcodec_open2(codec, decoder, null));
            var mono = new AVChannelLayout(); ffmpeg.av_channel_layout_default(&mono, 1);
            try { Check(ffmpeg.swr_alloc_set_opts2(&resampler, &mono, AVSampleFormat.AV_SAMPLE_FMT_FLT, Rate,
                &codec->ch_layout, codec->sample_fmt, codec->sample_rate, 0, null)); }
            finally { ffmpeg.av_channel_layout_uninit(&mono); }
            Check(ffmpeg.swr_init(resampler));
            packet = ffmpeg.av_packet_alloc(); frame = ffmpeg.av_frame_alloc();
            if (packet == null || frame == null) throw new OutOfMemoryException();
            var activeResampler = resampler; var activeCodec = codec; var activeFrame = frame;
            long cursor = 0; long? origin = null; long expectedInput = 0;
            bool Convert(AVFrame* current)
            {
                var inputCount = current == null ? 0 : current->nb_samples;
                var capacity = ffmpeg.swr_get_out_samples(activeResampler, inputCount);
                if (capacity is < 0 or > 1024 * 1024) throw new IOException("Invalid activeResampler buffer size.");
                var output = new float[capacity]; int count;
                if (current != null && current->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE)
                {
                    var position = ffmpeg.av_rescale_q(current->best_effort_timestamp, stream->time_base, new AVRational { num = 1, den = Rate });
                    origin ??= position;
                    var relative = position - origin.Value;
                    var difference = relative - expectedInput;
                    if (difference < -2) throw new IOException("Source timestamps moved backwards.");
                    if (difference > 2) cursor += difference;
                    expectedInput = relative + (long)Math.Round(inputCount * (double)Rate / activeCodec->sample_rate);
                }
                fixed (float* samples = output)
                {
                    byte* destination = (byte*)samples;
                    count = ffmpeg.swr_convert(activeResampler, &destination, capacity, current == null ? null : current->extended_data, inputCount);
                }
                Check(count); if (count == 0) return true;
                Array.Resize(ref output, count);
                for (var i = 0; i < count; i++)
                { if (!float.IsFinite(output[i])) throw new IOException("Audio contains non-finite samples."); output[i] = Math.Clamp(output[i], -1, 1); }
                var keepGoing = consume(output, cursor); cursor += count; return keepGoing;
            }
            bool Receive()
            {
                while (true)
                {
                    var result = ffmpeg.avcodec_receive_frame(activeCodec, activeFrame);
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return true;
                    Check(result);
                    try { if (!Convert(activeFrame)) return false; } finally { ffmpeg.av_frame_unref(activeFrame); }
                }
            }
            while (true)
            {
                var read = ffmpeg.av_read_frame(format, packet);
                if (read == ffmpeg.AVERROR_EOF) break;
                Check(read);
                try
                {
                    if (packet->stream_index != streamIndex) continue;
                    Check(ffmpeg.avcodec_send_packet(activeCodec, packet));
                    if (!Receive()) return;
                }
                finally { ffmpeg.av_packet_unref(packet); }
            }
            Check(ffmpeg.avcodec_send_packet(activeCodec, null)); if (!Receive()) return;
            // Drain delayed activeResampler output; skip-sample trimming is performed by libavcodec.
            while (ffmpeg.swr_get_delay(activeResampler, Rate) > 0)
            { var before = cursor; if (!Convert(null) || cursor == before) break; }
        }
        finally
        {
            ffmpeg.av_dict_free(&options); ffmpeg.av_frame_free(&frame); ffmpeg.av_packet_free(&packet);
            ffmpeg.swr_free(&resampler); ffmpeg.avcodec_free_context(&codec); ffmpeg.avformat_close_input(&format);
        }
    }

    public static (float[] Samples, JsonObject Details) Clip(string path, int start, int duration)
    {
        if (start < 0 || duration is < 0 or > 120) throw new IOException("Start must be nonnegative and duration 0..120 seconds.");
        var first = (long)start * Rate; var limit = (duration == 0 ? 120 : duration) * Rate;
        var samples = new List<float>(limit); long last = 0; bool capped = false; double? sourceLength = null;
        Decode(path, (block, position) =>
        {
            last = position + block.Length;
            var left = (int)Math.Clamp(first - position, 0, block.Length);
            var right = (int)Math.Clamp(first + limit - position, 0, block.Length);
            if (right > left) samples.AddRange(block.AsSpan(left, right - left).ToArray());
            capped = last >= first + limit; return !capped;
        }, sourceDuration: seconds => sourceLength = seconds);
        if (samples.Count == 0) throw new IOException("Selected audio range is empty.");
        var result = samples.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(result.AsSpan()))).ToLowerInvariant();
        return (result, new JsonObject { ["startSeconds"] = (double)start, ["durationSeconds"] = result.Length / (double)Rate,
            ["sourceDurationSeconds"] = sourceLength, ["sampleRate"] = Rate, ["channels"] = 1,
            ["partial"] = start > 0 || sourceLength is null || start + result.Length / (double)Rate < sourceLength - .01,
            ["clipHash"] = hash, ["clipEncoding"] = "float32-le-mono-16000" });
    }

    public static JsonObject Prepare(JsonObject request)
    {
        var destination = Path.GetFullPath(request["destination"]!.GetValue<string>());
        var partial = Path.ChangeExtension(destination, ".partial"); var maximum = request["maxBytes"]!.GetValue<long>();
        var inputs = request["inputs"]!.AsArray(); if (inputs.Count is 0 or > 100000) throw new IOException("Invalid input count.");
        long cursor = 0; var mappings = new JsonArray(); var coverage = new JsonArray(); var ownsPartial = false;
        try
        {
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsPartial = true;
                void Write(float[] samples)
                {
                    if (samples.Length > maximum / 4 - cursor) throw new IOException("Analysis storage budget reached during decoding.");
                    if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < 1073741824L + samples.Length * 4L)
                        throw new IOException("Analysis paused at 1 GiB free-disk reserve.");
                    output.Write(MemoryMarshal.AsBytes(samples.AsSpan())); cursor += samples.Length;
                }
                void Pad(long end) { while (cursor < end) Write(new float[(int)Math.Min(Rate, end - cursor)]); }
                foreach (var input in inputs)
                {
                    var item = input!.AsObject(); var start = item["start"]?.GetValue<long>() ?? cursor;
                    if (start < cursor) throw new IOException("Overlapping source chunks."); Pad(start);
                    var sourceStart = cursor; var path = item["path"]!.GetValue<string>();
                    Decode(path, (samples, position) =>
                    {
                        var expected = sourceStart + position;
                        if (expected < cursor - 2) throw new IOException("Source timestamps moved backwards.");
                        if (expected > cursor + 2)
                        { coverage.Add(new JsonObject { ["start"] = cursor, ["frames"] = expected - cursor, ["kind"] = "TimestampGap" }); Pad(expected); }
                        Write(samples); return true;
                    }, item);
                    if (item["frames"] is { } framesNode)
                    {
                        var frames = framesNode.GetValue<long>(); var difference = cursor - sourceStart - frames;
                        if (frames < 0 || Math.Abs(difference) > 1) throw new IOException("Decoded capture length disagrees with manifest.");
                        if (difference == 1) { output.SetLength(output.Length - 4); output.Position = output.Length; cursor--; }
                        else if (difference == -1) Write(new float[1]);
                    }
                    mappings.Add(new JsonObject { ["file"] = Path.GetFileName(path), ["start"] = sourceStart, ["end"] = cursor });
                }
                if (cursor == 0) throw new IOException("Audio is empty."); output.Flush(true);
            }
            using var file = File.OpenRead(partial); var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); file.Dispose();
            File.Move(partial, destination, true);
            return new JsonObject { ["version"] = 2, ["frames"] = cursor, ["hash"] = hash, ["mappings"] = mappings, ["coverage"] = coverage };
        }
        finally { if (ownsPartial && File.Exists(partial)) File.Delete(partial); }
    }
}
