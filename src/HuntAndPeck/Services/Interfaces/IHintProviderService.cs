using System;
using System.Windows;
using HuntAndPeck.Models;
using HuntAndPeck.Services;

namespace HuntAndPeck.Services.Interfaces
{
    /// <summary>
    /// Provides hints for the entire desktop or a given window handle
    /// </summary>
    public interface IHintProviderService
    {
        /// <summary>
        /// Enumerate the available hints for the current foreground window
        /// </summary>
        /// <returns>The hint session containing the available hints or null if there is no foreground window</returns>
        HintSession EnumHints();

        HintSession EnumHints(IntPtr handle);

        /// <summary>
        /// Grid-mode enumeration with an explicit layout preset (single-session path).
        /// Automation ignores the layout and falls back to the real-control walk.
        /// </summary>
        HintSession EnumHints(IntPtr handle, GridLayout layout);

        /// <summary>
        /// Build a Grid-mode hint session covering the given bounds (physical screen
        /// coordinates), regardless of which monitor the foreground window is on. Used to
        /// generate one grid per monitor for monitor cycling.
        /// </summary>
        HintSession EnumGridHintsForBounds(IntPtr handle, Rect bounds);

        /// <summary>
        /// Layout-aware monitor-cycling entry: a null layout reads the legacy flat keys.
        /// </summary>
        HintSession EnumGridHintsForBounds(IntPtr handle, Rect bounds, GridLayout layout);
    }
}
