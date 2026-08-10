using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using EQBuddy.Core;

namespace EQBuddy.Avalonia.Tests;

/// <summary>
/// The UI backup window's placement.
///
/// It shipped without a startup location and opened at 0,0 - on a three-monitor desktop that
/// puts it in the far corner of a different screen from the widget you clicked. Every other
/// dialog in the app centres on its owner.
/// </summary>
[Collection("avalonia")]
public class UiBackupWindowTests : IDisposable
{
    private readonly string _store =
        Directory.CreateTempSubdirectory("eqbuddy-bkwin-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_store, recursive: true); } catch { /* best effort */ }
    }

    [AvaloniaFact]
    public void ItOpensOverTheWidgetRatherThanInAScreenCorner()
    {
        var service = new UiBackupService(
            new UiBackupStore(_store), () => null, TimeSpan.FromSeconds(15));

        var window = new UiBackupWindow(service, () => null, () => false);

        Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);
    }
}
