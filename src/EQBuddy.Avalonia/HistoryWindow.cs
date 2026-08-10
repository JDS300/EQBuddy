using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Avalonia;

public sealed partial class HistoryWindow : Window
{
    private readonly HistoryViewModel _viewModel;
    private bool _refreshing;

    public HistoryWindow(SessionRepository repository)
    {
        Resources["BgBrush"] = AppTheme.BgBrush;
        Resources["PanelBrush"] = AppTheme.PanelBrush;
        Resources["PanelHoverBrush"] = AppTheme.PanelHoverBrush;
        Resources["BorderBrush"] = AppTheme.BorderBrush;
        Resources["TextBrush"] = AppTheme.TextBrush;
        Resources["DimBrush"] = AppTheme.DimBrush;
        Resources["AccentBrush"] = AppTheme.AccentBrush;
        Resources["BadBrush"] = AppTheme.BadBrush;
        Resources["ComboBoxBrush"] = AppTheme.ComboBoxBrush;
        _viewModel = new HistoryViewModel(repository);
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || CharFilter.SelectedItem is not HistoryFilterOption selected) return;
        _viewModel.SelectedFilter = selected;
        RefreshSessions();
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_refreshing) return;
        _viewModel.SearchText = SearchBox.Text ?? "";
        RefreshSessions();
    }

    private void OnSessionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;

        foreach (var removed in e.RemovedItems.OfType<HistorySessionItem>())
            _viewModel.SelectSession(removed.Row.Id, additive: true);
        foreach (var added in e.AddedItems.OfType<HistorySessionItem>())
            _viewModel.SelectSession(added.Row.Id, additive: true);
        RenderDetail();
    }

    private void RefreshSessions()
    {
        _refreshing = true;
        try
        {
            _viewModel.RefreshSessions();
            SessionList.SelectedItems?.Clear();
            RenderDetail();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void OnImportLog(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import an existing log into session history",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("EverQuest logs") { Patterns = ["eqlog_*.txt", "*.txt"] },
                FilePickerFileTypes.All,
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;

        try
        {
            await _viewModel.ImportAsync(path);
        }
        catch (Exception ex)
        {
            CoreLog.Error(ex);
        }
    }

    private void OnSaveMeta(object? sender, RoutedEventArgs e)
    {
        _viewModel.Note = NoteBox.Text ?? "";
        _viewModel.Tags = TagsBox.Text ?? "";
        _viewModel.SaveMetadata();
    }

    private async void OnCopySummary(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSummary is not { } summary) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(summary);
    }

    private async void OnExportJson(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.ExportFileName is not { } fileName || _viewModel.ExportJson is not { } json) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export session JSON",
            SuggestedFileName = fileName,
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is not null) await File.WriteAllTextAsync(path, json);
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        _viewModel.DeleteSelected();
        RenderDetail();
    }

    /// <summary>Single-session detail uses native breakdown rows. Comparison, import,
    /// and empty states continue to use the shared plain-text report.</summary>
    private void RenderDetail()
    {
        if (_viewModel.SelectedDetail is not { } detail)
        {
            DetailText.Text = _viewModel.DetailText;
            RenderDpsGraph(null);
            DamageVisualList.Children.Clear();
            HealVisualList.Children.Clear();
            DamageVisualLabel.IsVisible = HealVisualLabel.IsVisible = false;
            DetailRest.IsVisible = false;
            RenderFights([]);
            return;
        }

        DetailText.Text = detail.HeaderText;
        RenderDpsGraph(detail.Timeline);
        FillBreakdownRows(DamageVisualList, detail.DamageRows);
        DamageVisualLabel.IsVisible = detail.DamageRows.Count > 0;
        FillBreakdownRows(HealVisualList, detail.HealRows);
        HealVisualLabel.IsVisible = detail.HealRows.Count > 0;
        RenderFights(detail.Fights);
        DetailRest.Text = detail.RestText;
        DetailRest.IsVisible = detail.RestText.Length > 0;
    }

    /// <summary>Renders one collapsed row per pull. Expanding it reveals the complete
    /// fight story: per-creature split, outgoing abilities (pets included), incoming
    /// attacks/spells, and healing during the pull.</summary>
    internal void RenderFights(IReadOnlyList<PullInfo> fights)
    {
        FightsPanel.Children.Clear();
        FightsLabel.IsVisible = fights.Count > 0;
        if (fights.Count == 0) return;

        FightsLabel.Text = $"Encounters ({fights.Count})";
        foreach (var fight in fights)
        {
            var header = AppTheme.IconButton("▸ " + HistoryPresentation.BuildFightHeader(fight),
                "Show or hide this encounter's breakdown");
            header.FontSize = 11;
            header.HorizontalAlignment = HorizontalAlignment.Left;
            var body = new StackPanel
            {
                Margin = new Thickness(14, 0, 0, 4),
                IsVisible = false,
            };
            var built = false;
            header.Click += (_, _) =>
            {
                body.IsVisible = !body.IsVisible;
                header.Content = (body.IsVisible ? "▾ " : "▸ ") +
                    HistoryPresentation.BuildFightHeader(fight);
                if (!body.IsVisible || built) return;
                built = true;
                BuildFightDetail(body, fight);
            };
            FightsPanel.Children.Add(header);
            FightsPanel.Children.Add(body);
        }
    }

    internal static void BuildFightDetail(StackPanel body, PullInfo pull)
    {
        if (pull.Fights.Count > 1)
            body.Children.Add(AppTheme.DimText(string.Join(" · ",
                pull.Fights.Select(fight => $"{fight.Name} {fight.DamageOut:N0}")),
                new Thickness(0, 1, 0, 1)));

        AddSection("Your damage", pull.ByAbility, "dps");
        AddSection("Damage you took", pull.ByIncoming, "dps");
        AddSection("Heals during the fight", pull.HealsBySpell, "hps");
        if (body.Children.Count == 0)
            body.Children.Add(AppTheme.DimText(
                "No per-fight detail — session recorded before EQBuddy 1.28."));

        void AddSection(string title, IReadOnlyList<SourceDamage> rows, string rateLabel)
        {
            if (rows.Count == 0) return;
            var heading = AppTheme.Heading(title);
            heading.Margin = new Thickness(0, 2, 0, 1);
            body.Children.Add(heading);
            var list = new StackPanel();
            FillBreakdownRows(list,
                HistoryPresentation.BuildBreakdownRows(rows, pull.DurationSeconds, rateLabel));
            body.Children.Add(list);
        }
    }

    private static void FillBreakdownRows(StackPanel panel, IReadOnlyList<HistoryBreakdownRow> rows)
    {
        panel.Children.Clear();
        foreach (var row in rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(new Border
            {
                Background = AppTheme.PanelHoverBrush,
                Width = Math.Max(1, 150 * row.Fraction),
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(2),
            });
            grid.Children.Add(new TextBlock
            {
                Text = row.Name,
                FontSize = 11,
                Foreground = AppTheme.TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(3, 1),
            });
            var value = new TextBlock
            {
                Text = row.Value,
                FontSize = 11,
                Foreground = AppTheme.DimBrush,
                Margin = new Thickness(8, 1, 3, 1),
            };
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            if (row.Tooltip is not null) ToolTip.SetTip(grid, row.Tooltip);
            panel.Children.Add(grid);
        }
    }

    private IReadOnlyList<TimelinePoint>? _graphTimeline;

    internal void RenderDpsGraph(IReadOnlyList<TimelinePoint>? timeline)
    {
        _graphTimeline = timeline;
        DpsGraphCanvas.Children.Clear();
        var width = DpsGraphCanvas.Bounds.Width > 0 ? DpsGraphCanvas.Bounds.Width : 300;
        var graph = timeline is null
            ? null
            : HistoryPresentation.BuildDpsGraph(timeline, width, DpsGraphCanvas.Height - 8);
        DpsGraphLabel.IsVisible = DpsGraphBorder.IsVisible = graph is not null;
        if (graph is null) return;

        DpsGraphLabel.Text = $"DPS over time — peak {graph.PeakDps:0.#}/s " +
            $"({graph.Start:h:mm tt}–{graph.End:h:mm tt}, per minute)";
        var line = new Polyline
        {
            Stroke = AppTheme.AccentBrush,
            StrokeThickness = 1.5,
            StrokeJoin = PenLineJoin.Round,
        };
        foreach (var (x, y) in graph.Points)
            line.Points.Add(new Point(x, y + 4));
        DpsGraphCanvas.Children.Add(line);
    }

    private void OnGraphSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_graphTimeline is { } timeline && e.NewSize.Width > 0)
            RenderDpsGraph(timeline);
    }
}
