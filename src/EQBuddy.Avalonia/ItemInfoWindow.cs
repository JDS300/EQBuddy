using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EQBuddy.Core;

namespace EQBuddy.Avalonia;

/// <summary>On-demand eqlwiki item panel opened from loot rows. Lookups use the shared
/// seven-day cache and retain honest live/cached/stale/offline state labels.</summary>
public sealed class ItemInfoWindow : Window
{
    private readonly EqlWikiItemService _service;
    private readonly TextBlock _title = new();
    private readonly TextBlock _state = new();
    private readonly TextBox _search = new();
    private readonly StackPanel _body = new();
    private readonly TextBlock _wikiLink = new();
    private string? _currentUrl;
    private int _requestSequence;

    public ItemInfoWindow(EqlWikiItemService service)
    {
        _service = service;
        Title = "EQBuddy Item Info";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Content = BuildContent();
        Opened += (_, _) =>
        {
            if (Screens.Primary is { } screen)
                Position = new PixelPoint(screen.WorkingArea.Right - (int)(Width * screen.Scaling) - 60,
                    screen.WorkingArea.Y + 120);
        };
        PointerPressed += (_, e) =>
        {
            if (e.Source is not (Button or TextBox)
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
    }

    private Control BuildContent()
    {
        _title.Text = "Item info";
        _title.FontSize = 13;
        _title.FontWeight = FontWeight.Bold;
        _title.Foreground = AppTheme.AccentBrush;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _state.FontSize = 10;
        _state.Foreground = AppTheme.DimBrush;
        _state.VerticalAlignment = VerticalAlignment.Center;
        var close = AppTheme.IconButton("x", "Close");
        close.Click += (_, _) => Close();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(_title);
        Grid.SetColumn(_state, 1);
        _state.Margin = new Thickness(8, 0);
        header.Children.Add(_state);
        Grid.SetColumn(close, 2);
        header.Children.Add(close);

        _search.FontSize = 12;
        ToolTip.SetTip(_search, "Type an item name and press Enter");
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_search.Text)) Lookup(_search.Text);
        };
        var go = AppTheme.IconButton("🔍", "Look up");
        go.Margin = new Thickness(6, 0, 0, 0);
        go.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(_search.Text)) Lookup(_search.Text); };
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        searchRow.Children.Add(_search);
        Grid.SetColumn(go, 1);
        searchRow.Children.Add(go);

        _wikiLink.Text = "Open wiki page ↗";
        _wikiLink.FontSize = 11;
        _wikiLink.Foreground = AppTheme.AccentBrush;
        _wikiLink.Cursor = new Cursor(StandardCursorType.Hand);
        _wikiLink.Margin = new Thickness(0, 8, 0, 0);
        _wikiLink.IsVisible = false;
        _wikiLink.PointerPressed += (_, e) =>
        {
            if (_currentUrl is null) return;
            try { Process.Start(new ProcessStartInfo(_currentUrl) { UseShellExecute = true }); }
            catch (Exception ex) { App.LogError(ex); }
            e.Handled = true;
        };

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(searchRow);
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _body,
        });
        panel.Children.Add(_wikiLink);
        return new Border
        {
            Background = AppTheme.BgBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = panel,
        };
    }

    public async void Lookup(string itemName)
    {
        _search.Text = itemName;
        _title.Text = EqlWikiItemService.NormalizeTitle(itemName);
        _state.Text = "looking up…";
        _wikiLink.IsVisible = false;
        _body.Children.Clear();
        _body.Children.Add(Dim("Asking eqlwiki…"));
        var sequence = ++_requestSequence;
        ItemLookupResult result;
        try { result = await _service.LookupAsync(itemName); }
        catch { result = new ItemLookupResult(null, ItemLookupState.Offline, null); }
        if (sequence == _requestSequence) Render(result);
    }

    internal void Render(ItemLookupResult result)
    {
        _body.Children.Clear();
        _state.Text = result.State switch
        {
            ItemLookupState.Live => "LIVE",
            ItemLookupState.Cached => $"CACHED {result.FetchedAt:M/d}",
            ItemLookupState.StaleCache => $"STALE {result.FetchedAt:M/d}",
            ItemLookupState.Offline => "OFFLINE",
            _ => "NOT FOUND",
        };
        if (result.Item is not { } item)
        {
            _body.Children.Add(Dim(result.State == ItemLookupState.Offline
                ? "Couldn't reach eqlwiki and nothing is cached for this item."
                : "No wiki page found. Upgrade suffixes (+4) are stripped automatically."));
            return;
        }
        _title.Text = item.Name;
        _currentUrl = item.WikiUrl;
        _wikiLink.IsVisible = true;
        foreach (var line in item.StatsLines)
            _body.Children.Add(new TextBlock { Text = line, FontSize = 12, FontFamily = new FontFamily("monospace"), Foreground = AppTheme.TextBrush, TextWrapping = TextWrapping.Wrap });
        if (item.MerchantValue.Length > 0) _body.Children.Add(Dim($"Vendor value: {item.MerchantValue}"));
        Section("Drops from", item.DropsFrom.SelectMany(drop => drop.Mobs.Count == 0
            ? [drop.Zone] : drop.Mobs.Select(mob => drop.Zone.Length > 0 ? $"{mob} — {drop.Zone}" : mob)));
        Section("Sold by", item.SoldBy.Select(seller => $"{seller.Merchant} — {seller.Zone}{(seller.Loc.Length > 0 ? $" {seller.Loc}" : "")}"));
        Section("Quests", item.Quests);
        Section("Recipes", item.Recipes);
    }

    private void Section(string title, IEnumerable<string> values)
    {
        var lines = values.Where(value => value.Length > 0).ToList();
        if (lines.Count == 0) return;
        var heading = AppTheme.Heading(title);
        heading.Margin = new Thickness(0, 6, 0, 1);
        _body.Children.Add(heading);
        foreach (var line in lines.Take(12))
            _body.Children.Add(new TextBlock { Text = line, FontSize = 11, Foreground = AppTheme.TextBrush, TextWrapping = TextWrapping.Wrap });
        if (lines.Count > 12) _body.Children.Add(Dim($"…and {lines.Count - 12} more (see wiki page)"));
    }

    private static TextBlock Dim(string text) => AppTheme.DimText(text, new Thickness(0, 1));
}
