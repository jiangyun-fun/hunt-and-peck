# Arrow-Key Cursor Nudging + Real-Click Default — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the hint action with a two-mode model — completing a hint moves + real-clicks by default; pressing `Space` first enters a continuous move-only positioning mode (jump by label, nudge with arrows, click manually).

**Architecture:** The overlay (`OverlayView`, a `ForegroundWindow`) already closes itself on `Deactivated` (`ForegroundWindow.cs:28-35`). We make the overlay mouse-click-through via `WS_EX_TRANSPARENT` so that (a) a synthesized real click in default mode falls through to the app, and (b) the user's real click in move-only mode falls through, activates the app, and the resulting `Deactivated` closes the overlay. Keyboard focus is unaffected by `WS_EX_TRANSPARENT`, so label-typing keeps working while click-through is on. No timers, no global mouse hook.

**Tech Stack:** WPF, .NET Framework 4.5.1, C# 5/6, Win32 P/Invoke (`User32.dll`), xUnit 2.2.0.

**Build/test reality:** This project builds and runs only on Windows (WPF + COM interop + .NET Framework 4.5.1). On this Linux box you CANNOT build or run tests. All "build"/"run test" steps are executed on Windows or via the GitHub Actions CI (`.github/workflows/build.yml`). The plan marks each verification step with where it runs. Automated testing is limited to pure logic (Task 2); UI/P-Invoke behavior is verified by CI compile + the manual matrix in Task 7.

**Spec:** `docs/superpowers/specs/2026-06-29-arrow-nudge-cursor-design.md`

**Non-SDK csproj note:** Both `HuntAndPeck.csproj` and `HuntAndPeck.Tests.csproj` use explicit `<Compile Include>` lists. Every new `.cs` file MUST be added to the relevant csproj, or it will not compile.

**XML comment rule:** `App.config` is XML. XML comments `<!-- ... -->` MUST NOT contain a double-hyphen `--` anywhere in their text (MSBuild error MSB3249). The provided comment text is already safe; do not add ranges like "3-15" or em-dashes inside comments.

---

## File map

| File | Responsibility | Action |
|------|----------------|--------|
| `src/NativeMethods/User32.cs` | Win32 P/Invoke declarations | Add `GetCursorPos`, `mouse_event` (+flags), `GetWindowLong`/`SetWindowLong` (+constants) |
| `src/HuntAndPeck/Services/OverlayActionConfig.cs` | Pure config parsing for click mode + nudge steps | Create |
| `src/HuntAndPeck.Tests/Services/OverlayActionConfigTest.cs` | Unit tests for the pure parsers | Create |
| `src/HuntAndPeck/HuntAndPeck.csproj` | Build items | Add `OverlayActionConfig.cs` |
| `src/HuntAndPeck.Tests/HuntAndPeck.Tests.csproj` | Build items | Add `OverlayActionConfigTest.cs` |
| `src/HuntAndPeck/ViewModels/OverlayViewModel.cs` | Hint-match state machine, mode flag, callbacks | Modify |
| `src/HuntAndPeck/Views/OverlayView.xaml` | Overlay visuals | Add move-only indicator `TextBlock` |
| `src/HuntAndPeck/Views/OverlayView.xaml.cs` | Key handling, click-through, click synthesis | Modify |
| `src/HuntAndPeck/App.config` | Shipped defaults | Replace `HintAction` with `ClickMode`; add `NudgeStep`/`NudgeStepFast` |

---

## Task 1: Add the Win32 P/Invoke surface

**Files:**
- Modify: `src/NativeMethods/User32.cs`

- [ ] **Step 1: Append the new declarations**

Add the following to the `User32` static class in `src/NativeMethods/User32.cs`, immediately after the existing `SetCursorPos` declaration (currently the last member, at `User32.cs:47-49`):

```csharp
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
```

`POINT` is already defined (`src/NativeMethods/POINT.cs`, fields `X`/`Y`). `GetWindowLong`/`SetWindowLong` are correct for `GWL_EXSTYLE` on 64-bit (the extended style fits in 32 bits).

- [ ] **Step 2: Verify it compiles**

Run (Windows/CI): `msbuild src\HuntAndPeck\HuntAndPeck.csproj /p:Configuration=Debug /v:minimal`
Expected: BUILD succeeded (no new errors). This only adds declarations, so nothing references them yet.

- [ ] **Step 3: Commit**

```bash
git add src/NativeMethods/User32.cs
git commit -m "feat(uia): add GetCursorPos, mouse_event, window-extended-style P/Invoke"
```

---

## Task 2 (TDD): Pure config parsing — `OverlayActionConfig`

This is the one piece of logic that is genuinely unit-testable. Parsing lives in a pure static method; the `ConfigurationManager`-reading wrappers delegate to it.

**Files:**
- Create: `src/HuntAndPeck/Services/OverlayActionConfig.cs`
- Create: `src/HuntAndPeck.Tests/Services/OverlayActionConfigTest.cs`
- Modify: `src/HuntAndPeck/HuntAndPeck.csproj`
- Modify: `src/HuntAndPeck.Tests/HuntAndPeck.Tests.csproj`

- [ ] **Step 1: Write the failing tests**

Create `src/HuntAndPeck.Tests/Services/OverlayActionConfigTest.cs`:

```csharp
using HuntAndPeck.Services;
using Xunit;

namespace HuntAndPeck.Tests.Services
{
    public class OverlayActionConfigTest
    {
        [Theory]
        [InlineData(null, ClickMode.RealClick)]
        [InlineData("", ClickMode.RealClick)]
        [InlineData("RealClick", ClickMode.RealClick)]
        [InlineData("realclick", ClickMode.RealClick)]
        [InlineData("Invoke", ClickMode.Invoke)]
        [InlineData("invoke", ClickMode.Invoke)]
        [InlineData("something-else", ClickMode.RealClick)]
        public void ParseClickMode_DefaultsToRealClick_ExceptExplicitInvoke(string raw, ClickMode expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ParseClickMode(raw));
        }

        [Theory]
        [InlineData("3", 7, 3)]
        [InlineData("15", 7, 15)]
        [InlineData("0", 7, 7)]      // non-positive falls back to default
        [InlineData("-5", 7, 7)]
        [InlineData("not-a-number", 7, 7)]
        [InlineData(null, 7, 7)]
        public void ParseInt_UsesDefaultWhenInvalidOrNonPositive(string raw, int defaultValue, int expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ParseInt(raw, defaultValue));
        }
    }
}
```

- [ ] **Step 2: Register the test file in the test csproj**

In `src/HuntAndPeck.Tests/HuntAndPeck.Tests.csproj`, inside the `<ItemGroup>` that contains `<Compile Include="Services\HintLabelServiceTest.cs" />` (around line 62-64), add:

```xml
    <Compile Include="Services\OverlayActionConfigTest.cs" />
```

- [ ] **Step 3: Run the tests to verify they fail**

Run (Windows/CI):
`vstest.console.exe src\HuntAndPeck.Tests\bin\Debug\HuntAndPeck.Tests.dll /TestCaseFilter:"FullyQualifiedName~OverlayActionConfigTest"`
Expected: FAIL — the type `OverlayActionConfig` does not exist (compile error in the test project).

- [ ] **Step 4: Write the implementation**

Create `src/HuntAndPeck/Services/OverlayActionConfig.cs`:

```csharp
using System;
using System.Configuration;

namespace HuntAndPeck.Services
{
    /// <summary>
    /// What happens when the user completes a hint (types its full label).
    /// </summary>
    public enum ClickMode
    {
        /// <summary>Move the cursor onto the target and fire a real left click.</summary>
        RealClick,

        /// <summary>Call the UI Automation Invoke pattern on the target.</summary>
        Invoke
    }

    /// <summary>
    /// Reads the click-action and nudge settings from hap.exe.config. Parsing is
    /// split into pure methods (unit-tested) and ConfigurationManager wrappers.
    /// Unknown or missing values fall back to safe defaults so a bad config never
    /// breaks the overlay.
    /// </summary>
    public static class OverlayActionConfig
    {
        /// <summary>Indicator text shown in move-only mode.</summary>
        public const string MoveOnlyHint =
            "move-only  |  arrows = nudge (shift = fast)  |  type a label = jump  |  click or Esc = done";

        /// <summary>Parses the ClickMode setting; only "Invoke" yields Invoke, everything else RealClick.</summary>
        public static ClickMode ParseClickMode(string raw)
        {
            if (string.Equals(raw, "Invoke", StringComparison.OrdinalIgnoreCase))
            {
                return ClickMode.Invoke;
            }
            return ClickMode.RealClick;
        }

        /// <summary>Parses an integer; returns defaultValue when blank, non-numeric, or non-positive.</summary>
        public static int ParseInt(string raw, int defaultValue)
        {
            int v;
            if (int.TryParse(raw, out v) && v > 0)
            {
                return v;
            }
            return defaultValue;
        }

        /// <summary>Reads ClickMode from appSettings (hot-reload). Defaults to RealClick.</summary>
        public static ClickMode ReadClickMode()
        {
            try
            {
                ConfigurationManager.RefreshSection("appSettings");
                return ParseClickMode(ConfigurationManager.AppSettings["ClickMode"]);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return ClickMode.RealClick;
            }
        }

        /// <summary>Reads the plain-arrow nudge step (px). Default 3.</summary>
        public static int ReadNudgeStep()
        {
            return ReadIntSetting("NudgeStep", 3);
        }

        /// <summary>Reads the Shift+arrow nudge step (px). Default 15.</summary>
        public static int ReadNudgeStepFast()
        {
            return ReadIntSetting("NudgeStepFast", 15);
        }

        private static int ReadIntSetting(string key, int defaultValue)
        {
            try
            {
                ConfigurationManager.RefreshSection("appSettings");
                return ParseInt(ConfigurationManager.AppSettings[key], defaultValue);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return defaultValue;
            }
        }
    }
}
```

- [ ] **Step 5: Register the new file in the main csproj**

In `src/HuntAndPeck/HuntAndPeck.csproj`, inside the `<ItemGroup>` that lists the other Services compiles (the one with `<Compile Include="Services\HintLabelService.cs" />`, around line 100), add:

```xml
    <Compile Include="Services\OverlayActionConfig.cs" />
```

- [ ] **Step 6: Build both projects**

Run (Windows/CI):
`msbuild src\HuntAndPeck.sln /p:Configuration=Debug /v:minimal`
Expected: BUILD succeeded for both projects.

- [ ] **Step 7: Run the tests to verify they pass**

Run (Windows/CI):
`vstest.console.exe src\HuntAndPeck.Tests\bin\Debug\HuntAndPeck.Tests.dll /TestCaseFilter:"FullyQualifiedName~OverlayActionConfigTest"`
Expected: PASS (7 test cases).

- [ ] **Step 8: Commit**

```bash
git add src/HuntAndPeck/Services/OverlayActionConfig.cs \
        src/HuntAndPeck.Tests/Services/OverlayActionConfigTest.cs \
        src/HuntAndPeck/HuntAndPeck.csproj \
        src/HuntAndPeck.Tests/HuntAndPeck.Tests.csproj
git commit -m "feat(config): add OverlayActionConfig with tested click-mode/nudge parsing"
```

---

## Task 3: Rework `OverlayViewModel` into the two-mode state machine

**Files:**
- Modify: `src/HuntAndPeck/ViewModels/OverlayViewModel.cs`

The contract established here is consumed by Task 5 (the view). Names must match exactly: callbacks `PerformClickAndClose` (`Action<Point>`), `ResetInput` (`Action`); methods `EnterMoveOnlyMode()`, `Nudge(int dx, int dy)`; properties `IsMoveOnlyMode` (INPC `bool`), `MoveOnlyHint` (`string`).

- [ ] **Step 1: Update the `using` directives**

At the top of `src/HuntAndPeck/ViewModels/OverlayViewModel.cs`, replace the `using System.Configuration;` line with the two namespaces we now need. The final `using` block should read:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Windows;
using HuntAndPeck.Models;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services;
using HuntAndPeck.Services.Interfaces;
```

(`System.Configuration` is no longer used here — config reading moved to `OverlayActionConfig`.)

- [ ] **Step 2: Add fields, properties, and callbacks**

Inside the class `OverlayViewModel` (e.g., right after the existing `_bounds` / `_hints` field declarations near the top), add:

```csharp
        private bool _isMoveOnlyMode;
        private int _nudgeX;
        private int _nudgeY;
```

Add the callback properties next to the existing `public Action CloseOverlay { get; set; }`:

```csharp
        /// <summary>
        /// Default mode: move the cursor onto the matched target, synthesize a real
        /// left click there, then close. Implemented by the view (click-through +
        /// mouse_event). Null in Invoke mode.
        /// </summary>
        public Action<Point> PerformClickAndClose { get; set; }

        /// <summary>
        /// Move-only mode: clear the TextBox so the next label can be typed fresh.
        /// Implemented by the view.
        /// </summary>
        public Action ResetInput { get; set; }
```

Add the INPC mode property and the hint string (e.g., after the `Bounds` property):

```csharp
        /// <summary>True once Space has been pressed: continuous move-only positioning.</summary>
        public bool IsMoveOnlyMode
        {
            get { return _isMoveOnlyMode; }
            private set { _isMoveOnlyMode = value; NotifyOfPropertyChange(); }
        }

        /// <summary>Indicator text shown by the view while in move-only mode.</summary>
        public string MoveOnlyHint => OverlayActionConfig.MoveOnlyHint;
```

- [ ] **Step 3: Add `EnterMoveOnlyMode` and `Nudge`**

Add these public methods (e.g., after the `MatchString` setter):

```csharp
        /// <summary>
        /// Enters move-only mode (Space): the overlay stays open, becomes
        /// click-through, and never auto-clicks. Captures the current cursor
        /// position so arrows can nudge from there.
        /// </summary>
        public void EnterMoveOnlyMode()
        {
            if (_isMoveOnlyMode)
            {
                return;
            }
            IsMoveOnlyMode = true;
            POINT p;
            User32.GetCursorPos(out p);
            _nudgeX = p.X;
            _nudgeY = p.Y;
        }

        /// <summary>
        /// Nudges the cursor by (dx, dy) in physical pixels, then clears the input
        /// so the next label starts fresh. Move-only mode only.
        /// </summary>
        public void Nudge(int dx, int dy)
        {
            _nudgeX += dx;
            _nudgeY += dy;
            User32.SetCursorPos(_nudgeX, _nudgeY);
            ResetInput?.Invoke();
        }
```

- [ ] **Step 4: Replace the `MatchString` setter**

Replace the entire current `MatchString` setter (the block starting `public string MatchString { set { ... } }`) with:

```csharp
        public string MatchString
        {
            set
            {
                var matching = Hints.Where(x => x.Label.StartsWith(value, StringComparison.OrdinalIgnoreCase)).ToList();
                var matchingSet = new HashSet<HintViewModel>(matching);

                // Only flip hints whose Active state actually changes, so we don't
                // raise PropertyChanged (and trigger WPF binding/layout work) for
                // every hint on each keystroke.
                foreach (var x in Hints)
                {
                    bool shouldMatch = matchingSet.Contains(x);
                    if (x.Active != shouldMatch)
                    {
                        x.Active = shouldMatch;
                    }
                }

                if (matching.Count == 1)
                {
                    var target = matching[0].Hint;
                    target.MoveMouseToCenter();

                    // Capture the real landed cursor position so nudging and the
                    // click use it (consistent for PointHint and UIA hints).
                    POINT p;
                    User32.GetCursorPos(out p);
                    _nudgeX = p.X;
                    _nudgeY = p.Y;

                    if (_isMoveOnlyMode)
                    {
                        // Jump only; clear input for the next label; never click.
                        ResetInput?.Invoke();
                    }
                    else if (OverlayActionConfig.ReadClickMode() == ClickMode.Invoke)
                    {
                        target.Invoke();
                        CloseOverlay?.Invoke();
                    }
                    else
                    {
                        // RealClick (default): synthesize a real left click at the target.
                        PerformClickAndClose?.Invoke(new Point(_nudgeX, _nudgeY));
                    }
                }
            }
        }
```

- [ ] **Step 5: Remove the obsolete helper**

Delete the entire `ShouldMoveMouseInsteadOfClick()` private method (the old `ConfigurationManager`-based reader). It is no longer referenced.

- [ ] **Step 6: Build**

Run (Windows/CI): `msbuild src\HuntAndPeck.sln /p:Configuration=Debug /v:minimal`
Expected: BUILD succeeded. (Warnings about `PerformClickAndClose`/`ResetInput` never being assigned are expected — Task 5 assigns them.)

- [ ] **Step 7: Commit**

```bash
git add src/HuntAndPeck/ViewModels/OverlayViewModel.cs
git commit -m "feat(overlay): two-mode state machine, real-click default, move-only mode"
```

---

## Task 4: Add the move-only indicator to the overlay XAML

**Files:**
- Modify: `src/HuntAndPeck/Views/OverlayView.xaml`

- [ ] **Step 1: Insert the indicator TextBlock**

In `src/HuntAndPeck/Views/OverlayView.xaml`, inside `<Grid x:Name="layoutGrid">`, as the FIRST child (before the existing `<TextBox x:Name="MatchStringControl" ...>`), add:

```xml
        <TextBlock Text="{Binding MoveOnlyHint}"
                   FontFamily="Helvetica, Arial" FontWeight="Bold" FontSize="14"
                   Foreground="Black" Background="Yellow"
                   HorizontalAlignment="Left" VerticalAlignment="Top" Margin="4">
            <TextBlock.Style>
                <Style TargetType="TextBlock">
                    <Setter Property="Visibility" Value="Collapsed" />
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsMoveOnlyMode}" Value="True">
                            <Setter Property="Visibility" Value="Visible" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>
```

The visibility is controlled by the Style (base `Collapsed`, `Visible` when `IsMoveOnlyMode`), not by a local attribute, so the DataTrigger takes effect.

- [ ] **Step 2: Build**

Run (Windows/CI): `msbuild src\HuntAndPeck.sln /p:Configuration=Debug /v:minimal`
Expected: BUILD succeeded (XAML compiles).

- [ ] **Step 3: Commit**

```bash
git add src/HuntAndPeck/Views/OverlayView.xaml
git commit -m "feat(overlay): add move-only mode indicator"
```

---

## Task 5: Wire key handling, click-through, and click synthesis in the view

**Files:**
- Modify: `src/HuntAndPeck/Views/OverlayView.xaml.cs`

This task consumes the contract from Task 3 and the P/Invoke from Task 1.

- [ ] **Step 1: Update `using` directives**

At the top of `src/HuntAndPeck/Views/OverlayView.xaml.cs`, make the `using` block read:

```csharp
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services;
using HuntAndPeck.ViewModels;
```

(Adds `System.Windows.Interop`, `HuntAndPeck.NativeMethods`, `HuntAndPeck.Services`.)

- [ ] **Step 2: Wire the callbacks in `OnLoaded`**

In `OverlayView_OnLoaded`, after the existing `Height = vm.Bounds.Height / scaleY;` line, append:

```csharp
            if (vm != null)
            {
                // Default mode: click-through so the synthesized click reaches the
                // app beneath, re-position exactly, fire a real left click, then close.
                vm.PerformClickAndClose = p =>
                {
                    SetClickThrough(true);
                    User32.SetCursorPos((int)p.X, (int)p.Y);
                    DoLeftClick();
                    Close();
                };

                // Move-only mode: clear the TextBox between labels.
                vm.ResetInput = () => MatchStringControl.Clear();
            }
```

(`vm.CloseOverlay` is already assigned in `App.xaml.cs:26`, so do not set it here.)

- [ ] **Step 3: Rewrite `OverlayView_OnPreviewKeyDown`**

Replace the entire current `OverlayView_OnPreviewKeyDown` method with:

```csharp
        private void OverlayView_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var vm = DataContext as OverlayViewModel;

            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }

            if (vm != null && vm.IsMoveOnlyMode)
            {
                int step = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
                    ? OverlayActionConfig.ReadNudgeStepFast()
                    : OverlayActionConfig.ReadNudgeStep();

                switch (e.Key)
                {
                    case Key.Up:    vm.Nudge(0, -step); e.Handled = true; return;
                    case Key.Down:  vm.Nudge(0,  step); e.Handled = true; return;
                    case Key.Left:  vm.Nudge(-step, 0); e.Handled = true; return;
                    case Key.Right: vm.Nudge( step, 0); e.Handled = true; return;
                }

                // Let letters through to the TextBox to drive MatchString; swallow
                // everything else (Space, Enter, etc.) so the input stays clean.
                if (!IsLabelKey(e.Key))
                {
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Key.Space && vm != null)
            {
                vm.EnterMoveOnlyMode();
                SetClickThrough(true);
                e.Handled = true;   // never let Space enter the TextBox
                return;
            }

            // Default mode: let letters through to the TextBox.
        }

        private static bool IsLabelKey(Key key)
        {
            return key >= Key.A && key <= Key.Z;
        }
```

- [ ] **Step 4: Add the `SetClickThrough` and `DoLeftClick` helpers**

Add these methods to the `OverlayView` class:

```csharp
        /// <summary>
        /// Toggles WS_EX_TRANSPARENT on the overlay HWND. When on, the window is
        /// transparent to MOUSE hit-testing only (clicks fall through to the app
        /// beneath) while keyboard focus is unaffected, so typing keeps working.
        /// </summary>
        private void SetClickThrough(bool on)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ext = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
            if (on)
            {
                ext |= User32.WS_EX_TRANSPARENT;
            }
            else
            {
                ext &= ~User32.WS_EX_TRANSPARENT;
            }
            User32.SetWindowLong(hwnd, User32.GWL_EXSTYLE, ext);
        }

        /// <summary>Fires a real left click at the current cursor position.</summary>
        private static void DoLeftClick()
        {
            User32.mouse_event(User32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            User32.mouse_event(User32.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }
```

- [ ] **Step 5: Build**

Run (Windows/CI): `msbuild src\HuntAndPeck.sln /p:Configuration=Debug /v:minimal`
Expected: BUILD succeeded, no warnings about unassigned callbacks.

- [ ] **Step 6: Commit**

```bash
git add src/HuntAndPeck/Views/OverlayView.xaml.cs
git commit -m "feat(overlay): Space move-only mode, arrow nudge, click-through real click"
```

---

## Task 6: Ship the new defaults in `App.config`

**Files:**
- Modify: `src/HuntAndPeck/App.config`

Remember: no `--` inside XML comments.

- [ ] **Step 1: Replace the `HintAction` block with `ClickMode`**

In `src/HuntAndPeck/App.config`, replace this block (currently lines 12-16):

```xml
        <!-- What happens when you type a unique hint's letters.
             "Click" invokes the target; "MoveMouse" moves the cursor onto the
             target without clicking (useful when Invoke doesn't fire; the user
             then nudges to click). Hot-reload: edit, save, press Alt+; . -->
        <add key="HintAction" value="MoveMouse" />
```

with:

```xml
        <!-- What happens when you complete a hint (type its full label).
             "RealClick" (default) moves the cursor onto the target and fires a
             real left click there. A real click works on Chromium and Electron
             apps (e.g. Feishu) where the UI Automation Invoke pattern does not
             fire. "Invoke" calls the UI Automation Invoke pattern instead. Press
             Space before typing a label to enter move-only mode: jump by label
             and nudge with arrows, then click manually. Hot-reload: edit, save,
             press Alt+; . -->
        <add key="ClickMode" value="RealClick" />
```

- [ ] **Step 2: Add the nudge-step settings**

Immediately after the `HintCharacters` `<add>` block (currently lines 46-49), add:

```xml

        <!-- Arrow-key nudge step (px) in move-only mode: NudgeStep for plain
             arrows, NudgeStepFast for Shift plus arrows. Hot-reload. -->
        <add key="NudgeStep" value="3" />
        <add key="NudgeStepFast" value="15" />
```

- [ ] **Step 3: Sanity-check for forbidden XML sequences**

Run (anywhere): `grep -n -- '--' src/HuntAndPeck/App.config`
Expected: only matches inside attribute values or the `<?xml` declaration line, NEVER inside a comment. If any `--` appears inside an XML comment, rewrite that comment text before building.

- [ ] **Step 4: Build**

Run (Windows/CI): `msbuild src\HuntAndPeck.sln /p:Configuration=Release /v:minimal`
Expected: BUILD succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/HuntAndPeck/App.config
git commit -m "feat(config): ClickMode=RealClick default, add NudgeStep/NudgeStepFast"
```

---

## Task 7: CI green + manual verification matrix + rsync

**Files:** none (verification + delivery)

- [ ] **Step 1: Push and confirm CI is green**

```bash
git push origin master
```
Watch `.github/workflows/build.yml` on `master`: the build + test run must succeed (the new `OverlayActionConfigTest` cases run as part of vstest).

- [ ] **Step 2: Build a Release drop locally (Windows) and rsync to the test folder**

Run (Windows): `msbuild src\HuntAndPeck\HuntAndPeck.csproj /p:Configuration=Release /v:minimal`
Then rsync the Release output (`hap.exe`, `hap.exe.config`, the DLLs) to the user's machine, e.g.:

```bash
rsync -az src/HuntAndPeck/bin/Release/ jiangy@10.10.10.222:/mnt/c/5.sibio/93.bk/20260627-144526.test/hap-nudge/
```

Use a fresh folder name (`hap-nudge`) — do not overwrite a running `hap.exe` (Windows file lock).

- [ ] **Step 3: Manual test matrix (run `hap.exe`, then for each: press `Alt+;`)**

Default config is `HintSource=Grid`, `ClickMode=RealClick`.

1. **Default real-click (native):** over a Notepad/control window, type a unique label → cursor moves to the target and it is clicked; overlay closes.
2. **Default real-click (Chromium):** over Feishu, type a label → the Feishu control is clicked (this is the regression we care about; `Invoke` did not fire here before).
3. **Move-only teleport:** press `Space` → indicator appears, overlay stays open, becomes click-through. Type a label → cursor jumps, no click, overlay stays; type a second label → cursor jumps again; TextBox resets after each jump.
4. **Arrow nudge:** in move-only mode, arrows move the cursor ~3 px; `Shift`+arrows ~15 px.
5. **Finish by real click:** in move-only mode, click the real mouse on a target → the target is clicked AND the overlay closes (deactivation).
6. **Cancel:** `Esc` closes the overlay in both modes with no click.
7. **Hot-reload:** edit `NudgeStep`/`NudgeStepFast`/`ClickMode` in the deployed `hap.exe.config`, save, re-trigger → new values apply without restart.

- [ ] **Step 4: Record results and commit any tuning**

If the default 3/15 px steps or the indicator text need tweaking, edit `App.config` (or `OverlayActionConfig` defaults) and re-rsync. Commit the final tuned values:

```bash
git add src/HuntAndPeck/App.config
git commit -m "chore(config): tune nudge step defaults after manual testing"
```

---

## Self-review notes

- **Spec coverage:** Mode 1 (move+real-click) → Task 3 setter + Task 5 `PerformClickAndClose`/`DoLeftClick`. Mode 2 (move-only: Space, label-jump, nudge, manual-click close) → Task 3 `EnterMoveOnlyMode`/`Nudge`/setter + Task 5 key handling + `SetClickThrough`. Click-through (`WS_EX_TRANSPARENT`) → Task 1 P/Invoke + Task 5 `SetClickThrough`. Deactivation close → already provided by `ForegroundWindow` base class (no new handler needed; spec updated in plan header). Config (`ClickMode`, `NudgeStep`, `NudgeStepFast`) → Task 2 + Task 6. Indicator → Task 4. Testing matrix → Task 7.
- **Type consistency:** `PerformClickAndClose` (`Action<Point>`), `ResetInput` (`Action`), `EnterMoveOnlyMode()`, `Nudge(int,int)`, `IsMoveOnlyMode`, `MoveOnlyHint` are used identically in Tasks 3 and 5. `POINT.X`/`POINT.Y` (capital) used in Task 3. `OverlayActionConfig` members (`ReadClickMode`, `ReadNudgeStep`, `ReadNudgeStepFast`, `MoveOnlyHint`, `ParseClickMode`, `ParseInt`, `ClickMode`) match across Tasks 2/3/5.
- **No placeholders:** every code step contains the full code; every build/test step has an exact command and expected result.
- **Scope:** single feature, one plan, produces working software after Task 7.
