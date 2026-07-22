# Changelog

All notable changes to this fork are documented here. Versions track the
`v<MAJOR>.<MINOR>.<PATCH>` git tags; the GitHub release for each tag carries the
built `HuntAndPeck-<tag>.zip`.

## [Unreleased]

## [v2.2.0] — 2026-07-22

### Added
- **Label dim (backtick)**: drops label opacity to ~20% so the text behind is readable;
  press backtick again to restore. Keys stay captured, so labels remain typeable while
  dim. (Replaces a two-tone-outline read-mode that read as ugly/hard to read. Tradeoff:
  opacity-dim couples label contrast to the background, so dimmed labels are harder to
  see on dark surfaces — accepted, since base mode stays readable on dark.)
- **Suspend now hides labels (backslash)**: persistent suspend stops capturing keys and
  sets label opacity to 0 (was ~20% dim), leaving only the `SUSPENDED` status visible,
  so you can type into the app beneath (vimium, Excel) with zero key collision; clicks
  pass through (no dismiss). Resume by pressing the main hotkey again; Esc closes.
  Per-session.
- **Softer label pills**: the pill fill is α≈0.4 by default (was vivid solid yellow) —
  less glaring, background peeks through — while the text stays fully opaque, so labels
  stay crisp. Base mode is not dimmed canvas-wide.
- **Configurable pill opacity** (`HintPillOpacity`, hot-reload): percent 0-100 (default
  40) controlling the pill fill alpha; the label text stays fully opaque regardless.
  Exposed in the Options dialog.
- **Configurable dimmed opacity** (`HintDimOpacity`, hot-reload): percent 0-100 (default
  20) controlling the backtick-dim canvas opacity for reading the text behind. Exposed in
  the Options dialog.
- **Continuous is the default trigger mode** (`OverlayTriggerMode=Continuous`): the
  overlay now stays up for repeated clicks by default (Grid only); set `OneClick` to
  restore the close-after-one-click behavior.
- **Transparent status strip**: the bottom-center status badges are now semi-transparent
  (Opacity 0.75) so they don't fully obscure content behind them.
- **Decluttered overlay**: removed the top-left gesture legend; moved the click-mode and
  trigger-mode badges into one bottom-center status strip (over the empty taskbar
  middle). The click-mode badge hides while suspended so only `SUSPENDED` shows.
- **Capslock+f now toggles continuous mode**: Alt/Capslock held-state is now tracked
  from the raw hook events (not `GetAsyncKeyState`), so it detects a Capslock that
  AutoHotkey has neutralized for a custom combo. The 2nd `Capslock+f` reaches AHK and
  fires the toggle (previously the overlay's hook swallowed the physical `f` first).
- **Alt / Capslock passthrough**: while Alt or Capslock is held the overlay suspends
  key capture, so `Alt+Tab` (window switcher) + arrows and Capslock-based AutoHotkey
  mappings pass through. This lets you switch apps mid-overlay (the grid is screen-based)
  and makes an AHK-mapped hotkey (e.g. `Capslock+f` → `Ctrl+Shift+M`) toggle continuous
  mode on the 2nd press — previously the overlay's hook swallowed the physical key
  before AutoHotkey saw it. (Native `Capslock+F` via `RegisterHotKey` is impossible:
  Win32 doesn't accept Capslock as a modifier.)
- **Continuous trigger mode** (`OverlayTriggerMode`, hot-reload; Grid only): the
  overlay can stay up for repeated clicks until Esc / a mouse click, instead of closing
  after one. `OneClick` (default) is the old behavior; `Continuous` keeps labels on
  screen across clicks — e.g. `af`→navigate to a page, `bd`→click another button,
  `Space`→right-click mode, `aa`→open a context menu, `bb`→click a menu item, then
  `Esc`. Press the hotkey again while the overlay is up to toggle one-click ⇄
  continuous (badge bottom-center). In continuous mode the click mode reverts to Left
  after every click. Automation stays one-shot (its labels go stale on navigation).

### Changed
- **Default hotkey is now `Ctrl+Shift+M`** (was `Ctrl+Shift+Alt+F`). Alt is removed on
  purpose: pressing Alt — even inside a chord — dismisses an open context menu, so the
  old Alt-containing hotkey was closing the very menu you wanted to label-click.
  Startup-only; restart to apply. (`Alt`/`F10` are still not swallowed globally.)
- **Overlay input model**: label typing, Esc, Space, Tab and arrows are captured by
  `OverlayKeyboardHook` (a global LL keyboard hook), not by a focused `TextBox` /
  WPF `PreviewKeyDown`. A LL mouse hook restores click-to-dismiss. The overlay is now
  modeless (`Show()`), one at a time, and dismisses on match / Esc / any mouse click.
- `ForegroundWindow` exposes `ForceForegroundOnRender` / `CloseOnDeactivate` virtuals;
  `OverlayView` opts out of both, `DebugOverlayView` keeps the old behavior.

### Fixed
- **Esc clears the typed prefix instead of exiting.** If at least one label char has been
  typed, Esc now clears the prefix (cancel the selection, re-highlight all labels, stay
  up) so a mistyped char can be retyped from scratch; Esc on an empty prefix still closes
  the overlay. Pan and click-mode are preserved on a clear.
- **Numpad keys no longer captured by the overlay.** With NumLock off, the numpad arrow
  keys reuse the arrow VK codes (numpad 6 → `VK_RIGHT`, etc.), so the overlay was
  swallowing them as label pan — breaking numpad-mouse tools (e.g. an AutoHotkey
  `*NumpadRight` mouse-move script). The low-level hook's extended-key flag
  (`LLKHF_EXTENDED`) reliably separates the dedicated arrow cluster (extended → still
  pans) from the numpad keys (not extended → pass through). Numpad digit/operator keys
  were already not captured.
- **Alt+Tab works again.** While the overlay was up, `Alt+Tab` stopped switching
  windows (Tab was swallowed as "cycle monitor"). Root cause: the low-level hook
  delivers Alt as `VK_LMENU`/`VK_RMENU`, not the `VK_MENU` the event tracker checked,
  so the held-Alt passthrough never armed; and Tab is classified before the Ctrl/Alt/
  Win gate, so it was eaten. Now Alt is also recognized as `VK_LMENU`/`VK_RMENU` and
  backstopped with `GetAsyncKeyState(VK_MENU)`, so Alt+Tab (and Alt+anything) passes
  through. Capslock stays event-only (its OS state can be neutralized by AutoHotkey).
- **Open context menus / popups survive the hotkey.** Pressing the hotkey no longer
  dismisses an open right-click menu (File Manager, Edge, etc.). The overlay used to
  force itself to the foreground to capture typed label chars via WPF focus, and that
  foreground transfer is what closes open menus. It now shows **non-activated**
  (`ShowActivated=False`, no force-foreground) and reads typed input through a global
  low-level **keyboard hook** (`WH_KEYBOARD_LL`) instead of WPF focus, so it never
  steals foreground — you can even label-click items inside an open context menu.
- **Label highlight at start.** All labels are highlighted (yellow) when the overlay
  appears, narrowing as you type — restoring the behavior lost when input moved to the
  keyboard hook. (Re-highlighted again after each continuous-mode click.)

## [v2.1.0] — 2026-07-13

### Added
- **Full-screen overlay (`HintBoundsSource`)**: the overlay and its hint grid now
  cover the whole monitor the foreground window is on by default (`Screen`), so
  labels fill the screen regardless of window size. Set `Window` to restore the
  prior per-window behavior. Hot-reload.
- **Monitor cycling**: in Grid + Screen, **Tab** cycles the overlay across monitors
  (sorted left-to-right, then top-to-bottom), **Shift+Tab** cycles back. Each monitor
  shows its own labels; the typed prefix and pan offset reset on switch.
- **Options dialog**: the Overlay and Keyboard tabs expose every hot-reload setting
  (hint source/bounds, hint characters, grid density, click-mode order, timing log,
  nudge steps, hotkey) — no more hand-editing `hap.exe.config`.
- **Tray menu keyboard navigation**: the tray icon uses a WinForms `NotifyIcon` +
  `ContextMenuStrip`, so `Shift+F10` and right-click open a keyboard-navigable menu
  (arrows, `O` for Options, `E` for Exit). Esc closes.
- **First-char label grouping**: hint labels are sorted so consecutive labels share
  their first character (AA, AB, AC, …, BA, BB, …), which reads cleaner and lets the
  first char encode a spatial chunk.

### Changed
- **Config freshness cache**: `hap.exe.config` is re-parsed only when its last-write
  time changes (`OverlayActionConfig.EnsureFresh`), not once per read. A Grid +
  Screen press dropped from ~12 ms to ~2 ms `enum+merge` (measured). Hot-reload is
  preserved.
- **Incremental render**: each hint label is its own `DrawingVisual`, so a keystroke
  redraws only the changed labels instead of the whole overlay (better typing
  responsiveness at 1000+ labels).

### Fixed
- **Overlapping labels in full-screen mode**: in Grid + Screen the taskbar
  enumeration produced a second full-screen grid stacked on the foreground's,
  doubling every label (e.g. "KZ" + "M" rendered as "KZM"). The taskbar merge is now
  skipped in that mode.
- **Multi-monitor taskbar merge**: merging the primary taskbar when the target is on
  a secondary monitor no longer stretches the overlay across both displays.
