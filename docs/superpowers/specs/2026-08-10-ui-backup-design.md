# Character UI backup

**Date:** 2026-08-10
**Status:** approved, ready for implementation

## Problem

EverQuest keeps a character's entire UI in a handful of ini files in the install root, and
rewrites them when the client exits. Anything that resets a window layout - a stray /loadskin,
a patch, a crash mid-save, a mistaken drag - is unrecoverable unless a copy exists.

Evidence from the dev box on 2026-08-10: the only backup present was hand-made and a month
stale. `UI_Daggo_freeport_LO1_Backup_1.ini` was 34,983 bytes from 2026-07-07 against a live
file of 58,950 bytes. Restoring it would have discarded a month of layout work.

## What is captured

The EQ root is the parent of the configured log folder, so a Wine prefix needs no extra
configuration (the dev box install lives under
`/mnt/Data4TB/Games/everquest/prefix/drive_c/users/Public/Daybreak Game Company/Installed
Games/EverQuest Legends/`).

| Pattern | Holds |
|---|---|
| `UI_<char>_<server>.ini` | window positions, sizes, colors, skin choice |
| `<char>_<server>.ini` | socials/macros, blocked spells, hotbars |
| `eqclient.ini` | display and video options, keymaps, text colors |
| `_characters.ini` | character list |

Excluded: `defaults.ini` (stock, not user data) and `*_Backup_*` (the user's own hand-made
copies - backing up backups is noise). The whole set is under 100 KB.

Custom skin folders under `uifiles/` are out of scope for v1. Only stock skins are installed,
and a single skin is ~200 MB, which would turn a 4 MB feature into a 10 GB one.

## Storage

`<profile>/ui-backups/<utc-timestamp>/` holding the **raw files** plus `manifest.json`
(taken-at, character, server, and each file's size and SHA-256).

Not an archive format. If EQBuddy is broken or gone, recovery must be `cp` and nothing more -
a backup tool that needs its own software to read is one that fails on the worst day.
Snapshots live in the profile rather than the game folder so a patcher or reinstall cannot
take them along.

## Capture

- Debounced watch: snapshot once the files have been quiet for ~5s, so a half-written file is
  never captured.
- A manual **Back up now** button.
- Content-hashed against the newest snapshot; an identical set is not stored again.
- Keep the newest 50 unique snapshots (~4 MB), pruning oldest beyond that.

## Restore

Whole snapshot, every file at once - it matches how the loss happens: one bad moment, one
rollback. Two rails:

1. The current files are captured as a safety snapshot first, so restore is itself undoable.
2. **Restore is refused while the client is live.** EQ holds UI state in memory and rewrites
   these files on exit, so restoring under a running client is silently overwritten and the
   tool looks broken. Liveness is decided by the existing log tail - a log that grew in the
   last 30 seconds means the client is running - which avoids guessing Wine process names.

## Structure

- `EQBuddy.Core/UiBackup/` - discovery, store, capture, restore, prune. No UI dependency.
- `EQBuddy.Avalonia` - a small "UI Backups" window: list of snapshots with time and size,
  Back up now, Restore.
- WPF is out of scope: that project cannot be compiled on this machine, and shipping it
  unverified is how the Linux sound picker went missing for a release.

## Testing

Core, against temp directories: discovery includes/excludes the right patterns; root derived
from the log folder; capture writes raw files plus manifest; an unchanged second capture is
skipped; a changed capture is stored; restore replaces files and takes a safety snapshot
first; restore throws while the client is live; prune keeps the newest N.
