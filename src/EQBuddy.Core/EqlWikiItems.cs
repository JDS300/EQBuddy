using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQBuddy.Core;

/// <summary>Parsed eqlwiki item page, ready for display.</summary>
public sealed class ItemInfo
{
    public string Name { get; set; } = "";
    /// <summary>The in-game item window text, line by line (MAGIC ITEM, Slot, DMG…).</summary>
    public List<string> StatsLines { get; set; } = [];
    /// <summary>Vendor value as compact coin text ("5g 8s 2c"), or "" when unlisted.</summary>
    public string MerchantValue { get; set; } = "";
    public List<(string Zone, List<string> Mobs)> DropsFrom { get; set; } = [];
    public List<(string Zone, string Merchant, string Loc)> SoldBy { get; set; } = [];
    public List<string> Quests { get; set; } = [];
    public List<string> Recipes { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public string WikiUrl { get; set; } = "";
}

public enum ItemLookupState { Live, Cached, StaleCache, Offline, NotFound }

public sealed record ItemLookupResult(ItemInfo? Item, ItemLookupState State, DateTime? FetchedAt);

/// <summary>
/// Explicit, on-demand item lookups against eqlwiki.com — the Lore-Lens idea without the
/// OCR: the LOG names every looted item exactly, so a click is all the input needed.
/// One bounded fetch per request, a 7-day disk cache, and honest LIVE/CACHED/STALE
/// labels; nothing is scraped in the background (the same etiquette the spawn-catalog
/// sync uses). Page structure per the 2026-08-04 survey: {{Itempage}} template inside
/// &lt;onlyinclude&gt;, categories outside, api.php with redirects=1 resolves case and
/// name variants; in-game "+N" suffixes have no wiki pages (strip before lookup).
/// </summary>
public sealed partial class EqlWikiItemService
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private readonly string _cacheDir;
    private readonly Func<string, Task<string?>> _fetch;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    /// <param name="fetchOverride">Tests inject a fake fetcher; null = real api.php.
    /// The fetcher returns the page's raw wikitext, or null when the page is missing.</param>
    public EqlWikiItemService(string cacheDir, Func<string, Task<string?>>? fetchOverride = null)
    {
        _cacheDir = cacheDir;
        _fetch = fetchOverride ?? FetchFromApi;
    }

    /// <summary>Strips the in-game upgrade suffix ("Rusty Broad Sword +4" → base name) —
    /// the wiki has no "+N" pages (verified: 404s, no redirects).</summary>
    public static string NormalizeTitle(string inGameName) =>
        Regex.Replace(inGameName.Trim(), @"\s+\+\d+$", "");

    public async Task<ItemLookupResult> LookupAsync(string inGameName)
    {
        var title = NormalizeTitle(inGameName);
        if (title.Length == 0) return new ItemLookupResult(null, ItemLookupState.NotFound, null);

        var cached = ReadCache(title);
        if (cached is { } fresh && DateTime.UtcNow - fresh.FetchedAt < CacheLifetime)
            return new ItemLookupResult(Parse(fresh.Wikitext, fresh.Title), ItemLookupState.Cached, fresh.FetchedAt);

        // Exact title first; the backtick-stripped variant is a fallback only (the wiki
        // KEEPS backticks on at least some pages, contrary to its mob-page habits).
        foreach (var candidate in Candidates(title))
        {
            string? wikitext;
            try { wikitext = await _fetch(candidate).ConfigureAwait(false); }
            catch
            {
                // Network trouble: any cache, however old, beats nothing.
                return cached is { } stale
                    ? new ItemLookupResult(Parse(stale.Wikitext, stale.Title), ItemLookupState.StaleCache, stale.FetchedAt)
                    : new ItemLookupResult(null, ItemLookupState.Offline, null);
            }
            if (wikitext is null) continue;
            WriteCache(title, candidate, wikitext);
            return new ItemLookupResult(Parse(wikitext, candidate), ItemLookupState.Live, DateTime.UtcNow);
        }
        return new ItemLookupResult(null, ItemLookupState.NotFound, null);
    }

    private static IEnumerable<string> Candidates(string title)
    {
        yield return title;
        var noBacktick = title.Replace("`", "");
        if (noBacktick != title) yield return noBacktick;
    }

    /// <summary>Cache-only peek: synchronous, accepts any age, never fetches — for
    /// surfaces that must cost nothing (row values, first-paint tooltips).</summary>
    public ItemInfo? CachedInfo(string inGameName)
    {
        var cached = ReadCache(NormalizeTitle(inGameName));
        return cached is null ? null : Parse(cached.Wikitext, cached.Title);
    }

    /// <summary>Cache-only stats peek for hover tooltips: synchronous, accepts any age,
    /// never fetches. A hover must cost nothing — the click path does the real lookup.</summary>
    public string? CachedStatsText(string inGameName)
    {
        var info = CachedInfo(inGameName);
        return info is { StatsLines.Count: > 0 } ? string.Join("\n", info.StatsLines) : null;
    }

    private async Task<string?> FetchFromApi(string title)
    {
        var url = "https://eqlwiki.com/api.php?action=query&prop=revisions&rvprop=content&format=json&redirects=1&titles="
            + Uri.EscapeDataString(title);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
        var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
        foreach (var page in pages.EnumerateObject())
        {
            if (page.Value.TryGetProperty("missing", out _)) return null;
            if (page.Value.TryGetProperty("revisions", out var revs) && revs.GetArrayLength() > 0
                && revs[0].TryGetProperty("*", out var content))
                return content.GetString();
        }
        return null;
    }

    // ---- cache ----

    private sealed record CacheEntry(string Title, string Wikitext, DateTime FetchedAt);

    private string CachePath(string title) =>
        Path.Combine(_cacheDir, string.Concat(title.Select(c =>
            char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')) + ".json");

    private CacheEntry? ReadCache(string title)
    {
        try
        {
            var path = CachePath(title);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private void WriteCache(string title, string resolvedTitle, string wikitext)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(CachePath(title),
                JsonSerializer.Serialize(new CacheEntry(resolvedTitle, wikitext, DateTime.UtcNow)));
        }
        catch { /* cache is a convenience; lookups still work without it */ }
    }

    // ---- wikitext parsing ({{Itempage}} template, field grammar per the survey) ----

    [GeneratedRegex(@"\[\[:?(?:[^\]|]*\|)?([^\]]*)\]\]")]
    private static partial Regex WikiLinkRx();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRx();

    [GeneratedRegex(@"(\d+)\s+(Platinum|Gold|Silver|Copper)s?", RegexOptions.IgnoreCase)]
    private static partial Regex CoinWordRx();

    [GeneratedRegex(@"\{\{ItemWhereRow(.*?)\}\}", RegexOptions.Singleline)]
    private static partial Regex WhereRowRx();

    [GeneratedRegex(@"\[\[Category:([^\]]+)\]\]")]
    private static partial Regex CategoryRx();

    private static string StripLinks(string s) => WikiLinkRx().Replace(s, "$1").Trim();

    public static ItemInfo Parse(string wikitext, string title)
    {
        var info = new ItemInfo
        {
            Name = title,
            WikiUrl = "https://eqlwiki.com/" + Uri.EscapeDataString(title.Replace(' ', '_')),
        };
        foreach (Match c in CategoryRx().Matches(wikitext)) info.Categories.Add(c.Groups[1].Value.Trim());

        var fields = SplitTemplateFields(wikitext);
        if (fields.TryGetValue("itemname", out var name) && name.Trim().Length > 0)
            info.Name = name.Trim();

        if (fields.TryGetValue("statsblock", out var stats))
            info.StatsLines = Regex.Split(stats, @"<br\s*/?>")
                .Select(l => StripLinks(HtmlTagRx().Replace(l, "")).Trim())
                .Where(l => l.Length > 0)
                .ToList();

        if (fields.TryGetValue("merchant_value", out var value))
            info.MerchantValue = ParseMerchantValue(value);

        if (fields.TryGetValue("dropsfrom", out var drops))
            info.DropsFrom = ParseDropsFrom(drops);

        if (fields.TryGetValue("soldby", out var sold))
            foreach (Match m in WhereRowRx().Matches(sold))
            {
                // Positional row: | zone | merchant | area | (x, y) — but zone/merchant
                // are often piped links ("[[Freeport|East Freeport]]"), so the split has
                // to ignore pipes inside [[…]].
                var parts = SplitTopLevelPipes(m.Groups[1].Value);
                if (parts.Count >= 4)
                    info.SoldBy.Add((StripLinks(parts[0]), StripLinks(parts[1]), parts[3].Trim()));
            }

        if (fields.TryGetValue("relatedquests", out var quests))
            info.Quests = BulletLines(quests);
        if (fields.TryGetValue("recipes", out var recipes))
            info.Recipes = BulletLines(recipes);
        return info;
    }

    private static Dictionary<string, string> SplitTemplateFields(string wikitext) =>
        EqlWikiText.TemplateFields(wikitext, "Itempage");

    /// <summary>Two shapes in the wild: plain "5p 9g 2s 8c", or an HTML list of per-coin
    /// lines ("0 Platinums / 5 Silvers / 8 Coppers" with markup). Both normalize to
    /// compact coin text; zero denominations drop out.</summary>
    private static string ParseMerchantValue(string raw)
    {
        if (!raw.Contains('<')) return raw.Trim();
        var text = HtmlTagRx().Replace(raw, " ");
        var parts = new List<string>();
        foreach (Match m in CoinWordRx().Matches(text))
        {
            var n = int.Parse(m.Groups[1].Value);
            if (n > 0) parts.Add(n + m.Groups[2].Value[..1].ToString().ToLowerInvariant());
        }
        return string.Join(" ", parts);
    }

    private static List<(string Zone, List<string> Mobs)> ParseDropsFrom(string raw)
    {
        var result = new List<(string, List<string>)>();
        List<string>? mobs = null;
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = StripLinks(rawLine.Trim());
            if (line.Length == 0) continue;
            if (rawLine.TrimStart().StartsWith('*'))
            {
                mobs ??= NewZone("");
                mobs.Add(line.TrimStart('*', ' '));
            }
            else
            {
                mobs = NewZone(line);
            }
        }
        return result;

        List<string> NewZone(string zone)
        {
            var list = new List<string>();
            result.Add((zone, list));
            return list;
        }
    }

    /// <summary>Splits on '|' except inside [[wiki links]]; the leading empty segment
    /// (rows begin with '|') is dropped.</summary>
    private static List<string> SplitTopLevelPipes(string s)
    {
        var parts = new List<string>();
        var depth = 0; var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (i + 1 < s.Length && s[i] == '[' && s[i + 1] == '[') { depth++; i++; }
            else if (i + 1 < s.Length && s[i] == ']' && s[i + 1] == ']') { depth--; i++; }
            else if (s[i] == '|' && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        if (parts.Count > 0 && parts[0].Trim().Length == 0) parts.RemoveAt(0);
        return parts;
    }

    private static List<string> BulletLines(string raw) =>
        raw.Split('\n')
            .Select(l => StripLinks(l.Trim()).TrimStart('*', ':', ' ').Trim())
            .Where(l => l.Length > 0)
            .ToList();
}
