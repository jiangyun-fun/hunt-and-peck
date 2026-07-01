---
name: ship-drop
description: Build hunt-and-peck via CI (push to the fork), download the Release artifact, and rsync it to the Windows test box into a fresh folder. Use after editing C#, XAML, or App.config to produce a runnable drop for manual testing on Windows.
---

# ship-drop

Ship a runnable hunt-and-peck drop to the Windows test box. The app is Windows-only
WPF (.NET Framework 4.5.1 + COM interop), so it builds on GitHub Actions CI and runs
on the Windows box — **not** on the dev (Linux) box.

## Preconditions
- `.env` at the repo root defines `HAP_FORK_REPO`, `HAP_WIN_HOST`, `HAP_WIN_USER`,
  `HAP_WIN_TEST_DIR` (copy `.env.example` to `.env`).
- Working on `master` of the fork — we commit directly, no PRs.
- `gh` is authenticated for `$HAP_FORK_REPO`.

## Steps

Run from the repo root. Shell state does **not** persist between Bash calls, so
**source `.env` in every command**: `set -a; . ./.env; set +a`.

1. **Pick a fresh folder name** for the drop on the Windows box (e.g. `hap-grid`,
   `hap-perf`). Never overwrite a running `hap.exe` — Windows holds a file lock.

2. **Commit + push** (Conventional Commits, no `Co-Authored-By`):
   ```bash
   set -a; . ./.env; set +a
   git add -A src/
   git commit -m "<type>: <summary>"
   git push origin master
   ```

3. **Watch CI go green** (it compiles + runs tests + uploads `HuntAndPeck-Release`):
   ```bash
   set -a; . ./.env; set +a
   sleep 6
   RID=$(gh run list --repo "$HAP_FORK_REPO" --limit 1 --json databaseId --jq '.[0].databaseId')
   echo "run: $RID"
   gh run watch "$RID" --repo "$HAP_FORK_REPO" --exit-status
   ```
   On failure: `gh run view "$RID" --repo "$HAP_FORK_REPO" --log-failed`, fix, push again.

4. **Download the Release artifact** locally and sanity-check it:
   ```bash
   set -a; . ./.env; set +a
   rm -rf /tmp/hap-drop
   gh run download "$RID" --repo "$HAP_FORK_REPO" --name HuntAndPeck-Release --dir /tmp/hap-drop
   ls /tmp/hap-drop/hap.exe /tmp/hap-drop/hap.exe.config
   ```
   Verify the shipped `hap.exe.config` has the keys you expect (e.g.
   `grep -nE 'key="(HotkeyKey|HintCharacters|TimingLogEnabled)"' /tmp/hap-drop/hap.exe.config`).

5. **rsync to the Windows box** into the fresh folder:
   ```bash
   set -a; . ./.env; set +a
   FOLDER=hap-<suffix>
   ssh -o StrictHostKeyChecking=accept-new "$HAP_WIN_USER@$HAP_WIN_HOST" "mkdir -p '$HAP_WIN_TEST_DIR/$FOLDER'"
   rsync -az --delete /tmp/hap-drop/ "$HAP_WIN_USER@$HAP_WIN_HOST:$HAP_WIN_TEST_DIR/$FOLDER/"
   ssh "$HAP_WIN_USER@$HAP_WIN_HOST" "ls '$HAP_WIN_TEST_DIR/$FOLDER/hap.exe'"
   ```

6. **Report** the Windows path (`$HAP_WIN_TEST_DIR/$FOLDER/hap.exe`) and remind the
   user to **quit the old `hap.exe` (tray → Exit) before launching**. Code changes
   and the global hotkey are **not** hot-reloadable — a restart is required.
   (Hot-reloadable settings can still be tuned by editing the shipped
   `hap.exe.config` and re-triggering, no restart.)
