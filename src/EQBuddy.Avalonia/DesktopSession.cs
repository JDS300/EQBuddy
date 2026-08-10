namespace EQBuddy.Avalonia;

/// <summary>What kind of desktop session EQBuddy is running in.</summary>
internal static class DesktopSession
{
    /// <summary>True on a Wayland session, including when EQBuddy runs as an X11 client
    /// under XWayland.
    ///
    /// That last part is the whole reason this exists. The first attempt at this check asked
    /// "is WAYLAND_DISPLAY set and DISPLAY unset?", which is never true under XWayland — both
    /// are set — so it passed on a KDE Wayland desktop and the global hotkeys registered as
    /// if X11 were in charge. They then never fired, and the one binding the user had made
    /// was reported as taken by another application. DISPLAY tells us nothing about who owns
    /// global input, so it is deliberately not consulted here.</summary>
    public static bool IsWayland() => IsWayland(Environment.GetEnvironmentVariable);

    /// <summary>Testable overload: the environment is passed in rather than read from the
    /// process, so the session table can be exercised without mutating global state.</summary>
    public static bool IsWayland(Func<string, string?> env) =>
        string.Equals(env("XDG_SESSION_TYPE")?.Trim(), "wayland", StringComparison.OrdinalIgnoreCase)
        || env("WAYLAND_DISPLAY") is { Length: > 0 };
}
