# Changelog

All notable changes to this fork are documented here. Versions track the
`v<MAJOR>.<MINOR>.<PATCH>` git tags; the GitHub release for each tag carries the
built `HuntAndPeck-<tag>.zip`.

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
