using System.Diagnostics;
using System.IO;

namespace EQBuddy.Core;

/// <summary>
/// Manages the game's own configuration and log hygiene:
/// - keeps Log=1 in eqclient.ini so /log is always on,
/// - truncates stale log files so they never grow across sessions.
/// Both only act while the game is NOT running (the game rewrites its ini on exit,
/// and truncating a file the game holds open is unsafe).
/// </summary>
public static class EqConfig
{
    public static bool IsGameRunning() => Process.GetProcessesByName("eqgame").Length > 0;

    /// <summary>
    /// Other tools that tail the same eqlog files (COMPAT-001). GINA keeps a byte
    /// offset into the log; emptying the file under it leaves that offset past
    /// end-of-file and GINA silently stops firing triggers until restarted — reported
    /// on Reddit as "this breaks GINA" (2026-08-02). Reading never conflicts; only our
    /// truncation does, so it stands down while any of these is running, same as it
    /// does for the game itself. GamParse gets the same courtesy for the same reason.
    /// </summary>
    private static readonly string[] KnownLogReaders = ["GINA", "GamParse"];

    /// <summary>A log-tailing tool (GINA, GamParse) is running right now.</summary>
    public static bool IsLogReaderRunning() =>
        KnownLogReaders.Any(name => Process.GetProcessesByName(name).Length > 0);

    /// <summary>eqclient.ini lives in the install root, one level above Logs.</summary>
    public static string? FindClientIni(string logFolder)
    {
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(logFolder));
        if (root is null) return null;
        var ini = Path.Combine(root, "eqclient.ini");
        return File.Exists(ini) ? ini : null;
    }

    /// <summary>
    /// Set Log=1 in the [Defaults] section only (REL-002: section-aware — a "Log=" key in
    /// any other section is left alone, as is every other line). Returns true if changed.
    /// </summary>
    public static bool EnsureLoggingEnabled(string logFolder, bool ignoreGameCheck = false)
    {
        try
        {
            if (!ignoreGameCheck && IsGameRunning()) return false;
            var ini = FindClientIni(logFolder);
            if (ini is null) return false;

            var lines = File.ReadAllLines(ini).ToList();
            int defaultsHeader = -1;
            int logLine = -1;
            var inDefaults = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith('[') && t.EndsWith(']'))
                {
                    inDefaults = t.Equals("[Defaults]", StringComparison.OrdinalIgnoreCase);
                    if (inDefaults) defaultsHeader = i;
                    continue;
                }
                if (inDefaults && t.StartsWith("Log=", StringComparison.OrdinalIgnoreCase))
                {
                    logLine = i;
                    break;
                }
            }

            if (logLine >= 0)
            {
                if (lines[logLine].Trim().Equals("Log=1", StringComparison.OrdinalIgnoreCase))
                    return false;
                lines[logLine] = "Log=1";
            }
            else if (defaultsHeader >= 0)
            {
                lines.Insert(defaultsHeader + 1, "Log=1");
            }
            else
            {
                lines.Insert(0, "[Defaults]");
                lines.Insert(1, "Log=1");
            }
            File.WriteAllLines(ini, lines);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Error(ex);
            return false;
        }
    }

    /// <summary>
    /// Empty every character log whose last activity is older than <paramref name="staleAfter"/>.
    /// A stale log is a finished play session; wiping it keeps files session-sized forever.
    /// With <paramref name="archive"/>, the content is first copied to
    /// Logs\archive\eqlog_name_server_yyyyMMddHHmmss.txt (stamp = the log's last write,
    /// i.e. when the session actually ended) — joeymavity's ask, discussion #52. The
    /// archive SUBFOLDER is load-bearing: a timestamped copy in the log folder itself
    /// would match this very method's glob next sweep and get truncated too.
    /// Returns the number of files truncated.
    /// </summary>
    public static int TruncateStaleLogs(string logFolder, TimeSpan staleAfter,
        bool ignoreGameCheck = false, bool archive = false)
    {
        if (!ignoreGameCheck && (IsGameRunning() || IsLogReaderRunning())) return 0;
        if (!Directory.Exists(logFolder)) return 0;
        int truncated = 0;
        foreach (var f in Directory.EnumerateFiles(logFolder, "eqlog_*.txt"))
        {
            try
            {
                var fi = new FileInfo(f);
                if (fi.Length > 0 && DateTime.Now - fi.LastWriteTime > staleAfter)
                {
                    if (archive) ArchiveCopy(fi);
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    fs.SetLength(0);
                    truncated++;
                }
            }
            catch { /* file busy — skip, try next sweep */ }
        }
        return truncated;
    }

    private static void ArchiveCopy(FileInfo log)
    {
        var dir = Path.Combine(log.DirectoryName!, "archive");
        Directory.CreateDirectory(dir);
        var stamp = log.LastWriteTime.ToString("yyyyMMddHHmmss");
        var dest = Path.Combine(dir,
            $"{Path.GetFileNameWithoutExtension(log.Name)}_{stamp}.txt");
        // Same second twice (clock oddities, manual copies): keep both rather than clobber.
        for (var n = 2; File.Exists(dest); n++)
            dest = Path.Combine(dir,
                $"{Path.GetFileNameWithoutExtension(log.Name)}_{stamp}-{n}.txt");
        log.CopyTo(dest);
    }
}
