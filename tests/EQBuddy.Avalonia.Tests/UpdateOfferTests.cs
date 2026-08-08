using EQBuddy.Core;

namespace EQBuddy.Avalonia.Tests;

/// <summary>
/// Pins the platform routing of the update banner (issues #30 and #56): Windows installs
/// or is sent to the installer, Linux is pointed at the tarball asset — never at
/// EQBuddySetup.exe. Pure-function tests; the real click handler feeds these outputs to
/// Process.Start and the banner TextBlock verbatim.
/// </summary>
public class UpdateOfferTests
{
    private static readonly Version V = new(1, 40, 0);

    private static UpdateInfo FullRelease => new(V, SetupPath: null,
        DownloadUrl: "https://gh/EQBuddySetup.exe",
        Sha256Url: "https://gh/EQBuddySetup.exe.sha256",
        LinuxTarballUrl: "https://gh/EQBuddy-linux-x64.tar.gz");

    private static UpdateInfo WindowsOnlyRelease => new(V, SetupPath: null,
        DownloadUrl: "https://gh/EQBuddySetup.exe",
        Sha256Url: "https://gh/EQBuddySetup.exe.sha256");

    private static UpdateInfo OneDriveUpdate => new(V, SetupPath: @"C:\OneDrive\EQBuddySetup.exe");

    // ---- Windows keeps its silent install ----

    [Fact]
    public void WindowsAutoInstallsFromGitHubOrOneDrive()
    {
        Assert.True(UpdateOffer.CanAutoInstall(FullRelease, isWindows: true));
        Assert.True(UpdateOffer.CanAutoInstall(OneDriveUpdate, isWindows: true));
        Assert.Contains("click here to install", UpdateOffer.OfferText(FullRelease, isWindows: true));
    }

    // ---- Linux never touches the installer ----

    [Fact]
    public void LinuxNeverAutoInstalls()
    {
        Assert.False(UpdateOffer.CanAutoInstall(FullRelease, isWindows: false));
        Assert.False(UpdateOffer.CanAutoInstall(OneDriveUpdate, isWindows: false));
    }

    /// <summary>The heart of issue #56: the click on Linux must land on the tarball asset,
    /// and nothing user-visible may mention the Windows installer.</summary>
    [Fact]
    public void LinuxIsPointedAtTheTarballAsset()
    {
        Assert.Equal("https://gh/EQBuddy-linux-x64.tar.gz",
            UpdateOffer.BrowserTarget(FullRelease, isWindows: false));

        Assert.Contains("EQBuddy-linux-x64.tar.gz", UpdateOffer.OfferText(FullRelease, isWindows: false));
        Assert.DoesNotContain("EQBuddySetup.exe", UpdateOffer.OfferText(FullRelease, isWindows: false));
        Assert.DoesNotContain("EQBuddySetup.exe", UpdateOffer.OpenedText(FullRelease, isWindows: false));
    }

    /// <summary>A fresh release may not have its tarball yet (CI attaches it minutes after
    /// publish). Linux still learns about the update and gets the release page.</summary>
    [Fact]
    public void LinuxFallsBackToTheReleasePageWhenTheTarballIsMissing()
    {
        Assert.Equal(UpdateChecker.GitHubLatestPage,
            UpdateOffer.BrowserTarget(WindowsOnlyRelease, isWindows: false));
        Assert.Contains("download page", UpdateOffer.OfferText(WindowsOnlyRelease, isWindows: false));
        Assert.Contains("EQBuddy-linux-x64.tar.gz", UpdateOffer.OpenedText(WindowsOnlyRelease, isWindows: false));
    }

    /// <summary>Windows without a verifiable installer (fail-closed path) is browser-bound
    /// too, but goes to the release page, not the Linux tarball.</summary>
    [Fact]
    public void WindowsWithoutAnInstallerGetsTheReleasePage()
    {
        var noInstaller = new UpdateInfo(V, SetupPath: null, LinuxTarballUrl: "https://gh/EQBuddy-linux-x64.tar.gz");
        Assert.False(UpdateOffer.CanAutoInstall(noInstaller, isWindows: true));
        Assert.Equal(UpdateChecker.GitHubLatestPage, UpdateOffer.BrowserTarget(noInstaller, isWindows: true));
        Assert.Contains("EQBuddySetup.exe", UpdateOffer.OpenedText(noInstaller, isWindows: true));
    }
}
