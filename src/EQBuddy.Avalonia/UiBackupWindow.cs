using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EQBuddy.Core;

namespace EQBuddy.Avalonia;

/// <summary>
/// Snapshots of the EverQuest UI files, and the way back to one.
///
/// The motivating case was a hand-made copy a month stale: 34,983 bytes against a live
/// 58,950. This window exists so the answer to "I just lost my UI" is a click rather than an
/// archaeology session, and so the snapshot list makes it obvious when the newest copy is old.
/// </summary>
internal sealed class UiBackupWindow : Window
{
    private readonly UiBackupService _service;
    private readonly Func<string?> _eqRoot;
    private readonly Func<bool> _clientIsLive;
    private readonly ListBox _list = new() { Height = 260, Background = AppTheme.PanelBrush };
    private readonly TextBlock _status = new()
    {
        FontSize = 11,
        Foreground = AppTheme.DimBrush,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0),
    };

    public UiBackupWindow(UiBackupService service, Func<string?> eqRoot, Func<bool> clientIsLive)
    {
        _service = service;
        _eqRoot = eqRoot;
        _clientIsLive = clientIsLive;
        Title = "EQBuddy UI Backups";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        // Centred on the widget, like Options and Feedback. Without this it opened at 0,0,
        // which on a multi-monitor desktop is the far corner of a screen you were not
        // looking at.
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgBrush;

        var backup = AppTheme.IconButton("Back up now", "Snapshot the current UI files immediately");
        backup.Click += (_, _) => Capture();
        var restore = AppTheme.IconButton("Restore selected", "Put the selected snapshot back");
        restore.Click += (_, _) => Restore();

        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Children =
            {
                new TextBlock
                {
                    Text = "Window layout, socials, hotbars and client options are saved by "
                         + "EverQuest when it exits. These are copies of those files.",
                    FontSize = 12,
                    Foreground = AppTheme.TextBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10),
                },
                _list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 10, 0, 0),
                    Children = { backup, restore },
                },
                _status,
            },
        };

        Refresh();
    }

    private void Refresh(string? message = null)
    {
        var snapshots = _service.Store.List();
        _list.ItemsSource = snapshots.Select(Describe).ToList();
        if (snapshots.Count > 0 && _list.SelectedIndex < 0) _list.SelectedIndex = 0;

        _status.Text = message ?? (snapshots.Count == 0
            ? $"No snapshots yet. They are plain file copies in {_service.Store.Root} — "
              + "restorable by hand if EQBuddy is ever unavailable."
            : $"{snapshots.Count} snapshot(s) in {_service.Store.Root}. Newest "
              + $"{Age(snapshots[0].TakenUtc)}.");
    }

    private static string Describe(UiBackupSnapshot s) =>
        $"{s.TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}   "
        + $"{s.Character ?? "unknown"}   {s.Files.Count} files   {s.TotalBytes / 1024.0:0.#} KB";

    private static string Age(DateTime takenUtc)
    {
        var span = DateTime.UtcNow - takenUtc;
        if (span < TimeSpan.FromMinutes(2)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{span.TotalMinutes:0} minutes ago";
        if (span < TimeSpan.FromDays(1)) return $"{span.TotalHours:0} hours ago";
        return $"{span.TotalDays:0} days ago";
    }

    private void Capture()
    {
        try
        {
            // Bypasses the quiet period on purpose: the user pressed the button because they
            // are about to do something risky, and refusing over a file written four seconds
            // ago would be obtuse.
            var root = _eqRoot();
            if (root is null) { Refresh("No EverQuest install found — set the log folder first."); return; }
            var taken = _service.Store.Capture(root, DateTime.UtcNow);
            _service.Store.Prune(_service.Keep);
            Refresh(taken is null
                ? "Nothing to back up — the current files already match the newest snapshot."
                : $"Backed up {taken.Files.Count} files ({taken.TotalBytes / 1024.0:0.#} KB).");
        }
        catch (Exception ex) { Refresh($"Backup failed: {ex.Message}"); }
    }

    private void Restore()
    {
        var snapshots = _service.Store.List();
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= snapshots.Count)
        {
            Refresh("Select a snapshot to restore.");
            return;
        }
        try
        {
            var root = _eqRoot();
            if (root is null) { Refresh("No EverQuest install found — set the log folder first."); return; }
            var chosen = snapshots[_list.SelectedIndex];
            _service.Store.Restore(chosen, root, _clientIsLive, DateTime.UtcNow);
            Refresh($"Restored {chosen.TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}. "
                  + "Your previous files were saved as a snapshot first.");
        }
        catch (InvalidOperationException ex) { Refresh(ex.Message); }
        catch (Exception ex) { Refresh($"Restore failed: {ex.Message}"); }
    }
}
