# Chip scaling on Linux

**Date:** 2026-08-10
**Status:** approved, ready for implementation

## Problem

On Linux the chip windows cannot be sized independently of the widget.

`AppSettings.ChipScale` has existed in Core all along (clamped 0.5-2.0) and the shared
`OptionsViewModel` exposes it. The WPF app honors it: `ChipScale.Apply` puts a
`LayoutTransform` on the mez, spawn and alert windows, driven by its own Options slider.

The Avalonia app ignores it. `MainWindow.ApplyUiScale` pushes `UiScale` into all three chip
windows (lines 875-877), and `ShowStack` starts a freshly built stack at `_settings.UiScale`.
So chips are welded to the widget's Size slider, and the only way to enlarge chips is to
enlarge the whole widget. The alert tile is scaled by nothing at all.

## Decisions

Two forks were settled before design:

1. **Replace, not multiply.** The chip slider is an absolute scale that supersedes `UiScale`
   for chips, matching WPF, rather than a multiplier layered on top of it. This requires a
   one-time migration (below) or every existing user's chips would jump to 100% on upgrade.
2. **The alert tile is part of the chip family.** It is unscaled today, so it will render at
   the migrated chip scale after upgrade - visibly larger for anyone whose `UiScale` is above
   1.0. Accepted deliberately: "all chips should be able to be scaled".

## Design

### 1. Migration

New `AppSettings.ChipScaleFromUiScaleMigrated` flag plus a migration method shaped like the
existing `HotkeysUnboundMigrated` one (returns whether it changed anything). It sets
`ChipScale = UiScale`, clamped to the slider range, exactly once.

**The migration must not run inside `AppSettings.Load`.** `Load` is a pure read; a destructive
migration placed there once emptied the developer's live profile from a test run. It is
invoked from `MainWindow` startup, like the hotkey unbind migration.

Effect: chips keep their current size across the upgrade. The alert tile changes size, per
decision 2.

### 2. Wiring

- `ApplyUiScale` stops feeding the chip windows.
- New `MainWindow.ApplyChipScale(double)` feeds `_mezWindow`, `_hotWindow`, `_chipsWindow`
  and the alert tile.
- `ShowStack` starts a new stack at `_settings.ChipScale`.
- New `MainWindow.SetChipScale(double)` mirrors `SetUiScale`: clamp, persist, apply live.
- `AlertWindow` gains the same `LayoutTransformControl` idiom the three chip windows already
  use - `_scaleRoot.Child = <existing Border>`, `Content = _scaleRoot`, plus `ApplyScale`.
  It is lazily constructed (`AlertTile => _alertWindow ??= new AlertWindow(...)`), so it must
  pick up the current chip scale at construction, not only on slider moves.

### 3. Options UI

A "Chip size" slider directly under the existing Size slider, range **0.5-2.0** with a live
percentage label, applied instantly and saved - the same interaction the Size slider already
has.

Range note: WPF's slider floors at 0.8, but `UiScale` allows 0.5. Since migration copies
`UiScale` into `ChipScale`, a 0.6 widget scale must be representable or the value cannot be
round-tripped through the UI. The Linux slider therefore matches `UiScale`'s 0.5-2.0.

Dim note under the slider: "Hot, mez, spawn and alert chips. Watch chips follow Size."

### 4. What does not change

Watch chips render inside the mini dashboard in `MainWindow`, not as floating windows, so they
continue to follow `UiScale`. Saying so in the UI is the entire fix for that asymmetry.

## Testing

Headless Avalonia and Core tests:

| Case | Expected |
|---|---|
| Migration on a profile with `UiScale` 1.3, `ChipScale` 1.0 | `ChipScale` becomes 1.3, flag set |
| Migration run a second time | no change (idempotent) |
| Migration when the user already set `ChipScale` | flag respected, value untouched |
| `AppSettings.Load` on a pre-migration profile | pure read: nothing written, `ChipScale` unchanged on disk |
| `SetChipScale` | clamps, persists, and resizes chips live |
| `ApplyUiScale` | no longer changes chip window scale |
| Options | "Chip size" slider exists and drives `SetChipScale` |
| Alert tile | scales with chip scale, including when constructed after the scale was set |

Real-app verification: widget and chips at deliberately different scales, screenshotted per
window. No input synthesis - the desktop-freeze hook forbids it, and clicks must be asked for.

## Files

- `src/EQBuddy.Core/AppSettings.cs` - flag + migration method
- `src/EQBuddy.Avalonia/MainWindow.cs` - `ApplyChipScale`, `SetChipScale`, `ShowStack`, startup
- `src/EQBuddy.Avalonia/AlertWindow.cs` - scale root + `ApplyScale`
- `src/EQBuddy.Avalonia/OptionsWindow.cs` - slider
- `tests/EQBuddy.Tests/ChipScaleMigrationTests.cs` (new)
- `tests/EQBuddy.Avalonia.Tests/ChipWindowRenderTests.cs` / `OptionsRenderTests.cs` (extend)
