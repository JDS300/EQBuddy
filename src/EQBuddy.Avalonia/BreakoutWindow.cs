using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Avalonia;

public enum BreakoutKind { Damage, Healing, Pet }

/// <summary>Floating fight/session bar chart for outgoing damage, healing, or pet damage.</summary>
public sealed class BreakoutWindow : Window
{
    private readonly AppSettings _settings;
    private readonly BreakoutKind _kind;
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = AppTheme.DimText("");
    private readonly TextBlock _empty = AppTheme.DimText("");
    private readonly StackPanel _rows = new();
    private readonly Button _fight;
    private readonly Button _session;
    private bool _fightScope;
    private string _signature = "";
    private PixelPoint _savedPosition;

    public event Action<BreakoutKind>? Dismissed;

    public BreakoutWindow(AppSettings settings, BreakoutKind kind)
    {
        _settings = settings;
        _kind = kind;
        _fightScope = ScopeSetting() != "session";
        Title = $"EQBuddy {kind} breakout";
        Width = 310;
        SizeToContent = SizeToContent.Height;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;

        _title.FontSize = 13;
        _title.FontWeight = FontWeight.Bold;
        _title.Foreground = AppTheme.TextBrush;
        _fight = ScopeButton("Fight", true);
        _session = ScopeButton("Session", false);
        var close = AppTheme.IconButton("x", "Hide until the next minimize");
        close.Click += (_, _) => { HideAndSave(); Dismissed?.Invoke(_kind); };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        header.Children.Add(_title);
        Grid.SetColumn(_fight, 1); header.Children.Add(_fight);
        Grid.SetColumn(_session, 2); header.Children.Add(_session);
        Grid.SetColumn(close, 3); header.Children.Add(close);
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(_subtitle);
        panel.Children.Add(_empty);
        panel.Children.Add(_rows);
        Content = new Border
        {
            Background = AppTheme.BgBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = panel,
        };
        PointerPressed += (_, e) =>
        {
            if (e.Source is not Button && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        Opened += (_, _) => RestorePosition();
        PositionChanged += (_, _) => { if (IsVisible) _savedPosition = Position; };
        Closed += (_, _) => SavePosition();
        PaintScope();
    }

    private Button ScopeButton(string text, bool fight)
    {
        var button = AppTheme.IconButton(text, $"Show {text.ToLowerInvariant()} numbers");
        button.FontSize = 11;
        button.Click += (_, _) =>
        {
            _fightScope = fight;
            SetScopeSetting(fight ? "fight" : "session");
            _signature = "";
            PaintScope();
        };
        return button;
    }

    public void Update(StatsSnapshot snapshot)
    {
        var fight = snapshot.LastFight;
        var (title, stats, seconds, rateLabel) = _kind switch
        {
            BreakoutKind.Damage => ("⚔ Your damage", _fightScope ? fight?.ByAbility ?? [] : snapshot.DamageBySource,
                _fightScope ? fight?.DurationSeconds ?? 0 : snapshot.CombatSeconds, "dps"),
            BreakoutKind.Healing => ("⚕ Your healing", _fightScope ? fight?.HealsBySpell ?? [] : snapshot.HealsBySpell,
                _fightScope ? fight?.DurationSeconds ?? 0 : snapshot.CombatSeconds, "hps"),
            _ => (snapshot.PetName.Length > 0 ? $"🐾 Pet damage — {snapshot.PetName}" : "🐾 Pet damage",
                _fightScope ? fight?.PetAbilities ?? [] : snapshot.PetAbilities,
                _fightScope ? fight?.DurationSeconds ?? 0 : snapshot.CombatSeconds, "dps"),
        };
        _title.Text = title;
        var rate = stats.Sum(row => row.Total) / Math.Max(1, seconds);
        _subtitle.Text = _fightScope
            ? fight is null ? "No fights yet" : $"{fight.Name} · {fight.DurationSeconds:0}s · {fight.Outcome} · {rate:0.#} {rateLabel}"
            : $"Session · {snapshot.CombatSeconds / 60:0}m in combat · {rate:0.#} {rateLabel}";
        _empty.IsVisible = stats.Count == 0;
        _empty.Text = _kind switch
        {
            BreakoutKind.Healing => "No healing seen yet.",
            BreakoutKind.Pet => "No pet damage seen yet.",
            _ => "No damage seen yet.",
        };
        var signature = $"{_fightScope}|{fight?.Name}|{seconds:0}|{string.Join(',', stats.Select(row => $"{row.Name}:{row.Total}"))}";
        if (signature == _signature) return;
        _signature = signature;
        RenderRows(stats, seconds, rateLabel);
    }

    private void RenderRows(IReadOnlyList<SourceDamage> stats, double seconds, string rateLabel)
    {
        _rows.Children.Clear();
        var top = Math.Max(1, stats.Count > 0 ? stats.Max(row => row.Total) : 1);
        foreach (var row in stats.Take(10))
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 1) };
            grid.Children.Add(new Border { Background = AppTheme.PanelHoverBrush, Width = 190.0 * row.Total / top, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) });
            grid.Children.Add(new TextBlock { Text = row.Name, FontSize = 11, Foreground = AppTheme.TextBrush, Margin = new Thickness(3, 1), TextTrimming = TextTrimming.CharacterEllipsis });
            var value = new TextBlock { Text = $"{row.Total:N0} · {row.Total / Math.Max(1, seconds):0.#} {rateLabel}", FontSize = 11, Foreground = AppTheme.DimBrush, Margin = new Thickness(6, 1) };
            Grid.SetColumn(value, 1); grid.Children.Add(value);
            _rows.Children.Add(grid);
        }
    }

    public void HideAndSave() { SavePosition(); Hide(); }

    private void RestorePosition()
    {
        var (left, top) = PositionSetting();
        if (ScreenGuard.OnScreen(this, left, top, Width, 120)) Position = new PixelPoint((int)left, (int)top);
        else if (Screens.Primary is { } screen)
            Position = new PixelPoint(screen.WorkingArea.Right - (int)(Width * screen.Scaling) - 40,
                screen.WorkingArea.Y + 80 + 150 * (int)_kind);
        _savedPosition = Position;
    }

    private void SavePosition()
    {
        var p = _savedPosition;
        switch (_kind)
        {
            case BreakoutKind.Damage: _settings.BreakoutDamageLeft = p.X; _settings.BreakoutDamageTop = p.Y; break;
            case BreakoutKind.Healing: _settings.BreakoutHealingLeft = p.X; _settings.BreakoutHealingTop = p.Y; break;
            default: _settings.BreakoutPetLeft = p.X; _settings.BreakoutPetTop = p.Y; break;
        }
        _settings.Save();
    }

    private (double, double) PositionSetting() => _kind switch
    {
        BreakoutKind.Damage => (_settings.BreakoutDamageLeft, _settings.BreakoutDamageTop),
        BreakoutKind.Healing => (_settings.BreakoutHealingLeft, _settings.BreakoutHealingTop),
        _ => (_settings.BreakoutPetLeft, _settings.BreakoutPetTop),
    };

    private string ScopeSetting() => _kind switch
    {
        BreakoutKind.Damage => _settings.BreakoutDamageScope,
        BreakoutKind.Healing => _settings.BreakoutHealingScope,
        _ => _settings.BreakoutPetScope,
    };

    private void SetScopeSetting(string value)
    {
        if (_kind == BreakoutKind.Damage) _settings.BreakoutDamageScope = value;
        else if (_kind == BreakoutKind.Healing) _settings.BreakoutHealingScope = value;
        else _settings.BreakoutPetScope = value;
        _settings.Save();
    }

    private void PaintScope()
    {
        _fight.Foreground = _fightScope ? AppTheme.AccentBrush : AppTheme.DimBrush;
        _session.Foreground = _fightScope ? AppTheme.DimBrush : AppTheme.AccentBrush;
    }
}
