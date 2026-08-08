using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// AppSettings.UnbindLegacyHotkeyDefaults — the one-time pass that stops EQBuddy holding
/// hotkeys nobody chose.
///
/// Upstream deleted global hotkeys in 1.34 because they shipped bound and RegisterHotKey is
/// system-wide: EQBuddy quietly owned Ctrl+Shift+T (reopen browser tab) for the whole
/// machine. This fork keeps the feature with empty defaults, but a default only reaches new
/// installs — everyone already running EQBuddy has the old combinations sitting in their
/// settings.json. This pass clears those, and only those: a combination the user picked
/// themselves is a decision, not a leftover.
/// </summary>
public class HotkeyUnbindMigrationTests
{
    [Fact]
    public void ANewInstallHasNothingBound()
    {
        var s = new AppSettings();

        Assert.Equal("", s.HotkeyToggleOverlay);
        Assert.Equal("", s.HotkeyClickThrough);
        Assert.Equal("", s.HotkeyMiniMode);
        Assert.Equal("", s.HotkeyCampMarker);
    }

    /// <summary>The whole point: the shipped defaults, including the browser-eating one.</summary>
    [Fact]
    public void TheOldShippedDefaultsAreCleared()
    {
        var s = new AppSettings
        {
            HotkeyToggleOverlay = "Ctrl+Shift+H",
            HotkeyClickThrough = "Ctrl+Shift+T",
            HotkeyMiniMode = "Ctrl+Shift+M",
            HotkeyCampMarker = "Ctrl+Shift+K",
        };

        Assert.True(s.UnbindLegacyHotkeyDefaults());

        Assert.Equal("", s.HotkeyToggleOverlay);
        Assert.Equal("", s.HotkeyClickThrough);
        Assert.Equal("", s.HotkeyMiniMode);
        Assert.Equal("", s.HotkeyCampMarker);
        Assert.True(s.HotkeysUnboundMigrated);
    }

    /// <summary>A binding the user went and typed is theirs. Clearing it would be the
    /// migration destroying the very setting it exists to hand back control of.</summary>
    [Fact]
    public void ACustomisedBindingIsLeftAlone()
    {
        var s = new AppSettings
        {
            HotkeyToggleOverlay = "Ctrl+Alt+F9",
            HotkeyClickThrough = "Ctrl+Shift+T",
        };

        s.UnbindLegacyHotkeyDefaults();

        Assert.Equal("Ctrl+Alt+F9", s.HotkeyToggleOverlay);
        Assert.Equal("", s.HotkeyClickThrough);
    }

    /// <summary>Runs once. Otherwise a binding set after the upgrade would be wiped at the
    /// next launch if it happened to match an old default — and the pass would rewrite
    /// settings.json forever.</summary>
    [Fact]
    public void ItDoesNotRunASecondTime()
    {
        var s = new AppSettings { HotkeyClickThrough = "Ctrl+Shift+T" };
        s.UnbindLegacyHotkeyDefaults();

        s.HotkeyClickThrough = "Ctrl+Shift+T";   // deliberately re-bound by the user
        Assert.False(s.UnbindLegacyHotkeyDefaults());
        Assert.Equal("Ctrl+Shift+T", s.HotkeyClickThrough);
    }

    /// <summary>The flag has to come back off disk, or "runs once" is a promise kept only to
    /// a fresh object while the real settings.json re-triggers the pass at every launch.</summary>
    [Fact]
    public void APersistedFlagStopsTheMigrationComingBack()
    {
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{"HotkeysUnboundMigrated":true,"HotkeyMiniMode":"Ctrl+Shift+M"}""")!;

        Assert.False(restored.UnbindLegacyHotkeyDefaults());
        Assert.Equal("Ctrl+Shift+M", restored.HotkeyMiniMode);
    }

    /// <summary>
    /// Load() must NOT run this pass. It is destructive — it empties a setting — and Load()
    /// is called by tests, by theme application, and by anything that reads settings without
    /// having been launched by a user. Wired into Load(), it reached out of a test run and
    /// emptied the developer's own live ~/.config/EQBuddy/settings.json. 552e7da closed the
    /// same trap for a different test; this pins it shut at the source.
    /// </summary>
    [Fact]
    public void LoadingSettingsNeverClearsAHotkeyByItself()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eqb-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var previous = Environment.GetEnvironmentVariable("EQBUDDY_APPDATA");
        try
        {
            Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{"HotkeyClickThrough":"Ctrl+Shift+T","HotkeyToggleOverlay":"Ctrl+Shift+H"}""");

            var loaded = AppSettings.Load();

            Assert.Equal("Ctrl+Shift+T", loaded.HotkeyClickThrough);
            Assert.Equal("Ctrl+Shift+H", loaded.HotkeyToggleOverlay);
            Assert.False(loaded.HotkeysUnboundMigrated);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>An old settings.json predates the flag, so it deserializes false and the
    /// pass fires exactly where it's needed.</summary>
    [Fact]
    public void SettingsSavedBeforeTheFlagExistedStillGetMigrated()
    {
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{"HotkeyClickThrough":"Ctrl+Shift+T"}""")!;

        Assert.False(restored.HotkeysUnboundMigrated);
        Assert.True(restored.UnbindLegacyHotkeyDefaults());
        Assert.Equal("", restored.HotkeyClickThrough);
    }
}
