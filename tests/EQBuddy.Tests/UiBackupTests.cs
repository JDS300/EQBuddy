using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// Backing up the character UI files EverQuest writes on exit.
///
/// The motivating case, measured on a real install: the only copy present was hand-made and
/// a month stale - 34,983 bytes against a live 58,950 - so restoring it would have thrown
/// away a month of layout work. These tests use temp directories shaped like an EQ install.
/// </summary>
public class UiBackupTests : IDisposable
{
    private readonly string _eq = Directory.CreateTempSubdirectory("eqbuddy-eqroot-").FullName;
    private readonly string _store = Directory.CreateTempSubdirectory("eqbuddy-backups-").FullName;

    public UiBackupTests()
    {
        Directory.CreateDirectory(Path.Combine(_eq, "Logs"));
        Write("UI_Daggo_freeport_LO1.ini", "[Window]\nChatWindow=1\n");
        Write("Daggo_freeport_LO1.ini", "[Socials]\nrow=1\n");
        Write("eqclient.ini", "[Defaults]\nGamma=1\n");
        Write("_characters.ini", "Daggo\n");
        Write("defaults.ini", "stock content, not the user's");
        Write("UI_Daggo_freeport_LO1_Backup_1.ini", "a hand-made copy");
    }

    private void Write(string name, string body) =>
        File.WriteAllText(Path.Combine(_eq, name), body);

    public void Dispose()
    {
        foreach (var dir in new[] { _eq, _store })
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static bool ClientIdle() => false;

    [Fact]
    public void TheEqRootIsTheParentOfTheLogFolder()
    {
        var root = UiBackupSet.RootFromLogFolder(Path.Combine(_eq, "Logs"));

        Assert.Equal(_eq, root);
    }

    [Fact]
    public void DiscoveryTakesTheUsersFilesAndLeavesStockAndHandMadeCopies()
    {
        var found = UiBackupSet.Discover(_eq).Select(Path.GetFileName).ToList();

        Assert.Contains("UI_Daggo_freeport_LO1.ini", found);
        Assert.Contains("Daggo_freeport_LO1.ini", found);
        Assert.Contains("eqclient.ini", found);
        Assert.Contains("_characters.ini", found);
        Assert.DoesNotContain("defaults.ini", found);
        Assert.DoesNotContain("UI_Daggo_freeport_LO1_Backup_1.ini", found);
    }

    /// <summary>Raw files, not an archive: recovery has to be a copy even if EQBuddy is
    /// gone.</summary>
    [Fact]
    public void ASnapshotStoresTheFilesVerbatimBesideAManifest()
    {
        var store = new UiBackupStore(_store);

        var snap = store.Capture(_eq, new DateTime(2026, 8, 10, 21, 52, 0, DateTimeKind.Utc));

        Assert.NotNull(snap);
        var dir = Path.Combine(_store, snap!.Id);
        Assert.Equal("[Window]\nChatWindow=1\n",
            File.ReadAllText(Path.Combine(dir, "UI_Daggo_freeport_LO1.ini")));
        Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));
        Assert.Equal(4, snap.Files.Count);
    }

    [Fact]
    public void AnUnchangedSecondCaptureIsNotStoredTwice()
    {
        var store = new UiBackupStore(_store);
        store.Capture(_eq, new DateTime(2026, 8, 10, 21, 0, 0, DateTimeKind.Utc));

        var again = store.Capture(_eq, new DateTime(2026, 8, 10, 22, 0, 0, DateTimeKind.Utc));

        Assert.Null(again);
        Assert.Single(store.List());
    }

    [Fact]
    public void AChangedFileProducesANewSnapshot()
    {
        var store = new UiBackupStore(_store);
        store.Capture(_eq, new DateTime(2026, 8, 10, 21, 0, 0, DateTimeKind.Utc));

        Write("UI_Daggo_freeport_LO1.ini", "[Window]\nChatWindow=2\n");
        store.Capture(_eq, new DateTime(2026, 8, 10, 22, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void RestorePutsTheFilesBack()
    {
        var store = new UiBackupStore(_store);
        var snap = store.Capture(_eq, new DateTime(2026, 8, 10, 21, 0, 0, DateTimeKind.Utc))!;
        Write("UI_Daggo_freeport_LO1.ini", "everything moved to the wrong corner");

        store.Restore(snap, _eq, ClientIdle, new DateTime(2026, 8, 10, 23, 0, 0, DateTimeKind.Utc));

        Assert.Equal("[Window]\nChatWindow=1\n",
            File.ReadAllText(Path.Combine(_eq, "UI_Daggo_freeport_LO1.ini")));
    }

    /// <summary>Restore must itself be undoable: the thing being overwritten is the only copy
    /// of whatever the user had a second ago.</summary>
    [Fact]
    public void RestoreCapturesTheCurrentStateFirst()
    {
        var store = new UiBackupStore(_store);
        var snap = store.Capture(_eq, new DateTime(2026, 8, 10, 21, 0, 0, DateTimeKind.Utc))!;
        Write("UI_Daggo_freeport_LO1.ini", "the state being replaced");

        store.Restore(snap, _eq, ClientIdle, new DateTime(2026, 8, 10, 23, 0, 0, DateTimeKind.Utc));

        var safety = store.List().Single(s => s.Id != snap.Id);
        Assert.Equal("the state being replaced",
            File.ReadAllText(Path.Combine(_store, safety.Id, "UI_Daggo_freeport_LO1.ini")));
    }

    /// <summary>EQ holds the UI in memory and rewrites these files on exit, so a restore
    /// under a live client is silently undone and the tool looks broken.</summary>
    [Fact]
    public void RestoreIsRefusedWhileTheClientIsRunning()
    {
        var store = new UiBackupStore(_store);
        var snap = store.Capture(_eq, new DateTime(2026, 8, 10, 21, 0, 0, DateTimeKind.Utc))!;
        Write("UI_Daggo_freeport_LO1.ini", "live client state");

        Assert.Throws<InvalidOperationException>(() =>
            store.Restore(snap, _eq, () => true, new DateTime(2026, 8, 10, 23, 0, 0, DateTimeKind.Utc)));

        Assert.Equal("live client state",
            File.ReadAllText(Path.Combine(_eq, "UI_Daggo_freeport_LO1.ini")));
    }

    [Fact]
    public void PruningKeepsTheNewestSnapshots()
    {
        var store = new UiBackupStore(_store);
        for (var i = 0; i < 5; i++)
        {
            Write("UI_Daggo_freeport_LO1.ini", $"[Window]\nChatWindow={i}\n");
            store.Capture(_eq, new DateTime(2026, 8, 10, 18 + i, 0, 0, DateTimeKind.Utc));
        }

        store.Prune(keep: 2);

        var left = store.List();
        Assert.Equal(2, left.Count);
        Assert.All(left, s => Assert.True(s.TakenUtc.Hour >= 21, $"kept an older snapshot from {s.TakenUtc:HH:mm}"));
    }
}
