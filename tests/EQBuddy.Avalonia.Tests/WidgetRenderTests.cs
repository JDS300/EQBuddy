using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Avalonia.Tests;

/// <summary>
/// Renders the Linux widget without a display server.
///
/// These exist because of a specific failure of process: for a week, changes shipped to the
/// Avalonia UI — themes, a delay box, a guide panel, per-fight sections — verified only by
/// "it compiles and the unit tests pass". Neither of those would have caught a window that
/// draws nothing, a theme that leaves everything the same colour, or a card that throws when
/// it populates. A frame captured here is the cheapest thing that would.
///
/// The widget is deliberately built as the app builds it, so a break in construction shows
/// up here rather than on a user's desktop.
/// </summary>
[Collection("avalonia")]
public class WidgetRenderTests : IDisposable
{
    private readonly string _profile =
        Directory.CreateTempSubdirectory("eqbuddy-render-").FullName;

    public WidgetRenderTests()
    {
        // Isolate settings/history: constructing the widget opens a SQLite history db and
        // reads settings, and a test must not touch the real profile.
        Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", _profile);
        Directory.CreateDirectory(Path.Combine(_profile, "logs"));
        File.WriteAllText(Path.Combine(_profile, "settings.json"),
            $$"""
              { "LogFolder": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_profile, "logs"))}},
                "TruncateLogs": false, "ShowTutorial": false, "Theme": "ParchmentBrass" }
              """);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", null);
        try { Directory.Delete(_profile, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The widget builds, shows, and paints something. A window that constructs but
    /// draws nothing is the failure mode "it compiles" can't see.</summary>
    [AvaloniaFact]
    public void TheWidgetRendersAFrame()
    {
        var window = new MainWindow();
        window.Show();

        var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.True(frame!.Size.Width > 100, $"window rendered only {frame.Size.Width}px wide");
        Assert.True(frame.Size.Height > 100, $"window rendered only {frame.Size.Height}px tall");
        window.Close();
    }

    /// <summary>The cards are actually in the visual tree, not just fields on the class.</summary>
    [AvaloniaFact]
    public void TheCardsArePresent()
    {
        var window = new MainWindow();
        window.Show();

        var headings = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "").ToList();

        Assert.Contains(headings, h => h.Contains("Combat"));
        Assert.Contains(headings, h => h.Contains("Healing"));
        Assert.Contains(headings, h => h.Contains("Kills"));
        window.Close();
    }

    /// <summary>Applying a snapshot is where a card that mis-formats or dereferences null
    /// blows up — and it's the path every refresh takes.</summary>
    [AvaloniaFact]
    public void ApplyingStatsDoesNotThrow()
    {
        var window = new MainWindow();
        window.Show();

        var stats = new SessionStats { CharacterName = "Testchar" };
        foreach (var line in (string[])
                 [
                     "[Sat Jul 18 15:00:00 2026] You slash orc pawn for 12 points of damage.",
                     "[Sat Jul 18 15:00:02 2026] Orc pawn hits YOU for 4 points of damage.",
                     "[Sat Jul 18 15:00:03 2026] You healed Testchar for 20 hit points by Light Healing.",
                     "[Sat Jul 18 15:00:04 2026] You have slain orc pawn!",
                     "[Sat Jul 18 15:00:05 2026] --You have looted a Mote of Minor Potential from orc pawn's corpse.--",
                 ])
            if (LogParser.Parse(line) is { } evt) stats.Apply(evt);

        var snapshot = stats.Snapshot(null, null);

        // The exception, if any, is the point — this is the call every refresh makes.
        window.RenderSnapshotForTest(snapshot);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        window.Close();
    }

    /// <summary>A theme swap has to change what's on screen. Mutating brushes in place is
    /// clever but invisible to a compiler: this is the check that it actually repaints.</summary>
    [AvaloniaFact]
    public void SwitchingThemeChangesTheColours()
    {
        // Set the starting theme rather than reading whatever the last test left: AppTheme's
        // brushes are process-wide singletons, so an ambient starting point makes this test
        // depend on execution order.
        AppTheme.Apply("ParchmentBrass");
        var parchment = AppTheme.BgBrush.Color;
        AppTheme.Apply("Solarized");
        var light = AppTheme.BgBrush.Color;
        AppTheme.Apply("SolarizedDark");
        var dark = AppTheme.BgBrush.Color;

        Assert.NotEqual(parchment, light);
        Assert.NotEqual(light, dark);

        AppTheme.Apply("ParchmentBrass");
        Assert.Equal(parchment, AppTheme.BgBrush.Color);   // and back again
    }

    /// <summary>Every catalogued theme has to produce a paintable palette on this platform
    /// too — a hex the shared table accepts but Avalonia's parser rejects would only show up
    /// at runtime, on Linux, for whoever picked that theme.</summary>
    [AvaloniaFact]
    public void EveryThemeApplies()
    {
        foreach (var (key, _) in ThemeCatalog.Themes)
        {
            AppTheme.Apply(key);
            Assert.NotEqual(default, AppTheme.BgBrush.Color);
            Assert.NotEqual(default, AppTheme.TextBrush.Color);
        }
        AppTheme.Apply("ParchmentBrass");
    }

    /// <summary>Regression test for the bug where dragging the widget to another monitor,
    /// closing it, and reopening it snapped it back to the launch monitor. MainWindow.OnClosed
    /// used to read <c>Position</c> to persist it, but OnClosed fires after the platform window
    /// is already torn down, so Position reads back (0,0) no matter where the user left it — the
    /// spawn-timer chip stack got this right (it saves from a Closing handler, while the window
    /// still exists) but the widget itself never did. If this regresses, the assertions below
    /// fail with WindowLeft/WindowTop pinned at 0 instead of the point the test moved to.</summary>
    [AvaloniaFact]
    public void ClosingTheWidgetAfterMovingItSavesWhereItActuallyWas()
    {
        var window = new MainWindow();
        window.Show();

        // A distinctive, non-zero, non-default point — the same shape as the user's real bug
        // report (SpawnChipsLeft: 3086, SpawnChipsTop: 2038 while WindowLeft/Top sat at 0).
        window.Position = new PixelPoint(3086, 2038);
        window.Close();

        // Reload from disk rather than trusting the in-memory _settings instance: the bug is
        // specifically that what got written to settings.json is wrong, and a stale in-memory
        // read would pass even with the bug present.
        var reloaded = AppSettings.Load();
        Assert.Equal(3086, reloaded.WindowLeft);
        Assert.Equal(2038, reloaded.WindowTop);

        // OnClosed calls Shutdown(), which asks the lifetime to close every window - and that
        // re-enters OnClosing on this already-destroyed one, where Position has collapsed to
        // (0,0). On a real X11 desktop that second pass overwrote the good save and the widget
        // came back on the launch monitor every time; the first version of this test missed it
        // because headless never re-enters. Closing again reproduces that second pass.
        window.Close();

        var afterSecondClose = AppSettings.Load();
        Assert.Equal(3086, afterSecondClose.WindowLeft);
        Assert.Equal(2038, afterSecondClose.WindowTop);
    }

    /// <summary>Companion to the Closing test above: RestorePosition (run from the
    /// constructor, before Show) is the other half of the position round-trip, and it's
    /// worth pinning too now that OnClosing owns the save. On an on-screen point it restores
    /// correctly here — the X11-specific failure mode (a window manager overriding a position
    /// set before the window is mapped) has no headless equivalent to probe, since headless
    /// has no real window manager to do the overriding.</summary>
    [AvaloniaFact]
    public void RestoringASavedOnScreenPositionPutsTheWidgetThere()
    {
        File.WriteAllText(Path.Combine(_profile, "settings.json"),
            $$"""
              { "LogFolder": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_profile, "logs"))}},
                "TruncateLogs": false, "ShowTutorial": false, "Theme": "ParchmentBrass",
                "WindowLeft": 400, "WindowTop": 300 }
              """);

        var window = new MainWindow();
        window.Show();

        Assert.Equal(400, window.Position.X);
        Assert.Equal(300, window.Position.Y);
        window.Close();
    }
}
