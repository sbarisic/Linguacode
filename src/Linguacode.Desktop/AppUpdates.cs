using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Linguacode.Application;
using Linguacode.Audio;
using Linguacode.Core;

namespace Linguacode.Desktop;

public partial class MainWindow
{
    private AppUpdateController? updates;
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly CancellationTokenSource updateLifetime = new();
    private CancellationTokenSource? updateDownload;
    private bool updateApplying;
    private void InitializeUpdates()
    {
        try
        {
            updates = new(new GithubAppUpdates(), recorder.DataDirectory);
            updates.Changed += () => { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RenderUpdates); };
            AutomaticUpdates.IsChecked = updates.Preferences.AutomaticChecks;
            updateTimer.Tick += async (_, _) => await updates.Check(false, updateLifetime.Token);
            Loaded += async (_, _) =>
            {
                if (!App.IsSmokeTest && updates.Installed) { updateTimer.Start(); await updates.Check(false, updateLifetime.Token); }
            };
            Closed += (_, _) => { updateTimer.Stop(); updateLifetime.Cancel(); updateDownload?.Cancel(); };
            RenderUpdates();
        }
        catch (Exception ex) { UpdateStatus.Text = "Updates unavailable: " + ex.Message; }
    }
    private void RenderUpdates()
    {
        if (updates is null) return;
        UpdateVersion.Text = "Installed version: " + updates.Version;
        UpdateStatus.Text = updates.Message;
        UpdateNotes.Text = updates.Release?.Notes ?? "";
        UpdateSize.Text = updates.Release is { } release ? $"Version {release.Version} · {release.Bytes / 1048576d:0.0} MiB download" : "";
        UpdateDownloadProgress.Value = updates.Progress;
        UpdateDownloadProgress.Visibility = updates.State == AppUpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        AutomaticUpdates.IsEnabled = updates.Installed && !updateApplying;
        CheckUpdates.IsEnabled = updates.Installed && !updateApplying && updates.State is not (AppUpdateState.Checking or AppUpdateState.Downloading);
        DownloadUpdate.Visibility = updates.Release is not null && updates.State is AppUpdateState.Available or AppUpdateState.Error ? Visibility.Visible : Visibility.Collapsed;
        SkipUpdate.Visibility = updates.State == AppUpdateState.Available ? Visibility.Visible : Visibility.Collapsed;
        CancelUpdate.Visibility = updates.State == AppUpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        RestartUpdate.Visibility = updates.State == AppUpdateState.Ready ? Visibility.Visible : Visibility.Collapsed;
        RestartUpdate.IsEnabled = !updateApplying;
        UpdateNotice.Visibility = updates.State is AppUpdateState.Available or AppUpdateState.Ready ? Visibility.Visible : Visibility.Collapsed;
        UpdateNoticeText.Text = updates.State == AppUpdateState.Ready ? "Update downloaded. Open Updates to restart when ready." : updates.Message;
    }
    private async void CheckUpdatesClick(object sender, RoutedEventArgs e)
    { if (updates is not null) await updates.Check(true, updateLifetime.Token); }
    private void AutomaticUpdatesClick(object sender, RoutedEventArgs e)
    { try { updates?.SetAutomatic(AutomaticUpdates.IsChecked == true); } catch (Exception ex) { UpdateStatus.Text = ex.Message; } }
    private void SkipUpdateClick(object sender, RoutedEventArgs e)
    { try { updates?.Skip(); } catch (Exception ex) { UpdateStatus.Text = ex.Message; } }
    private async void DownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        if (updates is null || updateDownload is not null) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token); updateDownload = cancel;
        try { await updates.Download(cancel.Token); } finally { updateDownload = null; }
    }
    private void CancelUpdateClick(object sender, RoutedEventArgs e) => updateDownload?.Cancel();
    private void ReleasePageClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(GithubAppUpdates.Repository + "/releases") { UseShellExecute = true });
    private void OpenUpdatesClick(object sender, RoutedEventArgs e) { Navigate("Configuration"); UpdatesSection.BringIntoView(); }
    private async void RestartUpdateClick(object sender, RoutedEventArgs e)
    {
        if (updates?.InstallationRoot is not { } root || updateApplying) return;
        if (busy || work is not null || activeId is not null || signingIn || closing)
        { UpdateStatus.Text = "Finish recording and other work before updating."; return; }
        string? token = null;
        updateApplying = true; busy = true; SetControls(); RenderUpdates();
        try
        {
            if (System.IO.Path.GetFullPath(recorder.DataDirectory).StartsWith(System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetFullPath(recorder.DataDirectory).Equals(System.IO.Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Move the data directory outside the application installation before updating.");
            token = InstallationActivity.BeginUpdate(root);
            var processes = InstallationActivity.Processes(root).Where(p => p.ProcessId != Environment.ProcessId).ToArray();
            if (processes.Any(p => p.Role != "audio-service")) throw new IOException("Close other Linguacode windows and audio commands before updating.");
            var package = await updates.Verify(updateLifetime.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            foreach (var process in processes)
                if (!await AudioServiceControl.StopIfIdle(process.DataDirectory, process.ProcessId, timeout.Token))
                    throw new IOException("The audio service is in use by another client. Finish its work before updating.");
            if (InstallationActivity.Processes(root).Any(p => p.ProcessId != Environment.ProcessId)) throw new IOException("An installed process is still active.");
            playback?.Dispose(); playback = null;
            await updates.StartHandoff(token, recorder.DataDirectory, package, timeout.Token);
            App.Activity?.Dispose(); App.Activity = null;
            closingAllowed = true; closing = true; Close();
        }
        catch (Exception ex)
        {
            if (token is not null) InstallationActivity.EndUpdate(root, token);
            updateApplying = false; busy = false; SetControls(); RenderUpdates(); UpdateStatus.Text = "Update not applied: " + ex.Message;
        }
    }
}
