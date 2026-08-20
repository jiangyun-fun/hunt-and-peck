using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using HuntAndPeck.Models;
using HuntAndPeck.Services;
using HuntAndPeck.ViewModels;
using Xunit;

namespace HuntAndPeck.Tests.ViewModels
{
    public class OverlayViewModelTest
    {
        [Fact]
        public void GroupView_GridSession_DefaultsOn_AndToggles()
        {
            // 40 PointHints in a row: labels are 1-2 chars (capacity > 40), so the
            // session is groupable and GroupViewEnabled defaults true.
            var hints = new List<Hint>();
            for (int i = 0; i < 40; i++)
            {
                hints.Add(new PointHint(IntPtr.Zero, new Rect(i * 10, 0, 8, 8), new Point(i * 10, 4)));
            }
            var session = new HintSession
            {
                Hints = hints,
                OwningWindow = IntPtr.Zero,
                OwningWindowBounds = new Rect(0, 0, 400, 100)
            };
            var vm = new OverlayViewModel(session, new HintLabelService());

            // On by default: one box per distinct label first char, no prefix typed.
            Assert.True(vm.GroupView);
            Assert.NotNull(vm.GroupBoxes);
            Assert.Equal(vm.Hints.Select(h => h.Label[0]).Distinct().Count(), vm.GroupBoxes.Count);
            Assert.Equal(0, vm.MatchLength);

            // <leader>p semantics: off -> boxes null; back on -> boxes return.
            vm.ToggleGroupView();
            Assert.False(vm.GroupView);
            Assert.Null(vm.GroupBoxes);

            vm.ToggleGroupView();
            Assert.True(vm.GroupView);
            Assert.NotNull(vm.GroupBoxes);
        }

        [Fact]
        public void Leader_PreservesTypedPrefix_SnapshotClearsIt()
        {
            // The leader must NOT reset a typed prefix: dispatch branches on the
            // pending flag (the hook checks IsLeaderPending before label chars), so a
            // stale prefix never intercepts leader keys, and preserving it keeps the
            // user inside the drilled zone after e.g. <Space>r (mode switch). The
            // 2-pick phases clear the prefix on entry themselves.
            var hints = new List<Hint>();
            for (int i = 0; i < 40; i++)
            {
                hints.Add(new PointHint(IntPtr.Zero, new Rect(i * 10, 0, 8, 8), new Point(i * 10, 4)));
            }
            var vm = new OverlayViewModel(new HintSession
            {
                Hints = hints,
                OwningWindow = IntPtr.Zero,
                OwningWindowBounds = new Rect(0, 0, 400, 100)
            }, new HintLabelService());

            // Pick a first char shared by >= 2 labels so typing it narrows without
            // firing (a unique match would synthesize input).
            char first = vm.Hints.GroupBy(h => h.Label[0]).First(g => g.Count() >= 2).Key;
            vm.AppendLabelChar(first);
            Assert.Equal(1, vm.MatchLength);

            vm.EnterLeader();
            Assert.True(vm.IsLeaderPending);
            Assert.Equal(1, vm.MatchLength);      // preserved across the leader

            vm.LeaderCommand('1');                 // unmapped key: cancels the dispatcher only
            Assert.False(vm.IsLeaderPending);
            Assert.Equal(1, vm.MatchLength);      // still preserved after the leader closes

            vm.EnterSnapshotRegion();
            Assert.Equal(0, vm.MatchLength);      // 2-pick phases start fresh
        }

        [Fact]
        public void Snapshot_Complete_ClosesEvenInContinuousMode()
        {
            // d/t/v already close after completing in Continuous mode
            // (SelectionActionsClose=true default); snapshot must behave the same
            // (one-shot), not follow the raw trigger mode. Hints span 2D (4 rows x
            // 10 cols) so the two picked corners differ on BOTH axes -- same-row
            // corners are a zero-height rect, which is the documented no-op pick.
            var hints = new List<Hint>();
            for (int i = 0; i < 40; i++)
            {
                int x = (i % 10) * 10;
                int y = (i / 10) * 10;
                hints.Add(new PointHint(IntPtr.Zero, new Rect(x, y, 8, 8), new Point(x + 4, y + 4)));
            }
            var vm = new OverlayViewModel(new HintSession
            {
                Hints = hints,
                OwningWindow = IntPtr.Zero,
                OwningWindowBounds = new Rect(0, 0, 100, 40)
            }, new HintLabelService())
            {
                IsContinuous = true
            };
            Rect? captured = null;
            int closed = 0;
            vm.CaptureRegion = r => captured = r;
            vm.CloseOverlay = () => closed++;

            vm.EnterSnapshotRegion();
            Assert.Equal("SNAP 1/2", vm.SnapshotBadgeLabel);

            TypeLabel(vm, vm.Hints[0].Label);      // corner 1 (anchor)
            Assert.Equal("SNAP 2/2", vm.SnapshotBadgeLabel);

            TypeLabel(vm, vm.Hints[39].Label);     // corner 2: capture + close

            Assert.NotNull(captured);
            Assert.True(captured.Value.Width > 0);
            Assert.True(captured.Value.Height > 0);
            Assert.Equal(1, closed);               // closed even though IsContinuous
        }

        private static void TypeLabel(OverlayViewModel vm, string label)
        {
            foreach (char c in label)
            {
                vm.AppendLabelChar(c);
            }
        }

        [Fact]
        public void NormalizeRegion_CornersInOrder_YieldsPositiveRect()
        {
            var r = OverlayViewModel.NormalizeRegion(new Point(10, 20), new Point(110, 220));
            Assert.Equal(10, r.X);
            Assert.Equal(20, r.Y);
            Assert.Equal(100, r.Width);
            Assert.Equal(200, r.Height);
        }

        [Fact]
        public void NormalizeRegion_CornersReversed_NormalizesToSameRect()
        {
            // Corner-entry order must not matter.
            var r = OverlayViewModel.NormalizeRegion(new Point(110, 220), new Point(10, 20));
            Assert.Equal(10, r.X);
            Assert.Equal(20, r.Y);
            Assert.Equal(100, r.Width);
            Assert.Equal(200, r.Height);
        }

        [Fact]
        public void NormalizeRegion_NegativeCoords_Normalized()
        {
            // A monitor left of the primary has negative coords; both axes must normalize.
            var r = OverlayViewModel.NormalizeRegion(new Point(-200, -100), new Point(-50, 0));
            Assert.Equal(-200, r.X);
            Assert.Equal(-100, r.Y);
            Assert.Equal(150, r.Width);
            Assert.Equal(100, r.Height);
        }

        [Fact]
        public void NormalizeRegion_SamePoint_IsDegenerateZero()
        {
            var r = OverlayViewModel.NormalizeRegion(new Point(50, 50), new Point(50, 50));
            Assert.Equal(0, r.Width);
            Assert.Equal(0, r.Height);
        }

        [Fact]
        public void SwitchToMonitor_MovesToMatchingSession_AndResetsState()
        {
            // Landscape primary + PORTRAIT secondary (1080x1920 at x=1920) -- the
            // mixed-orientation setup focus-follow must handle. Each session was
            // built for its own monitor, so switching is a pure swap: Bounds moves,
            // the portrait session's labels load, and prefix + pan reset (Tab
            // semantics).
            var landscape = new Rect(0, 0, 1920, 1080);
            var portrait = new Rect(1920, 0, 1080, 1920);
            var vm = new OverlayViewModel(new List<HintSession>
            {
                MonitorSession(landscape, 40),
                MonitorSession(portrait, 60)
            }, 0, new HintLabelService());
            Assert.True(vm.CanFollowForegroundMonitor);

            char first = vm.Hints.GroupBy(h => h.Label[0]).First(g => g.Count() >= 2).Key;
            vm.AppendLabelChar(first);
            vm.OffsetX = 42;
            vm.OffsetY = 7;

            vm.SwitchToMonitor(portrait);

            Assert.Equal(portrait, vm.Bounds);       // overlay moved
            Assert.Equal(60, vm.Hints.Count);        // portrait session's labels
            Assert.Equal(0, vm.MatchLength);         // prefix cleared
            Assert.Equal(0, vm.OffsetX);             // pan reset
            Assert.Equal(0, vm.OffsetY);

            vm.SwitchToMonitor(landscape);           // and back again
            Assert.Equal(landscape, vm.Bounds);
            Assert.Equal(40, vm.Hints.Count);
        }

        [Fact]
        public void SwitchToMonitor_SameOrUnknownMonitor_IsNoOp()
        {
            var landscape = new Rect(0, 0, 1920, 1080);
            var portrait = new Rect(1920, 0, 1080, 1920);
            var vm = new OverlayViewModel(new List<HintSession>
            {
                MonitorSession(landscape, 40),
                MonitorSession(portrait, 60)
            }, 1, new HintLabelService());

            char first = vm.Hints.GroupBy(h => h.Label[0]).First(g => g.Count() >= 2).Key;
            vm.AppendLabelChar(first);

            // Same monitor (a focus event within the viewed monitor): the typed
            // prefix must survive.
            vm.SwitchToMonitor(portrait);
            Assert.Equal(1, vm.MatchLength);

            // Unknown bounds (no session covers that monitor): no-op.
            vm.SwitchToMonitor(new Rect(-3840, 0, 3840, 2160));
            Assert.Equal(1, vm.MatchLength);
            Assert.Equal(portrait, vm.Bounds);
            Assert.Equal(60, vm.Hints.Count);
        }

        [Fact]
        public void SwitchToMonitor_SingleSession_OrQuadrantMode_CannotFollow()
        {
            // Single-session (Automation / Grid+Window): never follow-capable.
            var bounds = new Rect(0, 0, 1920, 1080);
            var single = new OverlayViewModel(MonitorSession(bounds, 40), new HintLabelService());
            Assert.False(single.CanFollowForegroundMonitor);
            single.SwitchToMonitor(bounds);
            Assert.Equal(bounds, single.Bounds);

            // Quadrant mode WITHOUT a rebuild delegate (not wired): all four sessions
            // share one monitor's bounds, so a bounds match could not pick the right
            // quadrant -- follow must stay gated off (no-op; stays on quadrant index 2).
            var quad = new OverlayViewModel(new List<HintSession>
            {
                MonitorSession(bounds, 40),
                MonitorSession(bounds, 50),
                MonitorSession(bounds, 60),
                MonitorSession(bounds, 70)
            }, 2, new HintLabelService())
            {
                IsQuadrantMode = true
            };
            Assert.False(quad.CanFollowForegroundMonitor);
            quad.SwitchToMonitor(bounds);
            Assert.Equal(60, quad.Hints.Count);
        }

        [Fact]
        public void SwitchToMonitor_QuadrantFollow_RebuildsForNewMonitor_KeepsQuadrant()
        {
            // Quadrant follow with the rebuild delegate wired: a change of monitor
            // REBUILDS the four quadrant sessions for it (portrait bounds here) and
            // keeps the quadrant the user is on (index 2), resetting prefix + pan.
            // Same-monitor and null-rebuild switches are no-ops.
            var bounds = new Rect(0, 0, 1920, 1080);
            var portrait = new Rect(1920, 0, 1080, 1920);
            var quad = new OverlayViewModel(new List<HintSession>
            {
                MonitorSession(bounds, 40),
                MonitorSession(bounds, 50),
                MonitorSession(bounds, 60),
                MonitorSession(bounds, 70)
            }, 2, new HintLabelService())
            {
                IsQuadrantMode = true,
                RebuildForMonitor = r => new List<HintSession>
                {
                    MonitorSession(r, 80),
                    MonitorSession(r, 90),
                    MonitorSession(r, 100),
                    MonitorSession(r, 110)
                }
            };
            Assert.True(quad.CanFollowForegroundMonitor);

            char first = quad.Hints.GroupBy(h => h.Label[0]).First(g => g.Count() >= 2).Key;
            quad.AppendLabelChar(first);
            quad.OffsetX = 11;

            quad.SwitchToMonitor(portrait);

            Assert.Equal(portrait, quad.Bounds);     // overlay moved to the portrait monitor
            Assert.Equal(100, quad.Hints.Count);     // quadrant index 2 kept, rebuilt session
            Assert.Equal(0, quad.MatchLength);       // prefix cleared (Tab semantics)
            Assert.Equal(0, quad.OffsetX);           // pan reset
            Assert.Equal("Q3/4", quad.QuadrantLabel); // badge still reads the same quadrant

            // Same monitor again: a focus event within the viewed monitor must not
            // disturb a typed prefix.
            char again = quad.Hints.GroupBy(h => h.Label[0]).First(g => g.Count() >= 2).Key;
            quad.AppendLabelChar(again);
            quad.SwitchToMonitor(portrait);
            Assert.Equal(1, quad.MatchLength);

            // A rebuild that comes back null (degenerate monitor): no-op.
            quad.RebuildForMonitor = r => null;
            var other = new Rect(-3840, 0, 3840, 2160);
            quad.SwitchToMonitor(other);
            Assert.Equal(portrait, quad.Bounds);
            Assert.Equal(100, quad.Hints.Count);
        }

        /// <summary>
        /// A grid-like session covering <paramref name="monitorBounds"/> with
        /// pointCount PointHints (enough that some labels are 2-char, so a typed
        /// shared first char narrows without firing -- see the existing leader test).
        /// </summary>
        private static HintSession MonitorSession(Rect monitorBounds, int pointCount)
        {
            var hints = new List<Hint>();
            for (int i = 0; i < pointCount; i++)
            {
                int x = (int)(monitorBounds.Left + (i % 10) * 10);
                int y = (int)(monitorBounds.Top + (i / 10) * 10);
                hints.Add(new PointHint(IntPtr.Zero, new Rect(x, y, 8, 8), new Point(x + 4, y + 4)));
            }
            return new HintSession
            {
                Hints = hints,
                OwningWindow = IntPtr.Zero,
                OwningWindowBounds = monitorBounds
            };
        }
    }
}
