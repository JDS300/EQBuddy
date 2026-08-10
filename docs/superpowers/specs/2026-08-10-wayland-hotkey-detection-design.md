# Wayland hotkey detection

**Date:** 2026-08-10
**Status:** approved, ready for implementation

## Problem

On a Wayland session EQBuddy accepts global hotkey bindings that can never fire, and then
misreports why.

The fork keeps global hotkeys (upstream deleted them in 1.34) and ships every binding empty.
`X11HotkeyService` implements them with `XGrabKey` on the X11 root window. Under Wayland the
compositor owns global input and never routes those keys to an X11 client, so a grab either
fails or succeeds and stays silent forever.

Two concrete failures observed on the dev box (CachyOS, KDE Plasma, `XDG_SESSION_TYPE=wayland`,
EQBuddy running under XWayland with `DISPLAY=:0`):

1. **The existing guard never fires.** `X11HotkeyService.cs:32` throws only when
   `WAYLAND_DISPLAY` is set *and* `DISPLAY` is unset. Under XWayland both are set, so the guard
   passes and registration proceeds as if X11 were available.
2. **The failure message blames the wrong thing.** The user's one binding, `Ctrl+Alt+K`, has
   been logging `Hotkey 'Ctrl+Alt+K' is already taken by another application` since it was set.
   That message comes from the `BadAccess` trap. It is technically true — kwin holds the
   combination — but it sends the user hunting for a conflicting app instead of telling them
   the session type is the problem.

The user's camp-marker hotkey has therefore never worked on this machine, and nothing in the UI
said so.

## Non-goals

- Making global hotkeys actually work on Wayland. That needs the XDG desktop portal
  GlobalShortcuts interface, a different implementation, and its own design.
- Changing what is stored in `settings.json`. Bindings are the user's data and stay untouched.
- Any change to `X11ClickThrough`. It uses the XFixes input-shape API, which XWayland honors,
  and it is unaffected.

## Design

### 1. Detection: `DesktopSession`

New static class in `src/EQBuddy.Avalonia/DesktopSession.cs`.

```csharp
internal static bool IsWayland(Func<string, string?> env)
```

Returns true when either holds:

- `XDG_SESSION_TYPE` equals `wayland`, case-insensitive, trimmed
- `WAYLAND_DISPLAY` is non-empty

A public parameterless overload delegates to `Environment.GetEnvironmentVariable`. The injected
lookup exists so the rule is testable headlessly without mutating process environment state.

`DISPLAY` deliberately plays no part. Its presence is what made the old guard useless.

### 2. Registration keeps running; only the diagnosis changes

Registration is **not** short-circuited on Wayland. Attempting the grab is cheap and harmless —
`XGrabKey` from an X client is not the hazard; XTEST input synthesis is — it self-corrects if
KDE later routes global shortcuts to XWayland clients, and skipping would disable the feature
with no way left to observe it working.

What changes:

- `MainWindow.RegisterGlobalHotkeys` logs one accurate line when at least one binding exists
  and `DesktopSession.IsWayland()` is true, before constructing the service.
- `X11HotkeyService.Register`'s `BadAccess` message becomes session-aware. On Wayland it names
  the compositor as the owner of global shortcuts instead of "another application".
- The existing `PlatformNotSupportedException` for a genuinely absent X display is unchanged.
  So is the `s_trapped` / `XSetErrorHandler` machinery around the grab.

### 3. Options UI

Under the four hotkey rows in `OptionsWindow.BuildAppearanceTab`, on Wayland only, an amber
warning line:

> ⚠ This is a Wayland session. Global hotkeys cannot fire here — the compositor never delivers
> them to EQBuddy. Bindings are still saved and will work in an X11 session.

The four text boxes stay **editable** and saved values stay untouched, so a binding typed here
still applies if the user logs into an X11 session. The existing dim explanatory note below the
rows is unchanged.

The control comes from a static factory, `WaylandHotkeyNote(bool isWayland)`, returning
`Control?` — `null` when not on Wayland. This keeps the text assertable in a headless test
without constructing an `OptionsWindow`.

## Testing

Headless xUnit in `tests/EQBuddy.Avalonia.Tests`:

| Case | Expected |
|---|---|
| `XDG_SESSION_TYPE=wayland` | `IsWayland` true |
| `XDG_SESSION_TYPE=x11`, `WAYLAND_DISPLAY` unset | false |
| both unset | false |
| `WAYLAND_DISPLAY=wayland-0` alone | true |
| `XDG_SESSION_TYPE=Wayland` (mixed case) | true |
| `DISPLAY=:0` set alongside `XDG_SESSION_TYPE=wayland` | true — the XWayland case that broke the old guard |
| `WaylandHotkeyNote(true)` | non-null, text names Wayland and says bindings are still saved |
| `WaylandHotkeyNote(false)` | null |

Existing `HotkeyServiceTests` must stay green.

**Verification constraints on this dev box:** check `ps -C EQBuddy.Avalonia` before running the
Avalonia tests — a running instance holds the X11 grabs. Real-app verification is visual only:
launch the app, open Options, read the warning. **No input synthesis of any kind** — XTEST froze
the desktop on 2026-08-10 and is now blocked by a PreToolUse hook.

## Files

- `src/EQBuddy.Avalonia/DesktopSession.cs` (new)
- `src/EQBuddy.Avalonia/X11HotkeyService.cs` (message wording)
- `src/EQBuddy.Avalonia/MainWindow.cs` (`RegisterGlobalHotkeys` logging)
- `src/EQBuddy.Avalonia/OptionsWindow.cs` (warning note)
- `tests/EQBuddy.Avalonia.Tests/DesktopSessionTests.cs` (new)
