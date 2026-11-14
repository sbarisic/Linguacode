using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Linguacode.Core;
using Velopack;
using Velopack.Sources;
using Velopack.Locators;

namespace Linguacode.Application;

public sealed record AppRelease(string Version, string Notes, long Bytes);
public sealed record VerifiedUpdate(string Path, string Sha256);
public interface IAppUpdateBackend
{
    bool Installed { get; }
    string CurrentVersion { get; }
    string? InstallationRoot { get; }
    AppRelease? Pending { get; }
    Task<AppRelease?> Check(CancellationToken cancellation);
    Task Download(AppRelease release, Action<int> progress, CancellationToken cancellation);
    Task<VerifiedUpdate> Verify(AppRelease release, CancellationToken cancellation);
}

public sealed class GithubAppUpdates : IAppUpdateBackend
{
    public const string Repository = "https://github.com/sbarisic/Linguacode";
    private readonly UpdateManager manager;
    private readonly IVelopackLocator locator;
    private UpdateInfo? available;
    private readonly Dictionary<string, VelopackAsset> assets = [];
    public GithubAppUpdates() : this(new UpdateManager(new GithubSource(Repository, null, false),
        new UpdateOptions { ExplicitChannel = "win-x64", AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = -1 })) { }
    public GithubAppUpdates(UpdateManager manager, IVelopackLocator? locator = null)
    {
        this.manager = manager; this.locator = locator ?? VelopackLocator.Current;
        if (Installed && manager.UpdatePendingRestart is { } pending && File.Exists(ReceiptPath))
        {
            try
            {
                var receipt = JsonSerializer.Deserialize<DownloadReceipt>(File.ReadAllText(ReceiptPath));
                if (receipt is not null && receipt.Version == pending.Version.ToString())
                {
                    var asset = new VelopackAsset { PackageId = "Linguacode.App", Version = SemanticVersion.Parse(receipt.Version), Type = VelopackAssetType.Full,
                        FileName = receipt.FileName, Size = receipt.Bytes, SHA256 = receipt.Sha256, NotesMarkdown = receipt.Notes };
                    Validate(asset); assets[receipt.Version] = asset; Pending = Describe(asset);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException) { /* Recheck and download if a receipt is missing or damaged. */ }
        }
    }
    private sealed record DownloadReceipt(string Version, string FileName, long Bytes, string Sha256, string Notes);
    private string ReceiptPath => Path.Combine(locator.RootAppDir ?? throw new IOException("Installation root is unavailable."), ".linguacode", "downloaded.json");
    public bool Installed => manager.IsInstalled && !manager.IsPortable && manager.AppId == "Linguacode.App";
    public string? InstallationRoot => Installed ? locator.RootAppDir : null;
    public string CurrentVersion => manager.CurrentVersion?.ToString() ?? typeof(CallRecorder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "development";
    public AppRelease? Pending { get; }
    private static void Validate(VelopackAsset asset)
    {
        if (asset.PackageId != "Linguacode.App" || asset.Version.IsPrerelease || asset.Type != VelopackAssetType.Full ||
            asset.Size <= 0 || asset.SHA256?.Length != 64 || !asset.SHA256.All(Uri.IsHexDigit) ||
            Path.GetFileName(asset.FileName) != asset.FileName || !asset.FileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The release has missing or invalid update assets.");
    }
    private static AppRelease Describe(VelopackAsset asset) => new(asset.Version.ToString(), asset.NotesMarkdown ?? "", asset.Size);
    public async Task<AppRelease?> Check(CancellationToken cancellation)
    {
        if (!Installed) return null;
        available = await manager.CheckForUpdatesAsync().WaitAsync(cancellation);
        if (available is null) return null;
        Validate(available.TargetFullRelease);
        if (available.IsDowngrade || available.TargetFullRelease.Version <= manager.CurrentVersion) return null;
        var release = Describe(available.TargetFullRelease); assets[release.Version] = available.TargetFullRelease; return release;
    }
    public async Task Download(AppRelease release, Action<int> progress, CancellationToken cancellation)
    {
        if (available?.TargetFullRelease.Version.ToString() != release.Version) throw new IOException("Check for updates again before downloading.");
        await manager.DownloadUpdatesAsync(available, progress, cancellation);
        await Verify(release, cancellation);
        var asset = available.TargetFullRelease;
        Directory.CreateDirectory(Path.GetDirectoryName(ReceiptPath)!);
        File.WriteAllText(ReceiptPath + ".new", JsonSerializer.Serialize(new DownloadReceipt(release.Version, asset.FileName, asset.Size, asset.SHA256, release.Notes)));
        File.Move(ReceiptPath + ".new", ReceiptPath, true);
    }
    public async Task<VerifiedUpdate> Verify(AppRelease release, CancellationToken cancellation)
    {
        if (!Installed || !assets.TryGetValue(release.Version, out var asset)) throw new IOException("Update is not available.");
        Validate(asset);
        var path = Path.Combine(locator.PackagesDir ?? throw new IOException("Package directory is unavailable."), asset.FileName);
        await using var file = File.OpenRead(path);
        if (file.Length != asset.Size || !Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)).Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Update verification failed. Download the release again.");
        return new(path, asset.SHA256);
    }
}

public sealed class UpdatePreferences
{
    public bool AutomaticChecks { get; set; } = true;
    public DateTimeOffset? LastCheck { get; set; }
    public string? SkippedVersion { get; set; }
}
public enum AppUpdateState { Development, Idle, Checking, Available, Downloading, Ready, Error }

public sealed class AppUpdateController
{
    private readonly IAppUpdateBackend backend;
    private readonly string settingsPath;
    private readonly Func<DateTimeOffset> now;
    private readonly SemaphoreSlim operation = new(1, 1);
    public UpdatePreferences Preferences { get; }
    public AppUpdateState State { get; private set; }
    public AppRelease? Release { get; private set; }
    public string Message { get; private set; } = "";
    public int Progress { get; private set; }
    public string Version => backend.CurrentVersion;
    public bool Installed => backend.Installed;
    public string? InstallationRoot => backend.InstallationRoot;
    public event Action? Changed;
    public AppUpdateController(IAppUpdateBackend backend, string data, Func<DateTimeOffset>? now = null)
    {
        this.backend = backend; this.now = now ?? (() => DateTimeOffset.UtcNow);
        settingsPath = Path.Combine(data, "updates", "settings.json");
        try { Preferences = File.Exists(settingsPath) ? JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(settingsPath)) ?? new() : new(); }
        catch (JsonException) { Preferences = new(); }
        Release = backend.Pending;
        State = !backend.Installed ? AppUpdateState.Development : Release is null ? AppUpdateState.Idle : AppUpdateState.Ready;
        Message = State == AppUpdateState.Development ? "Updates are available in installed releases." : Release is null ? "No update check yet." : "Update downloaded. Restart when you are ready.";
        var result = Path.Combine(data, "updates", "apply-result.json");
        if (backend.Installed && File.Exists(result))
        {
            try { using var json = JsonDocument.Parse(File.ReadAllText(result)); Message = json.RootElement.GetProperty("Message").GetString() ?? Message; }
            catch (JsonException) { }
        }
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath + ".new", JsonSerializer.Serialize(Preferences)); File.Move(settingsPath + ".new", settingsPath, true);
    }
    public void SetAutomatic(bool enabled) { Preferences.AutomaticChecks = enabled; Save(); Changed?.Invoke(); }
    public void Skip()
    {
        if (State != AppUpdateState.Available || Release is null) return;
        Preferences.SkippedVersion = Release.Version; Save(); Release = null; State = AppUpdateState.Idle; Message = "Version skipped."; Changed?.Invoke();
    }
    public async Task Check(bool manual, CancellationToken cancellation)
    {
        if (!Installed || !manual && (!Preferences.AutomaticChecks || Preferences.LastCheck is { } last && now() - last < TimeSpan.FromHours(24))) return;
        if (!await operation.WaitAsync(0, cancellation)) return;
        try
        {
            if (State == AppUpdateState.Ready) return;
            State = AppUpdateState.Checking; Message = "Checking GitHub Releases…"; Changed?.Invoke();
            Preferences.LastCheck = now(); Save();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var release = await backend.Check(deadline.Token);
            Release = !manual && release?.Version == Preferences.SkippedVersion ? null : release;
            State = Release is null ? AppUpdateState.Idle : AppUpdateState.Available;
            Message = Release is null ? "No newer release available." : "Linguacode " + Release.Version + " is available.";
        }
        catch (OperationCanceledException) { State = AppUpdateState.Idle; Message = "Update check cancelled or timed out."; }
        catch (Exception ex) { State = AppUpdateState.Error; Message = "Could not check for updates: " + ex.Message; }
        finally { operation.Release(); Changed?.Invoke(); }
    }
    public async Task Download(CancellationToken cancellation)
    {
        if (Release is null || State is not (AppUpdateState.Available or AppUpdateState.Error) || !await operation.WaitAsync(0, cancellation)) return;
        try
        {
            State = AppUpdateState.Downloading; Progress = 0; Message = "Downloading update…"; Changed?.Invoke();
            await backend.Download(Release, n => { Progress = Math.Clamp(n, 0, 100); Changed?.Invoke(); }, cancellation);
            State = AppUpdateState.Ready; Message = "Update verified. Restart when you are ready.";
        }
        catch (OperationCanceledException) { State = AppUpdateState.Available; Message = "Download cancelled. You can retry."; }
        catch (Exception ex) { State = AppUpdateState.Error; Message = "Could not download update: " + ex.Message; }
        finally { operation.Release(); Changed?.Invoke(); }
    }
    public async Task<VerifiedUpdate> Verify(CancellationToken cancellation)
    {
        if (State != AppUpdateState.Ready || Release is not { } release) throw new IOException("Download and verify an update first.");
        try { return await backend.Verify(release, cancellation); }
        catch
        {
            State = AppUpdateState.Error; Message = "Update verification failed. Check for updates and download again."; Changed?.Invoke();
            throw;
        }
    }

    public async Task StartHandoff(string token, string data, VerifiedUpdate package, CancellationToken cancellation)
    {
        var root = InstallationRoot ?? throw new IOException("Only installed releases can update.");
        var source = Path.Combine(AppContext.BaseDirectory, "UpdateHost");
        var staging = Path.Combine(Path.GetTempPath(), "Linguacode-update-" + token); Directory.CreateDirectory(staging);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(staging, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        var handoff = new UpdateHandoff(root, token, InstallationActivity.Current("gui", data), package.Path, package.Sha256, data, ["--data-dir", data]);
        var request = Path.Combine(staging, "handoff.json"); File.WriteAllText(request, JsonSerializer.Serialize(handoff));
        var start = new ProcessStartInfo(Path.Combine(staging, "Linguacode.UpdateHost.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = staging };
        start.ArgumentList.Add(request);
        using var helper = Process.Start(start) ?? throw new IOException("Could not start the updater helper.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!InstallationActivity.HelperReady(root, token))
        {
            if (helper.HasExited) throw new IOException("Updater helper exited before handoff.");
            await Task.Delay(100, timeout.Token);
        }
    }
}
