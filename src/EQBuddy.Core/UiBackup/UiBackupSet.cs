using System.Text.RegularExpressions;

namespace EQBuddy.Core;

/// <summary>
/// Which files in an EverQuest install actually are "your UI".
///
/// Everything else in that folder is game art shipped by the patcher and replaceable; these
/// four kinds are the only things a player cannot get back. Measured on a real install the
/// whole set is under 100 KB, which is why snapshots can be kept liberally.
/// </summary>
public static partial class UiBackupSet
{
    /// <summary>The install root is the parent of the Logs folder, so a Wine prefix or any
    /// custom install path is found without asking the user for a second path they would
    /// have to keep in sync.</summary>
    public static string? RootFromLogFolder(string? logFolder)
    {
        if (string.IsNullOrWhiteSpace(logFolder)) return null;
        var trimmed = logFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(trimmed), "Logs", StringComparison.OrdinalIgnoreCase))
            return null;
        var root = Path.GetDirectoryName(trimmed);
        return Directory.Exists(root) ? root : null;
    }

    /// <summary>`UI_<char>_<server>.ini` (window layout) and `<char>_<server>.ini` (socials,
    /// hotbars, blocked spells), plus the two client-wide files.</summary>
    [GeneratedRegex(@"^(UI_)?[^_]+_[^_]+.*\.ini$", RegexOptions.IgnoreCase)]
    private static partial Regex PerCharacter();

    public static List<string> Discover(string? eqRoot)
    {
        if (string.IsNullOrWhiteSpace(eqRoot) || !Directory.Exists(eqRoot)) return [];
        return Directory.EnumerateFiles(eqRoot, "*.ini")
            .Where(path => Wanted(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool Wanted(string name)
    {
        // The user's own hand-made copies. Backing up backups buries the real files in a
        // list the user has to read under pressure.
        if (name.Contains("_Backup_", StringComparison.OrdinalIgnoreCase)) return false;
        // Shipped by the patcher, identical on every install, and restoring it over a live
        // eqclient.ini would hand back settings the user never chose.
        if (name.Equals("defaults.ini", StringComparison.OrdinalIgnoreCase)) return false;

        if (name.Equals("eqclient.ini", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("_characters.ini", StringComparison.OrdinalIgnoreCase)) return true;
        return PerCharacter().IsMatch(name);
    }
}
