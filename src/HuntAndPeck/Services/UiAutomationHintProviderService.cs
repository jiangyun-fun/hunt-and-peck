using HuntAndPeck.Extensions;
using HuntAndPeck.Models;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Windows;
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
        /// Refreshes a cached session's hint positions by re-reading each element's
        /// current bounding rectangle, without re-walking the automation tree. Best-effort:
        /// elements that have disappeared keep their last known bounds. Runs off the UI
        /// thread (the caller is Task.Run), so this does not freeze the app.
        /// </summary>
        private void RefreshSessionPositions(HintSession session, IntPtr hWnd)
        {
            try
            {
                var rawWindowBounds = new RECT();
                User32.GetWindowRect(hWnd, ref rawWindowBounds);
                Rect windowBounds = rawWindowBounds;
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

            // Window bounds
            var rawWindowBounds = new RECT();
            User32.GetWindowRect(hWnd, ref rawWindowBounds);
            Rect windowBounds = rawWindowBounds;

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
                ConfigurationManager.RefreshSection("appSettings");
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
        /// Generates a grid of synthetic PointHints covering the window. Each point's
        /// label, when typed, jumps the cursor there so the user can nudge to a target.
        /// Instant -- no UI Automation walk.
        /// </summary>
        private HintSession EnumGridHints(IntPtr hWnd)
        {
            var rawBounds = new RECT();
            User32.GetWindowRect(hWnd, ref rawBounds);
            Rect windowBounds = rawBounds;

            var cols = ReadIntSetting("GridCols", 12);
            var rows = ReadIntSetting("GridRows", 8);

            // Inset from the window edges so labels (and their cursor targets) avoid
            // the title bar and screen edges (otherwise top-row labels get clipped).
            double insetX = windowBounds.Width * 0.05;
            double insetY = windowBounds.Height * 0.05;
            double cellW = (windowBounds.Width - 2 * insetX) / cols;
            double cellH = (windowBounds.Height - 2 * insetY) / rows;

            var hints = new List<Hint>();
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    // Place the label at the cell's top-left and target that same point
                    // with the cursor, so it lands on the label (not the cell center).
                    double relX = insetX + c * cellW;
                    double relY = insetY + r * cellH;
                    var relBounds = new Rect(relX, relY, cellW, cellH);
                    double screenX = windowBounds.Left + relX;
                    double screenY = windowBounds.Top + relY;
                    hints.Add(new PointHint(hWnd, relBounds, new Point(screenX, screenY)));
                }
            }

            return new HintSession
            {
                Hints = hints,
                OwningWindow = hWnd,
                OwningWindowBounds = windowBounds,
            };
        }

        private static bool IsGridMode()
        {
            return string.Equals(ReadStringSetting("HintSource"), "Grid", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadStringSetting(string key)
        {
            try
            {
                ConfigurationManager.RefreshSection("appSettings");
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
                ConfigurationManager.RefreshSection("appSettings");
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
