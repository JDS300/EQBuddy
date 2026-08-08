using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EQBuddy.Core;

namespace EQBuddy;

/// <summary>
/// The standalone Quest Tracker (QUEST-*, David's spec 2026-08-07): every wiki quest
/// whose turn-in items overlap what this character owns — looted since the ledger began,
/// or declared via "+ I have this" for pre-EQBuddy inventory. One card per quest,
/// most-complete first; expanding a card lists each item as have/need; the quest name
/// opens the eqlwiki walkthrough. "all quests" flips from the overlap view to the whole
/// catalog for browsing ahead.
/// </summary>
public partial class QuestsWindow : Window
{
    private readonly MainWindow _main;
    private readonly AppSettings _settings;
    private string _signature = "";
    private DateTime _lastRefresh = DateTime.MinValue;
    private string _mode = "mine";   // mine = items+pins · zone = current zone · all

    public QuestsWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _settings = main.Settings;
        ClassCombo.Items.Add("Any class");
        foreach (var c in QuestClassFilter.Classes) ClassCombo.Items.Add(c);
        ClassCombo.SelectedIndex = 0;
        ApplyModeVisual();
        ChipScale.Apply(this, 1.0);   // quests read at widget size, not chip size
        if (ScreenGuard.OnScreen(_settings.QuestsLeft, _settings.QuestsTop, Width, 200))
        { Left = _settings.QuestsLeft; Top = _settings.QuestsTop; }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + 80;
        }
        MaxHeight = SystemParameters.WorkArea.Height * 0.85;
        Closed += (_, _) =>
        {
            _settings.QuestsLeft = Left;
            _settings.QuestsTop = Top;
            _settings.Save();
        };
        Refresh(force: true);
    }

    /// <summary>Jump the window to one item's quests (the 🗺 badge in the Loot views):
    /// browse mode + the item as filter, so the quests appear even before any overlap
    /// and each carries its 📌 as the invitation to track.</summary>
    public void FilterToItem(string item)
    {
        _mode = "all";
        ApplyModeVisual();
        FilterBox.Text = item;
        Refresh(force: true);
        Activate();
    }

    /// <summary>Programmatic mode switch (screenshot hook + the 🗺 badge path).</summary>
    internal void SetMode(string mode)
    {
        _mode = mode is "zone" or "all" ? mode : "mine";
        ApplyModeVisual();
        Refresh(force: true);
    }

    private void OnModeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _mode = (string)((FrameworkElement)sender).Tag;
        ApplyModeVisual();
        Refresh(force: true);
    }

    private void ApplyModeVisual()
    {
        foreach (var (tb, key) in new[] { (ModeMine, "mine"), (ModeZone, "zone"), (ModeAll, "all") })
        {
            tb.SetResourceReference(TextBlock.ForegroundProperty, key == _mode ? "AccentBrush" : "DimBrush");
            if (key == _mode) tb.SetResourceReference(TextBlock.BackgroundProperty, "ToggleHighlightBrush");
            else tb.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void OnClassChanged(object sender, SelectionChangedEventArgs e) => Refresh(force: true);

    private string SelectedClass =>
        ClassCombo.SelectedItem is string s && s != "Any class" ? s : "";

    /// <summary>Called from MainWindow's 1 s tick while visible; cheap unless the ledger
    /// or filters actually changed (signature idiom, same as the chip windows).</summary>
    public void MaybeRefresh()
    {
        if ((DateTime.Now - _lastRefresh).TotalSeconds >= 2) Refresh(force: false);
    }

    private void Refresh(bool force)
    {
        _lastRefresh = DateTime.Now;
        var key = _main.QuestCharacterKey;
        var character = key.Length > 0 ? key.Split('_')[0] : "";
        TitleText.Text = character.Length > 0
            ? $"🗺 Quest Tracker — {char.ToUpper(character[0])}{character[1..]}"
            : "🗺 Quest Tracker";

        var owned = _main.QuestLedger?.For(key)
            ?? new Dictionary<string, QuestLedgerStore.Entry>(StringComparer.OrdinalIgnoreCase);
        var tracked = _main.QuestLedger?.TrackedFor(key)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hidden = _main.QuestLedger?.HiddenFor(key)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = _main.QuestLedger?.CompletedFor(key)
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var filter = FilterBox.Text.Trim();
        var cls = SelectedClass;

        var sig = $"{key}|{filter}|{_mode}|{cls}|{_main.CurrentZoneName}" +
            $"|{string.Join(";", tracked.Order(StringComparer.OrdinalIgnoreCase))}" +
            $"|{string.Join(";", hidden.Order(StringComparer.OrdinalIgnoreCase))}" +
            $"|{string.Join(";", completed.Select(kv => $"{kv.Key}:{kv.Value}"))}" +
            $"|{string.Join(",", owned.Select(kv => $"{kv.Key}:{kv.Value.Total}"))}";
        if (!force && sig == _signature) return;
        _signature = sig;

        QuestsPanel.Children.Clear();

        bool ClassOk(QuestEntry q) => cls.Length == 0 || QuestClassFilter.Matches(q.Classes, cls);
        QuestMatch Progressed(QuestEntry quest)
        {
            var progress = quest.Items
                .Select(i => new QuestItemProgress(i.Name, i.Qty,
                    owned.TryGetValue(i.Name, out var e) ? e.Total : 0)).ToList();
            return new QuestMatch(quest, progress.Count(p => p.Have > 0), progress.Count,
                progress, tracked.Contains(quest.Name));
        }
        void AddCard(QuestMatch m) => QuestsPanel.Children.Add(
            Card(m, hidden.Contains(m.Quest.Name), completed.GetValueOrDefault(m.Quest.Name)));
        void EmptyNote(string text)
        {
            var note = new TextBlock
            {
                Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 8, 0, 8),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
            QuestsPanel.Children.Add(note);
        }

        switch (_mode)
        {
            case "all":
                foreach (var quest in _main.QuestCatalog.Quests
                             .Where(q => MatchesFilter(q, filter) && ClassOk(q))
                             .OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase))
                    AddCard(Progressed(quest));
                break;

            case "zone" when _main.CurrentZoneName.Length == 0:
                EmptyNote("No zone seen in the log yet — zone view fills in once " +
                          "you've zoned somewhere.");
                break;

            case "zone":
            {
                // Everything workable where you stand — including dialogue chains the
                // item parser found nothing for (David: "not everything is item driven").
                var zoneLabel = new TextBlock
                {
                    Text = $"📍 {_main.CurrentZoneName}", FontSize = 11,
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 5),
                };
                zoneLabel.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
                QuestsPanel.Children.Add(zoneLabel);
                var zoneQuests = _main.QuestCatalog.Quests
                    .Where(q => q.TouchesZone(_main.CurrentZoneName)
                                && MatchesFilter(q, filter) && ClassOk(q))
                    .Select(Progressed)
                    .OrderByDescending(m => m.Tracked)
                    .ThenByDescending(m => m.Fraction)
                    .ThenBy(m => m.Quest.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var m in zoneQuests) AddCard(m);
                if (zoneQuests.Count == 0)
                    EmptyNote($"No catalogued quests touch {_main.CurrentZoneName}.");
                break;
            }

            default:
            {
                // "mine": item overlap + pins, minus dismissed and finished-for-good
                // (completed non-repeatables stay visible in zone/all with their ✓).
                var doneForGood = new HashSet<string>(
                    completed.Where(kv => kv.Value > 0).Select(kv => kv.Key)
                        .Where(name => _main.QuestCatalog.Quests.FirstOrDefault(q =>
                            q.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Repeatable: false }),
                    StringComparer.OrdinalIgnoreCase);
                doneForGood.UnionWith(hidden);
                var matches = QuestMatcher.Match(_main.QuestCatalog, owned, tracked, doneForGood);
                var shown = matches.Where(m => MatchesFilter(m.Quest, filter) && ClassOk(m.Quest)).ToList();
                foreach (var m in shown) AddCard(m);
                if (shown.Count == 0)
                    EmptyNote(matches.Count == 0
                        ? "Nothing yet — loot a quest item (they show green in the Loot list)\n" +
                          "or add what you already carry with \"+ I have this\" above.\n" +
                          "Try \"zone\" for what's workable here, or \"all\" to browse."
                        : "No quest matches that filter.");
                break;
            }
        }
    }

    private static bool MatchesFilter(QuestEntry q, string filter) =>
        filter.Length == 0
        || q.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || q.StartZone.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || q.QuestGiver.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || q.Items.Any(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    // ---- card building ----

    private Border Card(QuestMatch m, bool isHidden = false, int completedCount = 0)
    {
        var body = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock
        {
            Text = m.Quest.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand,
            ToolTip = "Open the wiki walkthrough",
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        name.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenUrl(m.Quest.Url);
        };
        Grid.SetColumn(name, 0);
        header.Children.Add(name);

        var count = new TextBlock
        {
            Text = m.ItemsTotal == 0 ? "steps"
                : m.Complete
                    ? m.Quest.Repeatable && m.ReadyCount > 1 ? $"✔ ready ×{m.ReadyCount}" : "✔ ready"
                    : $"{m.ItemsHave}/{m.ItemsTotal}",
            FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(8, 0, 0, 0),
        };
        count.SetResourceReference(TextBlock.ForegroundProperty,
            m.Complete ? "GoodBrush" : m.ItemsHave > 0 ? "AccentBrush" : "DimBrush");
        // A ready card's count doubles as the "I handed it in" button: consumes one set
        // of turn-ins and bumps the done counter. Dialogue quests mark done for free.
        if (m.Complete || m.ItemsTotal == 0)
        {
            count.Cursor = Cursors.Hand;
            count.ToolTip = m.ItemsTotal == 0
                ? "Click when you finish this quest to mark it done"
                : "Click when you hand it in — consumes one set of turn-in items and counts a completion";
            count.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                var key = _main.QuestCharacterKey;
                if (_main.QuestLedger is { } ledger && key.Length > 0)
                {
                    ledger.RecordCompletion(key, m.Quest.Name, m.Quest.Items);
                    Refresh(force: true);
                }
            };
        }
        Grid.SetColumn(count, 1);
        header.Children.Add(count);

        // 📌 = "keep this quest in front of me": tracked quests sort first and stay
        // visible even with zero items — the choose-to-track affordance (David,
        // 2026-08-07: "players can choose to track quests or not, easily").
        var pin = new TextBlock
        {
            Text = "📌", FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand, Opacity = m.Tracked ? 1.0 : 0.35,
            ToolTip = m.Tracked ? "Stop tracking this quest" : "Track this quest",
        };
        pin.SetResourceReference(TextBlock.ForegroundProperty, m.Tracked ? "AccentBrush" : "DimBrush");
        pin.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var key = _main.QuestCharacterKey;
            if (_main.QuestLedger is { } ledger && key.Length > 0)
            {
                ledger.SetTracked(key, m.Quest.Name, !m.Tracked);
                Refresh(force: true);
            }
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pin, 2);
        header.Children.Add(pin);

        // ✕ = "not interested": drops the quest from the overlap view AND un-greens
        // loot only it wants (David, 2026-08-07: "there are definitely some I don't
        // want to track"). Hidden quests reappear dimmed under "all quests", where ✕
        // becomes the way back.
        var dismiss = new TextBlock
        {
            Text = "✕", FontSize = 11, Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand, Opacity = isHidden ? 1.0 : 0.35,
            ToolTip = isHidden
                ? "Show this quest again"
                : "Not interested — hide this quest (its items stop showing green unless another quest wants them)",
        };
        dismiss.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
        dismiss.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var key = _main.QuestCharacterKey;
            if (_main.QuestLedger is { } ledger && key.Length > 0)
            {
                ledger.SetHidden(key, m.Quest.Name, !isHidden);
                Refresh(force: true);
            }
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(dismiss, 3);
        header.Children.Add(dismiss);
        body.Children.Add(header);

        if (m.Quest.Rewards.Count > 0)
        {
            // The payoff sits right under the title (David, 2026-08-07: "Crude Stein
            // Quest should show the Crude Stein item"), with the same hover/click as
            // loot: hover pulls the item's wiki stats live, click opens its page.
            var wrap = new WrapPanel { Margin = new Thickness(0, 1, 0, 1) };
            var label = new TextBlock
            {
                Text = "Rewards:", FontSize = 10.5, Margin = new Thickness(0, 0, 6, 0),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
            wrap.Children.Add(label);
            const int shown = 6;
            foreach (var reward in m.Quest.Rewards.Take(shown))
                wrap.Children.Add(RewardLink(reward));
            if (m.Quest.Rewards.Count > shown)
            {
                var more = new TextBlock
                {
                    Text = $"+{m.Quest.Rewards.Count - shown} more", FontSize = 10.5,
                    ToolTip = string.Join("\n", m.Quest.Rewards.Skip(shown)),
                };
                more.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
                wrap.Children.Add(more);
            }
            body.Children.Add(wrap);
        }

        // "How far is the turn-in from here" — BFS hops over the harvested zone graph,
        // path in the tooltip (David, 2026-08-07: "3 zones away, zone 1 → zone 2 →
        // zone 3"). Multi-zone quests measure to the nearest listed start zone.
        var distance = "";
        string? route = null;
        if (_main.CurrentZoneName.Length > 0 && m.Quest.StartZone.Length > 0)
        {
            var best = m.Quest.StartZone.Split(',')
                .Select(z => _main.ZoneGraph.Distance(_main.CurrentZoneName, z.Trim()))
                .Where(d => d is not null)
                .OrderBy(d => d!.Value.Hops)
                .FirstOrDefault();
            if (best is { } b)
            {
                distance = b.Hops == 0 ? " · you're here" : $" · {b.Hops} zone{(b.Hops == 1 ? "" : "s")} away";
                route = b.Hops == 0 ? null : string.Join(" → ", b.Path);
            }
        }

        // Classes go LAST: they're the longest fragment and the only one that can
        // afford to vanish into the ellipsis — "done ×2" never should.
        var meta = string.Join(" · ", new[]
        {
            m.Quest.StartZone,
            m.Quest.QuestGiver.Length > 0 ? $"from {m.Quest.QuestGiver}" : "",
            m.Quest.MinLevel > 0 ? $"lvl {m.Quest.MinLevel}+" : "",
            m.Quest.Repeatable ? "repeatable" : "",
            completedCount > 0
                ? m.Quest.Repeatable ? $"done ×{completedCount}" : "✓ done"
                : "",
        }.Where(s => s.Length > 0));
        if (meta.Length > 0 || distance.Length > 0)
        {
            var full = meta + distance
                + (m.Quest.Classes.Length > 0 ? $" · {m.Quest.Classes}" : "");
            var metaText = new TextBlock
            {
                Text = full, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 2),
                ToolTip = route is { Length: > 0 } ? $"{route}\n{full}" : full,
            };
            metaText.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
            body.Children.Add(metaText);
        }

        foreach (var item in m.Items)
            body.Children.Add(ItemRow(item));
        if (m.ItemsTotal == 0)
        {
            // The item parser found no turn-ins: a dialogue/kill/exploration chain.
            var dialogue = new TextBlock
            {
                Text = "Dialogue or task chain — steps on the wiki page.",
                FontSize = 11, FontStyle = FontStyles.Italic, Margin = new Thickness(8, 0.5, 0, 0.5),
            };
            dialogue.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
            body.Children.Add(dialogue);
        }

        var card = new Border
        {
            Child = body, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 6), Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(1),
            Opacity = isHidden ? 0.55 : 1.0,
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, m.Complete ? "GoodBrush" : "BorderBrush");
        return card;
    }

    /// <summary>One reward item: hover fetches its eqlwiki stats on the spot (the
    /// tooltip live-updates from "Looking up…", same as the Loot breakout rows), click
    /// opens the wiki page.</summary>
    private TextBlock RewardLink(string name)
    {
        var cached = _main.CachedItemStats(name);
        var link = new TextBlock
        {
            Text = name, FontSize = 10.5, Margin = new Thickness(0, 0, 10, 1),
            Cursor = Cursors.Hand,
        };
        link.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        var tipText = new TextBlock
        {
            Text = cached ?? "Looking up on eqlwiki…",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        };
        var tip = new ToolTip { Content = tipText };
        link.ToolTip = tip;
        var fetched = false;
        tip.Opened += async (_, _) =>
        {
            if (fetched) return;
            fetched = true;
            var text = await _main.FetchItemTooltip(name);
            tipText.Text = text ?? (cached ?? "Not on the wiki.");
        };

        link.MouseLeftButtonDown += (_, e) => e.Handled = true;
        link.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            MainWindow.OpenWikiPage(name);
        };
        return link;
    }

    private const string ItemRowHint =
        "Left-click: +1 (you have one more) · Right-click: clear your count (after a hand-in)";

    private TextBlock ItemRow(QuestItemProgress item)
    {
        var met = item.Have >= item.Need;
        var row = new TextBlock
        {
            Text = $"{(met ? "✔" : "•")} {item.Name} — {item.Have}/{item.Need}",
            FontSize = 11.5, Margin = new Thickness(8, 0.5, 0, 0.5),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Cursor = Cursors.Hand,
        };
        // Same live wiki-stats hover the Loot window has (David, 2026-08-07), with the
        // count-adjust hint riding underneath.
        var cached = _main.CachedItemStats(item.Name);
        var tipText = new TextBlock
        {
            Text = (cached ?? "Looking up on eqlwiki…") + "\n\n" + ItemRowHint,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        };
        var tip = new ToolTip { Content = tipText };
        row.ToolTip = tip;
        var fetched = false;
        tip.Opened += async (_, _) =>
        {
            if (fetched) return;
            fetched = true;
            var text = await _main.FetchItemTooltip(item.Name);
            tipText.Text = (text ?? cached ?? "Not on the wiki.") + "\n\n" + ItemRowHint;
        };
        row.SetResourceReference(TextBlock.ForegroundProperty,
            met ? "GoodBrush" : item.Have > 0 ? "TextBrush" : "DimBrush");
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            AdjustManual(item.Name, +1);
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ClearCount(item.Name);
        };
        return row;
    }

    private void AdjustManual(string item, int delta)
    {
        var key = _main.QuestCharacterKey;
        if (_main.QuestLedger is not { } ledger || key.Length == 0) return;
        ledger.For(key).TryGetValue(item, out var entry);
        ledger.SetManual(key, item, (entry?.Manual ?? 0) + delta);
        Refresh(force: true);
    }

    /// <summary>A hand-in happened: zero the whole count for this item. The looted count
    /// is history we can't re-earn, so it becomes a negative manual offset instead —
    /// net zero now, and future loot counts up from there.</summary>
    private void ClearCount(string item)
    {
        var key = _main.QuestCharacterKey;
        if (_main.QuestLedger is not { } ledger || key.Length == 0) return;
        ledger.For(key).TryGetValue(item, out var entry);
        if (entry is null) return;
        ledger.SetManual(key, item, -entry.Looted);
        Refresh(force: true);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { CoreLog.Error(ex); }
    }

    // ---- "+ I have this" ----

    private List<string> Suggestions(string typed) =>
        _main.QuestCatalog.ByItem().Keys
            .Where(n => n.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => !n.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(8).ToList();

    private void OnAddItemTyped(object sender, TextChangedEventArgs e)
    {
        var typed = AddItemBox.Text.Trim();
        if (typed.Length < 2) { SuggestList.Visibility = Visibility.Collapsed; return; }
        var suggestions = Suggestions(typed);
        SuggestList.ItemsSource = suggestions;
        SuggestList.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSuggestPicked(object sender, MouseButtonEventArgs e)
    {
        if (SuggestList.SelectedItem is not string picked) return;
        AddItemBox.Text = picked;
        SuggestList.Visibility = Visibility.Collapsed;
        AddQtyBox.Focus();
    }

    private void OnAddItemKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnAddItem(sender, e); e.Handled = true; }
        if (e.Key == Key.Escape) SuggestList.Visibility = Visibility.Collapsed;
    }

    private void OnAddItem(object sender, RoutedEventArgs e)
    {
        var item = AddItemBox.Text.Trim();
        if (item.Length == 0) return;
        if (!int.TryParse(AddQtyBox.Text.Trim(), out var qty) || qty < 1) qty = 1;
        AdjustManual(item, qty);
        AddItemBox.Clear();
        AddQtyBox.Text = "1";
        SuggestList.Visibility = Visibility.Collapsed;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Refresh(force: true);
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
