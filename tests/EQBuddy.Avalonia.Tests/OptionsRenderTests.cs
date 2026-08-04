using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
