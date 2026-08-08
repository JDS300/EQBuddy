using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQBuddy.Core;

namespace EQBuddy;

/// <summary>
/// Session drops grouped by source creature, with export (discussion #55 — LeBigNasty
/// tracking Plane of Sky and feeding corrected drop tables back to eqlwiki for revamped
/// zones). Read-only view over StatsSnapshot.Mobs; the filter narrows both the display
/// and what the export buttons emit, so "just the golems" is one filter away.
/// </summary>
public partial class DropsWindow : Window
{
    private readonly MainWindow _main;
    private StatsSnapshot _snapshot = new();
    private string _signature = "";
    private DateTime _lastRefresh = DateTime.MinValue;

    public DropsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        WindowZoom.Attach(this, "drops", main.Settings);
    }

    /// <summary>Called on open and from MainWindow's tick while visible.</summary>
    public void Update(StatsSnapshot s)
    {
        _lastRefresh = DateTime.Now;
        _snapshot = s;
        Render();
    }

    public void MaybeRefresh()
    {
        if ((DateTime.Now - _lastRefresh).TotalSeconds >= 3) Update(_main.CurrentSnapshot());
    }

    private List<MobSummary> Filtered()
    {
        var filter = FilterBox.Text.Trim();
        var mobs = _snapshot.Mobs.Where(m => m.Loot.Count > 0);
        if (filter.Length > 0)
            mobs = mobs.Where(m =>
                m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || m.Loot.Any(l => l.Item.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        return mobs.ToList();
    }

    /// <summary>Wiki knowledge per creature, via MainWindow's target-drops memo (#65):
    /// lookups fire for every creature shown, results land async, and the signature
    /// carries each item's status so rows re-render as answers arrive.</summary>
    private WikiDropStatus Status(MobSummary mob, MobLoot l) =>
        WikiContribution.Classify(_main.WikiMobResult(mob.Name), l.Item);

    private void Render()
    {
        var mobs = Filtered();
        foreach (var m in mobs) _main.EnsureMobLookup(m.Name);
        var sig = string.Join("|", mobs.Select(m =>
            $"{m.Name}:{m.Kills}:{string.Join(",", m.Loot.Select(l => $"{l.Item}{l.Count}{(int)Status(m, l)}"))}"));
        if (sig == _signature) return;
        _signature = sig;

        MobsPanel.Children.Clear();
        var (character, _) = _main.Identity;
        Title = character.Length > 0
            ? $"EQBuddy — Drops by Creature — {character}"
            : "EQBuddy — Drops by Creature";
        foreach (var mob in mobs)
        {
            var pageStatus = mob.Loot.Count > 0 ? Status(mob, mob.Loot[0]) : WikiDropStatus.Unknown;
            var header = new TextBlock
            {
                Text = $"{mob.Name} — {mob.Kills} kill{(mob.Kills == 1 ? "" : "s")}"
                    + pageStatus switch
                    {
                        WikiDropStatus.PageMissing => "  ·  no wiki page yet",
                        WikiDropStatus.PageHasNoLoot => "  ·  wiki page lists no loot yet",
                        _ => "",
                    },
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 2),
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            MobsPanel.Children.Add(header);

            foreach (var l in mob.Loot)
                MobsPanel.Children.Add(ItemRow(l, mob.Kills, Status(mob, l)));
        }
        if (mobs.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = FilterBox.Text.Trim().Length > 0
                    ? "Nothing matches that filter."
                    : "No drops recorded this session yet — loot lines name their corpse,\nso every kill you loot shows up here.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
            MobsPanel.Children.Add(empty);
        }
    }

    /// <summary>One drop row, wired like the Loot card (David, 2026-08-07: "in the loot
    /// window that we track drops in"): click the name → the item's wiki page, hover →
    /// its stats fetched live, and quest items carry the 🗺 that opens the Quest
    /// Tracker filtered to the quests that want them.</summary>
    private StackPanel ItemRow(MobLoot l, int kills, WikiDropStatus wikiStatus)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(14, 1, 0, 1),
        };
        var isQuest = _main.IsActiveQuestItem(l.Item);

        var name = new TextBlock
        {
            Text = l.Item, FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var cached = _main.CachedItemStats(l.Item);
        var tipText = new TextBlock
        {
            Text = cached ?? "Looking up on eqlwiki…",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
            FontFamily = new FontFamily("Consolas"),
        };
        var tip = new System.Windows.Controls.ToolTip { Content = tipText };
        name.ToolTip = tip;
        var fetched = false;
        tip.Opened += async (_, _) =>
        {
            if (fetched) return;
            fetched = true;
            var text = await _main.FetchItemTooltip(l.Item);
            tipText.Text = text ?? (cached ?? "Not on the wiki.");
        };
        name.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            MainWindow.OpenWikiPage(l.Item);
        };
        row.Children.Add(name);

        if (isQuest)
        {
            var badge = new TextBlock
            {
                Text = " 🗺", FontSize = 11, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Part of a quest — click for its quest info",
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.SetResourceReference(TextBlock.ForegroundProperty, "GoodBrush");
            badge.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _main.OpenQuestInfoForItem(l.Item);
            };
            row.Children.Add(badge);
        }

        // The ✦ David asked for (#65): color says "this observation is news to the
        // wiki" — the whole point of the window once the wiki already knows the rest.
        if (wikiStatus is WikiDropStatus.NewToPage or WikiDropStatus.PageHasNoLoot
            or WikiDropStatus.PageMissing)
        {
            var star = new TextBlock
            {
                Text = " ✦", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = wikiStatus switch
                {
                    WikiDropStatus.PageMissing =>
                        "This creature has no eqlwiki page — Copy for wiki prepares one",
                    WikiDropStatus.PageHasNoLoot =>
                        "The creature's wiki page lists no loot — Copy for wiki prepares the list",
                    _ => "Not in this creature's wiki loot list yet — Copy for wiki prepares the edit",
                },
            };
            star.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
            row.Children.Add(star);
        }

        var counts = new TextBlock
        {
            Text = $"  ×{l.Count}" + (l.DropRatePct is { } pct ? $"  ·  {pct:0.#}% of {kills}" : ""),
            FontSize = 12,
        };
        counts.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
        row.Children.Add(counts);
        return row;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _signature = "";
        Render();
    }

    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        var (character, server) = _main.Identity;
        TryClipboard(DropsReport.ToText(Filtered(), character, server, _snapshot.SessionStart));
    }

    private void OnCopyCsv(object sender, RoutedEventArgs e) =>
        TryClipboard(DropsReport.ToCsv(Filtered()));

    private void OnCopyWiki(object sender, RoutedEventArgs e)
    {
        var (character, server) = _main.Identity;
        TryClipboard(WikiContribution.BuildExport(
            Filtered().Select(m => new WikiContribution.MobObservation(m, _main.WikiMobResult(m.Name))),
            character, server, _main.CurrentZoneName, DateTime.Now));
    }

    private static void TryClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { CoreLog.Error(ex); }   // clipboard contention: rare, retry by hand
    }

    private void OnSaveCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"eqbuddy-drops-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, DropsReport.ToCsv(Filtered())); }
        catch (Exception ex) { CoreLog.Error(ex); }
    }
}
