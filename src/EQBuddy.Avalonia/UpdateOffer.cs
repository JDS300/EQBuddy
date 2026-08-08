using EQBuddy.Core;

namespace EQBuddy.Avalonia;

/// <summary>
/// The update banner's decisions, as pure functions of the found update and the platform,
/// so tests can pin them down without a network or a window. The stakes: this app ships
/// on Windows (installer) and Linux (tarball), and issue #56 was Linux users being steered
/// toward EQBuddySetup.exe — every string and URL here exists to send each platform to
/// the artifact it can actually use.
/// </summary>
internal static class UpdateOffer
{
    /// <summary>The staged file is always a Windows EQBuddySetup.exe run with an Inno
    /// Setup /SILENT flag — there's nothing installable that way on Linux, so updates
    /// there always go through the browser. OneDrive-sourced updates (SetupPath) are a
    /// Windows-only distribution channel already, so they're unaffected by this check.</summary>
    internal static bool CanAutoInstall(UpdateInfo info, bool isWindows) =>
        isWindows && (info.SetupPath is not null || info.DownloadUrl is not null);

    /// <summary>What the banner offers before the click.</summary>
    internal static string OfferText(UpdateInfo info, bool isWindows)
    {
        if (CanAutoInstall(info, isWindows))
            return $"Update v{info.Latest} is ready - click here to install.";
        if (!isWindows && info.LinuxTarballUrl is not null)
            return $"Update v{info.Latest} is available - click to download {UpdateChecker.LinuxTarballName}.";
        return $"Update v{info.Latest} is available - click to open the download page.";
    }

    /// <summary>Where a non-auto-install click sends the browser: Linux goes straight to
    /// the tarball asset when the release has one (the point of issue #56 — no hunting
    /// through a page whose most prominent asset is a Windows installer). The release
    /// page remains the fallback everywhere, including the minutes-wide window where CI
    /// hasn't attached the tarball to a fresh release yet.</summary>
    internal static string BrowserTarget(UpdateInfo info, bool isWindows) =>
        !isWindows && info.LinuxTarballUrl is { } tarball ? tarball : UpdateChecker.GitHubLatestPage;

    /// <summary>What the banner says once the browser is open. On Linux the setup exe
    /// means nothing — say what actually works there (issue #30: the old text told Linux
    /// users to run a Windows installer).</summary>
    internal static string OpenedText(UpdateInfo info, bool isWindows)
    {
        if (isWindows)
            return "Download page opened - run the new EQBuddySetup.exe to update.";
        return info.LinuxTarballUrl is not null
            ? $"Downloading {UpdateChecker.LinuxTarballName} - extract it over this install and restart."
            : $"Download page opened - get {UpdateChecker.LinuxTarballName} and extract it over this install.";
    }
}
