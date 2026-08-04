using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Avalonia.Tests;

/// <summary>
/// Renders the ported mez-chip stack, spawn-chip stack, and Spawns window without a
/// display server — same reasoning as <see cref="WidgetRenderTests"/>: these three
/// windows shipped to Linux verified only by "it compiles and the unit tests pass",
/// which is exactly the gap that let the Options sound picker go quietly missing on
/// Linux for a release (see <see cref="OptionsRenderTests"/>). A captured frame catches
/// a window that constructs but draws nothing; a visual-tree walk catches a chip stack
/// that silently drops a row, or a countdown that never flips to its due state.
/// </summary>
[Collection("avalonia")]
public class ChipWindowRenderTests : IDisposable
{
    private readonly string _profile =
        Directory.CreateTempSubdirectory("eqbuddy-render-").FullName;

    public ChipWindowRenderTests()
    {
        // Isolate settings/history: constructing MainWindow opens a SQLite history db and
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

    /// <summary>Every chip window takes the real MainWindow as its owner, so it's built
    /// headlessly the same way WidgetRenderTests builds the widget itself.</summary>
    private static MainWindow OpenMain()
    {
        var main = new MainWindow();
        main.Show();
        return main;
    }

    /// <summary>An in-memory catalog/overrides/timers trio — no disk paths, so a test
    /// never touches spawn-overrides.json or spawn-timers.json in the real profile.</summary>
    private static (SpawnsViewModel Vm, SpawnTimers Timers) NewSpawnStack(string server = "TestServer")
    {
        var catalog = new SpawnCatalog();
        var overrides = new SpawnOverrides();
        var timers = new SpawnTimers(catalog, overrides) { Server = server };
        var vm = new SpawnsViewModel(catalog, overrides, timers);
        return (vm, timers);
    }

    /// <summary>Just the per-chip Border rows, not the window template's own background
    /// borders (Avalonia's Fluent window chrome renders a couple of transparent, zero
    /// corner-radius borders of its own) — both chip windows lay their chips in a
    /// StackPanel, which the template borders never sit inside.</summary>
    private static List<Border> ChipBorders(Window window) =>
        window.GetVisualDescendants().OfType<Border>().Where(b => b.Parent is StackPanel).ToList();

    private static List<SpawnChip> TwoMezChips() =>
    [
        new SpawnChip("", "orc pawn", "1:30", false, "Mesmerize by Wizard1 · landed 3:00:00 PM", "💤"),
        new SpawnChip("", "a young forest spider (2)", "0:04", true, "Mesmerize by Wizard1 · landed 3:00:04 PM", "💤"),
    ];

    // ---- MezChipsWindow ----

    /// <summary>The mez chip stack builds, shows, and paints something. A window that
    /// constructs but draws nothing is the failure mode "it compiles" can't see.</summary>
    [AvaloniaFact]
    public void TheMezChipStackRendersAFrame()
    {
        var main = OpenMain();
        var window = new MezChipsWindow(main, _ => TwoMezChips());
        window.Show();
        window.RefreshChips(DateTime.Now);

        var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.True(frame!.Size.Width > 20, $"mez chip stack rendered only {frame.Size.Width}px wide");
        Assert.True(frame.Size.Height > 20, $"mez chip stack rendered only {frame.Size.Height}px tall");
        window.Close();
        main.Close();
    }

    /// <summary>Both chips the source function hands back are actually in the visual
    /// tree, not just entries in a list the window never rendered.</summary>
    [AvaloniaFact]
    public void TheMezChipStackShowsEveryChipItIsGiven()
    {
        var main = OpenMain();
        var window = new MezChipsWindow(main, _ => TwoMezChips());
        window.Show();
        window.RefreshChips(DateTime.Now);

        var chipBorders = ChipBorders(window);

        Assert.Equal(2, chipBorders.Count);
        window.Close();
        main.Close();
    }

    // ---- SpawnChipsWindow ----

    /// <summary>The spawn chip stack, driven by a real SpawnsViewModel over running
    /// timers, builds, shows, and paints something.</summary>
    [AvaloniaFact]
    public void TheSpawnChipStackRendersAFrame()
    {
        var main = OpenMain();
        var (vm, timers) = NewSpawnStack();
        timers.StartManual("East Commonlands", "Aree Sunwing", 1800);
        timers.StartManual("West Karana", "Verina Tomb", 3600);

        var window = new SpawnChipsWindow(main, vm);
        window.Show();
        window.RefreshChips(DateTime.Now);

        var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.True(frame!.Size.Width > 20, $"spawn chip stack rendered only {frame.Size.Width}px wide");
        Assert.True(frame.Size.Height > 20, $"spawn chip stack rendered only {frame.Size.Height}px tall");
        window.Close();
        main.Close();
    }

    /// <summary>Every running timer on the server gets its own chip in the visual tree —
    /// the double-click-to-browse row a chip stands in for. Catches a stack that renders
    /// but drops rows past the first, or that only shows chips for the current zone
    /// (the whole point is every zone's timer counts down regardless of where you are).</summary>
    [AvaloniaFact]
    public void TheSpawnChipStackShowsEveryRunningTimer()
    {
        var main = OpenMain();
        var (vm, timers) = NewSpawnStack();
        timers.StartManual("East Commonlands", "Aree Sunwing", 1800);
        timers.StartManual("West Karana", "Verina Tomb", 3600);

        var window = new SpawnChipsWindow(main, vm);
        window.Show();
        window.RefreshChips(DateTime.Now);

        var chipBorders = ChipBorders(window);

        Assert.Equal(2, chipBorders.Count);
        window.Close();
        main.Close();
    }

    /// <summary>A timer already past due renders the chip's warn state — "DUE" in place
    /// of a countdown. This is the moment the whole stack exists to announce, so a chip
    /// that keeps counting down (or blanks out) past zero would defeat the feature.</summary>
    [AvaloniaFact]
    public void ADueTimerRendersItsDueState()
    {
        var main = OpenMain();
        var (vm, timers) = NewSpawnStack();
        // Killed 2.5 minutes ago against a 2-minute respawn: 30 seconds into the due
        // state, well inside SpawnTimers.DueLinger (60s), so Snapshot still returns it.
        timers.StartManual("Blackburrow", "Gnorm Ablehismer", 120, TimeSpan.FromSeconds(150));

        var window = new SpawnChipsWindow(main, vm);
        window.Show();
        window.RefreshChips(DateTime.Now);

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "").ToList();

        Assert.Contains("DUE", texts);
        window.Close();
        main.Close();
    }

    // ---- SpawnsWindow ----

    /// <summary>The Spawns browser — no longer WPF-only — builds, shows, and paints
    /// something on Linux too.</summary>
    [AvaloniaFact]
    public void TheSpawnsWindowRendersAFrame()
    {
        var main = OpenMain();
        var (vm, _) = NewSpawnStack();

        var window = new SpawnsWindow(main, vm);
        window.Show();

        var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.True(frame!.Size.Width > 100, $"Spawns window rendered only {frame.Size.Width}px wide");
        Assert.True(frame.Size.Height > 100, $"Spawns window rendered only {frame.Size.Height}px tall");
        window.Close();
        main.Close();
    }
}
