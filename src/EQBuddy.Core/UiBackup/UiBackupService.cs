namespace EQBuddy.Core;

/// <summary>
/// Captures the UI files once they have stopped changing.
///
/// Driven by a tick the app already runs rather than a FileSystemWatcher: the question worth
/// asking is not "did something change" but "has everything been quiet long enough that the
/// client has finished writing", and the mtimes answer that directly. A copy taken mid-write
/// is worse than no copy - it looks like a backup and restores garbage.
/// </summary>
public sealed class UiBackupService(
    UiBackupStore store,
    Func<string?> eqRoot,
    TimeSpan quietPeriod)
{
    /// <summary>Newest snapshots kept. Four ini files under 100 KB, so 50 costs a few MB and
    /// buys weeks of history.</summary>
    public int Keep { get; init; } = 50;

    public UiBackupStore Store { get; } = store;

    /// <summary>Returns the snapshot taken, or null when there was nothing new to take.</summary>
    public UiBackupSnapshot? Tick(DateTime nowUtc)
    {
        var root = eqRoot();
        if (string.IsNullOrWhiteSpace(root)) return null;

        var files = UiBackupSet.Discover(root);
        if (files.Count == 0) return null;

        var newestWrite = files.Max(f => File.GetLastWriteTimeUtc(f));
        if (nowUtc - newestWrite < quietPeriod) return null;

        var taken = Store.Capture(root, nowUtc);
        if (taken is not null) Store.Prune(Keep);
        return taken;
    }
}
