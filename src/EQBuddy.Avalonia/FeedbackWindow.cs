using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace EQBuddy.Avalonia;

/// <summary>Composes a GitHub Discussions draft. Nothing is submitted by the app.</summary>
public sealed class FeedbackWindow : Window
{
    private const string Repo = "https://github.com/DranakCorps-bot/EQBuddy";
    private readonly RadioButton _feature = new() { GroupName = "kind", IsChecked = true };
    private readonly RadioButton _bug = new() { GroupName = "kind" };
    private readonly TextBox _body = new()
    {
        MinHeight = 110, MaxHeight = 240, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12, Padding = new Thickness(6),
        Background = AppTheme.PanelBrush, Foreground = AppTheme.TextBrush,
        BorderBrush = AppTheme.BorderBrush,
    };
    private readonly TextBlock _bugHint = AppTheme.DimText(
        "For bugs: pasting the exact log lines around the moment it happened is the single most useful thing you can add.");
    private readonly Button _send = AppTheme.IconButton("Continue on GitHub ↗", "Review and post this draft on GitHub");

    public FeedbackWindow()
    {
        Title = "Send feedback";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _feature.Content = Label("💡 Feature request");
        _bug.Content = Label("🐛 Bug report");
        _bugHint.IsVisible = false;
        _bugHint.Margin = new Thickness(0, 4, 0, 0);
        _bugHint.TextWrapping = TextWrapping.Wrap;
        _bug.IsCheckedChanged += (_, _) => _bugHint.IsVisible = _bug.IsChecked == true;
        _body.TextChanged += (_, _) => _send.IsEnabled = (_body.Text ?? "").Trim().Length > 0;
        _send.IsEnabled = false;
        _send.Click += (_, _) => Send();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock
        {
            Text = "Send feedback", FontSize = 14, FontWeight = FontWeight.SemiBold,
            Foreground = AppTheme.TextBrush,
        });
        var close = AppTheme.IconButton("x", "Close");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => Close();
        header.Children.Add(close);

        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _feature.Margin = new Thickness(0, 0, 16, 0);
        kinds.Children.Add(_feature);
        kinds.Children.Add(_bug);

        var note = AppTheme.DimText(
            $"Posting opens on GitHub for your review - nothing is sent from the app. Appended for context: {VersionLine()}",
            new Thickness(0, 8, 0, 0));
        note.TextWrapping = TextWrapping.Wrap;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        actions.Children.Add(_send);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(kinds);
        panel.Children.Add(_body);
        panel.Children.Add(_bugHint);
        panel.Children.Add(note);
        panel.Children.Add(actions);
        Content = new Border
        {
            Background = AppTheme.BgBrush, BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 12), Child = panel,
        };
        PointerPressed += OnDrag;
        Opened += (_, _) => _body.Focus();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = AppTheme.TextBrush };

    private static string VersionLine()
    {
        var v = typeof(FeedbackWindow).Assembly.GetName().Version;
        return $"EQBuddy {v?.ToString(3) ?? "?"} · {Environment.OSVersion.Platform} {Environment.OSVersion.Version}";
    }

    private void Send()
    {
        var text = (_body.Text ?? "").Trim();
        if (text.Length == 0) return;
        var firstLine = text.Split('\n')[0].Trim();
        var title = firstLine.Length <= 70 ? firstLine : firstLine[..69].TrimEnd() + "…";
        var body = $"{text}\n\n---\n_{VersionLine()}_";
        var category = _bug.IsChecked == true ? "q-a" : "ideas";
        var url = $"{Repo}/discussions/new?category={category}" +
                  $"&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    private void OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not (Button or TextBox or RadioButton) &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
