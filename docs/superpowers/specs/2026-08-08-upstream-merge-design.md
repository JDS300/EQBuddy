# Merging upstream 1.29.1 → 1.42.0 into the Daggo fork

**Date:** 2026-08-08
**Branch:** `upstream-merge`, in the worktree `.claude/worktrees/upstream-merge`
**Upstream:** `https://github.com/DranakCorps-bot/EQBuddy` (remote `upstream`)

## The situation

The fork shares history with upstream. The merge base is `89b4167`, upstream's own
**1.29.1** release from 2026-08-04. This is a real merge, not a patch port.

| | commits | files changed |
|---|---|---|
| upstream ahead | 99 (to `3d7b153`, **1.42.0**) | 1192 |
| fork ahead | 27 | 34 (+4717 lines) |

A trial `git merge-tree` reports **12 conflicted files**. That number understates the
problem: the marked conflicts are the easy part. The danger is the files git merges
*without* complaint — upstream put +441 lines into `src/EQBuddy.Avalonia/MainWindow.cs`
while the fork put in +243, and an auto-merge there can produce a file that compiles with
duplicated wiring.

## Decisions taken

These were settled with the user before design; they are inputs, not open questions.

1. **Fork posture: keep local work, take upstream's new features.** Where both sides solved
   the same problem, the fork's implementation wins unless noted below. Upstream's
   genuinely-new features (item wiki, AA ledger, breakouts, quest tracker, zoom) come in
   alongside.
2. **Global hotkeys: keep the fork's, defaulted to unbound.** Upstream deleted
   `X11HotkeyService.cs` in 1.34.0 because global hotkeys stole `Ctrl+Shift+T` from every
   browser. That complaint applies on Linux too (Firefox and Chrome both bind it to "reopen
   closed tab"). The fork keeps the service and its crash fix, but ships with no hotkey bound
   until the user opts in via Options.
3. **Versioning: fold the fork's changelog into one entry.** The fork's `1.30.0` and
   `1.31.0`–`1.31.5` describe different features than upstream's identically-numbered
   releases. Upstream's changelog is taken verbatim; the fork's six entries collapse into a
   single `1.42.1-Daggo` entry covering HoT tracking, mez learning, and the chip stacks.
4. **Target: 1.42.0**, not the 1.41.0 the user initially named. Upstream shipped 1.42.0
   ("the wiki learns back") on 2026-08-08.

## Approach: four batch merges with a semantic audit

Conflicts cluster by release. Merging tag by tag (13 merges) is mostly ceremony — nine of
those releases introduce no conflict at all. Merging all 99 commits at once gives no
diagnostic purchase when the app misbehaves. So: **four merges, cut on the conflict
boundaries**, each landing exactly one hard problem.

| Batch | Range | The one hard problem |
|---|---|---|
| 1 | `v1.29.2` → `v1.30.0` | Reconcile `MezTracker` |
| 2 | `v1.31.0` → `v1.33.0` | `AppSettings` union; drop the duplicated Options-scroll fix |
| 3 | `v1.34.0` → `v1.37.0` | Re-graft the hotkey service onto upstream's rewritten `MainWindow` |
| 4 | `v1.38.0` → `v1.42.0` | Pick between two independent `SpawnsWindow` implementations |

Work happens in a git worktree so the user's `main` stays untouched and the whole attempt
remains abandonable. `rerere` is enabled so repeated resolutions replay themselves.

### Batch 1 — `v1.29.2` → `v1.30.0`

`MezTracker.cs` is the hardest file in the merge and the only one requiring genuine
reconciliation rather than a side-pick. Both sides did substantive correctness work on one
algorithm, driven by real field reports:

- **Fork (+433):** one attack round is one mez break; learn durations from the cluster
  rather than the longest outlier; retire the retraction rule so every rank can learn; a
  chanter's own mez fade stops teaching other chanters' spell ranks.
- **Upstream (+101):** mez learning snaps to the server tick; a 7-second "Mesmerize" taught
  by an early break no longer poisons chips; measured clocks outrank re-kill learning; the
  awake ledger lets a woken twin fight without erasing its sleeping siblings.

Neither set may be dropped — each fixes a bug the other does not. The gate is both test
suites passing **together**: `MezTrackerTests.cs` from both sides, unioned.

Also in this batch: `LogParser.cs` (auto-merge, audit), and the WPF `MainWindow.xaml.cs`
(see *Known limitation* below).

### Batch 2 — `v1.31.0` → `v1.33.0`

Brings the item wiki, AA ledger, breakout windows, and target drops. Mostly new files, so
the conflict surface is thin:

- `AppSettings.cs` — union merge, both sides added independent settings.
- `OptionsWindow.cs` — the fork's `8ccfb87` (Options scrolls instead of growing off the
  bottom) and upstream's `c8c98cf` (fix Options inability to scroll) are the same fix.
  **Take upstream's, delete the fork's**, per decision 1's "same problem" clause — one fewer
  divergent hunk in a file batch 3 also touches.
- `FeatureGuide.md`, `LogWatcher.cs` — audit.

### Batch 3 — `v1.34.0` → `v1.37.0`

Upstream's 1.34.0 deleted `X11HotkeyService.cs` outright and stripped the `Hotkey*`
properties from `AppSettings` (leaving a comment noting old `settings.json` files still
deserialize fine). Per decision 2, restore all of it:

1. Keep the fork's `X11HotkeyService.cs` (resolve modify/delete as *keep ours*) and
   `HotkeyServiceTests.cs`.
2. Re-add the `Hotkey*` properties to `AppSettings`, **with empty-string defaults** so
   nothing is bound out of the box.
3. Re-graft 6 call sites into upstream's rewritten `MainWindow.cs`: the `_hotkeys` field,
   the `RegisterGlobalHotkeys()` call in startup, the method itself with its four bindings
   (overlay toggle, click-through, mini mode, camp marker), the click-through status string
   that names the bound key, and `_hotkeys?.Dispose()` in teardown.
4. `RegisterGlobalHotkeys()` must no-op cleanly when every binding is empty, and the
   click-through status string must not name a key that isn't bound.

This is the seam most likely to re-conflict on future merges, since `MainWindow.cs` is
upstream's most-churned file. That cost was accepted knowingly.

### Batch 4 — `v1.38.0` → `v1.42.0`

**The `SpawnsWindow` / `SpawnChipsWindow` add/add conflict.** Both sides independently wrote
files with these names, and they converged — both have `_signature`, `_syncingZone`,
`RefreshRows`, `Rebuild`, `DarkBox`, `RowButton`, `Kick`, `OnZonePicked`,
`IsInteractiveControl`. Git cannot merge them; it is pick-one-then-graft.

- Take **the fork's** (543 lines vs upstream's 432). It is a superset, adding
  `CommitDuration`, `OnAddCustom`, `ApplyHeightLimit`, `PositionFromSettings`, and custom
  spawn entries.
- Port across upstream's `_lastVisiblePosition` / `_haveVisiblePosition` tracking in
  `SpawnChipsWindow`, which the fork lacks.
- Reconcile visibility: upstream made both `public`, the fork has them `internal`. Match
  whatever upstream's new call sites require.
- `OptionsRenderTests.cs` and `WidgetRenderTests.cs` — union both sides' tests.

Then the tail: zoom, the quest tracker, and the Linux updater pointing at the tarball asset
rather than `EQBuddySetup.exe` (`529ff19`) — the last matters directly to this fork.

Finally the version reconciliation per decision 3:
- `Directory.Build.props` → `<Version>1.42.1</Version>`,
  `<InformationalVersion>1.42.1-Daggo</InformationalVersion>`. The `-Daggo` marker must
  never enter `<Version>`: `release.ps1` and `install-local.ps1` both match
  `<Version>([\d.]+)</Version>` and throw on a suffix.
- `WhatsNew.json` → upstream's list verbatim, plus one `1.42.1` entry at the top for the
  fork's features.

## Verification

Each batch must pass, before the next begins:

```
dotnet build src/EQBuddy.Avalonia/EQBuddy.Avalonia.csproj -c Release
dotnet test tests/EQBuddy.Tests/EQBuddy.Tests.csproj -c Release
dotnet test tests/EQBuddy.Avalonia.Tests/EQBuddy.Avalonia.Tests.csproj -c Release
```

`dotnet build EQBuddy.slnx` is **not** used — it pulls in the `net10.0-windows` WPF project,
which cannot build on this box. `dotnet test` can exit 0 despite a catastrophic failure, so
**read the test count**, never the exit code. A baseline run on `main` establishes the
pre-merge pass count; every batch is measured against it.

**The semantic audit is a required step, not a review nicety.** After each batch, every file
where both sides changed gets read — *including those git merged silently*. The marked
conflicts get human attention automatically; the auto-merges are where this class of merge
actually fails.

**Real-app gate, after batch 4.** A green `EQBuddy.Avalonia.Tests` run is not sufficient
evidence for anything touching X11 or window lifecycle — this has bitten twice with the
suite fully green. Publish, launch, drive, and read `$EQBUDDY_APPDATA/error.log`:

```
dotnet publish src/EQBuddy.Avalonia/EQBuddy.Avalonia.csproj -c Release \
  -r linux-x64 --self-contained -p:PublishSingleFile=true
```

Checks: the widget draws; saved position restores; Options opens and scrolls; the spawn and
chip windows open and position themselves; no hotkey is bound by default and binding one in
Options works; the update banner points at the tarball. Run against a copy of the real log,
never the user's live one. `EQBUDDY_APPDATA` must point at a scratch profile so the user's
real `~/.config/EQBuddy/` is untouched.

## Known limitation: WPF ships unverified

`src/EQBuddy/MainWindow.xaml.cs` conflicts in batch 1 and targets `net10.0-windows`, which
cannot compile on this box. It will be resolved by taking upstream's structure and
re-applying the fork's HoT and mez hooks by hand, then read carefully — but it ships without
a compiler ever having seen it. This is inherent to the environment, not a shortcut, and the
user does not run Windows. Should the fork ever go upstream, this file needs a real Windows
build first.

## Out of scope

- Upstreaming any fork feature. Structuring the result for clean PRs was explicitly declined.
- Refactoring beyond what conflict resolution requires.
- Any change to the user's live `~/.config/EQBuddy/` profile or real log file.
