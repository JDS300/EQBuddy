using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy;

/// <summary>Sort order for ability/heal breakdown lists — shared by the main cards and
/// the breakout windows.</summary>
internal enum StatSort { Total, Hits, Avg, Rate }

/// <summary>Details!-style bar rows shared by the live widget and the History window.</summary>
internal static class BreakdownRows
{
    public static SolidColorBrush BarBrush(FrameworkElement resources)
    {
        var accent = ((SolidColorBrush)resources.FindResource("AccentBrush")).Color;
        return new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B));
    }

    /// <summary>One breakdown row: a bar sized to frac behind "name … value".</summary>
    public static Grid Row(FrameworkElement resources, string name, string value, double frac,
        Brush barBrush, string? tooltip, Brush? nameBrush = null, UIElement? nameBadge = null)
    {
        frac = Math.Clamp(frac, 0.004, 1.0);
        var row = new Grid { Margin = new Thickness(0, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        var bar = new Border
        {
            Background = barBrush, CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left, Width = 0,
        };
        // Star columns collapse under infinite measure, so size the bar explicitly.
        row.SizeChanged += (_, se) => bar.Width = Math.Max(0, se.NewSize.Width * frac);
        row.Children.Add(bar);

        var content = new Grid { Margin = new Thickness(4, 1, 0, 1) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(new TextBlock
        {
            Text = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = nameBrush ?? (Brush)resources.FindResource("TextBrush"),
        });
        if (nameBadge is not null)
        {
            Grid.SetColumn(nameBadge, 1);
            content.Children.Add(nameBadge);
        }
        var right = new TextBlock
        {
            Text = value, FontSize = 11, Foreground = (Brush)resources.FindResource("DimBrush"),
            Margin = new Thickness(8, 1, 2, 0),
        };
        Grid.SetColumn(right, 2);
        content.Children.Add(right);
        row.Children.Add(content);
        if (tooltip is not null) row.ToolTip = tooltip;
        return row;
    }

    /// <summary>Render pre-built shared-presentation rows (HistoryPresentation).</summary>
    public static void FillRows(FrameworkElement resources, ItemsControl list,
        IEnumerable<HistoryBreakdownRow> rows)
    {
        list.Items.Clear();
        var barBrush = BarBrush(resources);
        foreach (var r in rows)
            list.Items.Add(Row(resources, r.Name, r.Value, r.Fraction, barBrush, r.Tooltip));
    }

    /// <summary>Fill an ItemsControl with ability rows (ordered by total): the standard
    /// "total · ×hits · avg · rate (· crit%)" columns with share bars. Rate uses the
    /// parser convention (ability total ÷ time in combat); burst is in the tooltip.</summary>
    public static void FillAbilityRows(FrameworkElement resources, ItemsControl list,
        IReadOnlyList<SourceDamage> stats, double combatSeconds, string rateLabel,
        int max = int.MaxValue) =>
        FillAbilityRowsSorted(resources, list, stats, StatSort.Total, combatSeconds, rateLabel, max);

    /// <summary>The sorted flavor (hoisted from MainWindow.FillBreakdown when the breakout
    /// windows grew sort bars): rows AND bars follow the chosen metric, so what's sorted
    /// biggest is also drawn longest.</summary>
    public static void FillAbilityRowsSorted(FrameworkElement resources, ItemsControl list,
        IEnumerable<SourceDamage> stats, StatSort sort, double combatSeconds, string rateLabel,
        int max = int.MaxValue)
    {
        var secs = Math.Max(1, combatSeconds);
        double Rate(SourceDamage d) => d.Total / secs;
        static double Avg(SourceDamage d) => (double)d.Total / Math.Max(1, d.Hits);
        var sorted = (sort switch
        {
            StatSort.Hits => stats.OrderByDescending(d => d.Hits),
            StatSort.Avg => stats.OrderByDescending(Avg),
            StatSort.Rate => stats.OrderByDescending(Rate),
            _ => stats.OrderByDescending(d => d.Total),
        }).ToList();
        list.Items.Clear();
        if (sorted.Count == 0) return;
        var grand = Math.Max(1, sorted.Sum(d => d.Total));
        Func<SourceDamage, double> metric = sort switch
        {
            StatSort.Hits => d => d.Hits,
            StatSort.Avg => Avg,
            StatSort.Rate => Rate,
            _ => d => d.Total,
        };
        var topMetric = Math.Max(1e-9, sorted.Max(metric));
        var barBrush = BarBrush(resources);
        foreach (var d in sorted.Take(max))
        {
            var critPart = d.Crits > 0 ? $" · {100.0 * d.Crits / Math.Max(1, d.Hits):0}% crit" : "";
            var value = $"{d.Total:N0} · ×{d.Hits} · avg {Avg(d):0.#} · {Rate(d):0.#} {rateLabel}{critPart}";
            var tooltip = $"{100.0 * d.Total / grand:0.#}% of total · {rateLabel} = total ÷ {secs:0}s in combat" +
                (d.ActiveSeconds > 0
                    ? $" · burst {d.Total / Math.Max(1, d.ActiveSeconds):0.#}/s over the ~{d.ActiveSeconds:0}s it was in use"
                    : "");
            list.Items.Add(Row(resources, d.Name, value, metric(d) / topMetric, barBrush, tooltip));
        }
    }
}
