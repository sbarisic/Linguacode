using Linguacode.Application;
using Linguacode.Core;
using Xunit;

namespace Linguacode.Tests;

public sealed class AppUpdateTests
{
    private sealed class Backend : IAppUpdateBackend
    {
        public bool Installed { get; set; } = true;
        public string CurrentVersion => "1.0.0";
        public string? InstallationRoot => "unused";
        public AppRelease? Pending { get; set; }
        public AppRelease? Next = new("1.1.0", "Release notes", 1234);
        public Exception? Failure;
        public int Checks, Downloads;
        public bool Wait;
        public Task<AppRelease?> Check(CancellationToken cancellation) { Checks++; if (Failure is not null) throw Failure; return Task.FromResult(Next); }
        public async Task Download(AppRelease release, Action<int> progress, CancellationToken cancellation)
        { Downloads++; progress(30); if (Failure is not null) throw Failure; if (Wait) await Task.Delay(Timeout.Infinite, cancellation); progress(100); }
        public Task<VerifiedUpdate> Verify(AppRelease release, CancellationToken cancellation)
        { if (Failure is not null) throw Failure; return Task.FromResult(new VerifiedUpdate("package", "hash")); }
    }
    [Fact]
    public async Task AutomaticChecksAreThrottledAndNeverDownload()
    {
        using var d = new TestDirectory(); var b = new Backend(); var now = DateTimeOffset.UtcNow;
        var c = new AppUpdateController(b, d.Path, () => now);
        await c.Check(false, default); await c.Check(false, default);
        Assert.Equal(1, b.Checks); Assert.Equal(0, b.Downloads); Assert.Equal(AppUpdateState.Available, c.State);
        now += TimeSpan.FromDays(1); await c.Check(false, default); Assert.Equal(2, b.Checks);
        c.SetAutomatic(false); now += TimeSpan.FromDays(1); await c.Check(false, default); Assert.Equal(2, b.Checks);
        await c.Check(true, default); Assert.Equal(3, b.Checks);
        Assert.False(new AppUpdateController(b, d.Path).Preferences.AutomaticChecks);
    }
    [Fact]
    public async Task SkippedVersionIsHiddenAutomaticallyButVisibleManually()
    {
        using var d = new TestDirectory(); var b = new Backend(); var now = DateTimeOffset.UtcNow;
        var c = new AppUpdateController(b, d.Path, () => now); await c.Check(true, default); c.Skip();
        now += TimeSpan.FromDays(1); await c.Check(false, default); Assert.Null(c.Release);
        await c.Check(true, default); Assert.Equal("1.1.0", c.Release?.Version);
    }
    [Fact]
    public async Task DevelopmentBuildDoesNotCheckDownloadOrCreateUpdateStorage()
    {
        using var d = new TestDirectory(); var b = new Backend { Installed = false };
        var c = new AppUpdateController(b, d.Path); await c.Check(true, default); await c.Download(default);
        Assert.Equal(AppUpdateState.Development, c.State); Assert.Equal(0, b.Checks); Assert.Equal(0, b.Downloads);
        Assert.False(Directory.Exists(Path.Combine(d.Path, "updates")));
    }
    [Fact]
    public async Task PendingDownloadRequiresExplicitRestartAndIsNotReplacedByChecks()
    {
        using var d = new TestDirectory(); var b = new Backend { Pending = new("1.1.0", "", 1234) };
        var c = new AppUpdateController(b, d.Path); await c.Check(true, default);
        Assert.Equal(AppUpdateState.Ready, c.State); Assert.Equal(0, b.Checks); Assert.Equal(0, b.Downloads);
        Assert.Equal("package", (await c.Verify(default)).Path);
    }
    [Fact]
    public async Task DamagedPendingPackageAllowsAnotherCheckAndDownload()
    {
        using var d = new TestDirectory(); var b = new Backend { Pending = new("1.1.0", "", 1234), Failure = new IOException("Hash mismatch") };
        var c = new AppUpdateController(b, d.Path);
        await Assert.ThrowsAsync<IOException>(() => c.Verify(default)); Assert.Equal(AppUpdateState.Error, c.State);
        b.Failure = null; await c.Check(true, default); await c.Download(default);
        Assert.Equal(1, b.Checks); Assert.Equal(1, b.Downloads); Assert.Equal(AppUpdateState.Ready, c.State);
    }
    [Fact]
    public async Task CancelledDownloadCanBeRetriedWithoutLosingRelease()
    {
        using var d = new TestDirectory(); var b = new Backend { Wait = true }; var c = new AppUpdateController(b, d.Path);
        await c.Check(true, default); using var stop = new CancellationTokenSource();
        var download = c.Download(stop.Token); stop.Cancel(); await download;
        Assert.Equal(AppUpdateState.Available, c.State); Assert.NotNull(c.Release);
        b.Wait = false; await c.Download(default); Assert.Equal(AppUpdateState.Ready, c.State); Assert.Equal(100, c.Progress);
    }
    [Theory]
    [InlineData("Offline")]
    [InlineData("GitHub rate limit")]
    [InlineData("Missing release asset")]
    [InlineData("Hash mismatch")]
    public async Task FailuresAreReportedAndRetryIsPossible(string message)
    {
        using var d = new TestDirectory(); var b = new Backend { Failure = new IOException(message) }; var c = new AppUpdateController(b, d.Path);
        await c.Check(true, default); Assert.Equal(AppUpdateState.Error, c.State); Assert.Contains(message, c.Message);
        b.Failure = null; await c.Check(true, default); b.Failure = new IOException(message);
        await c.Download(default); Assert.Equal(AppUpdateState.Error, c.State); Assert.Contains(message, c.Message);
        b.Failure = null; await c.Download(default); Assert.Equal(AppUpdateState.Ready, c.State);
    }
    [Fact]
    public void UpdateGateBlocksConcurrentStartsAndReleasesAfterAbort()
    {
        using var d = new TestDirectory(); using var activity = InstallationActivity.Register(d.Path, "gui", "custom-data");
        Assert.Single(InstallationActivity.Processes(d.Path));
        var token = InstallationActivity.BeginUpdate(d.Path);
        try
        {
            Assert.Throws<IOException>(() => InstallationActivity.BeginUpdate(d.Path));
            Assert.Throws<InstallationUpdateInProgressException>(() => InstallationActivity.Register(d.Path, "audio-service", "other-data"));
            InstallationActivity.AttachHelper(d.Path, token); Assert.True(InstallationActivity.HelperReady(d.Path, token));
        }
        finally { InstallationActivity.EndUpdate(d.Path, token); }
        Assert.False(InstallationActivity.HelperReady(d.Path, token));
        Assert.Throws<IOException>(() => InstallationActivity.ConfirmHelper(d.Path, token));
    }
}
