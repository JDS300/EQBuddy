using System.Text;

namespace EQBuddy.Core;

/// <summary>
/// Renders the session's per-creature drop observations for export (discussion #55,
/// LeBigNasty: tracking Plane of Sky and updating eqlwiki for revamped zones). Two
/// shapes: a human-readable text block that pastes cleanly into a wiki talk page or
/// Discord, and CSV for spreadsheets. Drop rates are observed personal rates, so the
/// kill denominator always rides along — a 100% rate off two kills should look exactly
/// as thin as it is.
/// </summary>
public static class DropsReport
{
    public static string ToText(IEnumerable<MobSummary> mobs, string character, string server,
        DateTime? sessionStart)
    {
        var sb = new StringBuilder();
        var who = character.Length > 0 ? $"{character} ({server})" : "unknown character";
        sb.AppendLine($"EQBuddy drops export — {who}" +
                      (sessionStart is { } t ? $" — session started {t:yyyy-MM-dd HH:mm}" : ""));
        var any = false;
        foreach (var mob in mobs.Where(m => m.Loot.Count > 0))
        {
            any = true;
            sb.AppendLine();
            sb.AppendLine($"{mob.Name} — {mob.Kills} kill{(mob.Kills == 1 ? "" : "s")}");
            foreach (var l in mob.Loot)
                sb.AppendLine($"  {l.Item} x{l.Count}" +
                              (l.DropRatePct is { } pct ? $" ({pct:0.#}% of {mob.Kills} kills)" : ""));
        }
        if (!any) sb.AppendLine().AppendLine("(no drops recorded this session)");
        return sb.ToString();
    }

    public static string ToCsv(IEnumerable<MobSummary> mobs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("creature,kills,item,count,observed_drop_pct");
        foreach (var mob in mobs.Where(m => m.Loot.Count > 0))
            foreach (var l in mob.Loot)
                sb.AppendLine($"{Csv(mob.Name)},{mob.Kills},{Csv(l.Item)},{l.Count}," +
                              (l.DropRatePct is { } pct ? pct.ToString("0.##") : ""));
        return sb.ToString();
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
