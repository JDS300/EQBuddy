using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQBuddy.Core;

/// <summary>Parsed eqlwiki mob page: what the creature can drop, per the wiki.</summary>
public sealed class MobInfo
{
    public string Name { get; set; } = "";
    public string Zone { get; set; } = "";
    public string Level { get; set; } = "";
    /// <summary>Wiki-listed drops in page order. Rarity is the wiki's own word ("Common",
    /// "Uncommon"…) or "" when the page doesn't annotate the entry.</summary>
    public List<(string Item, string Rarity)> Drops { get; set; } = [];
    public string WikiUrl { get; set; } = "";
}

public sealed record MobLookupResult(MobInfo? Mob, ItemLookupState State, DateTime? FetchedAt);

/// <summary>
/// On-demand mob lookups against eqlwiki.com for the Loot card's target-drops block
/// (David, 2026-08-06): the log names what you're fighting, the wiki knows what it can
/// drop. Same discipline as <see cref="EqlWikiItemService"/> — one bounded fetch per
/// request, 7-day disk cache, honest LIVE/CACHED/STALE labels, nothing background.
///
/// Page shapes (2026-08-06 survey): named and regular mobs BOTH use {{Namedmobpage}};
/// regular mobs live at article-titled pages ("A Spite Golem"), named at bare names
/// ("Lockjaw"); redirects=1 resolves letter case. known_loot lists drops as {{:Item}}
/// transclusions with an optional rarity span after each.
/// </summary>
public sealed partial class EqlWikiMobService
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private readonly string _cacheDir;
    private readonly Func<string, Task<string?>> _fetch;
    private readonly Func<string, Task<List<string>>> _search;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public EqlWikiMobService(string cacheDir, Func<string, Task<string?>>? fetchOverride = null,
        Func<string, Task<List<string>>>? searchOverride = null)
    {
        _cacheDir = cacheDir;
        _fetch = fetchOverride ?? FetchFromApi;
        _search = searchOverride ?? SearchFromApi;
    }

    public async Task<MobLookupResult> LookupAsync(string creatureName)
    {
        var title = creatureName.Trim();
        if (title.Length == 0) return new MobLookupResult(null, ItemLookupState.NotFound, null);

        var cached = ReadCache(title);
        if (cached is { } fresh && DateTime.UtcNow - fresh.FetchedAt < CacheLifetime)
            return new MobLookupResult(Parse(fresh.Wikitext, fresh.Title), ItemLookupState.Cached, fresh.FetchedAt);

        foreach (var candidate in Candidates(title))
        {
            string? wikitext;
            try { wikitext = await _fetch(candidate).ConfigureAwait(false); }
            catch
            {
                return cached is { } stale
                    ? new MobLookupResult(Parse(stale.Wikitext, stale.Title), ItemLookupState.StaleCache, stale.FetchedAt)
                    : new MobLookupResult(null, ItemLookupState.Offline, null);
            }
            if (wikitext is null) continue;
            WriteCache(title, candidate, wikitext);
            return new MobLookupResult(Parse(wikitext, candidate), ItemLookupState.Live, DateTime.UtcNow);
        }

        // Every exact form missed: ask the wiki's own search, and accept its top hits
        // only under the spawn catalog's fuzzy rule (bounded edit distance on folded
        // names, exact-first doctrine) — typos and punctuation drift resolve, but a
        // vaguely-related page can never masquerade as the creature (David, 2026-08-06).
        try
        {
            foreach (var found in (await _search(title).ConfigureAwait(false)).Take(5))
            {
                if (!SpawnCatalog.NameMatchesFuzzy(found, title)) continue;
                var wikitext = await _fetch(found).ConfigureAwait(false);
                if (wikitext is null) continue;
                WriteCache(title, found, wikitext);
                return new MobLookupResult(Parse(wikitext, found), ItemLookupState.Live, DateTime.UtcNow);
            }
        }
        catch
        {
            return cached is { } stale
                ? new MobLookupResult(Parse(stale.Wikitext, stale.Title), ItemLookupState.StaleCache, stale.FetchedAt)
                : new MobLookupResult(null, ItemLookupState.Offline, null);
        }
        return new MobLookupResult(null, ItemLookupState.NotFound, null);
    }

    private static async Task<List<string>> SearchFromApi(string query)
    {
        var url = "https://eqlwiki.com/api.php?action=query&list=search&srlimit=5&format=json&srsearch="
            + Uri.EscapeDataString(query);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
        var results = new List<string>();
        foreach (var hit in doc.RootElement.GetProperty("query").GetProperty("search").EnumerateArray())
            if (hit.GetProperty("title").GetString() is { Length: > 0 } t)
                results.Add(t);
        return results;
    }

    /// <summary>SessionStats mob names arrive article-stripped and first-letter-capitalized
    /// ("Spite golem"), but MediaWiki titles are case-sensitive past their FIRST letter —
    /// "A Spite golem" misses while both "A spite golem" and "A Spite Golem" exist. So:
    /// bare name (named mobs live there), lowercase article form (the wiki's redirect
    /// habit), title-case forms, then backtick-stripped variants (wikis drop EQ backticks —
    /// the Skeleton L`rodd lesson from the spawn catalog). Deduped, order preserved.</summary>
    private static IEnumerable<string> Candidates(string title)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Raw(title))
            if (t.Length > 0 && seen.Add(t)) yield return t;

        static IEnumerable<string> Raw(string title)
        {
            var lower = title.ToLowerInvariant();
            var titleCase = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower);
            yield return title;
            yield return "A " + lower;
            if ("aeiou".Contains(lower[0])) yield return "An " + lower;
            // Normalize strips EVERY leading article, including "The" — but named mobs
            // often live at "The <Name>" pages ("The Prophet" in Crushbone showed no
            // drops, David 2026-08-06; bare "Prophet" is missing on the wiki).
            yield return "The " + lower;
            yield return titleCase;
            yield return "A " + titleCase;
            yield return "The " + titleCase;
            var noBacktick = title.Replace("`", "");
            if (noBacktick != title)
            {
                yield return noBacktick;
                yield return "A " + noBacktick.ToLowerInvariant();
            }
        }
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

    // ---- cache (same shape as the item service) ----

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
        catch { /* cache is a convenience */ }
    }

    // ---- parsing ----

    // {{:Lockjaw Hide Vest}} <span class='drare'>(Common)</span>
    [GeneratedRegex(@"\{\{:([^}|]+)\}\}\s*(?:<span[^>]*>\(([^)]+)\)</span>)?")]
    private static partial Regex DropRx();

    public static MobInfo Parse(string wikitext, string title)
    {
        var info = new MobInfo
        {
            Name = title,
            WikiUrl = "https://eqlwiki.com/" + Uri.EscapeDataString(title.Replace(' ', '_')),
        };
        var fields = EqlWikiText.TemplateFields(wikitext, "Namedmobpage");
        if (fields.TryGetValue("name", out var name) && name.Trim().Length > 0)
            info.Name = EqlWikiText.StripLinks(name);
        if (fields.TryGetValue("zone", out var zone))
            info.Zone = EqlWikiText.StripLinks(zone);
        if (fields.TryGetValue("level", out var level))
            info.Level = level.Trim();
        if (fields.TryGetValue("known_loot", out var loot))
            foreach (Match m in DropRx().Matches(loot))
                info.Drops.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
        return info;
    }
}
