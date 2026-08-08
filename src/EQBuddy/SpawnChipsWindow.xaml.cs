using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy;

/// <summary>
/// The spawn-countdown chicklet stack (SPAWN-005, David's design): while timers run,
/// each shows as a small movable chip — "⏳ Asaka L`Rei 3:12" — and the full zone list
/// only exists behind a double-click. At zero a chip flips to a warn-colored DUE for
/// one minute, then clears itself — click it away sooner if you're watching. The stack shows every
/// running timer on the server regardless of zone: a Befallen camp timer keeps counting
/// while you bank in West Commonlands, because a timer you set is a timer you care
/// about. MainWindow's shared tick drives refresh and decides visibility — the stack
/// exists exactly while timers do, staying up alongside the full zone window too
/// (dropping it while the browser was open lost the countdowns you cared about).
/// </summary>
public partial class SpawnChipsWindow : Window
{
    private readonly MainWindow _main;
    private readonly SpawnsViewModel _vm;
    private readonly AppSettings _settings;
    private string _signature = "";
    private readonly List<TextBlock> _countdowns = [];
    private List<SpawnChip> _chips = [];

    public SpawnChipsWindow(MainWindow main, SpawnsViewModel vm)
    {
        InitializeComponent();
        _main = main;
        _vm = vm;
        _settings = main.Settings;
        ChipScale.Apply(this, _settings.ChipScale);
        WindowZoom.Route(this, () => _settings.ChipScale, main.SetChipScale);
        if (ScreenGuard.OnScreen(_settings.SpawnChipsLeft, _settings.SpawnChipsTop, Width, Height))
        { Left = _settings.SpawnChipsLeft; Top = _settings.SpawnChipsTop; }
        else { Left = SystemParameters.WorkArea.Left + 40; Top = SystemParameters.WorkArea.Top + 40; }
        Closed += (_, _) =>
        {
            _settings.SpawnChipsLeft = Left;
            _settings.SpawnChipsTop = Top;
            _settings.Save();
        };
    }

    /// <summary>Called from MainWindow's 1 s tick while the stack is visible.</summary>
    public void RefreshChips(DateTime now)
    {
        _chips = _vm.Chips(now);
        var signature = string.Join("", _chips.Select(c => $"{c.Zone}|{c.Name}|{c.IsDue}"));
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild();
        }
        else
        {
            for (var i = 0; i < _chips.Count && i < _countdowns.Count; i++)
                _countdowns[i].Text = _chips[i].IsDue ? "DUE" : _chips[i].CountdownText;
        }
    }

    private void Rebuild()
    {
        ChipsPanel.Children.Clear();
        _countdowns.Clear();
        foreach (var chip in _chips)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = $"{chip.Icon} {chip.Name}", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var countdown = new TextBlock
            {
                Text = chip.IsDue ? "DUE" : chip.CountdownText,
                FontSize = 11, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            countdown.SetResourceReference(TextBlock.ForegroundProperty, chip.IsDue ? "WarnBrush" : "AccentBrush");
            Grid.SetColumn(countdown, 1);
            row.Children.Add(countdown);
            _countdowns.Add(countdown);

            var border = new Border
            {
                Child = row,
                ToolTip = chip.Detail + "\nRight-click: dismiss this timer",
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 0, 3),
                BorderThickness = new Thickness(1),
                Tag = chip,
            };
            border.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            border.SetResourceReference(Border.BorderBrushProperty, chip.IsDue ? "WarnBrush" : "BorderBrush");
            border.MouseLeftButtonDown += OnChipMouseDown;
            border.MouseRightButtonUp += OnChipDismiss;
            ChipsPanel.Children.Add(border);
        }
    }

    /// <summary>Right-click dismisses a chip whether DUE or still counting — a camp
    /// abandoned mid-countdown shouldn't haunt the stack until it expires (Reddit,
    /// anyhow188: only due chips could be cleared).</summary>
    private void OnChipDismiss(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: SpawnChip chip } || chip.Zone.Length == 0) return;
        _vm.ClearTimer(chip.Zone, chip.Name);
        _signature = "";
        RefreshChips(DateTime.Now);
        e.Handled = true;
    }

    private void OnChipMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: SpawnChip chip }) return;
        if (e.ClickCount == 2)
        {
            // The full zone list, opened on the chip's zone. MainWindow's tick hides
            // the stack while the full window is up.
            _main.ShowSpawnsWindow(chip.Zone);
            e.Handled = true;
            return;
        }
        if (chip.IsDue)
        {
            // A due chip has said its piece — click acknowledges and clears the timer.
            _vm.ClearTimer(chip.Zone, chip.Name);
            _signature = "";
            RefreshChips(DateTime.Now);
            e.Handled = true;
            return;
        }
        DragMove();
    }
}
