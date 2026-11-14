#if UPDATE_SMOKE
using System.IO;
using System.Text.Json;
using Linguacode.Application;
using Linguacode.Core;
using Velopack;
using Velopack.Sources;

namespace Linguacode.Desktop;

// Compiled only into isolated acceptance packages, never into normal or published builds.
internal static class UpdatePackageSmoke
{
    public static async Task Run(string data, string[] args)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(data, "update-fixture.json")))!;
        var root = InstallationActivity.FindRoot() ?? throw new IOException("Probe must run inside an installed package.");
        using var activity = InstallationActivity.Register(root, "gui", data);
        var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(fixture.Feed)), new UpdateOptions { ExplicitChannel = "win-x64", MaximumDeltasBeforeFallback = -1 });
        var backend = new GithubAppUpdates(manager);
        var updates = new AppUpdateController(backend, data);
        var phase = File.ReadAllText(Path.Combine(data, "phase")).Trim();
        if (phase == "apply")
        {
            var token = InstallationActivity.BeginUpdate(root);
            try
            {
                var package = await updates.Verify(default);
                File.WriteAllText(Path.Combine(data, "phase"), "version");
                await updates.StartHandoff(token, data, package, default);
                return;
            }
            catch { InstallationActivity.EndUpdate(root, token); throw; }
        }
        if (phase is "check" or "download") await updates.Check(true, default);
        if (phase == "download") await updates.Download(default);
        File.WriteAllText(Path.Combine(data, "probe.json"), JsonSerializer.Serialize(new { backend.CurrentVersion, State = updates.State.ToString(), updates.Message, Arguments = args, Root = root }));
    }
    private sealed record Fixture(string Feed);
}
#endif
