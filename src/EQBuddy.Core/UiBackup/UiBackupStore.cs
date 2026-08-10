using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQBuddy.Core;

public sealed record UiBackupFile(string Name, long Size, string Sha256);

public sealed record UiBackupSnapshot(
    string Id,
    DateTime TakenUtc,
    IReadOnlyList<UiBackupFile> Files)
{
    [JsonIgnore]
    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>Best-effort character name from the UI file, for a list the user can read at
    /// a glance rather than a column of timestamps.</summary>
    [JsonIgnore]
    public string? Character => Files
        .Select(f => f.Name)
        .Where(n => n.StartsWith("UI_", StringComparison.OrdinalIgnoreCase))
        .Select(n => Path.GetFileNameWithoutExtension(n)["UI_".Length..].Split('_').FirstOrDefault())
        .FirstOrDefault(n => !string.IsNullOrEmpty(n));
}

/// <summary>
/// Snapshots of the EverQuest UI files, stored as plain copies.
///
/// Deliberately not an archive format: on the day this matters, EQBuddy may be the thing that
/// is broken. Every snapshot is a directory of ordinary files that can be restored with a
/// file manager and no software at all. The manifest is a convenience, never a requirement.
/// </summary>
public sealed class UiBackupStore(string root)
{
    private const string ManifestName = "manifest.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = root;

    public IReadOnlyList<UiBackupSnapshot> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateDirectories(Root)
            .Select(Read)
            .Where(s => s is not null)
            .Select(s => s!)
            .OrderByDescending(s => s.TakenUtc)
            .ToList();
    }

    private static UiBackupSnapshot? Read(string dir)
    {
        try
        {
            var manifest = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifest)) return null;
            var parsed = JsonSerializer.Deserialize<UiBackupSnapshot>(File.ReadAllText(manifest));
            return parsed is null ? null : parsed with { Id = Path.GetFileName(dir) };
        }
        catch { return null; }   // a half-written snapshot must not break the list
    }

    /// <summary>Copies the current UI files into a new snapshot, or returns null when they
    /// are byte-for-byte what the newest snapshot already holds - otherwise every launch
    /// would bury the one interesting state under identical copies.</summary>
    public UiBackupSnapshot? Capture(string eqRoot, DateTime nowUtc)
    {
        var sources = UiBackupSet.Discover(eqRoot);
        if (sources.Count == 0) return null;

        var files = sources.Select(Describe).ToList();
        if (List().FirstOrDefault() is { } newest && SameContent(newest.Files, files)) return null;

        var id = nowUtc.ToString("yyyy-MM-dd_HH-mm-ss");
        var dir = Path.Combine(Root, id);
        var unique = 1;
        while (Directory.Exists(dir)) dir = Path.Combine(Root, $"{id}_{++unique}");
        Directory.CreateDirectory(dir);

        foreach (var source in sources)
            File.Copy(source, Path.Combine(dir, Path.GetFileName(source)), overwrite: true);

        var snapshot = new UiBackupSnapshot(Path.GetFileName(dir), nowUtc, files);
        File.WriteAllText(Path.Combine(dir, ManifestName), JsonSerializer.Serialize(snapshot, Json));
        return snapshot;
    }

    /// <summary>Puts a snapshot back over the live files.
    ///
    /// Refuses while the client is running: EverQuest holds the UI in memory and rewrites
    /// these files when it exits, so a restore under a live client is silently undone and the
    /// user concludes the feature does not work. The current state is captured first, because
    /// the files being overwritten are the only copy of whatever they had a moment ago.</summary>
    public void Restore(UiBackupSnapshot snapshot, string eqRoot, Func<bool> clientIsLive, DateTime nowUtc)
    {
        if (clientIsLive())
            throw new InvalidOperationException(
                "Close EverQuest before restoring: the client rewrites these files when it exits, "
                + "which would undo the restore.");

        Capture(eqRoot, nowUtc);

        var dir = Path.Combine(Root, snapshot.Id);
        foreach (var file in snapshot.Files)
        {
            var source = Path.Combine(dir, file.Name);
            if (File.Exists(source)) File.Copy(source, Path.Combine(eqRoot, file.Name), overwrite: true);
        }
    }

    public int Prune(int keep)
    {
        var removed = 0;
        foreach (var old in List().Skip(Math.Max(0, keep)))
        {
            try { Directory.Delete(Path.Combine(Root, old.Id), recursive: true); removed++; }
            catch { /* a snapshot we cannot delete is not worth failing a backup over */ }
        }
        return removed;
    }

    private static UiBackupFile Describe(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new UiBackupFile(Path.GetFileName(path), new FileInfo(path).Length, hash);
    }

    private static bool SameContent(IReadOnlyList<UiBackupFile> a, IReadOnlyList<UiBackupFile> b) =>
        a.Count == b.Count
        && a.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Zip(b.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            .All(pair => pair.First.Name.Equals(pair.Second.Name, StringComparison.OrdinalIgnoreCase)
                      && pair.First.Sha256 == pair.Second.Sha256);
}
