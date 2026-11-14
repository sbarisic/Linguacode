using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args.Length != 2 || !Regex.IsMatch(args[0], @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$"))
{ Console.Error.WriteLine("Usage: dotnet run --project tools/Linguacode.Release -- <major.minor.patch> <release-notes.md>"); return 2; }
var root = new DirectoryInfo(Environment.CurrentDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "Linguacode.slnx"))) root = root.Parent;
if (root is null) throw new IOException("Run from the Linguacode checkout.");
var version = args[0]; var notes = Path.GetFullPath(args[1]);
if (!File.Exists(notes) || new FileInfo(notes).Length == 0) throw new IOException("Supply a non-empty release-notes file.");
async Task<string> Run(string executable, IEnumerable<string> arguments, bool capture = false, string? buildDirectory = null)
{
    var start = new ProcessStartInfo(executable) { WorkingDirectory = root.FullName, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = capture, RedirectStandardError = capture };
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    if (buildDirectory is not null) start.Environment["LINGUACODE_TEST_BUILD_DIR"] = buildDirectory;
    start.Environment.Remove("LINGUACODE_NATIVE_TEST_DATA");
    using var process = Process.Start(start) ?? throw new IOException("Could not start " + executable);
    var output = capture ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
    var error = capture ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new IOException(executable + " failed: " + await error);
    return (await output).Trim();
}
if (await Run("git", ["branch", "--show-current"], true) != "main") throw new IOException("Release packaging requires main.");
if ((await Run("git", ["status", "--porcelain", "--untracked-files=no"], true)).Length != 0) throw new IOException("Commit tracked changes before packaging a release.");
var commit = await Run("git", ["rev-parse", "HEAD"], true);
if ((await Run("git", ["tag", "--list", "v" + version], true)).Length != 0) throw new IOException("This version already has a local tag. Choose a new version.");
var outputRoot = Path.Combine(root.FullName, "artifacts", "releases", version);
if (Directory.Exists(outputRoot)) throw new IOException("Release output already exists; do not overwrite a version. Choose a new version or move the unpublished output aside.");
var stage = Path.Combine(root.FullName, "obj", "release-staging", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(stage); Directory.CreateDirectory(outputRoot);
var build = Path.Combine(stage, "app");
var props = new[] { "-p:Version=" + version, "-p:InformationalVersion=" + version + "+" + commit, "-p:DesktopOutputPath=" + build + Path.DirectorySeparatorChar };
await Run("dotnet", ["tool", "restore"]);
await Run("dotnet", new[] { "build", "Linguacode.slnx", "-c", "Release", "-t:Rebuild", "--nologo" }.Concat(props));
await Run("dotnet", ["test", "tests/Linguacode.Tests", "-c", "Release", "--no-build", "--nologo"], buildDirectory: build);
foreach (var required in new[] { "Linguacode.exe", "Linguacode.AudioHost.exe", "AudioWorker/Linguacode.AudioWorker.exe", "UpdateHost/Linguacode.UpdateHost.exe", "LocalAudio/downloads.json" })
    if (!File.Exists(Path.Combine(build, required))) throw new IOException("Incomplete package: " + required);
// Build output is fresh, never copied from the developer's running application folder.
if (Directory.EnumerateFiles(build, "*", SearchOption.AllDirectories).Any(p => p.Contains(".codex-attachments", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p) == ".py"))
    throw new IOException("Unexpected development/runtime files in release staging.");
var pack = new List<string> { "vpk", "pack", "--packId", "Linguacode.App", "--packTitle", "Linguacode", "--packVersion", version,
    "--packDir", build, "--mainExe", "Linguacode.exe", "--channel", "win-x64", "--runtime", "win-x64", "--delta", "None",
    "--icon", Path.Combine(root.FullName, "src/Linguacode.Desktop/Assets/Linguacode.ico"), "--shortcuts", "StartMenuRoot",
    "--releaseNotes", notes, "--outputDir", outputRoot };
if (Environment.GetEnvironmentVariable("LINGUACODE_SIGN_PARAMS") is { Length: > 0 } signing) pack.AddRange(["--signParams", signing]);
await Run("dotnet", pack);
// Portable archives are not part of the supported release workflow.
foreach (var portable in Directory.EnumerateFiles(outputRoot, "*-Portable.zip")) File.Delete(portable);
var assetIndex = Path.Combine(outputRoot, "assets.win-x64.json");
if (File.Exists(assetIndex))
{
    var index = JsonNode.Parse(File.ReadAllText(assetIndex))!.AsArray();
    foreach (var item in index.Where(n => n?["Type"]?.ToString() == "Portable").ToArray()) index.Remove(item);
    File.WriteAllText(assetIndex, index.ToJsonString());
}
File.Copy(notes, Path.Combine(outputRoot, "RELEASE_NOTES.md"));
File.WriteAllText(Path.Combine(outputRoot, "build-info.json"), JsonSerializer.Serialize(new { Version = version, Commit = commit, Channel = "win-x64", PackageId = "Linguacode.App" }));
var checksum = new StringBuilder();
var assets = Directory.GetFiles(outputRoot).Order().ToArray();
foreach (var file in assets)
{
    using var stream = File.OpenRead(file);
    checksum.AppendLine(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() + "  " + Path.GetFileName(file));
}
File.WriteAllText(Path.Combine(outputRoot, "SHA256SUMS.txt"), checksum.ToString());
File.WriteAllText(Path.Combine(outputRoot, "UPLOAD-CHECKLIST.txt"), $"Create a draft release v{version} at commit {commit}.\nAttach every file listed below, plus SHA256SUMS.txt.\nDo not mark as prerelease. Publish and mark latest only after manual acceptance.\nInitial builds are unsigned unless LINGUACODE_SIGN_PARAMS was set.\n" + string.Join('\n', assets.Select(Path.GetFileName)));
Console.WriteLine("Prepared release assets: " + outputRoot + "\nNothing was tagged, uploaded, published, or pushed.");
return 0;
