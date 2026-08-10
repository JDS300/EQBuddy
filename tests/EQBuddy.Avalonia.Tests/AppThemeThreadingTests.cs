using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EQBuddy.Avalonia;
using EQBuddy.Core;

namespace EQBuddy.Avalonia.Tests;

/// <summary>
/// Applying the theme from somewhere other than the UI thread.
///
/// App.OnFrameworkInitializationCompleted applies the saved theme inside a try/catch, so when
/// this throws nothing crashes - the app just runs with the previous palette and buries a
/// stack trace in error.log. That is the worst shape for a bug: invisible unless the user
/// picked a non-default theme, and indistinguishable from a real fault when it does surface.
/// Brushes are AvaloniaObjects with thread affinity, so the guard belongs in Apply.
/// </summary>
[Collection("avalonia")]
public class AppThemeThreadingTests
{
    [AvaloniaFact]
    public void TheThemeAppliesFromABackgroundThreadInsteadOfThrowing()
    {
        var settings = new AppSettings { Theme = "ParchmentBrass" };
        Exception? thrown = null;
        var done = false;

        var worker = new Thread(() =>
        {
            try { AppTheme.Apply(settings); }
            catch (Exception ex) { thrown = ex; }
            finally { Volatile.Write(ref done, true); }
        });
        worker.Start();

        // Pump the dispatcher rather than blocking on Join: a UI-thread guard marshals work
        // back here, and a blocking wait would deadlock the very thing under test.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!Volatile.Read(ref done) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.True(Volatile.Read(ref done), "AppTheme.Apply never completed off the UI thread");
        Assert.Null(thrown);
    }
}
