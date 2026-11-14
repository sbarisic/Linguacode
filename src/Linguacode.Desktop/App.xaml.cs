using System.IO;
using System.Windows;
using Linguacode.Application;

namespace Linguacode.Desktop;

public partial class App : System.Windows.Application
{
    internal static Linguacode.Core.InstallationActivity? Activity;
    internal static bool IsSmokeTest;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RecorderTheme.Apply();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += ThemeChanged;
        try
        {
            string? directory = null; var executable = Path.Combine(AppContext.BaseDirectory, "Linguacode.AudioHost.exe");
            var smoke = false;
            for (var i = 0; i < e.Args.Length; i++)
            {
                switch (e.Args[i])
                {
                    case "--data-dir" when i + 1 < e.Args.Length: directory = e.Args[++i]; break;
                    case "--audio-host" when i + 1 < e.Args.Length: executable = e.Args[++i]; break;
                    case "--smoke-test": smoke = true; break;
                    default: throw new ArgumentException("Usage: Linguacode [--data-dir directory] [--audio-host path]");
                }
            }
            if (smoke && directory is null) throw new ArgumentException("Smoke check requires an explicit disposable --data-dir.");
            IsSmokeTest = smoke;
            if (Linguacode.Core.InstallationActivity.FindRoot() is { } root) Activity = Linguacode.Core.InstallationActivity.Register(root, "gui", Linguacode.Core.LinguacodePaths.DataDirectory(directory));
            var recorder = new CallRecorder(directory, executable);
            var window = new MainWindow(recorder);
            if (smoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try { await window.RunSmokeCheck(); }
                finally { recorder.Dispose(); }
                Shutdown(0); return;
            }
            MainWindow = window; window.Show();
        }
        catch (Linguacode.Core.InstallationUpdateInProgressException) { Shutdown(2); }
        catch (Exception ex)
        {
            if (e.Args.Contains("--smoke-test")) Console.Error.WriteLine(ex);
            else MessageBox.Show(ex.Message, "Linguacode", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private void ThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(new Action(() => RecorderTheme.Apply()));
    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= ThemeChanged;
        Activity?.Dispose(); Activity = null;
        base.OnExit(e);
    }
}
