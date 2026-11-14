using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

// Explicit local acceptance: isolated root, first installer, second version's feed directory.
if (args.Length != 3) throw new ArgumentException("Usage: <new-test-directory> <first-Setup.exe> <second-feed-directory>");
using (var installed = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Linguacode.App"))
    if (installed is not null) throw new IOException("An installed Linguacode.App already exists. This test will not alter it.");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new IOException("Use a new disposable test directory.");
var data = Path.Combine(root, "custom data"); var install = Path.Combine(root, "installation"); Directory.CreateDirectory(data);
async Task Run(string executable, params string[] arguments)
{
    var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
    start.Environment["LINGUACODE_UPDATE_TEST_DATA"] = data;
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Could not start " + executable);
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
    if (process.ExitCode != 0) throw new IOException("Test process failed: " + executable + " (" + process.ExitCode + ")");
}
void Require(bool condition, string message) { if (!condition) throw new IOException(message); }
var preserved = new[] { "audio/models/keep.bin", "audio/recordings/keep.wav", "desktop/preferences.json" };
foreach (var file in preserved) { var path = Path.Combine(data, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "preserved test data"); }
string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(data, file))));
var hashes = preserved.ToDictionary(p => p, Hash);
File.WriteAllText(Path.Combine(data, "update-fixture.json"), JsonSerializer.Serialize(new { Feed = Path.GetFullPath(args[2]) }));
File.WriteAllText(Path.Combine(data, "phase"), "version");
try
{
    await Run(args[1], "--silent", "--installto", install);
    var exe = Path.Combine(install, "current", "Linguacode.exe");
    Require(File.Exists(exe), "Installer did not create the current application.");
    foreach (var file in new[] { "AudioWorker/Linguacode.AudioWorker.exe", "Linguacode.AudioHost.exe", "UpdateHost/Linguacode.UpdateHost.exe" })
        Require(File.Exists(Path.Combine(install, "current", file)), "Missing packaged component: " + file);
    await Run(exe, "--data-dir", data);
    using var initial = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "probe.json")));
    var firstVersion = initial.RootElement.GetProperty("CurrentVersion").GetString();
    File.WriteAllText(Path.Combine(data, "phase"), "check"); await Run(exe, "--data-dir", data);
    using var check = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "probe.json")));
    Require(check.RootElement.GetProperty("State").GetString() == "Available", "Local update was not detected.");
    File.WriteAllText(Path.Combine(data, "phase"), "download"); await Run(exe, "--data-dir", data);
    using var download = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "probe.json")));
    Require(download.RootElement.GetProperty("State").GetString() == "Ready", "Package was not downloaded and verified.");
    File.WriteAllText(Path.Combine(data, "phase"), "version"); await Run(exe, "--data-dir", data);
    using var normal = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "probe.json")));
    Require(normal.RootElement.GetProperty("CurrentVersion").GetString() == firstVersion, "Normal startup silently installed an update.");
    File.Delete(Path.Combine(data, "probe.json"));
    File.WriteAllText(Path.Combine(data, "phase"), "apply"); await Run(exe, "--data-dir", data);
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    while (!File.Exists(Path.Combine(data, "probe.json")))
    {
        if (File.Exists(Path.Combine(data, "probe-error.txt"))) throw new IOException(File.ReadAllText(Path.Combine(data, "probe-error.txt")));
        await Task.Delay(250, deadline.Token);
    }
    using var final = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "probe.json")));
    Require(final.RootElement.GetProperty("CurrentVersion").GetString() != firstVersion, "Update did not change the version.");
    Require(final.RootElement.GetProperty("Arguments").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "--data-dir", data }), "Restart arguments changed.");
    foreach (var item in hashes) Require(Hash(item.Key) == item.Value, "User data changed: " + item.Key);
    Console.WriteLine("PASS: installed, checked, downloaded, deferred normal startup, explicitly updated, restarted with custom data, and preserved data.");
    File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { Passed = true, From = firstVersion, To = final.RootElement.GetProperty("CurrentVersion").GetString() }));
}
finally
{
    // Removes only this test installation through its own uninstaller; test evidence/data remain.
    var updater = Path.Combine(install, "Update.exe");
    if (File.Exists(updater)) await Run(updater, "--silent", "uninstall");
}
