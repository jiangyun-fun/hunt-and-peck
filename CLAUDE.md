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
      OverlayActionConfig.cs               App.config readers (click modes, nudge, font, hotkey, timing, zones)
      KeyListenerService.cs                global hotkeys (RegisterHotKey) + quadrant hotkeys
      OverlayKeyboardHook.cs               global LL keyboard/mouse hook for overlay input (non-activating)
                                         + persistent Alt/Capslock tracker (above AutoHotkey)
      ZoneService.cs                       type-to-zoom zone helpers (slice, pick session, centered lens)
      TimingLog.cs                         optional latency log (gated by TimingLogEnabled)
      Macro/                               macro engine: MacroStep/Store/Service, InputSynthesis
                                         (SendInput chords), WindowFinder (EnumWindows focus)
    ViewModels/
      ShellViewModel.cs         hotkey → enumerate → merge taskbar → show overlay; macro hotkey → picker
      OverlayViewModel.cs        hint state machine, pan offset, click-mode cycle
      HintViewModel.cs           per-hint label/active/font
      MacroPickerViewModel.cs    macro palette (single-char select)
    Views/
      OverlayView.xaml(.cs)      the overlay window (click-through, non-activating)
      MacroPickerView.xaml(.cs)  macro palette window (topmost, single-char select)
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
  `HintSource`, `HintBoundsSource`, `OverlayTriggerMode`, `GridEdgeStep`,
  `GridCenterStep`, `GridDenseRegions`, `GridInset`, `GridEdgeBandPercent`,
  `HintCharacters`, `HintFontSize`, `HintFontFamily`, `HintPillOpacity`, `HintDimOpacity`,
  `NudgeStepSmall`, `NudgeStepMedium`, `NudgeStepLarge`, `NudgeKeysSmall`, `NudgeKeysMedium`, `NudgeKeysLarge`,
  `ClickModeOrder` (only the first entry matters now — it is the default mode; Space no longer cycles),
  `TextSelectMethod`, `SelectionActionsClose`, `TopmostReassertEnabled`, `LeaderBindings`, `ArrowKeyBehavior`, `MaxEnumerationDepth`, `GridLayouts`, `TimingLogEnabled`,
  `ZoneZoomEnabled`, `ZoneCols`, `ZoneRows`, `ZoneFontSize`, `ZoneZoomReturnToPickOnFire`,
  `ZoneGridStep`, `ZoneWidth`, `ZoneHeight`, `OverlayAutoCloseSec`, `HideNonMatchingLabels`,
  `GroupViewEnabled`, `GroupZones`, `GroupFontSize`.
  (`ActiveLayout` is also in appSettings but is rewritten by `<leader>g`, not hand-edited.)
- **Startup-only** (the global hotkey is registered once; **restart** to apply):
  `HotkeyKey`, `HotkeyModifier` (default `Ctrl+Shift+M` — no `Alt`, since Alt
  dismisses open context menus even inside a chord); and the quadrant hotkeys
  `QuadrantHotkeyKeys` (default `F1,F2,F3,F4` = TL/TR/BL/BR) +
  `QuadrantHotkeyModifier` (default `Control,Shift`), which open the overlay
  scoped to one screen quadrant. The macro-picker hotkey `MacroHotkeyKey`,
  `MacroHotkeyModifier` (default `Ctrl+Shift+;` / `OemSemicolon`, no `Alt`)
  opens the macro palette.

`HintCharacters` defaults to `A–Z` **minus `Q`** (25 letters, 625 two-char labels). The
punctuation set `,./;'[]\` is also supported — add any of them here to opt in (they then
also become typeable while the overlay is up). The first 25 entries double as the
`GroupZones` box keys. **`Q` is reserved** (the direct close/Esc alias — a label containing
`Q` could never be typed; `<leader>q` is therefore not a binding); digits are not labels
either (`LabelCharForVk` never returns them) and, since the `1` alias was retired, they all
pass through to the app — do not put `Q` or `0–9` in `HintCharacters`.

## Runtime behavior (current)

- **Hotkey** `Ctrl+Shift+M` → overlay (no `Alt`: Alt dismisses open context menus even
  inside a chord). By default (`HintBoundsSource=Screen`) it fills the whole monitor
  the foreground window is on; in Grid mode one grid is built per monitor. **Tab**
  cycles to the next monitor (wraps), **Shift+Tab** to the previous; each monitor shows
  its own labels and the typed prefix + pan reset on switch. **Ctrl+Tab / Ctrl+Shift+Tab
  / Win+Tab pass through** (browser/app tab switch, Task View) — only plain Tab/Shift+Tab
  cycle monitors. (Cycling is Grid + Screen only; Automation / Grid+Window stay
  single-session.)
- **Press the hotkey again while the overlay is up** to toggle one-click ⇄ continuous
  (Grid only). `OverlayTriggerMode=OneClick` (hot-reload) makes every open one-shot.
- **Macros** (`Ctrl+Shift;`, startup-only `MacroHotkeyKey`/`MacroHotkeyModifier`): opens a
  small topmost palette listing each macro's hotkey + name from `%APPDATA%\hap\macros.json`
  (personal, NOT in git; re-read each open, so edits need no restart). Type a macro's
  single-char key to run it; `Esc`/click-away cancels. Step types: `send`
  (`Mods`=`Ctrl,Shift` + `Key`=a `System.Windows.Forms.Keys` name), `wait` (`Ms`),
  `focusWindow` (`Title`, `Match`=`exact`|`contains`; aborts on 0/>1 match — a stale title
  never clicks the wrong window; preserves maximized state), `clickAbs` (`X`,`Y` physical
  screen px, 0,0 = top-left), `clickRel` (`Dx`,`Dy` from the focused window rect). The
  runner is on a background thread; `focusWindow` dispatches to the UI thread (foreground
  privilege). `openOverlay`/`overlayType`/`nudge`/`rawReplay` are in the schema but
  unimplemented (overlay targeting was descoped — `clickAbs` covers fixed-target macros).
- **Label font is JetBrains Mono NL**, bundled in the assembly (`Fonts/JetBrainsMonoNL-*.ttf`,
  SIL OFL) and resolved via pack URI, so it renders even if the font is not installed on the
  box. `HintFontFamily` (hot-reload, default `JetBrains Mono NL`) selects the family: the
  bundled name serves the embedded copy (NL cut = no ligatures, so punctuation label pairs
  render literally); any other name uses an installed family (e.g. `Consolas`). Was a hardcoded
  `Helvetica, Arial`; the chrome badges still use that. The family is read once per overlay and
  threaded to `HintCanvas` via `HintViewModel.FontFamilyReadValue`.
- **Labels are all highlighted (yellow) at start**; typing narrows the highlight to the
  matching labels; a unique match fires. (In continuous mode the highlight resets to
  all-yellow after each click.)
- **Group view (progressive 1-char labels; `GroupViewEnabled`, default on; `<leader>p`
  toggles per-session)**: the overlay opens with a **`GroupZones` grid (default 5×5) of
  dotted boxes** instead of every pill — zone i keyed by `HintCharacters[i]` in scan
  order (default = A–Z minus Q), the key char in a small pill **centered** in each box.
  The grid tiles the region the session's points actually OCCUPY (their union extent —
  the monitor for Grid+Screen, the window, or just the quadrant for quadrant hotkeys),
  not the session bounds: quadrant sessions set bounds = the full monitor (for the
  full-screen overlay) while their points cluster in one quarter, so bounds-based
  slicing overflowed every quadrant session into the fallback. Type a zone char → only
  that zone's points show, labeled by their SECOND char alone; the second char fires
  the click (2 keystrokes; a zone holding exactly one point is labeled by its key alone
  and fires on that single keystroke). **Grid-session labels are zone-based** (first
  char = the zone's key, second char cycles `HintCharacters` within the zone), in BOTH
  the group and full views — so `<leader>p` never relabels mid-session. **Grid
  generation is zone-aligned** while a zone spec is active: totalCols = zoneCols ×
  inCols, totalRows = zoneRows × inRows, spanEdges over the bounds, so every zone
  holds EXACTLY the same inCols×inRows points (6×4 = 24 on 16:9 with 25 letters; a
  single global lattice previously sliced unevenly into mixed 6×3 / 6×4 / 5-wide
  zones) and zone overflow is impossible by construction. inCols/inRows is derived
  from the bounds' aspect (near-square cells), clamped by a CONSTANT legibility
  floor (`MinZonePointSpacing`, 20 px — never one pill-width closer; a
  path-dependent layout-step floor once gave quadrants 6×3 while the main hotkey
  got 6×4). `GridLayouts` presets and dense regions do NOT apply while zoned
  (zone mode wants uniform), and zone-zoom's lens fill is zone-aligned too. On 1080p
  that is 30×20 = 600 points (~66×57 px step). A genuinely degenerate session (spec
  blank/invalid/oversized, or zone assignment still overflows) falls back to
  scan-order labels + derived first-char-group boxes (the original v1 shape).
  `Esc` backs out to the boxes (clears the prefix;
  `Esc` again closes); continuous mode returns to the boxes after each click;
  `<leader>s`/`v` 2-picks work through it. Non-matching labels are hidden at level 2
  regardless of `HideNonMatchingLabels`. Applies wherever the session is grid-like
  (all-`PointHint`: Grid+Screen, Grid+Window **without** a taskbar merge, quadrants);
  Automation, taskbar-merged and zone sessions keep full scan-order labels. `GroupFontSize`
  (default 14, `0` = follow `HintFontSize`) sizes the centered key chars.
- **Arrows move focus in the app beneath** by default (`ArrowKeyBehavior=Passthrough`) — e.g.
  Excel cell nav, list selection — so the dedicated arrow cluster is no longer eaten by the
  overlay. Set `ArrowKeyBehavior=Pan` to restore legacy arrow-panning (plain arrows 3 px,
  `Shift` 15 px). The **numpad** arrow keys (NumLock off) always pass through so a numpad-mouse
  tool (e.g. AutoHotkey `*NumpadRight`) keeps working while the overlay is up.
- **Nudge (label pan), 3 tiers** (Vim-style): `Shift+uiop` = Large, `Shift+hjkl` = Medium,
  `Shift+m , . /` = Small; each row maps positional L,D,U,R (`u/m`=←, `i/,`=↓, `o/.`=↑, `p//`=→).
  Plain row keys still type hint chars (Shift is the pan gate), so label-typing is unaffected.
  Each tier's step is `NudgeStep*` = `X,Y` px (X for ←/→, Y for ↑/↓) or `auto` (= the current
  zone's cell size, so one Large nudge traverses exactly one zone). Defaults: Small 3,3 ;
  Medium 15,15 ; Large auto. Dedicated arrows (when `ArrowKeyBehavior=Pan`) use Medium (plain)
  / Large (Shift). `Ctrl+Shift+<row>` and `Win+Shift+<row>` pass through (not captured).
- **`<Space>` is the leader key** (LazyVim/which-key style): pressing it opens a
  transient centered popup listing the bindings; the next key fires its action and
  closes the popup. Mode-cycling is gone — modes are reached only via leader chords.
  `Esc`/`Q` or an unmapped key cancels a pending leader; `<Space>` again also cancels
  (toggle). A typed label prefix is **preserved** across the leader (a mid-drill
  `<Space>r` returns you to the drilled zone's level-2 labels, not the boxes); the
  snapshot/select 2-pick phases clear the prefix on entry. In continuous mode the mode
  reverts to the default (first `ClickModeOrder`
  entry, Left) after every click. Default bindings (`LeaderBindings`, hot-reload):
  `<leader>l/r/d/m/t` = Left/Right/Double/Move/Triple (plain `Q` closes), `<leader>z` = suspend,
  `<leader>g` = cycle layout, `<leader>i` = toggle dim, `<leader>s` = snapshot region (see
  below), `<leader>v` = select text span (see below), `<leader>p` = toggle group view (see
  above). The badge still shows the active mode.
- **Type a label's 2 chars** → cursor jumps to its (panned) position and fires the
  current mode (left / right / double / triple click via `mouse_event`, or move-only).
  **Triple click** (`<leader>t`) = three rapid left clicks — selects a whole line in most
  apps (a sentence in Word). **Selection actions (Double/Triple/`<leader>v`) always close
  the overlay, even in Continuous mode** — keeping the overlay up clears the just-made
  selection in the target app (observed in Notepad3/Edge), so closing is what makes it
  persist and become copyable. Left/Right/Move stay continuous.
- **Snapshot region (`<leader>s`)**: enters a 2-pick mode (badge `SNAP 1/2`). Type the
  label of one corner, then the opposite corner (any order) → the screen rectangle between
  them is captured to the clipboard (in-process `CopyFromScreen` + `Clipboard.SetImage`;
  the overlay hides for ~40ms so its labels don't appear in the shot). Works in Grid and
  Automation; coords use the label's target point + pan offset (what you label is what you
  capture). After capture it follows trigger mode (one-shot closes; continuous stays up).
  `Esc`/`Q` cancels the pick; a degenerate pick (same point) is a no-op.
- **Select text span (`<leader>v`)**: a 2-pick mode like snapshot (badge `SEL 1/2`). Type
  the label of one end of the span, then the other → the text between them is selected. The
  gesture is `TextSelectMethod` (hot-reload): `ShiftClick` (default) = pick-1 plain click
  (anchor) + pick-2 Shift+click (extend); `Drag` = pick-2 synthesizes the whole drag
  (down@anchor → move → up) in one shot. Pick-1 **always plain-clicks** — it clears any
  prior selection (a mousedown *on* a selection starts a text drag-drop instead of a new
  selection drag, which made Drag alternate work/fail between attempts). ShiftClick holds no
  button while you type the second label; Drag is the fallback where an app remaps
  Shift+click (e.g. column-select in some editors).
  Works in Grid and Automation; coords use the label's
  target point + pan offset. After the selection the overlay always closes (even in
  Continuous mode — staying up clears the selection). `Esc`/`Q` cancels the pick.
  **Caveat (observed on-box 2026-08-14, build 8f47655):** Edge and Notepad3 work for
  d/t/v in BOTH modes. Feishu does not: `v` fails with BOTH methods (ShiftClick: no
  selection results; Drag: the selection is made then canceled), and d/t alternate
  work/fail when a selection already exists from the previous attempt. Physical
  click+Shift+click works in Feishu, so the gap is specific to synthesized input there;
  mechanism unverified from Linux. Workaround in Feishu: `<leader>t` (every other attempt,
  or plain-click empty background first to clear), physical mouse for arbitrary spans.
- **Alt or Capslock held → passthrough**: while Alt or Capslock is physically held the
  overlay stops capturing keys, so `Alt+Tab` (window switcher) + arrows and Capslock-
  based AutoHotkey mappings (e.g. `Capslock+f` → `Ctrl+Shift+M`) pass through. Held-state
  is tracked from the raw hook events (not `GetAsyncKeyState`), so it still detects a
  Capslock AutoHotkey has neutralized for a custom combo — letting `Capslock+f` toggle
  continuous mode on the 2nd press. Also lets you Alt+Tab between apps mid-overlay.
  A **persistent tracker hook** (armed once at app startup, above AutoHotkey) keeps this
  state accurate even when an overlay opens while a modifier is already held (e.g. hold
  Capslock and tap quadrant hotkeys). **Caveat:** if your AHK script fully remaps Capslock
  (e.g. `CapsLock::Send, _`), the tracker must sit above AHK's hook, which only holds when
  hap started *after* the last AHK reload — so **restart `hap.exe` after reloading your AHK
  script** if Capslock combos stop passing through. (Raw input does not help here: AHK's
  remap intercepts Capslock at the low-level-hook layer before raw input is generated.)
- **`<leader>i` dims the labels** (was backtick): drops label opacity to the configured
  dim level (default ~20%, `HintDimOpacity`) so the text passage behind is readable, then
  `<leader>i` again to restore. Keys stay captured, so labels stay typeable while dim
  (you can still type a label to fire it). Tradeoff: opacity-dim couples label contrast
  to the background, so dimmed labels are harder to see on dark surfaces (acceptable on
  light backgrounds; raise `HintDimOpacity` to improve). A two-tone-outline read-mode was
  tried and rejected as ugly/hard to read. Backtick now passes through.
- **`<leader>z` suspends the overlay** (was `2`/`\`): enters persistent suspend — the
  overlay stops capturing keys AND **hides its labels** (opacity 0), leaving only the
  `SUSPENDED` status, so you can type into the app beneath (vimium, Excel shortcuts)
  with zero key collision. Clicks pass through (no dismiss). Resume by pressing the
  **main hotkey** (`Ctrl+Shift+M` / `Capslock+f`) again; `Esc`/`Q` closes. Per-session
  (resets each new overlay). `2` now passes through to the app. (`\` is a label char.)
- **`<leader>g` cycles grid layouts** (Grid only; was `3`/`;`): `GridLayouts` lists N
  geometry presets (layouts separated by `||`, fields
  `edgeStep|centerStep|inset|edgeBandPercent|denseRegions`); it regenerates the grid with
  the next preset and wraps (badge shows e.g. `L2/2`). The active preset persists across
  Esc/reopen **and** a full restart (`ActiveLayout`). Ships with 2: the dense edge grid
  and a uniform full-screen grid (`Center` + equal steps). With no `GridLayouts`
  configured the overlay is not cycle-capable, so `<leader>g` is a no-op; `3` always
  passes through to the app. (`;` is a label char.)
- **Type-to-zoom zones** (`ZoneZoomEnabled`, Grid + Screen only, hot-reload; default
  off): instead of labeling the whole monitor at once (which the `HintCharacters²` cap
  auto-coarsens into a sparse grid), the overlay opens with a `ZoneCols`×`ZoneRows`
  (default 3×3) grid of large 1-char zone labels over the current monitor. Type a zone
  label → the overlay fills a **lens** (a `ZoneWidth`×`ZoneHeight` window, default the
  auto cell size, centered on the zone) with a uniform fine grid at `ZoneGridStep` (under
  the cap → dense labels AND more 1-char labels); the overlay stays full-screen (badge
  screen-centered, labels not clipped). Because the lens grid is uniform, a Large nudge
  of `auto` pans it by exactly one zone cell (h/l horizontally, j/k vertically) — a
  movable lens over the screen. Type the target label to fire. `Esc` (empty) returns to
  the zone-pick view; `Esc` again closes. In Continuous mode the overlay stays in the
  zone after each click (set `ZoneZoomReturnToPickOnFire=true` to re-zoom to the pick
  view each time). `3` does nothing in zone mode (zones use a single `ZoneGridStep`);
  `Tab` monitor-cycling is disabled (foreground monitor only). Requires `ZoneCols*ZoneRows
  ≤ HintCharacters` count (every zone needs a single-char label); otherwise zones are
  skipped and the overlay falls back to the full-monitor grid. `ZoneFontSize` (default 20)
  sizes the pick labels. Automation / Grid+Window / the `/hint`,`/tray` headless paths
  ignore zones.
- **Labels are slightly transparent by design**: the pill fill is α≈0.8 by default
  (softened yellow, background peeks through) while the text stays fully opaque
  (crisp). Configurable via `HintPillOpacity` (0-100 percent; hot-reload). Base mode
  is NOT dimmed canvas-wide, so it stays readable on dark backgrounds.
- **Trigger mode** (`OverlayTriggerMode`, hot-reload; Grid only): `Continuous` (default)
  keeps the overlay up for repeated clicks until `Esc` / a mouse click — e.g. `af`→
  navigate, `bd`→click again, `Space`→right-click, `aa`→open a menu, `bb`→click a menu
  item, then `Esc`. `OneClick` closes the overlay after one click. Press the hotkey
  again while the overlay is up to toggle one-click ⇄ continuous (badge bottom-center).
  Automation ignores this and stays one-shot.
- **Hide non-matching labels** (`HideNonMatchingLabels`, default on): after you type the
  first char of a 2-char label, labels that don't match **disappear** (not just dim) —
  only the candidates for the typed prefix remain. Esc / continuous reset restores them.
- **Auto-close** (`OverlayAutoCloseSec`, default 0 = off): when >0, the overlay closes after
  that many seconds with no key/click activity (any captured input resets the clock); it
  does not fire while suspended. A safety net for a misfire / walking away.
- **Quadrant hotkeys** (`Ctrl+Shift+F1/F2/F3/F4`, startup-only): open the overlay scoped to
  the TL/TR/BL/BR screen quadrant — a dense uniform grid (`ZoneGridStep`) over just that
  quarter, on a full-screen overlay (labels only in the quadrant). One chord, no zone-pick.
  The overlay holds all four quadrants and starts on the pressed one, so **plain Tab/Shift+Tab
  cycles the quarters** (badge `Q n/4`); `Ctrl+Tab`/`Ctrl+Shift+Tab`/`Win+Tab` still pass
  through. Each cycle resets the typed prefix + pan (same as monitor cycling).
  Keys/modifier configurable via `QuadrantHotkeyKeys`/`QuadrantHotkeyModifier`.
- **Esc** (or **`Q`**, an alias that's closer to type; `Ctrl+Q` passes through) first cancels
  a pending leader if one is open; otherwise clears the typed prefix if any has been typed
  (cancel the selection, stay up so you can retype from scratch); if nothing is typed, it
  closes the overlay. Pan and click-mode are kept on a clear. Any **mouse click** also
  dismisses the overlay (and still reaches the app beneath). Digits `0`–`9` are not labels
  (the former `1` alias was retired) and pass through to the app.
- **Doesn't dismiss open menus**: the overlay shows non-activated (`ShowActivated=False`
  + `WS_EX_NOACTIVATE`), so an open context menu / popup stays open when you press the
  hotkey, and closing the overlay with `Esc`/`Q`/a click no longer dismisses it either —
  you can label-click items inside it. (Trade-off: the app is no longer fully key-
  isolated while the overlay is up — non-label keystrokes, e.g. Ctrl-shortcuts, pass
  through to it.)
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
- **Multi-event input bursts run on a background thread** (`FireInputAsync`, ~20 ms gaps
  between events): double/triple click, Shift+click, and the select-drag. Load-bearing:
  the UI thread owns the LL hooks, so a burst run on it is HELD by the OS and flushed as
  one 0 ms batch when the thread pumps — apps randomly mishandled that (d/v/t succeeded
  only at low frequency). Off-thread, each event delivers promptly and the gaps are real.
  Single Left/Right clicks stay on-thread (always reliable). Mirrors the macro engine's
  off-thread SendInput. The 100ms topmost re-assert timer was tested as the deselect
  cause and disproven on-box.

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
