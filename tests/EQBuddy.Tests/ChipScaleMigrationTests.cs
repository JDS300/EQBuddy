using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// AppSettings.AdoptUiScaleAsChipScale - the one-time pass that keeps chips the size they
/// already are when chip scale stops following the widget.
///
/// ChipScale existed in Core from the start, but the Avalonia app never read it: MainWindow
/// pushed UiScale into every chip window, so on Linux chips were welded to the widget's Size
/// slider. Giving chips their own scale means the value in settings.json - 1.0 for everyone
/// who never had a working slider - would suddenly govern, and every Linux user with an
/// enlarged widget would watch their chips shrink on upgrade. Copying UiScale across once
/// makes the change invisible until the user moves the new slider.
/// </summary>
[Collection("profile-env")]
public class ChipScaleMigrationTests
{
    [Fact]
    public void TheFirstPassAdoptsTheWidgetScaleSoChipsDoNotResize()
    {
        var s = new AppSettings { UiScale = 1.3, ChipScale = 1.0 };

        Assert.True(s.AdoptUiScaleAsChipScale());

        Assert.Equal(1.3, s.ChipScale, 3);
        Assert.True(s.ChipScaleFromUiScaleMigrated);
    }

    /// <summary>Returns true the first time even when the value is unchanged, so the flag
    /// itself gets persisted - otherwise the pass reruns every launch and would overwrite a
    /// chip size the user had since chosen.</summary>
    [Fact]
    public void ASecondPassChangesNothing()
    {
        var s = new AppSettings { UiScale = 1.3, ChipScale = 1.0 };
        s.AdoptUiScaleAsChipScale();
        s.ChipScale = 0.9;                    // the user picks their own size afterwards

        Assert.False(s.AdoptUiScaleAsChipScale());

        Assert.Equal(0.9, s.ChipScale, 3);
    }

    [Fact]
    public void AWidgetAtDefaultScaleLeavesChipsAtDefault()
    {
        var s = new AppSettings { UiScale = 1.0, ChipScale = 1.0 };

        Assert.True(s.AdoptUiScaleAsChipScale());

        Assert.Equal(1.0, s.ChipScale, 3);
    }

    /// <summary>The adopted value has to survive a round trip through the Options slider,
    /// so it is clamped into the same range the slider offers.</summary>
    [Fact]
    public void AnOutOfRangeWidgetScaleIsClampedIntoTheSliderRange()
    {
        var s = new AppSettings { UiScale = 4.0, ChipScale = 1.0 };

        s.AdoptUiScaleAsChipScale();

        Assert.Equal(2.0, s.ChipScale, 3);
    }

    /// <summary>Load must not migrate. It is a pure read: a destructive migration placed
    /// there once emptied a live profile from a test run.</summary>
    [Fact]
    public void LoadDoesNotRunTheMigration()
    {
        var dir = Directory.CreateTempSubdirectory("eqbuddy-chipscale-").FullName;
        var previous = Environment.GetEnvironmentVariable("EQBUDDY_APPDATA");
        Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                """{"UiScale":1.3,"ChipScale":1.0}""");

            var loaded = AppSettings.Load();

            Assert.False(loaded.ChipScaleFromUiScaleMigrated);
            Assert.Equal(1.0, loaded.ChipScale, 3);
            Assert.DoesNotContain("ChipScaleFromUiScaleMigrated",
                File.ReadAllText(Path.Combine(dir, "settings.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
