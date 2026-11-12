using System.Text.Json.Nodes;
using Linguacode.Core;
using Linguacode.Audio;

// Dedicated local audio host. No coding-agent runtime or provider credentials.
var words = new List<string>();
string? directory = null;
var json = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--data-dir" && i + 1 < args.Length) directory = args[++i];
    else if (args[i] == "--json") json = true;
    else words.Add(args[i]);
}
directory = LinguacodePaths.DataDirectory(directory);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
void Write(string type, object payload) => Console.WriteLine(json ? Json.Serialize(new { schemaVersion = 1, type, payload }) : Json.Serialize(payload));
try
{
    if (words is ["service", "run"])
    {
        await new AudioServiceHost(directory).Run(cancel.Token);
        return 0;
    }
    if (words.Count >= 2 && words[0] == "service")
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the audio host.");
        string[] prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [typeof(Program).Assembly.Location] : [];
        var status = words[1] switch
        {
            "start" when words.Count == 2 => await AudioServiceControl.Start(directory, executable, prefix, cancel.Token),
            "status" when words.Count == 2 => await AudioServiceControl.Status(directory, cancel.Token),
            "stop" when words.Count == 2 || words is [_, _, "--force"] => await AudioServiceControl.Stop(directory, words.Contains("--force"), cancel.Token),
            _ => throw new ArgumentException("Use service start, run, status, or stop [--force].")
        };
        Write("service.status", status);
        return words[1] == "status" && !status.Available ? 2 : 0;
    }
    if (words is ["audio", "setup"])
    {
        await LocalAudioInstallation.Setup(directory, message => Write("audio.setup", new { message }), cancel.Token);
        return 0;
    }
    if (words is ["audio", "status"]) { Write("audio.status", LocalAudioInstallation.Status(directory)); return 0; }
    if (words is ["audio", "cleanup"]) { LocalAudioInstallation.Cleanup(directory, message => Write("audio.cleanup", new { message })); return 0; }
    if (words is ["audio", "capture", "list"] or ["audio", "capture", "list", _])
    {
        Write("audio.capture", await AudioCaptureControl.Request(directory, "list", new JsonObject { ["page"] = words.Count == 4 ? int.Parse(words[3]) : 1 }, cancel.Token));
        return 0;
    }
    if (words is ["audio", "capture", "export", var id, var destination])
    {
        Write("audio.export", await AudioCaptureControl.Export(directory, id, destination, cancel.Token));
        return 0;
    }
    throw new ArgumentException("Linguacode.AudioHost service <start|run|status|stop> or audio <setup|status|cleanup|capture list|capture export id directory> [--data-dir directory] [--json]");
}
catch (OperationCanceledException) { return 130; }
catch (Exception ex) { Write("error", new { message = ex.Message }); return 2; }
