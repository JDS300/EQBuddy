using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Controls;
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
        RenderDpsGraph(_viewModel.SelectedDetail?.Timeline);
    }

    private void RefreshSessions()
    {
        _refreshing = true;
        try
        {
            _viewModel.RefreshSessions();
            SessionList.SelectedItems?.Clear();
            RenderDpsGraph(null);
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
        RenderDpsGraph(null);
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
