using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Whisper.net.LibraryLoader;

namespace Linguacode.AudioWorker;

internal static partial class Program
{
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint flags);
    public static async Task<int> Main(string[] args)
    {
        SetDefaultDllDirectories(0x1000); // application, explicitly added directories and System32 only
        NativeLibrary.SetDllImportResolver(typeof(Yamnet).Assembly, (name, _, _) => name == "tensorflow.dll"
            ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, name)) : 0);
        NativeLibrary.SetDllImportResolver(typeof(SherpaOnnx.OfflineSpeakerDiarization).Assembly, (name, _, _) => name == "sherpa-onnx-c-api"
            ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "sherpa-onnx-c-api.dll")) : 0);
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
        AudioDecoder.Initialize(AppContext.BaseDirectory);
        Environment.SetEnvironmentVariable("TF_CPP_MIN_LOG_LEVEL", "2");
        Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", "-1");
        Environment.SetEnvironmentVariable("OMP_NUM_THREADS", "4");
        var output = Console.Out; Console.SetOut(Console.Error);
        SpeechEngines? speech = null; Yamnet? yamnet = null; string? models = null;
        try
        {
            var server = args.SequenceEqual(new[] { "--server" });
            if (!server && !args.SequenceEqual(new[] { "--prepare" }) && args.Length != 0) throw new IOException("Invalid worker command.");
            while (true)
            {
                string? id = null;
                void Emit(string kind, string? stage = null, JsonObject? result = null, string? error = null)
                {
                    var message = new JsonObject { ["protocol"] = 3, ["requestId"] = id, ["type"] = kind };
                    if (stage is not null) message["stage"] = stage;
                    if (result is not null) message["result"] = result;
                    if (error is not null) message["error"] = error;
                    var text = message.ToJsonString(); if (Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024) throw new IOException("Worker response exceeds 2 MiB.");
                    output.WriteLine(text); output.Flush();
                }
                try
                {
                    var line = ReadBounded(Console.In, args.Contains("--prepare") ? 16 * 1024 * 1024 : 16384);
                    if (line is null) return 0;
                    var request = JsonNode.Parse(line)!.AsObject();
                    if (args.Contains("--prepare")) { output.WriteLine(AudioDecoder.Prepare(request).ToJsonString()); return 0; }
                    if (request["protocol"]?.GetValue<int>() != 3) throw new IOException("Unsupported native worker protocol.");
                    id = request["requestId"]?.GetValue<string>();
                    if (server && (id is null || id.Length is < 1 or > 64)) throw new IOException("Invalid request identity.");
                    var root = Path.GetFullPath(request["models"]!.GetValue<string>());
                    if (models != root) { yamnet?.Dispose(); speech?.Dispose(); yamnet = null; speech = new SpeechEngines(root); models = root; }
                    var operation = request["operation"]!.GetValue<string>();
                    string[] allowed = operation switch
                    {
                        "audio_transcribe" => ["transcription", "diarization"], "audio_diarize" => ["diarization"],
                        "audio_classify" => ["classification"], "audio_listen" or "selftest" => ["transcription", "classification", "diarization"],
                        _ => throw new IOException("Unsupported audio operation.")
                    };
                    var selected = request["components"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? allowed;
                    if (selected.Length == 0 || selected.Distinct().Count() != selected.Length || selected.Except(allowed).Any()) throw new IOException("Invalid audio component selection.");
                    Emit("progress", "decoding"); var timer = Stopwatch.StartNew();
                    var (samples, details) = operation == "selftest" ? (new float[16000], new JsonObject { ["startSeconds"] = 0d, ["durationSeconds"] = 1d }) :
                        AudioDecoder.Clip(request["path"]!.GetValue<string>(), request["startSeconds"]!.GetValue<int>(), request["durationSeconds"]!.GetValue<int>());
                    details["decodeSeconds"] = timer.Elapsed.TotalSeconds;
                    foreach (var name in selected)
                    {
                        Emit("progress", name); timer.Restart(); JsonObject component;
                        try
                        {
                            var start = details["startSeconds"]!.GetValue<double>();
                            switch (name)
                            {
                                case "transcription": component = await speech!.Transcribe(samples, start, request["language"]?.GetValue<string>() ?? "auto", request["transcriptionHints"]?.GetValue<string>()); break;
                                case "diarization": component = speech!.Diarize(samples, start); break;
                                default:
                                    var load = Stopwatch.StartNew(); yamnet ??= new Yamnet(Path.Combine(root, "yamnet")); var seconds = load.Elapsed.TotalSeconds;
                                    component = yamnet.Classify(samples, start); component["loadSeconds"] = seconds; break;
                            }
                        }
                        catch (Exception ex) { component = new JsonObject { ["status"] = "failed", ["error"] = ex.Message[..Math.Min(500, ex.Message.Length)] }; }
                        component["elapsedSeconds"] = timer.Elapsed.TotalSeconds; details[name] = component;
                    }
                    Emit("result", result: details); if (!server) return 0; Emit("complete");
                }
                catch (Exception ex) { Emit("error", error: ex.Message[..Math.Min(500, ex.Message.Length)]); return 1; }
            }
        }
        finally { yamnet?.Dispose(); speech?.Dispose(); }
    }
    private static string? ReadBounded(TextReader reader, int maximum)
    {
        var line = new StringBuilder(); int c;
        while ((c = reader.Read()) >= 0 && c != '\n') { if (line.Length >= maximum) throw new IOException("Worker request is too large."); line.Append((char)c); }
        if (c < 0 && line.Length == 0) return null;
        var text = line.ToString(); if (Encoding.UTF8.GetByteCount(text) > maximum) throw new IOException("Worker request is too large."); return text;
    }
}
