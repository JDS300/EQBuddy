# Avalonia chip stacks — as built

**Status:** shipped on branch `avalonia-chip-stacks` (commit `a2c4ea5`), version 1.30.0.
Replaces the original mez-only PRD, which planned half this scope and got several details
wrong; its mistakes are recorded at the end rather than deleted, since each one would have
shipped a bug.

## What this closed

`MezTracker` and `SpawnTimers` were both fully written and tested in Core, but
`EQBuddy.Avalonia/MainWindow.cs` constructed its `LogWatcher` and never set `.Mez` or
`.Spawns`. Both trackers were **dead code on Linux**, and none of the three windows that
surface them existed there.

Two bullets left `docs/FeatureGuide.md`'s "Known limitations":

- the mez-target chips being WPF-only
- the Spawns window being WPF-only (issue #5)

The History DPS graph, the Custom-theme colour editor, and the History fight-by-fight
review remain WPF-only — untouched by this work.

## What shipped

| Piece | File | Role |
|---|---|---|
| Shared conversion | `src/EQBuddy.UI.Shared/MezChipPresentation.cs` | `MezState` → `SpawnChip`, used by **both** UIs |
| Mez stack | `src/EQBuddy.Avalonia/MezChipsWindow.cs` | one chip per active mez, counting down |
| Spawn stack | `src/EQBuddy.Avalonia/SpawnChipsWindow.cs` | ambient respawn countdowns |
| Zone browser | `src/EQBuddy.Avalonia/SpawnsWindow.cs` | edit durations, add custom named, start timers by hand |
| Integration | `src/EQBuddy.Avalonia/MainWindow.cs` | tracker wiring, tick, menu, lifecycle |
| Options | `src/EQBuddy.Avalonia/OptionsWindow.cs` | `TrackSpawns` toggle + `SyncTrackSpawns` |

### Public surfaces

```csharp
// EQBuddy.UI.Shared
public static class MezChipPresentation
{
    public static List<SpawnChip> Chips(IReadOnlyList<MezState> states, DateTime now);
}

// EQBuddy.Avalonia — both chip stacks expose the identical five members
internal sealed class MezChipsWindow : Window
{
    public MezChipsWindow(MainWindow owner, Func<DateTime, List<SpawnChip>> source);
    public void RefreshChips(DateTime now);
    public void ApplyScale(double scale);
    public void ApplyClickThrough(bool enabled);
    public void SavePosition();
}

internal sealed class SpawnChipsWindow : Window
{
    public SpawnChipsWindow(MainWindow owner, SpawnsViewModel vm);
    // … same five
}

// Owns its own 1s DispatcherTimer — deliberately NOT driven by MainWindow's tick,
// because it is a browser with its own lifetime, not an overlay.
internal sealed class SpawnsWindow : Window
{
    public SpawnsWindow(MainWindow main, SpawnsViewModel vm, string? initialZone = null);
}

// MainWindow additions
internal void ShowSpawnsWindow(string? zone = null);
internal void SetTrackSpawns(bool on);
```

### Wiring

In the `MainWindow` constructor, immediately after `_watcher = new LogWatcher(_stats)` and
**before** `FollowActiveCharacter()`:

```csharp
_mezTracker.AttachStore(Path.Combine(AppPaths.Dir, "mez-durations.json"));
_watcher.Mez = _mezTracker;
_watcher.Spawns = _spawnTimers;
```

Ordering is load-bearing. `Select()` replays the whole log, and both trackers key off log
timestamps, so the replay reconstructs their state exactly. Wire them *after* the replay and
the app starts blind to every mez and kill already in today's log.

`SpawnTimers.Server` needs no assignment here — `LogWatcher.Select` stamps it
(`LogWatcher.cs:124`), which is also why the assignment must precede log selection.

## Behaviour preserved

The mez display rules encode issue #32 decisions and were moved, not redesigned:

- same-named targets are numbered (`Wan ghoul knight (3)`); a unique target is unnumbered
- `"?"` when the spell's duration is unknown
- `IsDue` at ≤ 6s remaining, which flips the countdown and border to `WarnBrush`
- `💤` icon, empty `Zone` (mez chips belong to no zone list)

Spawn chips keep `⏳`, `"DUE"` in place of the countdown when due, double-click → zone list,
and single-click-when-due → `ClearTimer`.

## The three things that could not be ported from WPF

1. **`ShowActivated = false` on both chip stacks.** On X11 a topmost window that appears
   mid-fight steals keyboard focus from EverQuest. A stack that pops on every pull would
   fight the player for their keys. `AlertWindow` already guarded this; WPF never had to.
   This is the single most important line in either chip window.

2. **`IsLoaded` window-lifecycle guards.** WPF's `_mezWindow is not { IsLoaded: true }` has
   no safe Avalonia equivalent for a *closed* window. Both stacks instead use a nullable
   field cleared by a `Closed` handler, null the field *before* calling `Close()` so a
   handler cannot loop, call `SavePosition()` before every close, and are closed explicitly
   in `OnClosed` so app exit cannot strand them.

3. **`Keyboard.ClearFocus()` does not exist in Avalonia 12.1.** `IFocusManager` offers only
   `Focus`/`GetFocusedElement`/`TryMoveFocus`. `SpawnsWindow` parks focus on its chrome
   `Border` and sets a force-rebuild flag that bypasses the `IsKeyboardFocusWithin` guard —
   without it, clicking ▶ or 🔔 leaves focus inside the rows panel and the guard swallows the
   user's own duration edit forever. The forced rebuild destroys a focused `TextBox` whose
   `LostFocus` commits and re-enters, so it also needs a re-entrancy guard. WPF got away with
   this by ordering.

Smaller divergences: `OpenFileDialog` → `StorageProvider.OpenFilePickerAsync`;
`ScrollViewer.PanningMode` has no counterpart and was dropped (touch-only, no desktop loss);
`SystemParameters.WorkArea` → `MainWindow.UpdateWindowHeightLimit`'s
`WorkingArea.Height / Scaling` idiom; `ScrollViewer` deliberately left draggable, unlike
`OptionsWindow.IsInteractiveControl`, because excluding it removes the frame's only large
grab area.

**Theming note:** Avalonia `AppTheme` brushes are `static readonly SolidColorBrush`
instances mutated in place by `Apply()`. Assign them directly — there is no
`SetResourceReference` equivalent to reach for, and direct assignment already repaints on a
live theme switch.

## What the Linux stacks do that WPF's don't

- follow the UI-scale slider (`ApplyScale`, via a `LayoutTransformControl` matching
  `MainWindow.ApplyUiScale`)
- honour widget opacity
- join the global click-through toggle instead of staying permanently clickable

WPF's chip windows do none of these; this is a deliberate Linux-side improvement, not a
parity gap in the other direction.

## Version

`Directory.Build.props` carries a local-build marker:

```xml
<Version>1.30.0</Version>
<InformationalVersion>1.30.0-Daggo</InformationalVersion>
```

The suffix **must not** go in `<Version>`: `scripts/release.ps1:13` and
`scripts/install-local.ps1:12` both match `<Version>([\d.]+)</Version>` and would throw
`No <Version> in Directory.Build.props`. `UpdateChecker.CurrentVersion` reads
`AssemblyName.Version`, which strips the suffix anyway — so `UpdateChecker.DisplayVersion`
was added (reads `AssemblyInformationalVersionAttribute`, trims the `+<sha>` .NET appends,
falls back to `CurrentVersion`) and both gear menus now use it.

## Tests

`tests/EQBuddy.Tests/MezChipPresentationTests.cs` — 5 tests, real raw log lines through
`LogParser.Parse`, following `MezTrackerTests` conventions:
`TwoSameNamedMobsMezzedAtOnceGetNumberedChips`, `ASingleMezzedTargetHasNoNumberSuffix`,
`UnknownDurationRendersAQuestionMark`, `IsDueTurnsTrueAtSixSecondsRemainingAndNotBefore`,
`CountdownFormatsAsMinutesColonSeconds`.

`tests/EQBuddy.Avalonia.Tests/ChipWindowRenderTests.cs` — 6 headless `[AvaloniaFact]` render
tests covering all three windows, frame capture plus visual-tree chip counts, plus a due-state
test.

> Counting chip borders needs care: `GetVisualDescendants().OfType<Border>()` returns 4 for
> 2 chips, because Avalonia's Fluent `Window` template contributes two chrome borders of its
> own. The tests filter on `b.Parent is StackPanel` rather than weakening the assertion.

## Verification performed

```
dotnet build src/EQBuddy.Avalonia -c Release --no-incremental   → 0 errors
dotnet test  tests/EQBuddy.Tests -c Release                     → 523 passed
dotnet test  tests/EQBuddy.Avalonia.Tests -c Release            →  15 passed
```

`dotnet build EQBuddy.slnx` is **expected to fail on Linux** — the WPF project targets
`net10.0-windows`. Use the ubuntu CI job's three commands (`.github/workflows/ci.yml`).

Live replay against a real 690k-line log with timestamps shifted to the present
(`EQBUDDY_APPDATA` scratch profile, per FeatureGuide's "Testing without playing"):

- an AoE ghoul pull rendered 14 chips — `Wan ghoul knight (1)`…`(5)`, `Vis ghoul knight (1)`…`(4)`,
  `Urd ghoul wizard (1)`…`(3)`, and two unnumbered singles — from one cast, all at 0:29
- Befallen named produced spawn chips `Korven Nisere DUE` / `Baron Telyx V'Zher 3:56` /
  `Soldier of V'Zher 4:29`, persisted with `"Server":"freeport"`
- the duration learner wrote `{"Mesmerization III":36}` from real land→fade gaps (catalog
  base is 24s)
- the chip window mapped **without taking focus** while another window held it

## Known gaps

- **The WPF app has never been compiled.** Two one-line edits (the `MezChips` delegation and
  the `DisplayVersion` menu header) ship unbuilt; they need a Windows build before trusting.
- **Double-click chip → Spawns window is code-verified only.** The call is wired and the
  window renders headlessly, but no live click has exercised the path (no input-injection
  tooling on the dev box).
- **In-game focus behaviour unconfirmed.** The focus test used another desktop window, not
  EverQuest fullscreen. KWin sets `_NET_WM_STATE_DEMANDS_ATTENTION` on the chip window;
  with `SKIP_TASKBAR` also set this likely shows nothing, but taskbar flashing on every pull
  would trace back to it.

## Where the original PRD was wrong

Kept as a record, since each one would have produced broken or non-parity code:

| Original PRD | Reality |
|---|---|
| "feed events to `_mezTracker.Apply(...)` wherever other trackers already receive events — match that existing wiring pattern" | No such pattern existed. `.Spawns` and `.Mez` were both unset; wiring was written from scratch, with ordering that matters. |
| "copy the WPF `MezChips` logic verbatim" | It lived in WPF code-behind, already violating shared-first, and copying made the PRD's own testing deliverable unsatisfiable. Extracted to UI.Shared instead. |
| `_mezWindow is not { IsLoaded: true }` | Does not port — see divergence 2. |
| silent on focus | `ShowActivated = false` — the highest-risk omission. |
| silent on shutdown | `OnClosed` must close the new windows or exit strands. |
| silent on theming | `AppTheme` brushes mutate in place; assign directly. |
| scope: mez only | Expanded to spawn chips and the Spawns window, closing issue #5 in the same pass. |
