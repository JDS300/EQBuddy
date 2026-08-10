using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace EQBuddy.Avalonia;

public sealed class App : Application
{
    // Resolved per call, not cached in a static field. As a static readonly it was
    // frozen at type-initialization time, which in the test host happens at app boot -
    // before any test sets EQBUDDY_APPDATA - so every isolated profile the tests set up
    // afterwards was ignored and the app logged into the developer's real profile.
    private static string ErrorLog => Core.AppPaths.File("error.log");

    public static void LogError(object? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
            File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        EQBuddy.Core.CoreLog.Sink = LogError;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };

        // Applied before MainWindow is constructed so the saved theme is already live
        // for the very first frame (mirrors the WPF app's App.xaml.cs).
        try { AppTheme.Apply(Core.AppSettings.Load()); }
        catch (Exception ex) { LogError(ex); }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
