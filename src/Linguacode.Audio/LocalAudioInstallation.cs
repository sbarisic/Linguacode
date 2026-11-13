using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Compressors.BZip2;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Linguacode.Core;

namespace Linguacode.Audio;

public sealed record AudioReadiness(bool Transcription, bool Classification, string Location, string Message, bool Diarization = false)
{
    public bool Ready(string tool) => tool switch { "audio_transcribe" => Transcription, "audio_classify" => Classification, "audio_diarize" => Diarization, _ => Transcription && Classification };
}

public static partial class LocalAudioInstallation
{
    public static string Bundle => Path.Combine(AppContext.BaseDirectory, "LocalAudio");
    public static string Worker => Path.Combine(AppContext.BaseDirectory, "AudioWorker", "Linguacode.AudioWorker.exe");
    internal static string Fingerprint(string bundle) => Convert.ToHexString(SHA256.HashData(
        File.ReadAllBytes(Path.Combine(bundle, "downloads.json")).Concat(File.ReadAllBytes(Path.ChangeExtension(Worker, ".dll"))).ToArray()));
    public static AudioReadiness Status(string dataDirectory) => Status(dataDirectory, Bundle);
    internal static AudioReadiness Status(string dataDirectory, string bundle)
    {
        var root = Path.Combine(dataDirectory, "audio");
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
                return new(false, false, root, "Local audio requires Windows x64.");
            if (!File.Exists(Worker)) return new(false, false, root, "The AudioWorker folder is missing. Restore the complete application build.");
            var name = File.ReadAllText(Path.Combine(root, "current")).Trim();
            if (!Guid.TryParseExact(name, "N", out _)) throw new IOException("Invalid installation pointer.");
            var directory = Path.Combine(root, "installations", name);
            var ready = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "ready.json"))) ?? throw new IOException("Invalid audio readiness record.");
            var update = ready["fingerprint"]?.ToString() != Fingerprint(bundle);
            var runtime = !update && JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "helper", "downloads.json")))?["helperProtocol"]?.GetValue<int>() == 3;
            var whisper = runtime && new[] { "ggml-small-q8_0.bin", "ggml-silero-v6.2.0.bin" }.All(n => File.Exists(Path.Combine(directory, "models", "whisper", n)));
            var yamnet = runtime && new[] { "saved_model.pb", "assets/yamnet_class_map.csv", "variables/variables.index", "variables/variables.data-00000-of-00001" }.All(n => File.Exists(Path.Combine(directory, "models", "yamnet", n)));
            var diarization = runtime && new[] { "model.onnx", "nemo_en_titanet_small.onnx" }.All(n => File.Exists(Path.Combine(directory, "models", "diarization", n)));
            return new(whisper, yamnet, directory,
                $"Whisper small multilingual / Whisper.net 1.9.1 / CPU Q8_0: {(whisper ? "ready" : "missing files")}\nYAMNet 1 / TensorFlow C 2.18.1 / 521 classes: {(yamnet ? "ready" : "missing files")}\nLocation: {directory}" +
                (whisper && yamnet ? "\nInference is local and offline. Returned text and labels may enter your chat provider's context." : "\nRun Linguacode.AudioHost audio setup.") +
                (update ? "\nThe bundled native engines or model manifest changed; run Linguacode.AudioHost audio setup. Run setup before processing." : "") +
                $"\nSpeaker separation / sherpa-onnx 1.13.8: {(diarization ? "ready" : "unavailable; run Linguacode.AudioHost audio setup")}", diarization);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { return new(false, false, root, "Local audio is unavailable. Run Linguacode.AudioHost audio setup. " + ex.Message); }
    }

    public static async Task Setup(string dataDirectory, Action<string> progress, CancellationToken cancellation)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        if (!File.Exists(Worker)) throw new UserFacingException("The AudioWorker folder is missing. Restore the complete application build.");
        try
        {
            await Setup(dataDirectory, progress, cancellation, Bundle, http, LocalAudioProcess.Infer);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        { throw new UserFacingException("Local audio setup failed: " + ex.Message + " Previous installation retained. Inspect Linguacode.AudioHost audio status, then retry Linguacode.AudioHost audio setup."); }
    }
    internal static async Task Setup(string dataDirectory, Action<string> progress, CancellationToken cancellation, string bundle, HttpClient http,
        Func<string, JsonObject, Action<string>, CancellationToken, Task<JsonObject>> infer)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new UserFacingException("Managed audio setup requires Windows x64.");
        var root = Path.GetFullPath(Path.Combine(dataDirectory, "audio")); Directory.CreateDirectory(root);
        // Cross-process setup lock; inference continues using the previous generation.
        using var setupLock = new FileStream(Path.Combine(root, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "downloads.json")))!;
        var status = Status(dataDirectory, bundle);
        if (status.Transcription && status.Classification && (manifest["diarization"] is null || status.Diarization) &&
            JsonNode.Parse(File.ReadAllText(Path.Combine(status.Location, "ready.json")))?["fingerprint"]?.ToString() == Fingerprint(bundle))
        { CleanupLocked(dataDirectory, progress); progress(status.Message); return; }
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root, "installations", id); Directory.CreateDirectory(directory);
        var downloads = Path.Combine(root, "downloads"); Directory.CreateDirectory(downloads);
        var files = new Dictionary<string, string>();
        try
        {
            foreach (var item in manifest["downloads"]!.AsArray())
            {
                var name = item!["path"]!.ToString(); var hash = item["sha256"]!.ToString(); var size = item["size"]!.GetValue<long>();
                var cached = Path.Combine(downloads, hash);
                if (!await Matches(cached, hash, size, cancellation))
                {
                    progress("Downloading " + name);
                    var partial = cached + ".partial";
                    using var response = await http.GetAsync(item["url"]!.ToString(), HttpCompletionOption.ResponseHeadersRead, cancellation);
                    response.EnsureSuccessStatusCode();
                    await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
                    await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920]; long written = 0; int count;
                        while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                        {
                            written += count; if (written > size) throw new IOException("Download exceeds manifest size: " + name);
                            await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                        }
                    }
                    if (!await Matches(partial, hash, size, cancellation)) throw new IOException("Download checksum mismatch: " + name);
                    File.Move(partial, cached, true);
                }
                files[name] = cached;
            }
            progress("Installing verified local models");
            var models = Path.Combine(directory, "models"); Directory.CreateDirectory(Path.Combine(models, "whisper"));
            ExtractTar(files["yamnet.tar.gz"], Path.Combine(models, "yamnet"));
            foreach (var file in files.Where(f => f.Key.StartsWith("whisper/", StringComparison.Ordinal)))
                File.Copy(file.Value, Path.Combine(models, file.Key));
            if (!await Matches(Path.Combine(models, "yamnet", "assets", "yamnet_class_map.csv"), manifest["classMapSha256"]!.ToString(), null, cancellation))
                throw new IOException("YAMNet class map checksum mismatch.");
            var helper = Path.Combine(directory, "helper"); Directory.CreateDirectory(helper);
            File.Copy(Path.Combine(bundle, "downloads.json"), Path.Combine(helper, "downloads.json"));
            if (files.TryGetValue("segmentation.tar.bz2", out var segmentation))
            {
                var speakers = Path.Combine(models, "diarization"); Directory.CreateDirectory(speakers);
                File.Copy(files["diarization/nemo_en_titanet_small.onnx"], Path.Combine(speakers, "nemo_en_titanet_small.onnx"));
                using var compressed = File.OpenRead(segmentation);
                using var decompressed = BZip2Stream.Create(compressed, SharpCompress.Compressors.CompressionMode.Decompress, false);
                using var tar = new TarReader(decompressed);
                TarEntry? entry;
                var found = new HashSet<string>();
                while ((entry = tar.GetNextEntry()) is not null)
                {
                    var name = Path.GetFileName(entry.Name);
                    if (name is not ("model.onnx" or "LICENSE" or "README.md")) continue;
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.Length > 16 * 1024 * 1024 || !found.Add(name))
                        throw new IOException("Invalid segmentation archive member.");
                    entry.ExtractToFile(Path.Combine(speakers, name), false);
                }
                if (!found.Contains("model.onnx")) throw new IOException("Segmentation archive has no model.");
            }
            progress("Running offline self-tests for installed engines");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var result = await infer(directory, new() { ["protocol"] = 3, ["operation"] = "selftest", ["models"] = models }, progress, timeout.Token);
            if (result["transcription"]?["status"]?.ToString() != "succeeded" || result["classification"]?["status"]?.ToString() != "succeeded" || manifest["diarization"] is not null && result["diarization"]?["status"]?.ToString() != "succeeded")
                throw new IOException("Audio self-test failed: " + string.Join("; ", new[] { "transcription", "classification", "diarization" }
                    .Where(name => result[name]?["status"]?.ToString() != "succeeded")
                    .Select(name => name + ": " + result[name]?["error"])));
            await File.WriteAllTextAsync(Path.Combine(directory, "ready.json"), Json.Serialize(new { fingerprint = Fingerprint(bundle), installedAt = DateTimeOffset.UtcNow }), cancellation);
            // Generation paths never move: SavedModel asset paths remain valid.
            var pointer = Path.Combine(root, "current.new"); await File.WriteAllTextAsync(pointer, id, cancellation);
            File.Move(pointer, Path.Combine(root, "current"), true);
            File.Delete(Path.Combine(root, "last-error.txt"));
            progress(Status(dataDirectory, bundle).Message);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "last-error.txt"), ex.Message, CancellationToken.None);
            // Preserve previous current and resumable verified downloads. Incomplete generation is not advertised.
            throw;
        }
        finally { CleanupLocked(dataDirectory, progress); }
    }

    internal static async Task<bool> Matches(string path, string hash, long? size, CancellationToken cancellation)
    {
        if (!File.Exists(path)) return false;
        await using var file = File.OpenRead(path);
        return (size is null || file.Length == size) && Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    private static void ExtractTar(string path, string target)
    {
        Directory.CreateDirectory(target);
        using var file = File.OpenRead(path); using var gzip = new GZipStream(file, CompressionMode.Decompress); using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                throw new IOException("Unsupported link or special entry in audio archive.");
            var destination = Path.GetFullPath(Path.Combine(target, entry.Name));
            if (!destination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe archive path.");
            if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(destination);
            else { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); entry.ExtractToFile(destination, false); }
        }
    }
}
