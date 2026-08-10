using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace EQBuddy.Avalonia.Tests;

/// <summary>
/// Chip size as a scale of its own, separate from the widget's.
///
/// The Avalonia app pushed UiScale into every chip window and never read ChipScale, so the
/// only way to enlarge chips was to enlarge the whole widget - fine at 1080p, useless at 4K
/// where the widget is already the size you want and the chips are the thing you cannot read.
/// </summary>
[Collection("avalonia")]
public class ChipScalingTests : IDisposable
{
    private readonly string _profile =
        Directory.CreateTempSubdirectory("eqbuddy-chipscale-render-").FullName;

    public ChipScalingTests()
    {
        Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", _profile);
        Directory.CreateDirectory(Path.Combine(_profile, "logs"));
        File.WriteAllText(Path.Combine(_profile, "settings.json"),
            $$"""
              { "LogFolder": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_profile, "logs"))}},
                "TruncateLogs": false, "ShowTutorial": false, "Theme": "ParchmentBrass",
                "UiScale": 1.0, "ChipScale": 1.0, "ChipScaleFromUiScaleMigrated": true }
              """);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", TestProfile.Root);
        try { Directory.Delete(_profile, recursive: true); } catch { /* best effort */ }
    }

    private static double ScaleOf(Window window)
    {
        var root = Assert.IsType<LayoutTransformControl>(window.Content);
        if (root.LayoutTransform is null) return 1.0;
        return Assert.IsType<ScaleTransform>(root.LayoutTransform).ScaleX;
    }

    [AvaloniaFact]
    public void SettingTheChipScaleClampsAndPersistsIt()
    {
        var main = new MainWindow();
        main.Show();

        main.SetChipScale(1.5);
        Assert.Equal(1.5, main.ChipScale, 3);

        main.SetChipScale(9.0);
        Assert.Equal(2.0, main.ChipScale, 3);

        main.Close();
    }

    /// <summary>The whole point of the change: the widget's Size slider must stop dragging
    /// the chips around with it.</summary>
    [AvaloniaFact]
    public void ResizingTheWidgetLeavesTheChipScaleAlone()
    {
        var main = new MainWindow();
        main.Show();
        main.SetChipScale(1.4);

        main.SetUiScale(0.8);

        Assert.Equal(1.4, main.ChipScale, 3);
        main.Close();
    }

    [AvaloniaFact]
    public void TheAlertTileScalesWithTheChips()
    {
        var main = new MainWindow();
        main.Show();

        main.SetChipScale(1.5);

        Assert.Equal(1.5, ScaleOf(main.AlertTile), 3);
        main.Close();
    }

    /// <summary>The tile is built lazily on first alert, which can be long after the slider
    /// moved. Constructing it at the wrong size is the obvious way to get this wrong.</summary>
    [AvaloniaFact]
    public void AnAlertTileBuiltAfterTheSliderMovedStillComesUpScaled()
    {
        var main = new MainWindow();
        main.Show();

        main.SetChipScale(1.75);   // before the tile has ever been touched

        Assert.Equal(1.75, ScaleOf(main.AlertTile), 3);
        main.Close();
    }

    [AvaloniaFact]
    public void OptionsOffersAChipSizeSliderCoveringTheWidgetsRange()
    {
        var main = new MainWindow();
        main.Show();
        var options = new OptionsWindow(main);
        options.Show();

        var slider = options.GetVisualDescendants().OfType<Slider>()
            .Single(s => Math.Abs(s.Minimum - 0.5) < 0.001 && Math.Abs(s.Maximum - 2.0) < 0.001);

        slider.Value = 1.6;

        Assert.Equal(1.6, main.ChipScale, 3);
        options.Close();
        main.Close();
    }
}
