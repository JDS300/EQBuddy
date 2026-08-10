using EQBuddy.Avalonia;

namespace EQBuddy.Avalonia.Tests;

/// <summary>
/// Which session type EQBuddy thinks it is running under, and what Options says about it.
///
/// This exists because the ORIGINAL guard got the question backwards. It treated Wayland as
/// "WAYLAND_DISPLAY set AND DISPLAY unset", but an X11 client under XWayland has BOTH set —
/// so on a KDE Wayland desktop the guard passed, every binding registered, and the one the
/// user had bound reported "already taken by another application" while silently never firing.
/// The environment table below is the real one off that machine.
/// </summary>
public class DesktopSessionTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void AWaylandSessionTypeIsDetected() =>
        Assert.True(DesktopSession.IsWayland(Env(("XDG_SESSION_TYPE", "wayland"))));

    /// <summary>The case the old guard missed: XWayland exports DISPLAY too.</summary>
    [Fact]
    public void AWaylandSessionIsStillDetectedWhenXWaylandExportsADisplay() =>
        Assert.True(DesktopSession.IsWayland(
            Env(("XDG_SESSION_TYPE", "wayland"), ("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"))));

    [Fact]
    public void AWaylandDisplayAloneIsEnough() =>
        Assert.True(DesktopSession.IsWayland(Env(("WAYLAND_DISPLAY", "wayland-0"))));

    [Fact]
    public void TheSessionTypeIsMatchedCaseInsensitively() =>
        Assert.True(DesktopSession.IsWayland(Env(("XDG_SESSION_TYPE", "Wayland"))));

    [Fact]
    public void AnX11SessionIsNotWayland() =>
        Assert.False(DesktopSession.IsWayland(Env(("XDG_SESSION_TYPE", "x11"), ("DISPLAY", ":0"))));

    [Fact]
    public void AnEmptyEnvironmentIsNotWayland() =>
        Assert.False(DesktopSession.IsWayland(Env()));

    /// <summary>An empty variable is not a set variable — a stray export must not
    /// disable hotkeys on a real X11 session.</summary>
    [Fact]
    public void AnEmptyWaylandDisplayIsNotWayland() =>
        Assert.False(DesktopSession.IsWayland(Env(("WAYLAND_DISPLAY", ""))));

    // The Options warning itself is asserted against the real rendered tree in
    // OptionsRenderTests — constructing Avalonia controls from a plain [Fact] here binds the
    // dispatcher to an xUnit worker thread and breaks every [AvaloniaFact] that follows.

    /// <summary>The message the user actually saw for a year: their Ctrl+Alt+K camp marker
    /// logged "already taken by another application", sending them looking for a conflicting
    /// app. On Wayland the grab is refused because the compositor owns global shortcuts, and
    /// the log has to say so or it costs the reader an afternoon.</summary>
    [Fact]
    public void AConflictOnWaylandBlamesTheCompositorRatherThanAnotherApp()
    {
        var message = X11HotkeyService.ConflictMessage("Ctrl+Alt+K", isWayland: true);

        Assert.Contains("Ctrl+Alt+K", message, StringComparison.Ordinal);
        Assert.Contains("Wayland", message, StringComparison.Ordinal);
        Assert.DoesNotContain("another application", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConflictOnX11StillNamesTheOtherApplication()
    {
        var message = X11HotkeyService.ConflictMessage("Ctrl+Shift+T", isWayland: false);

        Assert.Contains("Ctrl+Shift+T", message, StringComparison.Ordinal);
        Assert.Contains("another application", message, StringComparison.Ordinal);
    }

    /// <summary>A grab that SUCCEEDS on Wayland is the quiet case — no conflict, no error,
    /// and the key still never arrives. That is what this warning is for.</summary>
    [Fact]
    public void TheStartupWarningSaysHotkeysWillNotFire()
    {
        Assert.Contains("Wayland", X11HotkeyService.WaylandDeliveryWarning, StringComparison.Ordinal);
        Assert.Contains("will not fire", X11HotkeyService.WaylandDeliveryWarning, StringComparison.Ordinal);
    }
}
