using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// Explicit model acceptance run: <build directory> <model directory> <report.json>.
// Models must already have been downloaded by audio setup; this runner never downloads anything.
var verifyOnly = args.Length == 2 && args[0] == "--verify-report";
var scenariosOnly = args.Length == 4 && args[3] == "--scenarios";
if (args.Length != 3 && !verifyOnly && !scenariosOnly) { Console.Error.WriteLine("Usage: Linguacode.Acceptance <build> <models> <report.json> [--scenarios], or --verify-report <report.json>"); return 2; }
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "Linguacode.slnx"))) root = root.Parent;
if (root is null) throw new IOException("Run from the repository.");
var fixtures = Path.Combine(root.FullName, "tests", "fixtures", "speech");
var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "manifest.json")))!;
if (verifyOnly)
{
    var saved = JsonNode.Parse(File.ReadAllText(args[1]))!; var success = true;
    foreach (var record in saved["records"]!.AsArray())
    {
        var file = record!["file"]!.GetValue<string>(); var current = record["native"]!;
        var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "baseline", file + ".baseline.json")))!;
        var windows = Close(NormalizeWindows(baseline["classification"]!["windows"]!), NormalizeWindows(current["classification"]!["windows"]!), 0.0001);
        var turns = Close(baseline["diarization"]!["turns"], current["diarization"]!["turns"], .002);
        var embeddings = Close(baseline["diarization"]!["embeddings"], current["diarization"]!["embeddings"], .0001);
        record["classificationWindowsMatch"] = windows; record["speakerTurnsMatch"] = turns; record["speakerEmbeddingsMatch"] = embeddings;
        success &= windows && turns && embeddings;
        if (!windows || !turns || !embeddings) Console.WriteLine($"{file}: windows={windows}, speaker turns={turns}, embeddings={embeddings}");
    }
    saved["componentBaselineComparisonPassed"] = success;
    await File.WriteAllTextAsync(args[1], saved.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(success ? "All classification windows, speaker turns and embeddings match baseline within tolerance." : "Component baseline differences require review.");
    return success ? 0 : 1;
}
var start = new ProcessStartInfo(Path.Combine(Path.GetFullPath(args[0]), "AudioWorker", "Linguacode.AudioWorker.exe"))
{ UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
start.ArgumentList.Add("--server");
using var worker = Process.Start(start)!;
var errors = worker.StandardError.ReadToEndAsync();
var records = new JsonArray(); var totals = new Dictionary<string, (int Words, int Baseline, int Native)>();
bool passed = true;
try
{
    foreach (var entry in manifest["entries"]!.AsArray())
    {
        var scenario = entry!["excludeFromWer"]?.GetValue<bool>() == true;
        if (scenariosOnly && !scenario) continue;
        var file = entry!["file"]!.GetValue<string>(); var language = entry["language"]!.GetValue<string>();
        var path = Path.Combine(fixtures, file); using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(entry["sha256"]!.ToString(), StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture checksum mismatch: " + file);
        var request = new JsonObject { ["protocol"] = 3, ["requestId"] = file, ["operation"] = "audio_listen", ["models"] = Path.GetFullPath(args[1]),
            ["path"] = path, ["startSeconds"] = 0, ["durationSeconds"] = 0, ["language"] = language };
        await worker.StandardInput.WriteLineAsync(request.ToJsonString()); await worker.StandardInput.FlushAsync();
        JsonObject? result = null; using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        while (true)
        {
            var line = await worker.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("Worker exited before completing " + file);
            var message = JsonNode.Parse(line)!;
            if (message["requestId"]?.ToString() != file || message["protocol"]?.GetValue<int>() != 3) throw new IOException("Invalid response identity.");
            if (message["type"]!.ToString() == "error") throw new IOException(message["error"]!.ToString());
            if (message["type"]!.ToString() == "result") result = message["result"]!.AsObject();
            if (message["type"]!.ToString() == "complete") break;
        }
        if (result is null) throw new IOException("Missing result.");
        foreach (var component in new[] { "transcription", "classification", "diarization" })
            if (result[component]?["status"]?.ToString() != "succeeded") throw new IOException(component + ": " + result[component]?["error"]);
        var baseline = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "baseline", file + ".baseline.json")))!;
        string Text(JsonNode node) => string.Join(" ", node["transcription"]!["segments"]!.AsArray().Select(s => s!["text"]!.ToString()));
        var reference = Words(entry["transcript"]!.ToString()); var oldErrors = scenario ? 0 : Distance(reference, Words(Text(baseline))); var newErrors = scenario ? 0 : Distance(reference, Words(Text(result)));
        if (!scenario) { var previous = totals.GetValueOrDefault(language); totals[language] = (previous.Words + reference.Length, previous.Baseline + oldErrors, previous.Native + newErrors); }
        if (file == "silence.wav") passed &= result["transcription"]!["noSpeech"]!.GetValue<bool>() && result["diarization"]!["speakerCount"]!.GetValue<int>() == 0;
        double[] Scores(JsonNode node) => node["classification"]!["aggregateScores"]!.AsArray().OrderBy(s => s!["index"]!.GetValue<int>()).Select(s => s!["score"]!.GetValue<double>()).ToArray();
        var oldScores = Scores(baseline); var scores = Scores(result); var scoreDelta = oldScores.Zip(scores, (a, b) => Math.Abs(a - b)).Max();
        var sameLabels = JsonNode.DeepEquals(baseline["classification"]!["classMap"], result["classification"]!["classMap"]);
        var sameTurns = JsonNode.DeepEquals(baseline["diarization"]!["turns"], result["diarization"]!["turns"]);
        var validTimes = true; var duration = result["durationSeconds"]!.GetValue<double>();
        foreach (var segment in result["transcription"]!["segments"]!.AsArray())
        {
            var begin = segment!["startSeconds"]!.GetValue<double>(); var end = segment["endSeconds"]!.GetValue<double>();
            validTimes &= double.IsFinite(begin) && double.IsFinite(end) && begin >= 0 && end > begin && end <= duration + .001;
            foreach (var word in segment["words"]!.AsArray())
            { var a = word!["startSeconds"]!.GetValue<double>(); var b = word["endSeconds"]!.GetValue<double>(); validTimes &= double.IsFinite(a) && double.IsFinite(b) && a >= begin && b >= a && b <= end + .001; }
        }
        passed &= sameLabels && scoreDelta <= .0001 && validTimes;
        records.Add(new JsonObject { ["file"] = file, ["words"] = reference.Length, ["baselineErrors"] = oldErrors, ["nativeErrors"] = newErrors,
            ["classMapEqual"] = sameLabels, ["maxClassScoreDelta"] = scoreDelta, ["speakerTurnsExactlyEqual"] = sameTurns, ["validWordTimeBounds"] = validTimes,
            ["native"] = result.DeepClone() });
        Console.WriteLine($"{file}: baseline errors {oldErrors}, native {newErrors}; classification delta {scoreDelta:g3}");
    }
}
finally
{
    worker.StandardInput.Close();
    try { await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); } catch { worker.Kill(true); }
    await File.WriteAllTextAsync(Path.GetFullPath(args[2]) + ".stderr.txt", await errors);
}
var languages = new JsonArray();
foreach (var (language, value) in totals)
{
    var delta = (value.Native - value.Baseline) * 100d / value.Words; passed &= delta <= 2;
    languages.Add(new JsonObject { ["language"] = language, ["words"] = value.Words, ["baselineWerPercent"] = value.Baseline * 100d / value.Words,
        ["nativeWerPercent"] = value.Native * 100d / value.Words, ["deltaPercentagePoints"] = delta, ["passed"] = delta <= 2 });
    Console.WriteLine($"{language}: WER change {delta:+0.00;-0.00;0.00} percentage points");
}
var report = new JsonObject { ["passed"] = passed, ["scope"] = scenariosOnly ? "Generated silence and controlled overlap; excluded from WER." : "First twenty FLEURS test utterances per language plus silence and overlap; not a population-level accuracy estimate or human word-alignment validation.",
    ["languages"] = languages, ["records"] = records };
await File.WriteAllTextAsync(Path.GetFullPath(args[2]), report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
return passed ? 0 : 1;

static string[] Words(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ')).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
static int Distance(string[] expected, string[] actual)
{
    var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
    for (var i = 0; i < expected.Length; i++)
    {
        var next = new int[actual.Length + 1]; next[0] = i + 1;
        for (var j = 0; j < actual.Length; j++) next[j + 1] = Math.Min(next[j] + 1, Math.Min(previous[j + 1] + 1, previous[j] + (expected[i] == actual[j] ? 0 : 1)));
        previous = next;
    }
    return previous[^1];
}
static bool Close(JsonNode? a, JsonNode? b, double tolerance)
{
    if (a is null || b is null) return a is null && b is null;
    if (a is JsonArray aa && b is JsonArray bb) return aa.Count == bb.Count && aa.Zip(bb).All(pair => Close(pair.First, pair.Second, tolerance));
    if (a is JsonObject ao && b is JsonObject bo) return ao.Count == bo.Count && ao.All(pair => bo.ContainsKey(pair.Key) && Close(pair.Value, bo[pair.Key], tolerance));
    if (a is JsonValue av && b is JsonValue bv && av.TryGetValue<double>(out var x) && bv.TryGetValue<double>(out var y)) return double.IsFinite(x) && double.IsFinite(y) && Math.Abs(x - y) <= tolerance;
    return JsonNode.DeepEquals(a, b);
}
static JsonNode NormalizeWindows(JsonNode source)
{
    var copy = source.DeepClone();
    foreach (var window in copy.AsArray())
        // NumPy's unstable sort and LINQ's stable sort can choose different labels tied at exactly zero.
        window!["labels"] = new JsonArray(window["labels"]!.AsArray().Where(n => n!["score"]!.GetValue<double>() != 0)
            .OrderBy(n => n!["index"]!.GetValue<int>()).Select(n => n!.DeepClone()).ToArray());
    return copy;
}
