namespace EQBuddy.UI.Shared;

/// <summary>Ctrl+wheel zoom stepping, pure for tests: 10% per notch, clamped to a range
/// wide enough for 4K-with-Lossless-Scaling setups (garr71, discussion #59) without
/// letting a runaway scroll shrink a window into an unfindable dot.</summary>
public static class WindowZoomMath
{
    public const double Min = 0.6, Max = 2.5;

    public static double Step(double current, int wheelDirection) =>
        Math.Clamp(Math.Round(current + 0.1 * Math.Sign(wheelDirection), 2), Min, Max);
}
