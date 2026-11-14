using System.Security.Cryptography;
using System.Text.Json;
using Linguacode.Application;

using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

namespace Linguacode.Tests;

public sealed class UpdateFeedTests
{
    private static GithubAppUpdates Backend(string root)
    {
        var packages = Path.Combine(root, "packages"); Directory.CreateDirectory(packages);
        var locator = new TestVelopackLocator("Linguacode.App", "1.0.0", packages, root, root, Path.Combine(root, "Update.exe"), "win-x64", null, null, Path.Combine(root, "Linguacode.exe"));
        return new(new UpdateManager(new SimpleFileSource(new DirectoryInfo(Path.Combine(root, "feed"))),
            new UpdateOptions { ExplicitChannel = "win-x64", MaximumDeltasBeforeFallback = -1 }, locator), locator);
    }
    private static VelopackAsset Feed(string root, string version, string? hash = null)
    {
        var feed = Path.Combine(root, "feed"); Directory.CreateDirectory(feed);
        var content = new byte[] { 1, 2, 3, 4 };
        var asset = new VelopackAsset { PackageId = "Linguacode.App", Version = SemanticVersion.Parse(version), Type = VelopackAssetType.Full,
            FileName = "Linguacode.App-" + version + "-full.nupkg", Size = content.Length,
            SHA256 = hash ?? Convert.ToHexString(SHA256.HashData(content)), SHA1 = Convert.ToHexString(SHA1.HashData(content)), NotesMarkdown = "Notes" };
        File.WriteAllBytes(Path.Combine(feed, asset.FileName), content);
        File.WriteAllText(Path.Combine(feed, "releases.win-x64.json"), JsonSerializer.Serialize(new { Assets = new[] { new { asset.PackageId, Version = version, Type = "Full", asset.FileName, asset.Size, asset.SHA256, asset.SHA1, asset.NotesMarkdown } } }));
        return asset;
    }
    [Theory]
    [InlineData("0.9.0", false)]
    [InlineData("1.0.0", false)]
    [InlineData("1.10.0", true)]
    public async Task ActualFeedUsesSemanticVersionOrdering(string version, bool newer)
    {
        using var d = new TestDirectory(); Feed(d.Path, version); var backend = Backend(d.Path);
        Assert.True(backend.Installed);
        Assert.Equal(newer, await backend.Check(default) is not null);
    }
    [Fact]
    public async Task ActualFeedRejectsPrereleaseAndMissingChecksum()
    {
        using var d = new TestDirectory(); Feed(d.Path, "2.0.0-beta.1");
        await Assert.ThrowsAsync<IOException>(() => Backend(d.Path).Check(default));
        Feed(d.Path, "2.0.0", ""); await Assert.ThrowsAsync<IOException>(() => Backend(d.Path).Check(default));
    }
    [Fact]
    public async Task ActualPackageVerificationRejectsTamperingAndMissingFile()
    {
        using var d = new TestDirectory(); var asset = Feed(d.Path, "2.0.0"); var backend = Backend(d.Path);
        var release = (await backend.Check(default))!;
        var target = Path.Combine(d.Path, "packages", asset.FileName);
        File.Copy(Path.Combine(d.Path, "feed", asset.FileName), target);
        Assert.Equal(target, (await backend.Verify(release, default)).Path);
        File.WriteAllBytes(target, [4, 3, 2, 1]); await Assert.ThrowsAsync<IOException>(() => backend.Verify(release, default));
        File.Delete(target); await Assert.ThrowsAsync<FileNotFoundException>(() => backend.Verify(release, default));
    }
    [Fact]
    public async Task MalformedFeedDoesNotBecomeAnAvailableUpdate()
    {
        using var d = new TestDirectory(); Feed(d.Path, "2.0.0");
        File.WriteAllText(Path.Combine(d.Path, "feed", "releases.win-x64.json"), "invalid JSON");
        var controller = new AppUpdateController(Backend(d.Path), d.Path);
        await controller.Check(true, default); Assert.Equal(AppUpdateState.Error, controller.State); Assert.Null(controller.Release);
    }
}
