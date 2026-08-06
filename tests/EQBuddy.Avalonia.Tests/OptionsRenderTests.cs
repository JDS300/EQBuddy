using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using EQBuddy.Core;
using EQBuddy.UI.Shared;

namespace EQBuddy.Avalonia.Tests;

/// <summary>
/// The Options window on Linux, rendered headlessly.
///
/// This is where the Avalonia port drifted furthest from WPF without anyone noticing: rules
/// had a boolean "sound on/off" toggle where Windows had a per-rule sound picker, so the
/// recommended way to use delayed alerts — two rules on one match, a quiet "heard it" and a
/// loud "cast now" — was silently useless on Linux. Nothing failed; the option simply wasn't
/// there. These tests assert the controls exist rather than trusting that they do.
/// </summary>
[Collection("avalonia")]
public class OptionsRenderTests : IDisposable
{
    private readonly string _profile =
        Directory.CreateTempSubdirectory("eqbuddy-options-").FullName;

    public OptionsRenderTests()
    {
        Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", _profile);
        Directory.CreateDirectory(Path.Combine(_profile, "logs"));
        // A rule of each interesting shape, so the editor has something to draw.
        File.WriteAllText(Path.Combine(_profile, "settings.json"),
            $$"""
              {
                "LogFolder": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_profile, "logs"))}},
                "TruncateLogs": false, "ShowTutorial": false, "Theme": "ParchmentBrass",
                "_comment": "DefaultRulesVersion is set so loading doesn't inject the built-in CC broke rule and change the rule count out from under these tests",
                "DefaultRulesVersion": 1,
                "TrackedRules": [
                  { "Name": "heard it", "Pattern": "CH -->", "Kind": 6, "Enabled": true,
                    "AlertBanner": true, "AlertSound": true, "AlertSoundName": "Ding" },
                  { "Name": "CAST NOW", "Pattern": "CH -->", "Kind": 6, "Enabled": true,
                    "AlertBanner": true, "AlertSound": true, "AlertSoundName": "Alarm",
                    "AlertDelaySeconds": 2.5 }
                ]
              }
              """);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EQBUDDY_APPDATA", null);
        try { Directory.Delete(_profile, recursive: true); } catch { /* best effort */ }
    }

    private static (MainWindow Main, OptionsWindow Options) Open()
    {
        var main = new MainWindow();
        main.Show();
        var options = new OptionsWindow(main);
        options.Show();
        return (main, options);
    }

    [AvaloniaFact]
    public void OptionsRendersAFrame()
    {
        var (main, options) = Open();

        var frame = options.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.True(frame!.Size.Width > 100, $"Options rendered only {frame.Size.Width}px wide");
        options.Close();
        main.Close();
    }

    /// <summary>Each rule offers a real sound choice, not just on/off — and the two rules in
    /// the fixture keep their different sounds.</summary>
    [AvaloniaFact]
    public void EachRuleHasItsOwnSoundPicker()
    {
        var (main, options) = Open();

        var soundPickers = options.GetVisualDescendants().OfType<ComboBox>()
            .Where(c => c.Items.Contains(AlertSoundCatalog.CustomChoice))
            .ToList();

        Assert.Equal(2, soundPickers.Count);   // one per rule
        Assert.NotEqual(soundPickers[0].SelectedIndex, soundPickers[1].SelectedIndex);
        options.Close();
        main.Close();
    }

    /// <summary>The delay box is present and shows what was saved — the entry point for the
    /// cue feature.</summary>
    [AvaloniaFact]
    public void TheDelayBoxShowsTheSavedValue()
    {
        var (main, options) = Open();

        var texts = options.GetVisualDescendants().OfType<TextBox>()
            .Select(t => t.Text ?? "").ToList();

        Assert.Contains("2.5", texts);
        options.Close();
        main.Close();
    }

    /// <summary>Every watch-rule kind is offered here too — a kind that exists in Core but
    /// never reaches the Linux dropdown is unreachable for those users.</summary>
    [AvaloniaFact]
    public void EveryWatchKindIsOffered()
    {
        var (main, options) = Open();

        var kindPicker = options.GetVisualDescendants().OfType<ComboBox>()
            .First(c => c.Items.Contains(OptionsViewModel.KindNames[0]));

        Assert.Equal(Enum.GetValues<WatchKind>().Length, kindPicker.Items.Count);
        options.Close();
        main.Close();
    }

    /// <summary>The bug report this pins: "when I select spell fade, I don't see any other
    /// option for class as a drop down." A SpellFade rule needs its class-filter combo
    /// (By name.../Any spell/Any CC/Charm/Mez/Root/Lull/Stun/HoT) visible and populated;
    /// every other kind must not show it. Every rule row builds one of these combos
    /// regardless of kind (it just toggles IsVisible), so the fixture uses one rule of
    /// each shape and asserts on IsVisible rather than presence in the tree — a bare
    /// GetVisualDescendants().OfType&lt;ComboBox&gt;() would find both.</summary>
    [AvaloniaFact]
    public void ASpellFadeRuleShowsTheClassFilterComboAndANonFadeRuleDoesNot()
    {
        var main = new MainWindow();
        main.Show();
        main.Settings.TrackedRules.Clear();
        main.Settings.TrackedRules.Add(new TrackedRule
        {
            Name = "mez wears off", Kind = WatchKind.SpellFade, SpellFilter = EQBuddy.Core.SpellFilter.Mesmerize,
        });
        main.Settings.TrackedRules.Add(new TrackedRule { Name = "ore", Kind = WatchKind.Loot });

        var options = new OptionsWindow(main);
        options.Show();

        // Every rule row builds this combo (populated from the same array the WPF picker
        // uses), so both rows produce one — identified by its "By name..." item rather than
        // by index, per the class combo's own item text rather than guessing position among
        // the theme picker, the recent-rate window, and each row's sound picker.
        var classCombos = options.GetVisualDescendants().OfType<ComboBox>()
            .Where(c => c.Items.Contains(OptionsViewModel.SpellFilterNames[0]))
            .ToList();
        Assert.Equal(2, classCombos.Count);

        var fadeCombo = classCombos.Single(c => c.IsVisible);
        var lootCombo = classCombos.Single(c => c != fadeCombo);
        Assert.False(lootCombo.IsVisible);

        Assert.Equal(OptionsViewModel.SpellFilterNames.Length, fadeCombo.Items.Count);
        Assert.Equal((int)EQBuddy.Core.SpellFilter.Mesmerize, fadeCombo.SelectedIndex);
        Assert.Equal("Watch one named spell, or a whole class of spells", ToolTip.GetTip(fadeCombo));

        options.Close();
        main.Close();
    }

    /// <summary>The other half of the bug fix: a class filter needs no match text, so the
    /// text box hides while the combo takes its place — but switch back to "By name..."
    /// and the text box has to reappear, live, without reopening the window.</summary>
    [AvaloniaFact]
    public void ChoosingAClassFilterHidesTheMatchTextBoxAndByNameBringsItBack()
    {
        var main = new MainWindow();
        main.Show();
        main.Settings.TrackedRules.Clear();
        main.Settings.TrackedRules.Add(new TrackedRule
        {
            Name = "mez wears off", Pattern = "", Kind = WatchKind.SpellFade,
            SpellFilter = EQBuddy.Core.SpellFilter.Mesmerize,
        });

        var options = new OptionsWindow(main);
        options.Show();

        var classCombo = options.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.Items.Contains(OptionsViewModel.SpellFilterNames[0]));
        var matchArea = (Grid)classCombo.Parent!;
        var matchBox = matchArea.Children.OfType<TextBox>().Single();

        Assert.False(matchBox.IsVisible);   // class filter already selected: no text needed

        classCombo.SelectedIndex = (int)EQBuddy.Core.SpellFilter.ByName;
        Assert.True(matchBox.IsVisible);
        Assert.Equal(EQBuddy.Core.SpellFilter.ByName, main.Settings.TrackedRules[0].SpellFilter);

        classCombo.SelectedIndex = (int)EQBuddy.Core.SpellFilter.Charm;
        Assert.False(matchBox.IsVisible);
        Assert.Equal(EQBuddy.Core.SpellFilter.Charm, main.Settings.TrackedRules[0].SpellFilter);

        options.Close();
        main.Close();
    }

    /// <summary>The bug report: "In the Spell fade, by name doesn't let me type or put
    /// anything in, so I can't add a custom spell like Clarity." IsVisible was true the
    /// whole time — the row simply had no room. The rule row's fixed 92 + 115 px name
    /// columns and its five auto columns of toggles left the match cell 77 px in a 520 px
    /// body, less than the class combo's own 104 px, so both the combo and the text box
    /// spilled out of their cell and were painted over by the P/B toggles and the sound
    /// picker that come after them in the row. Clicks landed on whatever sat on top, so
    /// the box could never take focus. Geometry, not visibility, so the assertions are
    /// about geometry: the cell contents stay inside the cell, the box keeps a width you
    /// can type a spell name into, and a click in the middle of it reaches the box.</summary>
    [AvaloniaFact]
    public void ASpellFadeRuleWatchingANamedSpellHasAMatchBoxYouCanActuallyClick()
    {
        var main = new MainWindow();
        main.Show();
        main.Settings.TrackedRules.Clear();
        main.Settings.TrackedRules.Add(new TrackedRule
        {
            Name = "Clarity", Pattern = "Clarity", Kind = WatchKind.SpellFade,
            SpellFilter = EQBuddy.Core.SpellFilter.ByName,
        });

        var options = new OptionsWindow(main);
        options.Show();
        options.CaptureRenderedFrame();   // force a layout pass, so Bounds are real

        var classCombo = options.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.Items.Contains(OptionsViewModel.SpellFilterNames[0]));
        var matchArea = (Grid)classCombo.Parent!;
        var matchBox = matchArea.Children.OfType<TextBox>().Single();
        var row = (Grid)matchArea.Parent!;

        // Wide enough for a spell name, not just a couple of characters.
        Assert.True(matchBox.Bounds.Width >= 80,
            $"match box is only {matchBox.Bounds.Width}px wide");

        // Nothing in the match cell may reach into the toggles that follow it: that
        // overlap is what swallowed the clicks.
        var pinToggle = row.Children.OfType<ToggleButton>().First();
        var cellRight = matchArea.Bounds.Right;
        Assert.True(matchBox.TranslatePoint(new Point(matchBox.Bounds.Width, 0), row)!.Value.X <= cellRight,
            $"match box runs past its {cellRight}px cell to "
            + $"{matchBox.TranslatePoint(new Point(matchBox.Bounds.Width, 0), row)!.Value.X}px");
        Assert.True(classCombo.Bounds.Width <= matchArea.Bounds.Width,
            $"class combo is {classCombo.Bounds.Width}px in a {matchArea.Bounds.Width}px cell");
        Assert.True(cellRight <= pinToggle.Bounds.Left,
            $"match cell ends at {cellRight}px, past the P toggle at {pinToggle.Bounds.Left}px");

        // The symptom itself: a click in the middle of the box has to reach the box.
        var centre = matchBox.TranslatePoint(
            new Point(matchBox.Bounds.Width / 2, matchBox.Bounds.Height / 2), options)!.Value;
        var hit = options.InputHitTest(centre);
        Assert.True(hit is Visual v && (v == matchBox || v.GetVisualAncestors().Contains(matchBox)),
            $"a click at {centre} landed on {hit?.GetType().Name ?? "nothing"}, not the match box");

        options.Close();
        main.Close();
    }

    /// <summary>The same cell with a class filter chosen: the combo spans the whole cell
    /// then, and it has to fit there rather than spilling over the toggles beside it.</summary>
    [AvaloniaFact]
    public void AClassFilterComboFitsInsideItsCell()
    {
        var main = new MainWindow();
        main.Show();
        main.Settings.TrackedRules.Clear();
        main.Settings.TrackedRules.Add(new TrackedRule
        {
            Name = "mez broke", Kind = WatchKind.SpellFade,
            SpellFilter = EQBuddy.Core.SpellFilter.Mesmerize,
        });

        var options = new OptionsWindow(main);
        options.Show();
        options.CaptureRenderedFrame();

        var classCombo = options.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.Items.Contains(OptionsViewModel.SpellFilterNames[0]));
        var matchArea = (Grid)classCombo.Parent!;
        var row = (Grid)matchArea.Parent!;
        var pinToggle = row.Children.OfType<ToggleButton>().First();

        Assert.True(classCombo.Bounds.Width <= matchArea.Bounds.Width,
            $"class combo is {classCombo.Bounds.Width}px in a {matchArea.Bounds.Width}px cell");
        Assert.True(matchArea.Bounds.Right <= pinToggle.Bounds.Left,
            $"match cell ends at {matchArea.Bounds.Right}px, past the P toggle at {pinToggle.Bounds.Left}px");

        options.Close();
        main.Close();
    }

    /// <summary>The class combo's visibility also has to track the *kind* combo, live —
    /// switching a rule into Spell fade must reveal it immediately, and switching back out
    /// must hide it again, all without closing and reopening the window.</summary>
    [AvaloniaFact]
    public void SwitchingTheKindComboToSpellFadeRevealsTheClassComboImmediately()
    {
        var main = new MainWindow();
        main.Show();
        main.Settings.TrackedRules.Clear();
        main.Settings.TrackedRules.Add(new TrackedRule { Name = "ore", Kind = WatchKind.Loot });

        var options = new OptionsWindow(main);
        options.Show();

        var classCombo = options.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.Items.Contains(OptionsViewModel.SpellFilterNames[0]));
        var kindCombo = options.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.Items.Contains(OptionsViewModel.KindNames[0]));

        Assert.False(classCombo.IsVisible);   // starts as Loot: no class filter to show

        kindCombo.SelectedIndex = (int)WatchKind.SpellFade;
        Assert.True(classCombo.IsVisible);
        Assert.Equal(WatchKind.SpellFade, main.Settings.TrackedRules[0].Kind);

        kindCombo.SelectedIndex = (int)WatchKind.Kill;
        Assert.False(classCombo.IsVisible);
        Assert.Equal(WatchKind.Kill, main.Settings.TrackedRules[0].Kind);

        options.Close();
        main.Close();
    }

    /// <summary>The bug report: with the watch-rules section expanded (a long rule list
    /// plus the worked-examples guide), this undecorated window used to grow past the
    /// screen's usable height — carrying its own title bar and close button out of reach
    /// with it, since there's no OS chrome to fall back on. Loaded with far more rules
    /// than any screen has room for, unclamped this content would run to several thousand
    /// pixels tall; the fix is a body ScrollViewer plus a work-area height clamp, so the
    /// window itself must stay put and the overflow must land in the scroller instead.</summary>
    [AvaloniaFact]
    public void TheWindowStaysWithinTheScreenWhenTheWatchSectionIsFullyExpanded()
    {
        var main = new MainWindow();
        main.Show();

        // Far more rows than fit on any real screen, plus the guide panel — both set
        // before OptionsWindow exists so its constructor builds them expanded, the same
        // as a returning user whose guide preference and rule list were already large.
        for (var i = 0; i < 40; i++)
            main.Settings.TrackedRules.Add(new TrackedRule { Name = $"rule{i}", Pattern = "x" });
        main.Settings.ShowWatchGuide = true;

        var options = new OptionsWindow(main);
        options.Show();

        var frame = options.CaptureRenderedFrame();
        var screen = options.Screens.ScreenFromWindow(options) ?? options.Screens.Primary;
        Assert.NotNull(screen);
        var workingHeight = screen!.WorkingArea.Height / screen.Scaling;

        Assert.NotNull(frame);
        // The clamp, not luck: 40 rules plus the guide is enough content that an
        // unclamped window would dwarf any real work area, so this only holds if the
        // MaxHeight ceiling is actually being applied.
        Assert.True(frame!.Size.Height <= workingHeight,
            $"Options window rendered {frame.Size.Height}px tall against a {workingHeight}px work area");

        // The overflow has to go somewhere — it should be sitting in the body
        // ScrollViewer's hidden extent, not just... not existing. Walked as a direct
        // child of the chrome Grid rather than GetVisualDescendants, because TextBox's
        // own control template wraps its text presenter in a ScrollViewer too, and with
        // 40 rule rows there are dozens of those in the tree alongside the one that
        // actually owns the body.
        var chromeGrid = (Grid)((Border)options.Content!).Child!;
        var bodyScroll = chromeGrid.Children.OfType<ScrollViewer>().Single();
        Assert.True(bodyScroll.Extent.Height > bodyScroll.Viewport.Height,
            $"body extent {bodyScroll.Extent.Height}px does not exceed its {bodyScroll.Viewport.Height}px viewport — nothing to scroll");
        Assert.Equal(ScrollBarVisibility.Disabled, bodyScroll.HorizontalScrollBarVisibility);

        // The close button lives in the fixed title row, outside the ScrollViewer — it
        // must stay near the top of the window regardless of how tall the body content
        // grows underneath it, not get pushed down or clamped off the bottom.
        var close = options.GetVisualDescendants().OfType<Button>()
            .First(b => ToolTip.GetTip(b) as string == "Close");
        var closeTop = close.TranslatePoint(new Point(0, 0), options);
        Assert.True(closeTop is { Y: >= 0 and < 80 },
            $"close button top landed at {closeTop}, expected near the top of the window");

        options.Close();
        main.Close();
    }
}
