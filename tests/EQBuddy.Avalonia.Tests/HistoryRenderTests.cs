using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EQBuddy.Core;
using Polyline = Avalonia.Controls.Shapes.Polyline;

namespace EQBuddy.Avalonia.Tests;

[Collection("avalonia")]
public sealed class HistoryRenderTests : IDisposable
{
    private readonly string _profile = Directory.CreateTempSubdirectory("eqbuddy-history-render-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_profile, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public void HistoryDrawsTheSharedDpsTimeline()
    {
        var repository = new SessionRepository(Path.Combine(_profile, "history.db"));
        var window = new HistoryWindow(repository);
        window.Show();
        window.RenderDpsGraph(
        [
            new TimelinePoint(new DateTime(2026, 8, 3, 15, 0, 0), 600),
            new TimelinePoint(new DateTime(2026, 8, 3, 15, 2, 0), 300),
        ]);

        var label = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text?.StartsWith("DPS over time — peak 10") == true);
        Assert.True(label.IsVisible);
        Assert.Single(window.GetVisualDescendants().OfType<Polyline>());

        window.Close();
        Dispatcher.UIThread.RunJobs();
        repository.Dispose();
    }
}
