using HuntAndPeck.Extensions;
using HuntAndPeck.Models;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services.Interfaces;
using System;
using System.Collections.Generic;
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
        private IUIAutomationCacheRequest CreateHintCacheRequest()
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

            var conditionControlView = _automation.ControlViewCondition;
            var conditionEnabled = _automation.CreatePropertyCondition(UIA_PropertyIds.UIA_IsEnabledPropertyId, true);
            var enabledControlCondition = _automation.CreateAndCondition(conditionControlView, conditionEnabled);

            var conditionOnScreen = _automation.CreatePropertyCondition(UIA_PropertyIds.UIA_IsOffscreenPropertyId, false);
            var condition = _automation.CreateAndCondition(enabledControlCondition, conditionOnScreen);

            var cacheRequest = CreateHintCacheRequest();
            var elementArray = automationElement.FindAllBuildCache(TreeScope.TreeScope_Descendants, condition, cacheRequest);
            if (elementArray != null)
            {
                for (var i = 0; i < elementArray.Length; ++i)
                {
                    result.Add(elementArray.GetElement(i));
                }
            }

            return result;
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
