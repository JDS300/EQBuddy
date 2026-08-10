using System.Runtime.CompilerServices;

namespace EQBuddy.Avalonia.Tests;

/// <summary>
/// Points the whole test assembly at a throwaway profile before anything else runs.
///
/// The Avalonia test host boots the real App, and App logs during startup. That write
/// happens before any test class constructor can set EQBUDDY_APPDATA, so without this the
/// first thing a test run does is append to the developer's live ~/.config/EQBuddy. It did
/// exactly that on 2026-08-10: four stack traces in the real error.log, which then cost an
/// investigation to tell apart from a genuine app fault.
/// </summary>
internal static class TestProfile
{
    /// <summary>The assembly-wide isolated profile. Test classes that swap in their own
    /// profile must restore THIS, never null - null means "use the real one".</summary>
    internal static string Root { get; private set; } = "";

    [ModuleInitializer]
    internal static void Isolate()
    {
        Root = Directory.CreateTempSubdirectory("eqbuddy-tests-").FullName;
        Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", Root);
    }
}
