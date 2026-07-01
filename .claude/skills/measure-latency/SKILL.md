---
name: measure-latency
description: Measure hunt-and-peck overlay latency with the TimingLog instrumentation (enum+merge and render phases). Ensures logging is enabled, clears the log, asks the user to run scenarios, reads hap-timing.log from the Windows box, and reports per-phase timings plus the layout gap and whether it scales with label count.
---

# measure-latency

Measure how long the overlay takes to appear, broken into phases. The app writes
the log via `TimingLog` (gated by `TimingLogEnabled`) to `%TEMP%\hap-timing.log`
on the Windows box — which over SSH/WSL is `$HAP_WIN_TEMP/hap-timing.log`.

Each trigger writes two lines:
- `enum+merge <ms>  hints=<N>` — off-thread enumeration + taskbar merge.
- `render <ms>` — window load → `ContentRendered` (label layout/draw).

The **latency you feel** ≈ `render_timestamp − enum_timestamp`. The slice between
`enum+merge` finishing and `render` starting is the **layout gap** — not covered by
either stopwatch, and that's where O(N) work hides.

## Preconditions
- `.env` at repo root with `HAP_FORK_REPO`, `HAP_WIN_HOST`, `HAP_WIN_USER`,
  `HAP_WIN_TEST_DIR`, `HAP_WIN_TEMP`.

## Steps

Source `.env` in every command: `set -a; . ./.env; set +a`.

1. **Ensure logging is enabled in the deployed drop.** The shipped `hap.exe.config`
   must have `<add key="TimingLogEnabled" value="true" />`. Check it:
   ```bash
   set -a; . ./.env; set +a
   FOLDER=hap-<the-drop-to-measure>
   CFG="$HAP_WIN_TEST_DIR/$FOLDER/hap.exe.config"
   ssh "$HAP_WIN_USER@$HAP_WIN_HOST" "grep -n TimingLogEnabled '$CFG'"
   ```
   If it's `false` or absent, ship a drop with it `true` (edit
   `src/HuntAndPeck/App.config` and use the `ship-drop` skill). `TimingLogEnabled`
   is hot-reload, so flipping the deployed file needs only a re-trigger, not a restart.

2. **Clear the log** so the next read is clean:
   ```bash
   set -a; . ./.env; set +a
   ssh "$HAP_WIN_USER@$HAP_WIN_HOST" ': > "'"$HAP_WIN_TEMP"'/hap-timing.log" && stat -c %s "'"$HAP_WIN_TEMP"'/hap-timing.log"'
   ```
   Expect `0`.

3. **Ask the user to run scenarios** that vary label count: e.g. small window ×2,
   maximized window ×2, a Chromium app (Feishu) ×1. Each trigger appends two lines.
   Wait for them to say "done".

4. **Read the log**:
   ```bash
   set -a; . ./.env; set +a
   ssh "$HAP_WIN_USER@$HAP_WIN_HOST" "cat '$HAP_WIN_TEMP/hap-timing.log'"
   ```

5. **Compute and report.** For each trigger:
   - `layout_gap_ms = (render_timestamp − enum_timestamp) − render_ms`
     (timestamps are `HH:MM:SS.mmm`; convert to a monotonic ms count within the run).
   - Present a table: scenario × {`hints`, `enum+merge`, `layout gap`, `render`, `total`}.
   - State whether the **layout gap scales with `hints=N`**. After the font-read fix
     it should be roughly flat; if it grows ~linearly with N, an O(N) config/disk
     read has crept back in.

## Known past culprit
`HintViewModel` once called `ConfigurationManager.RefreshSection` in its constructor
— re-reading the config file from disk N times per overlay, ~0.85 ms × N. Keep all
config reads to **once per overlay** (read in `OverlayViewModel`, pass values down).
