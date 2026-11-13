using System.Diagnostics;
using System.Text.Json.Nodes;
using Whisper.net;
using SherpaOnnx;

namespace Linguacode.AudioWorker;

internal sealed class SpeechEngines(string root) : IDisposable
{
    private WhisperFactory? whisper;
    private WhisperVadFactory? vadFactory;
    private WhisperVadProcessor? vad;
    private OfflineSpeakerDiarization? speakers;
    private SpeakerEmbeddingExtractor? embeddings;
    public async Task<JsonObject> Transcribe(float[] samples, double offset, string language, string? hints)
    {
        if (language != "auto" && !WhisperFactory.GetSupportedLanguages().Contains(language)) throw new IOException("Unsupported Whisper language code.");
        if (hints is { Length: > 1000 } || hints?.Any(char.IsControl) == true) throw new IOException("Invalid transcription spelling hints.");
        var loading = Stopwatch.StartNew();
        whisper ??= WhisperFactory.FromPath(Path.Combine(root, "whisper", "ggml-small-q8_0.bin"), new WhisperFactoryOptions { UseGpu = false });
        vadFactory ??= WhisperVadFactory.FromPath(Path.Combine(root, "whisper", "ggml-silero-v6.2.0.bin"), new WhisperFactoryOptions { UseGpu = false });
        vad ??= vadFactory.CreateBuilder().WithThreads(4).WithUseGpu(false).WithThreshold(.5f)
            .WithMinSpeechDuration(TimeSpan.Zero).WithMinSilenceDuration(TimeSpan.FromSeconds(2))
            .WithSpeechPadding(TimeSpan.FromMilliseconds(400)).Build();
        var segments = new JsonArray();
        var result = new JsonObject { ["status"] = "succeeded", ["model"] = "Whisper small multilingual Q8_0", ["computeType"] = "q8_0",
            ["engineVersion"] = "Whisper.net 1.9.1", ["loadSeconds"] = loading.Elapsed.TotalSeconds, ["language"] = null,
            ["segments"] = segments, ["noSpeech"] = true };
        if (samples.All(s => Math.Abs(s) < .0001f)) return result;
        var builder = whisper.CreateBuilder().WithThreads(4).WithNoContext().WithTokenTimestamps().WithProbabilities()
            .WithBeamSearchSamplingStrategy(b => b.WithBeamSize(5)).WithLanguage(language == "auto" ? "auto" : language);
        if (!string.IsNullOrWhiteSpace(hints)) builder.WithPrompt(hints);
        using var processor = builder.Build();
        var speech = vad.DetectSpeech(samples);
        if (speech.Count == 0) return result;
        // Detect on speech, then fix the language for every VAD span of this request.
        var detectStart = Math.Clamp((int)(speech[0].Start.TotalSeconds * AudioDecoder.Rate), 0, samples.Length);
        var detectLength = Math.Min(samples.Length - detectStart, 30 * AudioDecoder.Rate);
        var detection = processor.DetectLanguageWithProbability(samples.AsSpan(detectStart, detectLength));
        if (language == "auto") processor.ChangeLanguage(detection.language);
        result["languageScore"] = float.IsFinite(detection.probability) ? Math.Clamp(detection.probability, 0, 1) : throw new IOException("Invalid language probability.");
        foreach (var span in speech)
        {
            var begin = Math.Clamp((int)Math.Round(span.Start.TotalSeconds * AudioDecoder.Rate), 0, samples.Length);
            var end = Math.Clamp((int)Math.Round(span.End.TotalSeconds * AudioDecoder.Rate), begin, samples.Length);
            if (end <= begin) continue;
            var baseTime = offset + begin / (double)AudioDecoder.Rate; var duration = (end - begin) / (double)AudioDecoder.Rate;
            await foreach (var segment in processor.ProcessAsync(samples.AsMemory(begin, end - begin)))
            {
                var from = Math.Clamp(segment.Start.TotalSeconds, 0, duration); var to = Math.Clamp(segment.End.TotalSeconds, from, duration);
                if (string.IsNullOrWhiteSpace(segment.Text) || to <= from) continue;
                var words = Words(segment.Tokens, baseTime, from, to);
                segments.Add(new JsonObject { ["startSeconds"] = baseTime + from, ["endSeconds"] = baseTime + to,
                    ["text"] = segment.Text.Trim(), ["words"] = words });
            }
        }
        result["noSpeech"] = segments.Count == 0;
        result["language"] = segments.Count == 0 ? null : language == "auto" ? detection.language : language;
        return result;
    }
    internal static JsonArray Words(IEnumerable<WhisperToken> tokens, double offset, double begin, double end)
    {
        var words = new JsonArray(); string text = ""; double from = begin, to = begin;
        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(text)) words.Add(new JsonObject { ["text"] = text, ["startSeconds"] = offset + from, ["endSeconds"] = offset + Math.Max(from, to) });
            text = "";
        }
        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token.Text) || token.Text.StartsWith("[_", StringComparison.Ordinal) || token.Text.StartsWith("<|", StringComparison.Ordinal)) continue;
            // whisper.cpp token timestamps use 10 ms ticks. Keep subword tokens together.
            if (char.IsWhiteSpace(token.Text[0]) && text.Length > 0) Flush();
            if (text.Length == 0) from = Math.Clamp(token.Start * .01, begin, end);
            to = Math.Clamp(token.End * .01, from, end); text += token.Text;
        }
        Flush(); return words;
    }
    public JsonObject Diarize(float[] samples, double offset)
    {
        var loading = Stopwatch.StartNew();
        if (speakers is null)
        {
            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = Path.Combine(root, "diarization", "model.onnx");
            config.Segmentation.NumThreads = 4; config.Segmentation.Provider = "cpu";
            config.Embedding.Model = Path.Combine(root, "diarization", "nemo_en_titanet_small.onnx");
            config.Embedding.NumThreads = 4; config.Embedding.Provider = "cpu";
            config.Clustering.NumClusters = -1; config.Clustering.Threshold = .5f;
            config.MinDurationOn = .3f; config.MinDurationOff = .5f;
            foreach (var file in new[] { config.Segmentation.Pyannote.Model, config.Embedding.Model })
                if (!File.Exists(file)) throw new IOException("Missing speaker model: " + Path.GetFileName(file));
            speakers = new OfflineSpeakerDiarization(config);
            if (speakers.SampleRate != AudioDecoder.Rate) throw new IOException("Invalid speaker model sample rate.");
            embeddings = new SpeakerEmbeddingExtractor(config.Embedding);
            if (embeddings.Dim is < 16 or > 1024) throw new IOException("Invalid speaker embedding dimensions.");
        }
        var load = loading.Elapsed.TotalSeconds; var turns = new JsonArray(); var names = new Dictionary<int, string>();
        var spans = new List<(double Start, double End, string Speaker)>();
        if (samples.Any(s => Math.Abs(s) >= .0001f))
            foreach (var segment in speakers.Process(samples).OrderBy(s => s.Start))
            {
                if (!float.IsFinite(segment.Start) || !float.IsFinite(segment.End)) throw new IOException("Invalid speaker timestamps.");
                if (!names.TryGetValue(segment.Speaker, out var name)) names[segment.Speaker] = name = "Speaker " + (names.Count + 1);
                var start = Math.Clamp(segment.Start, 0, samples.Length / (double)AudioDecoder.Rate);
                var end = Math.Clamp(segment.End, start, samples.Length / (double)AudioDecoder.Rate);
                if (end <= start) continue; spans.Add((start, end, name));
                turns.Add(new JsonObject { ["startSeconds"] = offset + start, ["endSeconds"] = offset + end, ["speaker"] = name });
            }
        var vectors = new JsonArray();
        foreach (var name in spans.Select(s => s.Speaker).Distinct())
        {
            var selected = spans.Where(s => s.Speaker == name && s.End - s.Start >= 1.5 &&
                !spans.Any(other => other.Speaker != name && other.Start < s.End && other.End > s.Start))
                .OrderByDescending(s => s.End - s.Start).Take(5);
            var rows = new List<float[]>(); double seconds = 0;
            foreach (var span in selected)
            {
                var first = (int)Math.Round(span.Start * AudioDecoder.Rate);
                var last = Math.Min((int)Math.Round(span.End * AudioDecoder.Rate), first + 10 * AudioDecoder.Rate);
                using var stream = embeddings!.CreateStream(); stream.AcceptWaveform(AudioDecoder.Rate, samples[first..last]); stream.InputFinished();
                if (!embeddings.IsReady(stream)) continue;
                rows.Add(Normalize(embeddings.Compute(stream))); seconds += (last - first) / (double)AudioDecoder.Rate;
            }
            if (rows.Count == 0) continue;
            var mean = Normalize(Enumerable.Range(0, rows[0].Length).Select(i => rows.Average(r => r[i])).ToArray());
            vectors.Add(new JsonObject { ["speaker"] = name, ["seconds"] = seconds, ["vector"] = new JsonArray(mean.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) });
        }
        return new JsonObject { ["status"] = "succeeded", ["model"] = "pyannote segmentation 3.0 FP32 + NeMo TitaNet small",
            ["engineVersion"] = "sherpa-onnx 1.13.8", ["loadSeconds"] = load, ["speakerCount"] = spans.Select(s => s.Speaker).Distinct().Count(),
            ["turns"] = turns, ["embeddings"] = vectors, ["note"] = "Estimated anonymous speakers local to this analysis. Short and overlapping speech may be misassigned." };
    }
    private static float[] Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
        if (!double.IsFinite(norm) || norm <= 0 || vector.Length is < 16 or > 1024) throw new IOException("Invalid speaker embedding.");
        return vector.Select(x => (float)(x / norm)).ToArray();
    }
    public void Dispose() { embeddings?.Dispose(); speakers?.Dispose(); vad?.Dispose(); vadFactory?.Dispose(); whisper?.Dispose(); }
}
