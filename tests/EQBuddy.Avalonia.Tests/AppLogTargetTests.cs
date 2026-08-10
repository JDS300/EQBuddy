using EQBuddy.Avalonia;
using EQBuddy.Core;

namespace EQBuddy.Avalonia.Tests;

/// <summary>
/// Where App.LogError actually writes.
///
/// This is the bug that put four stack traces into the developer's live
/// ~/.config/EQBuddy/error.log during a test run: App.ErrorLog was a static readonly field,
/// so it resolved AppPaths ONCE when the type initialized — at app boot, before any test
/// class constructor sets EQBUDDY_APPDATA. Every isolated profile the tests set up after
/// that was ignored for logging purposes, and anything the app logged went to the real
/// user's profile. HotkeyServiceTests already carried a comment about this happening; the
/// cause was one field away the whole time.
/// </summary>
public class AppLogTargetTests
{
    [Fact]
    public void LogErrorFollowsTheProfileInEffectNowNotTheOneAtStartup()
    {
        var first = Directory.CreateTempSubdirectory("eqbuddy-logtarget-a-").FullName;
        var second = Directory.CreateTempSubdirectory("eqbuddy-logtarget-b-").FullName;
        var previous = Environment.GetEnvironmentVariable("EQBUDDY_APPDATA");
        try
        {
            // Force App's type initializer to run while the FIRST profile is active. This is
            // what the app host does at boot, and it is the moment the old code froze the
            // path forever. Without this the test passes by accident when the filter happens
            // to skip the Avalonia host.
            Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", first);
            App.LogError("probe: first profile");
            Assert.True(File.Exists(Path.Combine(first, "error.log")), "setup: first write failed");

            Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", second);
            App.LogError("probe: second profile");

            var log = Path.Combine(second, "error.log");
            Assert.True(File.Exists(log),
                $"App.LogError kept writing to the profile from startup instead of {log}");
            Assert.Contains("second profile", File.ReadAllText(log), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", previous);
            foreach (var dir in new[] { first, second })
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Whatever the tests do to EQBUDDY_APPDATA, the assembly must never resolve the
    /// developer's real profile — the boot-time log write happens before any test can set it,
    /// so isolation has to be in place before the app starts.</summary>
    [Fact]
    public void TheTestAssemblyNeverResolvesTheRealProfile()
    {
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQBuddy");

        Assert.NotEqual(real, AppPaths.Dir);
    }
}
