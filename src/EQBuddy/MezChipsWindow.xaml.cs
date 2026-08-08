using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EQBuddy.Core;
using SpawnChip = EQBuddy.UI.Shared.SpawnChip;

namespace EQBuddy;

/// <summary>
/// The mez-target stack: one chip per believed-active mez, counting down to wake-up.
/// Same chicklet language as the spawn stack but a separate window with its own saved
/// position — mez chips get parked next to the fight, spawn chips are ambient. Chips
/// only drag; there is nothing to click through to and nothing to clear (breaks clear
/// themselves via the log).
/// </summary>
public partial class MezChipsWindow : Window
{
    private readonly AppSettings _settings;
    private string _signature = "";
    private readonly List<TextBlock> _countdowns = [];
    private List<SpawnChip> _chips = [];
    private readonly Func<DateTime, List<SpawnChip>> _source;

    public MezChipsWindow(AppSettings settings, Func<DateTime, List<SpawnChip>> source)
    {
        InitializeComponent();
        _settings = settings;
        _source = source;
        ChipScale.Apply(this, _settings.ChipScale);
        if (ScreenGuard.OnScreen(_settings.MezChipsLeft, _settings.MezChipsTop, Width, Height))
        { Left = _settings.MezChipsLeft; Top = _settings.MezChipsTop; }
        else { Left = SystemParameters.WorkArea.Left + 40; Top = SystemParameters.WorkArea.Top + 120; }
        Closed += (_, _) =>
        {
            _settings.MezChipsLeft = Left;
            _settings.MezChipsTop = Top;
            _settings.Save();
        };
    }

    /// <summary>Called from MainWindow's 1 s tick while the stack is visible.</summary>
    public void RefreshChips(DateTime now)
    {
        _chips = _source(now);
        var signature = string.Join("", _chips.Select(c => $"{c.Name}|{c.IsDue}"));
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild();
        }
        else
        {
            for (var i = 0; i < _chips.Count && i < _countdowns.Count; i++)
                _countdowns[i].Text = _chips[i].CountdownText;
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
                Text = chip.CountdownText,
                FontSize = 11, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Warning tint inside the last tick — the wake-up is the urgent moment.
            countdown.SetResourceReference(TextBlock.ForegroundProperty, chip.IsDue ? "WarnBrush" : "AccentBrush");
            Grid.SetColumn(countdown, 1);
            row.Children.Add(countdown);
            _countdowns.Add(countdown);

            var border = new Border
            {
                Child = row, ToolTip = chip.Detail,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 0, 3),
                BorderThickness = new Thickness(1),
            };
            border.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            border.SetResourceReference(Border.BorderBrushProperty, chip.IsDue ? "WarnBrush" : "BorderBrush");
            border.MouseLeftButtonDown += (_, _) => DragMove();
            ChipsPanel.Children.Add(border);
        }
    }
}
