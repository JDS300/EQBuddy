using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// AppSettings.AdoptChipScaleAsAlertScale - the alert tile leaving the chip family.
///
/// The tile joined that family earlier the same day, which already resized it once for
/// anyone whose widget was above 100%. Giving it its own scale must not resize it a second
/// time, so the new setting starts life at whatever the tile is currently rendering: the
/// chip scale.
/// </summary>
public class AlertScaleMigrationTests
{
    [Fact]
    public void TheFirstPassAdoptsTheChipScaleSoTheTileDoesNotResize()
    {
        var s = new AppSettings { ChipScale = 1.3, AlertScale = 1.0 };

        Assert.True(s.AdoptChipScaleAsAlertScale());

        Assert.Equal(1.3, s.AlertScale, 3);
        Assert.True(s.AlertScaleFromChipScaleMigrated);
    }

    [Fact]
    public void ASecondPassLeavesAChosenSizeAlone()
    {
        var s = new AppSettings { ChipScale = 1.3, AlertScale = 1.0 };
        s.AdoptChipScaleAsAlertScale();
        s.AlertScale = 0.8;

        Assert.False(s.AdoptChipScaleAsAlertScale());

        Assert.Equal(0.8, s.AlertScale, 3);
    }

    [Fact]
    public void AnOutOfRangeChipScaleIsClampedIntoTheSliderRange()
    {
        var s = new AppSettings { ChipScale = 2.0, AlertScale = 1.0 };
        s.AdoptChipScaleAsAlertScale();
        Assert.Equal(2.0, s.AlertScale, 3);

        var tiny = new AppSettings { ChipScale = 0.1, AlertScale = 1.0 };
        tiny.AdoptChipScaleAsAlertScale();
        Assert.Equal(0.5, tiny.AlertScale, 3);
    }

    /// <summary>Load stays a pure read - the rule that outlives every one of these passes.</summary>
    [Fact]
    public void LoadDoesNotRunTheMigration()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-alertscale-").FullName;
        var previous = Environment.GetEnvironmentVariable("EQBUDDY_APPDATA");
        Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{"ChipScale":1.3}""");

            var loaded = AppSettings.Load();

            Assert.False(loaded.AlertScaleFromChipScaleMigrated);
            Assert.Equal(1.0, loaded.AlertScale, 3);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
