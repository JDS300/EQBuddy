using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
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

    [AvaloniaFact]
    public void HistoryRendersAndExpandsCompleteEncounterDetail()
    {
        var repository = new SessionRepository(Path.Combine(_profile, "encounters.db"));
        var window = new HistoryWindow(repository);
        window.Show();
        var fight = new EncounterInfo("Orc pawn", new DateTime(2026, 8, 3, 15, 0, 0),
            10, 100, 24, 10, "Killed", 0)
        {
            ByAbility = [new SourceDamage("Slash", 70, 2, 0), new SourceDamage("Pet: Bite", 30, 1, 0)],
            ByIncoming = [new SourceDamage("Orc pawn: Slash", 24, 2, 0)],
            HealsBySpell = [new SourceDamage("Light Healing", 10, 1, 0)],
        };
        var pull = EncounterGrouping.Group([fight]).Single();

        window.RenderFights([pull]);

        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Encounters (1)");
        var header = window.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content?.ToString()?.Contains("Orc pawn") == true);
        Assert.StartsWith("▸ ", header.Content?.ToString());
        var detail = new StackPanel();
        HistoryWindow.BuildFightDetail(detail, pull);
        var text = detail.GetLogicalDescendants().OfType<TextBlock>()
            .Select(block => block.Text).ToList();
        Assert.Contains("Your damage", text);
        Assert.Contains("Pet: Bite", text);
        Assert.Contains("Damage you took", text);
        Assert.Contains("Orc pawn: Slash", text);
        Assert.Contains("Heals during the fight", text);

        window.Close();
        Dispatcher.UIThread.RunJobs();
        repository.Dispose();
    }
}
