using HuntAndPeck.Extensions;
using HuntAndPeck.Models;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using UIAutomationClient;

namespace HuntAndPeck.Services
{
    internal class UiAutomationHintProviderService : IHintProviderService, IDebugHintProviderService
    {
        private readonly IUIAutomation _automation = new CUIAutomation();

        /// <summary>
        /// Per-window hint-session cache so repeated hotkey presses on the same window
        /// are instant. UI Automation enumeration of large trees (notably Chromium apps
        /// that expose 600+ controls) can take seconds; this avoids re-walking on
        /// repeat presses. Entries expire after <see cref="SessionCacheTtl"/>.
        /// </summary>
        private static readonly TimeSpan SessionCacheTtl = TimeSpan.FromSeconds(5);
        private readonly Dictionary<IntPtr, CacheEntry> _sessionCache = new Dictionary<IntPtr, CacheEntry>();
        private readonly object _sessionCacheLock = new object();

        private sealed class CacheEntry
        {
            public HintSession Session;
            public DateTime ExpiresAt;
        }

        public HintSession EnumHints()
        {
            var foregroundWindow = User32.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                return null;
            }
            return EnumHints(foregroundWindow);
        }

        public HintSession EnumHints(IntPtr hWnd)
        {
            // Grid mode: generate a synthetic grid of cursor-jump points covering the
            // window. Instant (no UI Automation walk), always fresh, works on any app.
            if (IsGridMode())
            {
                return EnumGridHints(hWnd);
            }

            // Fast path: return a cached session if this window was enumerated recently.
            lock (_sessionCacheLock)
            {
                if (_sessionCache.TryGetValue(hWnd, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
                {
                    // Cache hit: avoid the tree walk, but re-read each element's current
                    // bounding rectangle so hint positions follow scroll/content moves.
                    RefreshSessionPositions(cached.Session, hWnd);
                    return cached.Session;
                }
            }

            var session = EnumWindowHints(hWnd, CreateHint);

            if (session != null)
            {
                lock (_sessionCacheLock)
                {
                    _sessionCache[hWnd] = new CacheEntry
                    {
                        Session = session,
                        ExpiresAt = DateTime.UtcNow + SessionCacheTtl,
                    };
                }
            }

            return session;
        }

        /// <summary>
        /// Grid-mode enumeration with an explicit layout preset (the single-session path:
        /// Grid + Window, or whenever monitor cycling is off). Automation ignores the
        /// layout -- it has no grid concept -- and falls back to the real-control walk.
        /// A null layout also falls back to <see cref="EnumHints(IntPtr)"/> (flat keys).
        /// </summary>
        public HintSession EnumHints(IntPtr hWnd, GridLayout layout)
        {
            if (layout != null && IsGridMode())
            {
                return EnumGridHints(hWnd, ResolveOwningBounds(hWnd), layout);
            }
            return EnumHints(hWnd);
        }

        /// <summary>
        /// Refreshes a cached session's hint positions by re-reading each element's
        /// current bounding rectangle, without re-walking the automation tree. Best-effort:
        /// elements that have disappeared keep their last known bounds. Runs off the UI
        /// thread (the caller is Task.Run), so this does not freeze the app.
        /// </summary>
        private void RefreshSessionPositions(HintSession session, IntPtr hWnd)
        {
            try
            {
                Rect windowBounds = ResolveOwningBounds(hWnd);
                session.OwningWindowBounds = windowBounds;

                foreach (var hint in session.Hints)
                {
                    var element = hint.AutomationElement;
                    if (element == null)
                    {
                        continue;
                    }

                    try
                    {
                        var br = element.CurrentBoundingRectangle;
                        if (br.right > br.left && br.bottom > br.top)
                        {
                            var niceRect = new Rect(new Point(br.left, br.top), new Point(br.right, br.bottom));
                            var logicalRect = niceRect.PhysicalToLogicalRect(hWnd);
                            if (!logicalRect.IsEmpty)
                            {
                                hint.BoundingRectangle = niceRect.ScreenToWindowCoordinates(windowBounds);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Element may have gone; keep its last known bounds.
                    }
                }
            }
            catch (Exception)
            {
                // Best-effort refresh; fall back to cached positions.
            }
        }

        public HintSession EnumDebugHints()
        {
            var foregroundWindow = User32.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                return null;
            }
            return EnumDebugHints(foregroundWindow);
        }

        public HintSession EnumDebugHints(IntPtr hWnd)
        {
            return EnumWindowHints(hWnd, CreateDebugHint);
        }

        /// <summary>
        /// Enumerates all the hints from the given window
        /// </summary>
        /// <param name="hWnd">The window to get hints from</param>
        /// <param name="hintFactory">The factory to use to create each hint in the session</param>
        /// <returns>A hint session</returns>
        private HintSession EnumWindowHints(IntPtr hWnd, Func<IntPtr, Rect, IUIAutomationElement, Hint> hintFactory)
        {
            var result = new List<Hint>();
            var elements = EnumElements(hWnd);

            // Bounds the overlay covers: full monitor or window rect (HintBoundsSource).
            Rect windowBounds = ResolveOwningBounds(hWnd);

            foreach (var element in elements)
            {
                var boundingRectObject = element.CachedBoundingRectangle;
                if ((boundingRectObject.right > boundingRectObject.left) && (boundingRectObject.bottom > boundingRectObject.top))
                {
                    var niceRect = new Rect(new Point(boundingRectObject.left, boundingRectObject.top), new Point(boundingRectObject.right, boundingRectObject.bottom));
                    // Convert the bounding rect to logical coords
                    var logicalRect = niceRect.PhysicalToLogicalRect(hWnd);
                    if (!logicalRect.IsEmpty)
                    {
                        var windowCoords = niceRect.ScreenToWindowCoordinates(windowBounds);
                        var hint = hintFactory(hWnd, windowCoords, element);
                        if (hint != null)
                        {
                            hint.AutomationElement = element;
                            result.Add(hint);
                        }
                    }
                }
            }

            return new HintSession
            {
                Hints = result,
                OwningWindow = hWnd,
                OwningWindowBounds = windowBounds,
            };
        }

        /// <summary>
        /// Builds a cache request that pre-fetches the bounding rectangle and the
        /// patterns/properties inspected per element, so enumeration takes a single
        /// cross-process traversal instead of one round-trip per property and per
        /// pattern. See "Caching UI Automation Properties and Control Patterns"
        /// (Microsoft Win32 docs). This is the dominant cost when populating hints.
        /// </summary>
        /// <returns>A cache request covering the properties/patterns hints use</returns>
        private IUIAutomationCacheRequest CreateHintCacheRequest(bool includeFilterProperties)
        {
            var cacheRequest = _automation.CreateCacheRequest();

            // Cache the matching elements themselves (not their descendants).
            cacheRequest.TreeScope = TreeScope.TreeScope_Element;

            // Keep the default AutomationElementMode (Full) so the cached patterns
            // remain usable for Invoke()/Toggle()/etc. at click time. Setting it to
            // None would make pattern methods such as Invoke() unavailable.

            // Properties. AddPattern caches the pattern object but not its
            // properties, so the two IsReadOnly values must be added explicitly.
            cacheRequest.AddProperty(UIA_PropertyIds.UIA_BoundingRectanglePropertyId);
            if (includeFilterProperties)
            {
                // Needed to filter enabled/on-screen during the depth-limited walk.
                cacheRequest.AddProperty(UIA_PropertyIds.UIA_IsEnabledPropertyId);
                cacheRequest.AddProperty(UIA_PropertyIds.UIA_IsOffscreenPropertyId);
            }
            cacheRequest.AddProperty(UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId);
            cacheRequest.AddProperty(UIA_PropertyIds.UIA_RangeValueIsReadOnlyPropertyId);

            // Patterns (read later via GetCachedPattern).
            cacheRequest.AddPattern(UIA_PatternIds.UIA_InvokePatternId);
            cacheRequest.AddPattern(UIA_PatternIds.UIA_TogglePatternId);
            cacheRequest.AddPattern(UIA_PatternIds.UIA_SelectionItemPatternId);
            cacheRequest.AddPattern(UIA_PatternIds.UIA_ExpandCollapsePatternId);
            cacheRequest.AddPattern(UIA_PatternIds.UIA_ValuePatternId);
            cacheRequest.AddPattern(UIA_PatternIds.UIA_RangeValuePatternId);

            return cacheRequest;
        }

        /// <summary>
        /// Enumerates the automation elements from the given window, prefetching the
        /// properties and patterns used to build hints (see CreateHintCacheRequest).
        /// </summary>
        /// <param name="hWnd">The window handle</param>
        /// <returns>All of the automation elements found</returns>
        private List<IUIAutomationElement> EnumElements(IntPtr hWnd)
        {
            var result = new List<IUIAutomationElement>();
            var automationElement = _automation.ElementFromHandle(hWnd);
            var maxDepth = ReadMaxEnumerationDepth();

            if (maxDepth <= 0)
            {
                // Unbounded: original FindAll over all descendants.
                var conditionControlView = _automation.ControlViewCondition;
                var conditionEnabled = _automation.CreatePropertyCondition(UIA_PropertyIds.UIA_IsEnabledPropertyId, true);
                var enabledControlCondition = _automation.CreateAndCondition(conditionControlView, conditionEnabled);

                var conditionOnScreen = _automation.CreatePropertyCondition(UIA_PropertyIds.UIA_IsOffscreenPropertyId, false);
                var condition = _automation.CreateAndCondition(enabledControlCondition, conditionOnScreen);

                var elementArray = automationElement.FindAllBuildCache(TreeScope.TreeScope_Descendants, condition, CreateHintCacheRequest(false));
                if (elementArray != null)
                {
                    for (var i = 0; i < elementArray.Length; ++i)
                    {
                        result.Add(elementArray.GetElement(i));
                    }
                }
            }
            else
            {
                // Depth-limited: walk the control-view tree up to maxDepth levels.
                // Visits fewer nodes on large trees (e.g. Chromium), at the cost of
                // missing deeply-nested controls.
                WalkControlView(_automation.ControlViewWalker, automationElement, CreateHintCacheRequest(true), result, 0, maxDepth);
            }

            return result;
        }

        /// <summary>
        /// Depth-limited DFS over the control-view tree. Children are retrieved with the
        /// cache request so IsEnabled/IsOffscreen/bounds/patterns come back in one pass;
        /// enabled, on-screen elements are collected. <paramref name="elementDepth"/> is
        /// the depth of <paramref name="element"/> (the root window is 0).
        /// </summary>
        private void WalkControlView(IUIAutomationTreeWalker walker, IUIAutomationElement element, IUIAutomationCacheRequest cache, List<IUIAutomationElement> result, int elementDepth, int maxDepth)
        {
            if (elementDepth >= maxDepth)
            {
                return;
            }

            IUIAutomationElement child;
            try
            {
                child = walker.GetFirstChildElementBuildCache(element, cache);
            }
            catch (Exception)
            {
                return;
            }

            while (child != null)
            {
                try
                {
                    if (child.CachedIsEnabled != 0 && child.CachedIsOffscreen == 0)
                    {
                        result.Add(child);
                    }
                }
                catch (Exception)
                {
                    // Skip elements whose cached state cannot be read.
                }

                WalkControlView(walker, child, cache, result, elementDepth + 1, maxDepth);

                try
                {
                    child = walker.GetNextSiblingElementBuildCache(child, cache);
                }
                catch (Exception)
                {
                    child = null;
                }
            }
        }

        /// <summary>
        /// Reads MaxEnumerationDepth from hap.exe.config. 0 (or unset/invalid) means
        /// unbounded -- the original whole-window FindAll behavior.
        /// </summary>
        private static int ReadMaxEnumerationDepth()
        {
            try
            {
                // Refresh so edits to hap.exe.config take effect on the next hotkey
                // press without restarting (hot-reload).
                OverlayActionConfig.EnsureFresh();
                var raw = ConfigurationManager.AppSettings["MaxEnumerationDepth"];
                if (int.TryParse(raw, out var depth) && depth > 0)
                {
                    return depth;
                }
            }
            catch (Exception)
            {
                // Config read/parse issue (e.g. file mid-save); fall back to unbounded.
            }

            return 0;
        }

        /// <summary>
        /// Resolves the rectangle the overlay should cover, in physical screen coordinates.
        /// HintBoundsSource=Screen (default) returns the full monitor the window is on so
        /// labels fill the screen; Window returns the foreground window rect (the previous
        /// behavior). Grid PointHints store absolute screen coords (see PointHint), so
        /// enlarging the overlay never breaks cursor targeting.
        /// </summary>
        private static Rect ResolveOwningBounds(IntPtr hWnd)
        {
            if (OverlayActionConfig.ReadHintBounds() == HintBounds.Window)
            {
                var raw = new RECT();
                User32.GetWindowRect(hWnd, ref raw);
                return raw;
            }

            // Full monitor the window is on. Screen.FromHandle falls back to the primary
            // monitor in practice; guard anyway so a degenerate box never crashes the app.
            var screen = Screen.FromHandle(hWnd) ?? Screen.PrimaryScreen;
            if (screen == null)
            {
                var raw = new RECT();
                User32.GetWindowRect(hWnd, ref raw);
                return raw;
            }
            var b = screen.Bounds;
            return new Rect(b.X, b.Y, b.Width, b.Height);
        }

        /// <summary>
        /// Generates a grid of synthetic PointHints covering the window, denser in the
        /// edge bands (tabs/sidebars/widgets) and sparser in the center. Each point's
        /// label, when typed, jumps the cursor there so the user can nudge to a target.
        /// Instant -- no UI Automation walk.
        /// </summary>
        /// <summary>
        /// Builds a Grid-mode session over explicit bounds (physical screen coordinates),
        /// for monitor cycling. Bypasses HintBoundsSource so each monitor can be targeted
        /// regardless of where the foreground window is.
        /// </summary>
        public HintSession EnumGridHintsForBounds(IntPtr hWnd, Rect bounds)
        {
            return EnumGridHints(hWnd, bounds, null);
        }

        /// <summary>
        /// Layout-aware monitor-cycling entry: builds a Grid session over <paramref name="bounds"/>
        /// using the given preset. A null <paramref name="layout"/> reads the five legacy flat
        /// keys (GridLayout.FromFlatConfig), preserving the pre-layout behavior.
        /// </summary>
        public HintSession EnumGridHintsForBounds(IntPtr hWnd, Rect bounds, GridLayout layout)
        {
            return EnumGridHints(hWnd, bounds, layout);
        }

        private HintSession EnumGridHints(IntPtr hWnd)
        {
            return EnumGridHints(hWnd, ResolveOwningBounds(hWnd), null);
        }

        private HintSession EnumGridHints(IntPtr hWnd, Rect windowBounds, GridLayout layout)
        {
            // null layout = no GridLayouts configured: read the legacy flat keys so the
            // geometry is identical to the pre-layout behavior.
            if (layout == null)
            {
                layout = GridLayout.FromFlatConfig();
            }
            var inset = layout.Inset;
            var bandPct = layout.BandPercent / 100.0;
            var edgeStep = (double)layout.EdgeStep;
            var centerStep = (double)layout.CenterStep;
            var want = new HashSet<string>(
                (layout.DenseRegions ?? "Left,Top,TR,BR,Center").Split(',').Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);

            // Cap the total at the two-character label capacity (HintCharacters^2) so every
            // hint stays two chars. If the window is large and the steps would produce too
            // many points, scale the steps up and regenerate.
            int maxHints = ReadHintCharacterCount();
            maxHints *= maxHints;

            List<Hint> hints;
            var guard = 0;
            do
            {
                hints = GenerateGridPoints(hWnd, windowBounds, inset, bandPct, edgeStep, centerStep, want);
                if (hints.Count <= maxHints || guard >= 6)
                {
                    break;
                }

                double scale = Math.Sqrt((double)hints.Count / maxHints);
                edgeStep *= scale;
                centerStep *= scale;
                guard++;
            } while (true);

            return new HintSession
            {
                Hints = hints,
                OwningWindow = hWnd,
                OwningWindowBounds = windowBounds,
            };
        }

        /// <summary>
        /// Builds the grid points: dense full-length edge strips + corner squares, then a
        /// sparse pass over the FULL window (CENTER). Dedup by rounded coordinates means
        /// non-dense areas (e.g. bottom-middle, right-middle) still get sparse coverage
        /// instead of being empty, while dense regions are not doubled up.
        /// </summary>
        private static List<Hint> GenerateGridPoints(IntPtr hWnd, Rect windowBounds, double inset, double bandPct, double edgeStep, double centerStep, HashSet<string> want)
        {
            double left = windowBounds.Left + inset;
            double top = windowBounds.Top + inset;
            double right = windowBounds.Left + windowBounds.Width - inset;
            double bottom = windowBounds.Top + windowBounds.Height - inset;
            double bandW = (windowBounds.Width - (2 * inset)) * bandPct;
            double bandH = (windowBounds.Height - (2 * inset)) * bandPct;

            var hints = new List<Hint>();
            var seen = new Dictionary<string, List<double[]>>();
            double box = edgeStep * 0.8;
            // A Center-only layout is a uniform "fill the screen" grid: span its points
            // edge-to-edge so labels reach the screen edges. Edge-banded layouts keep the
            // original cell-center placement (their center points dedup against the strips).
            bool uniformOnly = want.Count == 1 && want.Contains("CENTER");

            if (want.Contains("LEFT"))   FillRegion(hints, seen, hWnd, windowBounds, left,          top,           left + bandW,   bottom,         edgeStep, box);
            if (want.Contains("TOP"))    FillRegion(hints, seen, hWnd, windowBounds, left,          top,           right,          top + bandH,    edgeStep, box);
            if (want.Contains("RIGHT"))  FillRegion(hints, seen, hWnd, windowBounds, right - bandW, top,           right,          bottom,         edgeStep, box);
            if (want.Contains("BOTTOM")) FillRegion(hints, seen, hWnd, windowBounds, left,          bottom - bandH, right,          bottom,         edgeStep, box);
            if (want.Contains("TL"))     FillRegion(hints, seen, hWnd, windowBounds, left,          top,           left + bandW,   top + bandH,    edgeStep, box);
            if (want.Contains("TR"))     FillRegion(hints, seen, hWnd, windowBounds, right - bandW, top,           right,          top + bandH,    edgeStep, box);
            if (want.Contains("BL"))     FillRegion(hints, seen, hWnd, windowBounds, left,          bottom - bandH, left + bandW,   bottom,         edgeStep, box);
            if (want.Contains("BR"))     FillRegion(hints, seen, hWnd, windowBounds, right - bandW, bottom - bandH, right,          bottom,         edgeStep, box);
            if (want.Contains("CENTER")) FillRegion(hints, seen, hWnd, windowBounds, left,          top,           right,          bottom,         centerStep, box, spanEdges: uniformOnly);

            return hints;
        }

        /// <summary>Number of distinct hint characters configured (for the label-capacity cap).</summary>
        private static int ReadHintCharacterCount()
        {
            try
            {
                OverlayActionConfig.EnsureFresh();
                var raw = ConfigurationManager.AppSettings["HintCharacters"];
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var n = raw.Trim().ToUpperInvariant().Distinct().Count();
                    if (n > 0)
                    {
                        return n;
                    }
                }
            }
            catch (Exception)
            {
            }

            return 14;
        }

        /// <summary>
        /// Fills a rectangular region (screen coords) with an evenly distributed grid of
        /// PointHints. Points are placed at cell centers spanning the full region (no edge
        /// gaps). A box-overlap proximity check (<paramref name="seen"/>) ensures no two
        /// labels -- even across overlapping regions (strip vs. full-window center) -- are
        /// placed within <paramref name="box"/> of each other, so labels never stack.
        /// <paramref name="step"/> sets the density; <paramref name="box"/> is the label size.
        /// </summary>
        private static void FillRegion(List<Hint> hints, Dictionary<string, List<double[]>> seen, IntPtr hWnd, Rect windowBounds, double x1, double y1, double x2, double y2, double step, double box, bool spanEdges = false)
        {
            if (step <= 0 || x2 <= x1 || y2 <= y1 || box <= 0)
            {
                return;
            }

            int cols = Math.Max(1, (int)Math.Round((x2 - x1) / step));
            int rows = Math.Max(1, (int)Math.Round((y2 - y1) / step));
            // spanEdges: first point at the region start, last at the end (edge-to-edge),
            // so a uniform / Center-only grid reaches the screen edges instead of leaving a
            // half-cell margin. Otherwise points sit at cell centers (edge strips/corners).
            bool spanX = spanEdges && cols > 1;
            bool spanY = spanEdges && rows > 1;
            double dx = spanX ? (x2 - x1) / (cols - 1) : (x2 - x1) / cols;
            double dy = spanY ? (y2 - y1) / (rows - 1) : (y2 - y1) / rows;
            double ox = spanX ? 0.0 : 0.5;
            double oy = spanY ? 0.0 : 0.5;

            for (int i = 0; i < cols; i++)
            {
                for (int j = 0; j < rows; j++)
                {
                    double sx = x1 + (i + ox) * dx;
                    double sy = y1 + (j + oy) * dy;

                    if (HasNearbyPoint(seen, sx, sy, box))
                    {
                        continue;
                    }

                    AddPoint(seen, sx, sy, box);
                    double relX = sx - windowBounds.Left;
                    double relY = sy - windowBounds.Top;
                    hints.Add(new PointHint(hWnd, new Rect(relX, relY, box, box), new Point(sx, sy)));
                }
            }
        }

        /// <summary>
        /// True if <paramref name="seen"/> contains a point within <paramref name="box"/>
        /// (Chebyshev distance) of (x, y). Checks the 3x3 cell neighborhood so dense strips
        /// and the full-window center pass cannot place overlapping labels.
        /// </summary>
        private static bool HasNearbyPoint(Dictionary<string, List<double[]>> seen, double x, double y, double box)
        {
            int cx = (int)(x / box);
            int cy = (int)(y / box);
            for (int ox = -1; ox <= 1; ox++)
            {
                for (int oy = -1; oy <= 1; oy++)
                {
                    if (seen.TryGetValue((cx + ox) + "," + (cy + oy), out var list))
                    {
                        foreach (var p in list)
                        {
                            if (Math.Abs(p[0] - x) < box && Math.Abs(p[1] - y) < box)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        private static void AddPoint(Dictionary<string, List<double[]>> seen, double x, double y, double box)
        {
            int cx = (int)(x / box);
            int cy = (int)(y / box);
            string key = cx + "," + cy;
            if (!seen.TryGetValue(key, out var list))
            {
                list = new List<double[]>();
                seen[key] = list;
            }
            list.Add(new[] { x, y });
        }

        private static bool IsGridMode()
        {
            return string.Equals(ReadStringSetting("HintSource"), "Grid", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadStringSetting(string key)
        {
            try
            {
                OverlayActionConfig.EnsureFresh();
                return ConfigurationManager.AppSettings[key];
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int ReadIntSetting(string key, int defaultValue)
        {
            try
            {
                OverlayActionConfig.EnsureFresh();
                var raw = ConfigurationManager.AppSettings[key];
                if (int.TryParse(raw, out var value) && value > 0)
                {
                    return value;
                }
            }
            catch (Exception)
            {
                // fall through to default
            }

            return defaultValue;
        }

        /// <summary>
        /// Creates a UI Automation element from the given automation element
        /// </summary>
        /// <param name="owningWindow">The owning window</param>
        /// <param name="hintBounds">The hint bounds</param>
        /// <param name="automationElement">The associated automation element</param>
        /// <returns>The created hint, else null if the hint could not be created</returns>
        private Hint CreateHint(IntPtr owningWindow, Rect hintBounds, IUIAutomationElement automationElement)
        {
            try
            {
                var invokePattern = (IUIAutomationInvokePattern)automationElement.GetCachedPattern(UIA_PatternIds.UIA_InvokePatternId);
                if (invokePattern != null)
                {
                    return new UiAutomationInvokeHint(owningWindow, invokePattern, hintBounds);
                }

                var togglePattern = (IUIAutomationTogglePattern)automationElement.GetCachedPattern(UIA_PatternIds.UIA_TogglePatternId);
                if (togglePattern != null)
                {
                    return new UiAutomationToggleHint(owningWindow, togglePattern, hintBounds);
                }

                var selectPattern = (IUIAutomationSelectionItemPattern) automationElement.GetCachedPattern(UIA_PatternIds.UIA_SelectionItemPatternId);
                if (selectPattern != null)
                {
                    return new UiAutomationSelectHint(owningWindow, selectPattern, hintBounds);
                }

                var expandCollapsePattern = (IUIAutomationExpandCollapsePattern) automationElement.GetCachedPattern(UIA_PatternIds.UIA_ExpandCollapsePatternId);
                if (expandCollapsePattern != null)
                {
                    return new UiAutomationExpandCollapseHint(owningWindow, expandCollapsePattern, hintBounds);
                }

                var valuePattern = (IUIAutomationValuePattern)automationElement.GetCachedPattern(UIA_PatternIds.UIA_ValuePatternId);
                if (valuePattern != null && valuePattern.CachedIsReadOnly == 0)
                {
                    return new UiAutomationFocusHint(owningWindow, automationElement, hintBounds);
                }

                var rangeValuePattern = (IUIAutomationRangeValuePattern) automationElement.GetCachedPattern(UIA_PatternIds.UIA_RangeValuePatternId);
                if (rangeValuePattern != null && rangeValuePattern.CachedIsReadOnly == 0)
                {
                    return new UiAutomationFocusHint(owningWindow, automationElement, hintBounds);
                }

                return null;
            }
            catch (Exception)
            {
                // May have gone
                return null;
            }
        }

        /// <summary>
        /// Creates a debug hint
        /// </summary>
        /// <param name="owningWindow">The window that owns the hint</param>
        /// <param name="hintBounds">The hint bounds</param>
        /// <param name="automationElement">The automation element</param>
        /// <returns>A debug hint</returns>
        private DebugHint CreateDebugHint(IntPtr owningWindow, Rect hintBounds, IUIAutomationElement automationElement)
        {
            // Enumerate all possible patterns. Note that the performance of this is *very* bad -- hence debug only.
            var programmaticNames = new List<string>();

            foreach (var pn in UiAutomationPatternIds.PatternNames)
            {
                try
                {
                    var pattern = automationElement.GetCurrentPattern(pn.Key);
                    if(pattern != null)
                    {
                        programmaticNames.Add(pn.Value);
                    }
                }
                catch (Exception)
                {
                }
            }

            if (programmaticNames.Any())
            {
                return new DebugHint(owningWindow, hintBounds, programmaticNames.ToList());
            }

            return null;
        }
    }
}
