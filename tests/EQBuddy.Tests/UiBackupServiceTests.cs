using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// The automatic half: capture once the files have stopped changing.
///
/// EverQuest writes these files when the client exits or camps, and a copy taken mid-write is
/// worse than none - it looks like a backup and restores garbage. Rather than watching for
/// filesystem events and debouncing them, the service asks a simpler question on a tick the
/// app already has: has everything been quiet for long enough?
/// </summary>
public class UiBackupServiceTests : IDisposable
{
    private readonly string _eq = Directory.CreateTempSubdirectory("eqbuddy-svc-eq-").FullName;
    private readonly string _store = Directory.CreateTempSubdirectory("eqbuddy-svc-store-").FullName;
    private readonly DateTime _t0 = new(2026, 8, 10, 21, 0, 0, DateTimeKind.Utc);

    public UiBackupServiceTests() => Touch("UI_Daggo_freeport_LO1.ini", "[Window]\n", 0);

    private void Touch(string name, string body, int secondsAgo)
    {
        var path = Path.Combine(_eq, name);
        File.WriteAllText(path, body);
        File.SetLastWriteTimeUtc(path, _t0.AddSeconds(-secondsAgo));
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _eq, _store })
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private UiBackupService NewService() =>
        new(new UiBackupStore(_store), () => _eq, quietPeriod: TimeSpan.FromSeconds(5));

    [Fact]
    public void AFileStillBeingWrittenIsNotCaptured()
    {
        Touch("UI_Daggo_freeport_LO1.ini", "half wri", secondsAgo: 1);

        var taken = NewService().Tick(_t0);

        Assert.Null(taken);
    }

    [Fact]
    public void AFileThatHasSettledIsCaptured()
    {
        Touch("UI_Daggo_freeport_LO1.ini", "[Window]\nChatWindow=1\n", secondsAgo: 30);

        var taken = NewService().Tick(_t0);

        Assert.NotNull(taken);
    }

    /// <summary>A quiet install must not produce a snapshot per tick.</summary>
    [Fact]
    public void TickingRepeatedlyOverUnchangedFilesCapturesOnce()
    {
        Touch("UI_Daggo_freeport_LO1.ini", "[Window]\nChatWindow=1\n", secondsAgo: 30);
        var service = NewService();

        service.Tick(_t0);
        service.Tick(_t0.AddMinutes(1));
        service.Tick(_t0.AddMinutes(2));

        Assert.Single(new UiBackupStore(_store).List());
    }

    [Fact]
    public void ANewSaveAfterAQuietPeriodIsCaptured()
    {
        Touch("UI_Daggo_freeport_LO1.ini", "[Window]\nChatWindow=1\n", secondsAgo: 30);
        var service = NewService();
        service.Tick(_t0);

        File.WriteAllText(Path.Combine(_eq, "UI_Daggo_freeport_LO1.ini"), "[Window]\nChatWindow=2\n");
        File.SetLastWriteTimeUtc(Path.Combine(_eq, "UI_Daggo_freeport_LO1.ini"), _t0.AddMinutes(1));

        var taken = service.Tick(_t0.AddMinutes(2));

        Assert.NotNull(taken);
        Assert.Equal(2, new UiBackupStore(_store).List().Count);
    }

    [Fact]
    public void AMissingInstallIsNotAnError()
    {
        var service = new UiBackupService(new UiBackupStore(_store), () => null, TimeSpan.FromSeconds(5));

        Assert.Null(service.Tick(_t0));
    }
}
