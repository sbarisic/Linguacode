using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Linguacode.Core;

public sealed class InstallationUpdateInProgressException() : IOException("Linguacode is being updated. Try again when the update finishes.");

public sealed record InstallationProcess(int ProcessId, long Started, string Role, string DataDirectory);
public sealed record InstallationUpdate(string Token, InstallationProcess Owner, InstallationProcess? Helper = null);

/// <summary>Coordinates installed processes across data directories. No locks live inside the replaceable current folder.</summary>
public sealed class InstallationActivity : IDisposable
{
    private readonly FileStream lease;
    public string Root { get; }
    private readonly string path;
    private InstallationActivity(string root, string role, string data)
    {
        Root = Path.GetFullPath(root);
        using var gate = Lock(Root);
        if (Pending(Root) is not null) throw new InstallationUpdateInProgressException();
        path = Path.Combine(State(Root), Environment.ProcessId + ".json");
        lease = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        JsonSerializer.Serialize(lease, Current(role, data)); lease.Flush(true);
    }
    public static string? FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        return current.Name.Equals("current", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(current.FullName, "sq.version")) && current.Parent is { } root &&
            File.Exists(Path.Combine(root.FullName, "Update.exe")) ? root.FullName : null;
    }
    public static InstallationActivity Register(string root, string role, string data) => new(root, role, data);
    public static InstallationProcess Current(string role, string data)
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, role, data);
    }
    public static bool Alive(InstallationProcess process)
    {
        try { using var p = Process.GetProcessById(process.ProcessId); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == process.Started; }
        catch (ArgumentException) { return false; }
    }
    private static string State(string root) { var path = Path.Combine(root, ".linguacode"); Directory.CreateDirectory(path); return path; }
    private static string Intent(string root) => Path.Combine(State(root), "update.json");
    private static InstallationUpdate? Pending(string root)
    {
        var path = Intent(root);
        if (!File.Exists(path)) return null;
        var intent = JsonSerializer.Deserialize<InstallationUpdate>(File.ReadAllText(path)) ?? throw new IOException("Invalid update state.");
        if (Alive(intent.Owner) || intent.Helper is { } helper && Alive(helper)) return intent;
        File.Delete(path); return null;
    }
    private static void Save(string root, InstallationUpdate intent)
    {
        var path = Intent(root); File.WriteAllText(path + ".new", JsonSerializer.Serialize(intent)); File.Move(path + ".new", path, true);
    }
    public static string BeginUpdate(string root)
    {
        using var gate = Lock(root);
        if (Pending(root) is not null) throw new IOException("Another update is already in progress.");
        var token = Guid.NewGuid().ToString("N"); Save(root, new(token, Current("gui", ""))); return token;
    }
    public static void AttachHelper(string root, string token)
    {
        using var gate = Lock(root); var intent = Pending(root);
        if (intent is null || intent.Token != token || intent.Helper is not null) throw new IOException("Update handoff expired.");
        Save(root, intent with { Helper = Current("updater", "") });
    }
    public static bool HelperReady(string root, string token)
    {
        using var gate = Lock(root); var intent = Pending(root);
        return intent?.Token == token && intent.Helper is { } helper && Alive(helper);
    }
    public static void ConfirmHelper(string root, string token)
    {
        using var gate = Lock(root); var intent = Pending(root);
        if (intent?.Token != token || intent.Helper?.ProcessId != Environment.ProcessId)
            throw new IOException("Update handoff was cancelled.");
    }
    public static void EndUpdate(string root, string token)
    {
        using var gate = Lock(root);
        if (File.Exists(Intent(root)) && JsonSerializer.Deserialize<InstallationUpdate>(File.ReadAllText(Intent(root)))?.Token == token) File.Delete(Intent(root));
    }
    public static InstallationProcess[] Processes(string root)
    {
        using var gate = Lock(root);
        return Directory.EnumerateFiles(State(root), "*.json").Where(p => int.TryParse(Path.GetFileNameWithoutExtension(p), out _))
            .Select(ReadProcess)
            .Where(Alive).ToArray();
    }
    public static bool HasRunningBinaries(string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        foreach (var name in new[] { "Linguacode", "Linguacode.AudioHost", "Linguacode.AudioWorker", "Linguacode.UpdateHost" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && process.MainModule?.FileName is { } file && Path.GetFullPath(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch (InvalidOperationException) { /* Exited during inspection. */ }
                }
        return false;
    }
    private static InstallationProcess ReadProcess(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<InstallationProcess>(file) ?? throw new IOException("Invalid process registration.");
    }
    private static IDisposable Lock(string root)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant())));
        var mutex = new Mutex(false, "Local\\Linguacode.Installation." + id);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("Installation is busy."); }
        catch (AbandonedMutexException) { }
        catch { mutex.Dispose(); throw; }
        return new HeldMutex(mutex);
    }
    private sealed class HeldMutex(Mutex mutex) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }
    public void Dispose() { lease.Dispose(); try { File.Delete(path); } catch (IOException) { } }
}
