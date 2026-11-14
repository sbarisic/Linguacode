using System.Windows;
using Linguacode.Application;

namespace Linguacode.Desktop;

public partial class MainWindow
{
    private sealed class UpdateSmokeBackend(string root) : IAppUpdateBackend
    {
        public bool Installed => true;
        public string CurrentVersion => "1.0.0";
        public string InstallationRoot => root;
        public AppRelease? Pending => null;
        public int Verifications;
        public Task<AppRelease?> Check(CancellationToken cancellation) => Task.FromResult<AppRelease?>(new("1.1.0", "A test release with local update checks.", 1048576));
        public Task Download(AppRelease release, Action<int> progress, CancellationToken cancellation) { progress(100); return Task.CompletedTask; }
        public Task<VerifiedUpdate> Verify(AppRelease release, CancellationToken cancellation) { Verifications++; throw new InvalidOperationException("Smoke test must not apply an update."); }
    }
    private async Task CheckUpdateControls()
    {
        var previous = updates; var previousBusy = busy; var previousWork = work; var previousCapture = activeId; var previousSignIn = signingIn;
        var backend = new UpdateSmokeBackend(System.IO.Path.Combine(recorder.DataDirectory, "update-test-install"));
        try
        {
            updates = new(backend, System.IO.Path.Combine(recorder.DataDirectory, "update-ui"));
            await updates.Check(true, default); RenderUpdates();
            if (DownloadUpdate.Visibility != Visibility.Visible || UpdateNotice.Visibility != Visibility.Visible) throw new InvalidOperationException("Available update was hidden.");
            await updates.Download(default); RenderUpdates();
            if (RestartUpdate.Visibility != Visibility.Visible) throw new InvalidOperationException("Restart action was hidden.");
            foreach (var state in new[] { "recording", "work", "busy", "sign-in" })
            {
                busy = state == "busy"; work = state == "work" ? Task.CompletedTask : null;
                activeId = state == "recording" ? "capture" : null; signingIn = state == "sign-in";
                RestartUpdateClick(this, new RoutedEventArgs());
                if (!UpdateStatus.Text.Contains("Finish recording") || backend.Verifications != 0) throw new InvalidOperationException("Update bypassed active work.");
            }
            busy = false; work = null; activeId = null; signingIn = false;
            Navigate("Configuration"); RenderUpdates();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            ((FrameworkElement)Content).InvalidateMeasure(); WriteSmokePreview("updates-ready.png", new Size(984, 681));
        }
        finally { updates = previous; busy = previousBusy; work = previousWork; activeId = previousCapture; signingIn = previousSignIn; RenderUpdates(); }
    }
}
