# CLAUDE.md — hunt-and-peck

A Vimium-style mouseless-clicking tool for Windows. Press a hotkey → an overlay of
labeled "hints" appears over the active window → type a label to move/click the
target without the mouse. Forked from `zsims/hunt-and-peck`; we develop on our own
fork (`$HAP_FORK_REPO`, see `.env`).

## Tech stack & build reality

- **WPF + .NET Framework 4.5.1, C#** — Windows-only. Uses COM interop
  (`UIAutomationClient`) and Win32 P/Invoke (`src/NativeMethods`).
- **You CANNOT build or run this on Linux/macOS.** The dev box is Linux; the only
  compile/test gate is **GitHub Actions CI** (`.github/workflows/build.yml`,
  `windows-latest`: MSBuild + vstest). Runtime testing happens on the Windows box.
- Tests: xUnit 2.2.0 in `src/HuntAndPeck.Tests`. Only pure logic is unit-tested;
  UI/P-Invoke behavior is verified by CI compile + manual testing on Windows.
- Non-SDK csprojs with **explicit `<Compile Include>` lists** — every new `.cs`
  file must be added to the relevant `.csproj` or it won't compile.

## Repo layout

```
src/
  HuntAndPeck/                 the app (WPF)
    Services/
      UiAutomationHintProviderService.cs   hint enumeration (Grid + Automation)
      HintLabelService.cs                  vimium hint-string generation
      OverlayActionConfig.cs               App.config readers (click modes, nudge, font, hotkey, timing)
      KeyListenerService.cs                global hotkeys (RegisterHotKey)
      OverlayKeyboardHook.cs               global LL keyboard/mouse hook for overlay input (non-activating)
      TimingLog.cs                         optional latency log (gated by TimingLogEnabled)
    ViewModels/
      ShellViewModel.cs         hotkey → enumerate → merge taskbar → show overlay
      OverlayViewModel.cs        hint state machine, pan offset, click-mode cycle
      HintViewModel.cs           per-hint label/active/font
    Views/
      OverlayView.xaml(.cs)      the overlay window (click-through, non-activating)
      HintCanvas.cs              single-DrawingVisual renderer for all labels
      ForegroundWindow.cs        base window: opt-out foreground + close-on-deactivate (virtuals)
    Models/                      Hint (abstract), PointHint (grid), UiAutomation*Hint, HintSession
    App.config                   shipped defaults (hot-reload + restart-only settings)
  HuntAndPeck.Tests/            xUnit
  NativeMethods/                User32, KeyModifier, POINT, RECT
docs/superpowers/{specs,plans}/ design docs (some superseded — read the banners)
```

## Architecture

- **Hint sources** (`HintSource` in App.config):
  - `Grid` (default): a synthetic grid of cursor-jump points over the window —
    instant, no UI Automation walk, works on any app (incl. Chromium).
  - `Automation`: enumerates the window's real UI Automation controls (precise,
    slow on huge trees — Chromium walks ~600+ elements cross-process).
- **Overlay bounds** (`HintBoundsSource` in App.config, default `Screen`): the area
  the overlay and its grid cover. `Screen` = the full monitor the foreground window
  is on, so labels fill the screen regardless of window size; `Window` = the
  foreground window rect (the previous behavior). Cursor targeting uses absolute
  screen coords, so enlarging the overlay never breaks clicks. Grid `PointHint`s
  store absolute screen points (`UiAutomationHintProviderService.ResolveOwningBounds`).
- **Overlay lifecycle** (`ShellViewModel`): hotkey → capture foreground window →
  `EnumHints` off-thread → merge the taskbar in (Grid + Window / Automation; skipped
  for Grid + Screen) → `OverlayViewModel` → `App.ShowOverlay` → `OverlayView.Show()`.
  The overlay is `Topmost`, `AllowsTransparency`, and click-through
  (`WS_EX_TRANSPARENT`) so real clicks and synthesized clicks reach the app beneath.
- **Overlay input (non-activating)**: the overlay shows with `ShowActivated=False` and
  does NOT force foreground, so pressing the hotkey does NOT steal foreground from /
  dismiss an open context menu. Typed label chars, Esc, Space, Tab and arrows are
  captured by a global low-level keyboard hook (`OverlayKeyboardHook`,
  `WH_KEYBOARD_LL`); a low-level mouse hook (`WH_MOUSE_LL`) provides click-to-dismiss.
  Hooks are armed in `App.ShowOverlay` and disarmed on close (idempotent).
  `ForegroundWindow` keeps force-foreground + close-on-deactivate for
  `DebugOverlayView`; `OverlayView` opts out via `ForceForegroundOnRender` /
  `CloseOnDeactivate`.
- **Rendering**: `HintCanvas` draws every label in one `OnRender` pass (one
  `DrawingVisual`), not one `TextBlock` per hint. `FormattedText` is cached per
  label; `InvalidateVisual` re-runs (cheaply) when a hint's `Active` flips.
- **Pan vs jump**: arrow keys pan ALL labels together (a `TranslateTransform` bound
  to `OffsetX`/`OffsetY`); typing a label's chars jumps the cursor to its moved
  position.

## Dev workflow (Linux edit → CI → Windows test)

This is the core loop — see the `ship-drop` skill for the automated version.

1. **Edit** C#/XAML/App.config on the dev box (`$HAP_REPO`).
2. **Commit + push** to `master` on `$HAP_FORK_REPO`. We commit directly to
   `master`; **no PRs** (this is our fork).
3. **CI builds** the Release drop and uploads artifact `HuntAndPeck-Release`
   (`src/HuntAndPeck/bin/Release`). Watch it green:
   `gh run watch <id> --repo $HAP_FORK_REPO --exit-status`.
4. **Download + rsync** the artifact to the Windows box into a **fresh folder**
   under `$HAP_WIN_TEST_DIR` (never overwrite a running `hap.exe` — Windows file lock):
   `gh run download <id> --repo $HAP_FORK_REPO --name HuntAndPeck-Release --dir <tmp>`
   then `rsync -az <tmp>/ $HAP_WIN_USER@$HAP_WIN_HOST:$HAP_WIN_TEST_DIR/<folder>/`.
5. **Manual test** on the Windows box (the user runs it; you cannot drive the GUI).
   For latency work, use the `measure-latency` skill.

## Releases

Pushing a tag (`git tag -a v2.0.0 -m v2.0.0` → `git push origin v2.0.0`) triggers
`.github/workflows/release.yml`: CI builds the Release drop, zips it as
`HuntAndPeck-<tag>.zip`, and attaches it to the GitHub release (creating the
release with generated notes if it doesn't exist). Rebuild an existing tag's asset
with `gh workflow run release.yml -f tag=v2.0.0` (workflow_dispatch). The
`ship-drop` skill is for ad-hoc dev drops to the Windows box; the release workflow
is for tagged public releases.

## Configuration (`src/HuntAndPeck/App.config`)

Two kinds of settings:

- **Hot-reload** (read each trigger; edit `hap.exe.config`, save, re-trigger):
  `HintSource`, `HintBoundsSource`, `GridEdgeStep`, `GridCenterStep`,
  `GridDenseRegions`, `GridInset`, `GridEdgeBandPercent`, `HintCharacters`,
  `HintFontSize`, `NudgeStep`, `NudgeStepFast`, `ClickModeOrder`,
  `MaxEnumerationDepth`, `TimingLogEnabled`.
- **Startup-only** (the global hotkey is registered once; **restart** to apply):
  `HotkeyKey`, `HotkeyModifier` (default `Ctrl+Shift+Alt+F`).

`HintCharacters` accepts any chars (letters **and** digits); the matching input
allows `A–Z` and `D0–D9`.

## Runtime behavior (current)

- **Hotkey** `Ctrl+Shift+Alt+F` → overlay. By default (`HintBoundsSource=Screen`) it
  fills the whole monitor the foreground window is on; in Grid mode one grid is built
  per monitor. **Tab** cycles to the next monitor (wraps), **Shift+Tab** to the
  previous; each monitor shows its own labels and the typed prefix + pan reset on
  switch. (Cycling is Grid + Screen only; Automation / Grid+Window stay
  single-session.)
- **Arrows** pan all labels (3 px; `Shift` = 15 px).
- **Space** cycles the click mode (badge top-right): `Left → Right → Double → Move`
  (`ClickModeOrder`, wraps; resets each trigger). `Move` positions without clicking.
- **Type a label's 2 chars** → cursor jumps to its (panned) position and fires the
  current mode (left / right / double click via `mouse_event`, or move-only).
- **Esc** cancels. Any **mouse click** also dismisses the overlay (and still reaches
  the app beneath).
- **Doesn't dismiss open menus**: the overlay shows non-activated, so an open context
  menu / popup stays open when you press the hotkey — you can label-click items inside
  it. (Trade-off: the app is no longer fully key-isolated while the overlay is up —
  non-label keystrokes, e.g. Ctrl-shortcuts, pass through to it.)
- **Tray icon** (WinForms `NotifyIcon` + `ContextMenuStrip` in `ShellView`):
  right-click or `Shift+F10` opens a keyboard-navigable menu (arrows, `O` Options,
  `E` Exit). The **Options** dialog exposes every hot-reload setting, so there is no
  need to hand-edit `hap.exe.config`.

## Performance (hard-won notes)

- **Never re-read App.config per hint.** A previous version called
  `ConfigurationManager.RefreshSection` inside `HintViewModel`'s ctor — that re-read
  the config file from disk N times per overlay (~0.85 ms × N → ~1 s at 1281 labels).
  Read config **once per overlay** (see `OverlayViewModel` ctor) and pass it down.
- **Config reads go through `OverlayActionConfig.EnsureFresh`.** A trigger reads ~12
  settings, and each used to `RefreshSection` (one disk re-parse per read, ~10 ms
  total per press). `EnsureFresh` stats `hap.exe.config`'s last-write time and only
  re-parses when the file changed, so a press costs one parse, not twelve (a Grid +
  Screen press dropped from ~12 ms to ~2 ms `enum+merge`, measured). Never call
  `ConfigurationManager.RefreshSection` directly — use `EnsureFresh` so hot-reload
  still works and reads stay cached.
- **Label count drives everything else.** Even with the `HintCanvas` renderer, very
  dense grids (1000+ labels) take longer to build `FormattedText` for. Lower
  `GridEdgeStep`/`GridCenterStep` density or `HintCharacters` count to go faster.
- **Overlay timing**: set `TimingLogEnabled=true` to log `enum+merge` and `render`
  phases to `%TEMP%\hap-timing.log` (see `measure-latency` skill).

## Environment variables (local; values live in `.env`, gitignored)

| Var | Meaning |
|-----|---------|
| `HAP_REPO` | absolute path to this repo on the dev box |
| `HAP_FORK_REPO` | GitHub fork `owner/repo` (CI + push target) |
| `HAP_WIN_HOST` | Windows test box host |
| `HAP_WIN_USER` | SSH user on the Windows box |
| `HAP_WIN_TEST_DIR` | WSL-mount path where build drops are rsync'd |
| `HAP_WIN_TEMP` | WSL-mount path of Windows `%TEMP%` (timing log) |

`.env.example` documents each; `.env` holds the real values. **Do not hardcode
these in code, docs, or skills** — read them from the environment.

## Conventions

- Commit directly to `master` on the fork; no PRs, no upstream PRs.
- Conventional Commits (`feat:`, `fix:`, `perf:`, `chore:`, `docs:`).
- No `Co-Authored-By` trailer.
- `App.config` is XML — never put `--` inside an XML comment (MSBuild MSB3249).

## Skills (`.claude/skills/`)

- `ship-drop` — push, watch CI, download the Release artifact, rsync to the
  Windows box into a fresh folder, report the path.
- `measure-latency` — clear the timing log, have the user run scenarios, read
  `%TEMP%\hap-timing.log`, report per-phase timings + the layout gap.
