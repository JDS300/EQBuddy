using System.Text.RegularExpressions;

namespace EQBuddy.Core;

/// <summary>Wikitext plumbing shared by the eqlwiki item and mob services.</summary>
internal static partial class EqlWikiText
{
    [GeneratedRegex(@"\[\[:?(?:[^\]|]*\|)?([^\]]*)\]\]")]
    private static partial Regex WikiLinkRx();

    /// <summary>"[[Oasis of Marr]]" / "[[Freeport|East Freeport]]" → display text.</summary>
    public static string StripLinks(string s) => WikiLinkRx().Replace(s, "$1").Trim();

    /// <summary>
    /// Splits a {{TemplateName ...}} block into its |field = value chunks. Values contain
    /// nested templates and HTML, so a naive '|' split breaks — fields start only at a
    /// line beginning with |name= at nesting depth zero relative to the template.
    /// (Hoisted verbatim from the item service when the mob service arrived; the grammar
    /// is the wiki's, not the template's, so one walker serves both.)
    /// </summary>
    public static Dictionary<string, string> TemplateFields(string wikitext, string templateName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var marker = "{{" + templateName;
        var start = wikitext.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return result;

        // Walk to the matching close of the template, tracking brace depth.
        var depth = 0; var end = wikitext.Length;
        for (var i = start; i < wikitext.Length - 1; i++)
        {
            if (wikitext[i] == '{' && wikitext[i + 1] == '{') { depth++; i++; }
            else if (wikitext[i] == '}' && wikitext[i + 1] == '}')
            {
                depth--; i++;
                if (depth == 0) { end = i - 1; break; }
            }
        }

        var body = wikitext[(start + marker.Length)..end];
        string? field = null; var valueStart = 0; depth = 0;
        var lineStart = 0;
        for (var i = 0; i <= body.Length; i++)
        {
            var atEnd = i == body.Length;
            var ch = atEnd ? '\n' : body[i];
            if (!atEnd && ch == '{' && i + 1 < body.Length && body[i + 1] == '{') { depth++; i++; continue; }
            if (!atEnd && ch == '}' && i + 1 < body.Length && body[i + 1] == '}') { depth--; i++; continue; }
            if (ch != '\n') continue;

            var line = body[lineStart..i];
            if (depth == 0 && Regex.Match(line, @"^\s*\|\s*(\w+)\s*=(.*)$") is { Success: true } m)
            {
                if (field is not null)
                    result[field] = body[valueStart..lineStart].Trim();
                field = m.Groups[1].Value;
                valueStart = lineStart + line.IndexOf('=') + 1;
            }
            lineStart = i + 1;
        }
        if (field is not null) result[field] = body[valueStart..].Trim();
        return result;
    }
}
