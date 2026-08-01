using System.Collections.Generic;
using System.Windows;
using HuntAndPeck.Models;
using HuntAndPeck.Services;
using Xunit;

namespace HuntAndPeck.Tests.Services
{
    public class ZoneServiceTest
    {
        // ---- SliceIntoZones ----

        [Fact]
        public void SliceIntoZones_3x3_ProducesNineEqualCellsInScanOrder()
        {
            // Arrange
            var monitor = new Rect(0, 0, 300, 300);

            // Act
            var zones = ZoneService.SliceIntoZones(monitor, 3, 3);

            // Assert: 9 cells, scan order L->R then T->B. Zone 0 = top-left.
            Assert.Equal(9, zones.Length);
            Assert.Equal(new Rect(0, 0, 100, 100), zones[0]);     // TL
            Assert.Equal(new Rect(100, 0, 100, 100), zones[1]);   // top-middle
            Assert.Equal(new Rect(100, 100, 100, 100), zones[4]); // center
            Assert.Equal(new Rect(200, 200, 100, 100), zones[8]); // BR
        }

        [Fact]
        public void SliceIntoZones_1x1_ReturnsSingleWholeRect()
        {
            var monitor = new Rect(10, 20, 300, 400);

            var zones = ZoneService.SliceIntoZones(monitor, 1, 1);

            Assert.Single(zones);
            Assert.Equal(monitor, zones[0]);
        }

        [Fact]
        public void SliceIntoZones_NonZeroOrigin_OffsetsCells()
        {
            var monitor = new Rect(100, 200, 300, 300);

            var zones = ZoneService.SliceIntoZones(monitor, 3, 3);

            Assert.Equal(new Rect(100, 200, 100, 100), zones[0]);
            // BR cell (r=2, c=2): top-left = origin + 2*cell, still 100x100; its
            // bottom-right corner (400, 500) is the monitor's bottom-right corner.
            Assert.Equal(new Rect(300, 400, 100, 100), zones[8]);
        }

        [Fact]
        public void SliceIntoZones_NonPositiveDims_ClampedToOne()
        {
            // cols<=0 and rows<=0 collapse to 1x1 so a degenerate config never
            // produces an empty array.
            var zones = ZoneService.SliceIntoZones(new Rect(0, 0, 100, 100), 0, -3);

            Assert.Single(zones);
        }

        [Fact]
        public void SliceIntoZones_2x3_CountAndShape()
        {
            var zones = ZoneService.SliceIntoZones(new Rect(0, 0, 200, 300), 2, 3);

            Assert.Equal(6, zones.Length);
            Assert.Equal(new Rect(0, 0, 100, 100), zones[0]);
            Assert.Equal(new Rect(100, 200, 100, 100), zones[5]); // last row, second col
        }

        // ---- LabelToIndexMap ----

        [Fact]
        public void LabelToIndexMap_AssignsByFirstCharCaseInsensitive()
        {
            var labels = new List<string> { "a", "B", "c" };

            var map = ZoneService.LabelToIndexMap(labels);

            Assert.Equal(0, map['A']);
            Assert.Equal(1, map['B']); // already uppercase stored
            Assert.Equal(2, map['C']);
            Assert.Equal(3, map.Count);
        }

        [Fact]
        public void LabelToIndexMap_EmptyLabelsYieldsEmptyMap()
        {
            var map = ZoneService.LabelToIndexMap(new List<string>());

            Assert.Empty(map);
        }

        // ---- BuildPickSession ----

        [Fact]
        public void BuildPickSession_ProducesOneHintPerZoneWithMonitorBounds()
        {
            var monitor = new Rect(0, 0, 300, 300);

            var session = ZoneService.BuildPickSession(System.IntPtr.Zero, monitor, 3, 3, 10.0);

            Assert.Equal(9, session.Hints.Count);
            Assert.Equal(monitor, session.OwningWindowBounds);
        }

        [Fact]
        public void BuildPickSession_BoundingRectangleIsRelativeCenterOfZone()
        {
            // PointHint's BoundingRectangle.Left/Top IS the cursor target (HintCanvas
            // centers the pill on it), stored relative to the monitor. For zone 0 of a
            // 3x3 over (0,0,300,300), center is (50,50) -> Rect(50,50,10,10). For BR
            // (zone 8) center is (250,250).
            var monitor = new Rect(0, 0, 300, 300);

            var session = ZoneService.BuildPickSession(System.IntPtr.Zero, monitor, 3, 3, 10.0);

            Assert.Equal(new Rect(50, 50, 10, 10), session.Hints[0].BoundingRectangle);
            Assert.Equal(new Rect(250, 250, 10, 10), session.Hints[8].BoundingRectangle);
        }

        [Fact]
        public void BuildPickSession_HintsArePointHints()
        {
            var session = ZoneService.BuildPickSession(System.IntPtr.Zero, new Rect(0, 0, 300, 300), 3, 3, 10.0);

            // All pick hints must be PointHints so HintCanvas centers their pills and
            // ApplyMatch's MoveMouseToCenter lands via absolute coords.
            Assert.All(session.Hints, h => Assert.IsType<PointHint>(h));
        }
    }
}
