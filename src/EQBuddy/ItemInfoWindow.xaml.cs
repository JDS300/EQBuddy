using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EQBuddy.Core;

namespace EQBuddy;

/// <summary>
/// On-demand eqlwiki item panel: opened from a loot row click or the widget menu,
/// driven by <see cref="EqlWikiItemService"/> (one fetch per request, 7-day cache,
/// honest source labels). Position remembered per session only — it's a popup,
/// not furniture.
/// </summary>
public partial class ItemInfoWindow : Window
{
    private readonly EqlWikiItemService _service;
    private string? _currentUrl;
    private int _requestSeq;

    public ItemInfoWindow(EqlWikiItemService service)
    {
        InitializeComponent();
        _service = service;
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 60;
        Top = wa.Top + 120;
    }

    public async void Lookup(string itemName)
    {
        SearchBox.Text = itemName;
        TitleText.Text = EqlWikiItemService.NormalizeTitle(itemName);
        StateChip.Text = "looking up…";
        WikiLink.Visibility = Visibility.Collapsed;
        Body.Children.Clear();
        Body.Children.Add(Dim("Asking eqlwiki…"));

        var seq = ++_requestSeq;
        ItemLookupResult result;
        try { result = await _service.LookupAsync(itemName); }
        catch { result = new ItemLookupResult(null, ItemLookupState.Offline, null); }
        if (seq != _requestSeq) return;   // a newer lookup superseded this one

        Render(result);
    }

    private void Render(ItemLookupResult result)
    {
        Body.Children.Clear();
        StateChip.Text = result.State switch
        {
            ItemLookupState.Live => "LIVE",
            ItemLookupState.Cached => $"CACHED {result.FetchedAt:M/d}",
            ItemLookupState.StaleCache => $"STALE {result.FetchedAt:M/d}",
            ItemLookupState.Offline => "OFFLINE",
            _ => "NOT FOUND",
        };
        if (result.Item is not { } item)
        {
            Body.Children.Add(Dim(result.State == ItemLookupState.Offline
                ? "Couldn't reach eqlwiki and nothing is cached for this item."
                : "No wiki page found for this item. Base names work best — upgrade suffixes (+4) are stripped automatically."));
            return;
        }

        TitleText.Text = item.Name;
        _currentUrl = item.WikiUrl;
        WikiLink.Visibility = Visibility.Visible;

        foreach (var line in item.StatsLines)
            Body.Children.Add(new TextBlock
            {
                Text = line, FontSize = 12, FontFamily = new FontFamily("Consolas"),
                Foreground = (Brush)FindResource("TextBrush"), TextWrapping = TextWrapping.Wrap,
            });
        if (item.MerchantValue.Length > 0)
            Body.Children.Add(Dim($"Vendor value: {item.MerchantValue}"));

        Section("Drops from", item.DropsFrom.SelectMany(d =>
            d.Mobs.Count == 0 ? [d.Zone] : d.Mobs.Select(m => d.Zone.Length > 0 ? $"{m} — {d.Zone}" : m)));
        Section("Sold by", item.SoldBy.Select(s =>
            $"{s.Merchant} — {s.Zone}{(s.Loc.Length > 0 ? $" {s.Loc}" : "")}"));
        Section("Quests", item.Quests);
        Section("Recipes", item.Recipes);
    }

    private void Section(string title, IEnumerable<string> lines)
    {
        var list = lines.Where(l => l.Length > 0).ToList();
        if (list.Count == 0) return;
        Body.Children.Add(new TextBlock
        {
            Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("AccentBrush"), Margin = new Thickness(0, 6, 0, 1),
        });
        foreach (var line in list.Take(12))
            Body.Children.Add(new TextBlock
            {
                Text = line, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextBrush"),
            });
        if (list.Count > 12) Body.Children.Add(Dim($"…and {list.Count - 12} more (see wiki page)"));
    }

    private TextBlock Dim(string text) => new()
    {
        Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)FindResource("DimBrush"), Margin = new Thickness(0, 1, 0, 1),
    };

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchBox.Text.Trim().Length > 0) Lookup(SearchBox.Text.Trim());
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length > 0) Lookup(SearchBox.Text.Trim());
    }

    private void OnOpenWiki(object sender, MouseButtonEventArgs e)
    {
        if (_currentUrl is null) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(_currentUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not (Button or TextBox)) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();
}
