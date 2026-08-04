using EQBuddy.Avalonia;

namespace EQBuddy.Avalonia.Tests;

/// <summary>
/// Global-hotkey registration against a real X server.
///
/// These need a live X11 display and are skipped without one (CI runs headless), because
/// the behaviour under test is an Xlib protocol error — something no fake can produce.
/// </summary>
public class HotkeyServiceTests
{
    private static bool HasX11 =>
        Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 }
        && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    /// <summary>
    /// A hotkey another client already holds must not take the process down with it.
    ///
    /// XGrabKey reports a conflict asynchronously as BadAccess, and Xlib's DEFAULT error
    /// handler prints the error and calls exit(1) — so a second EQBuddy, or the headless
    /// render tests running while the app is open, killed the whole process. The symptom
    /// was "[FATAL ERROR] Catastrophic failure: Test process crashed with exit code 1"
    /// out of a test run that had barely started.
    ///
    /// Grabbing the same key twice in one process reproduces exactly what two clients do.
    /// </summary>
    [Fact]
    public void AHotkeyAlreadyTakenIsReportedInsteadOfKillingTheProcess()
    {
        Assert.SkipUnless(HasX11, "needs a live X11 display");

        // An obscure combination, so a real desktop shortcut is unlikely to own it and
        // make the FIRST grab the failing one.
        const string spec = "Ctrl+Shift+Alt+F19";

        using var first = new X11HotkeyService([(spec, () => { })]);
        Assert.Empty(first.FailedHotkeys);

        // Reaching this line at all is most of the point: pre-fix, constructing the
        // second service exits(1) and the run dies here.
        using var second = new X11HotkeyService([(spec, () => { })]);

        Assert.Contains(spec, second.FailedHotkeys);
    }
}
