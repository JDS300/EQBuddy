using System.IO;
using System.Windows;
using System.Windows.Controls;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy;

/// <summary>
/// Thin WPF view over the shared HistoryViewModel (EQBuddy.UI.Shared) — the same
/// ViewModel drives the Avalonia HistoryWindow, so behavior stays identical across
/// platforms. This class only maps WPF controls/events onto the ViewModel and
/// renders the structured detail (native bar rows via BreakdownRows).
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly HistoryViewModel _vm;
    private bool _syncing;

    public HistoryWindow(SessionRepository repo, AppSettings settings)
    {
        InitializeComponent();
        WindowZoom.Attach(this, "history", settings);
        _vm = new HistoryViewModel(repo);

        CharFilter.ItemsSource = _vm.Filters;
        CharFilter.SelectedItem = _vm.SelectedFilter;
        SessionList.ItemsSource = _vm.Sessions;
        SessionList.DisplayMemberPath = nameof(HistorySessionItem.DisplayText);

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(_vm.CountText): CountText.Text = _vm.CountText; break;
                case nameof(_vm.Note): NoteBox.Text = _vm.Note; break;
                case nameof(_vm.Tags): TagsBox.Text = _vm.Tags; break;
                case nameof(_vm.DetailText):
                case nameof(_vm.SelectedDetail): RenderDetail(); break;
            }
        };
        CountText.Text = _vm.CountText;
        RenderDetail();
    }

    /// <summary>Structured detail gets native bar rows; comparison/import/empty states
    /// render the ViewModel's plain text.</summary>
    private void RenderDetail()
    {
        if (_vm.SelectedDetail is { } d)
        {
            DetailText.Text = d.HeaderText;
            RenderDpsGraph(d.Timeline);
            BreakdownRows.FillRows(this, DamageVisualList, d.DamageRows);
            DamageVisualLabel.Visibility = d.DamageRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BreakdownRows.FillRows(this, HealVisualList, d.HealRows);
            HealVisualLabel.Visibility = d.HealRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderFights(d.Fights);
            DetailRest.Text = d.RestText;
            DetailRest.Visibility = d.RestText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            DetailText.Text = _vm.DetailText;
            RenderDpsGraph(null);
            DamageVisualList.Items.Clear();
            HealVisualList.Items.Clear();
            RenderFights([]);
            DamageVisualLabel.Visibility = HealVisualLabel.Visibility = Visibility.Collapsed;
            DetailRest.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The session's encounter review: one collapsed row per PULL (fights
    /// separated by a quiet gap group together, adds included), chronological,
    /// expanding to your damage by ability (pet rows included), damage you took by
    /// the creatures' attacks/spells, and heals. Content builds on first expand.</summary>
    private void RenderFights(IReadOnlyList<Core.PullInfo> fights)
    {
        FightsPanel.Children.Clear();
        FightsLabel.Visibility = fights.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (fights.Count == 0) return;
        FightsLabel.Text = $"Encounters ({fights.Count})";
        foreach (var f in fights)
        {
            var header = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("IconButton"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = "▸ " + HistoryPresentation.BuildFightHeader(f),
            };
            var body = new System.Windows.Controls.StackPanel
            { Margin = new Thickness(14, 0, 0, 4), Visibility = Visibility.Collapsed };
            var fight = f;
            var built = false;
            header.Click += (_, _) =>
            {
                var opening = body.Visibility != Visibility.Visible;
                body.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
                header.Content = (opening ? "▾ " : "▸ ") + HistoryPresentation.BuildFightHeader(fight);
                if (!opening || built) return;
                built = true;
                BuildFightDetail(body, fight);
            };
            FightsPanel.Children.Add(header);
            FightsPanel.Children.Add(body);
        }
    }

    private void BuildFightDetail(System.Windows.Controls.StackPanel body, Core.PullInfo p)
    {
        // Multi-creature pulls lead with the per-creature damage split, so "which of
        // the three actually hurt" is answerable before any section is read.
        if (p.Fights.Count > 1)
            body.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = string.Join(" · ", p.Fights.Select(f => $"{f.Name} {f.DamageOut:N0}")),
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("DimBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1),
            });

        void Section(string title, IReadOnlyList<Core.SourceDamage> rows, string rateLabel)
        {
            if (rows.Count == 0) return;
            body.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = title,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                Margin = new Thickness(0, 2, 0, 1),
            });
            var list = new System.Windows.Controls.ItemsControl();
            BreakdownRows.FillRows(this, list,
                HistoryPresentation.BuildBreakdownRows(rows, p.DurationSeconds, rateLabel));
            body.Children.Add(list);
        }

        Section("Your damage", p.ByAbility, "dps");
        Section("Damage you took", p.ByIncoming, "dps");
        Section("Heals during the fight", p.HealsBySpell, "hps");
        if (body.Children.Count == 0)
            body.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "No per-fight detail — session recorded before EQBuddy 1.28.",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("DimBrush"),
            });
    }

    private IReadOnlyList<Core.TimelinePoint>? _graphTimeline;

    /// <summary>Draws the DPS-over-time polyline. Sessions archived before the timeline
    /// existed have no points and show no graph — a flat fake line would be a lie.</summary>
    private void RenderDpsGraph(IReadOnlyList<Core.TimelinePoint>? timeline)
    {
        _graphTimeline = timeline;
        DpsGraphCanvas.Children.Clear();
        var width = DpsGraphCanvas.ActualWidth > 0 ? DpsGraphCanvas.ActualWidth : 300;
        var graph = timeline is null
            ? null
            : HistoryPresentation.BuildDpsGraph(timeline, width, DpsGraphCanvas.Height - 8);
        var visible = graph is not null ? Visibility.Visible : Visibility.Collapsed;
        DpsGraphLabel.Visibility = DpsGraphBorder.Visibility = visible;
        if (graph is null) return;

        DpsGraphLabel.Text = $"DPS over time — peak {graph.PeakDps:0.#}/s " +
            $"({graph.Start:h:mm tt}–{graph.End:h:mm tt}, per minute)";
        var line = new System.Windows.Shapes.Polyline
        {
            Stroke = (System.Windows.Media.Brush)FindResource("AccentBrush"),
            StrokeThickness = 1.5,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
        };
        foreach (var (x, y) in graph.Points)
            line.Points.Add(new System.Windows.Point(x, y + 4));   // 4px breathing room top/bottom
        DpsGraphCanvas.Children.Add(line);
    }

    private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Redraw at the real width once layout settles (and on window resize).
        if (_graphTimeline is { } t && e.NewSize.Width > 0) RenderDpsGraph(t);
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CharFilter.SelectedItem is not HistoryFilterOption filter) return;
        _syncing = true;
        _vm.SelectedFilter = filter;
        _vm.RefreshSessions();
        _syncing = false;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        _vm.SearchText = SearchBox.Text;
        _vm.RefreshSessions();
        _syncing = false;
    }

    private void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var selected = SessionList.SelectedItems.OfType<HistorySessionItem>().ToList();
        if (selected.Count == 0) return;   // list refresh cleared it — ViewModel already knows
        _syncing = true;
        // Two selected → comparison (COMPARE-*); otherwise the last item is the session.
        _vm.SelectSession(selected[0].Row.Id, additive: false);
        if (selected.Count >= 2)
            _vm.SelectSession(selected[^1].Row.Id, additive: true);
        _syncing = false;
    }

    private void OnSaveMeta(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSelectedSession) return;
        _syncing = true;
        _vm.Note = NoteBox.Text;
        _vm.Tags = TagsBox.Text;
        _vm.SaveMetadata();
        _syncing = false;
    }

    private void OnCopySummary(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSummary is { } summary) Clipboard.SetText(summary);
    }

    private void OnExportJson(object sender, RoutedEventArgs e)
    {
        if (_vm.ExportFileName is not { } name || _vm.ExportJson is not { } json) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = name, Filter = "JSON|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, json);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedRow is not { } row) return;
        if (MessageBox.Show(this,
                $"Delete the {row.StartLocal:MMM d h:mm tt} session for {row.Character}? This cannot be undone.",
                "Delete session", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _syncing = true;
        _vm.DeleteSelected();
        CharFilter.SelectedItem = _vm.SelectedFilter;
        _syncing = false;
    }

    private async void OnImportLog(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import an existing log into session history",
            Filter = "EQ log files (eqlog_*.txt)|eqlog_*.txt|Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        _syncing = true;
        try
        {
            await _vm.ImportAsync(dlg.FileName);
            CharFilter.SelectedItem = _vm.SelectedFilter;
        }
        catch (Exception ex) { CoreLog.Error(ex); }
        finally { _syncing = false; }
    }
}
