using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Linguacode.Core;

// Runs from a temporary directory, never from the installation being replaced.
if (args.Length != 1) return 2;
var request = JsonSerializer.Deserialize<UpdateHandoff>(File.ReadAllText(args[0])) ?? throw new IOException("Invalid update handoff.");
var resultFile = Path.Combine(request.DataDirectory, "updates", "apply-result.json");
try
{
    InstallationActivity.AttachHelper(request.Root, request.Token);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    while (InstallationActivity.Alive(request.Owner)) await Task.Delay(100, timeout.Token);
    InstallationActivity.ConfirmHelper(request.Root, request.Token);
    if (InstallationActivity.Processes(request.Root).Length != 0) throw new IOException("Another installed process is still active. Update was not applied.");
    using (var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        while (InstallationActivity.HasRunningBinaries(request.Root)) await Task.Delay(100, exitDeadline.Token);
    // Recheck the verified package immediately before handing it to the external updater.
    await using (var input = File.OpenRead(request.Package))
        if (!Convert.ToHexString(await SHA256.HashDataAsync(input)).Equals(request.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Downloaded update checksum changed.");
    InstallationActivity.ConfirmHelper(request.Root, request.Token);
    var start = new ProcessStartInfo(Path.Combine(request.Root, "Update.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
    foreach (var arg in new[] { "--silent", "apply", "--norestart", "--package", request.Package }) start.ArgumentList.Add(arg);
    using var updater = Process.Start(start) ?? throw new IOException("Could not start updater.");
    await updater.WaitForExitAsync();
    if (updater.ExitCode != 0) throw new IOException("Installer failed (exit " + updater.ExitCode + "). Reinstall manually from GitHub Releases.");
    Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
    File.WriteAllText(resultFile, JsonSerializer.Serialize(new { Success = true, Message = "Update installed." }));
    InstallationActivity.EndUpdate(request.Root, request.Token);
    var restart = new ProcessStartInfo(Path.Combine(request.Root, "Linguacode.exe")) { UseShellExecute = false, WorkingDirectory = request.DataDirectory };
    foreach (var arg in request.RestartArguments) restart.ArgumentList.Add(arg);
    Process.Start(restart)?.Dispose();
    return 0;
}
catch (Exception ex)
{
    Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
    File.WriteAllText(resultFile, JsonSerializer.Serialize(new { Success = false, Message = ex.Message }));
    Console.Error.WriteLine(ex.Message); return 1;
}
finally { InstallationActivity.EndUpdate(request.Root, request.Token); }
