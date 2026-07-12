using System;
using System.Windows;
using HuntAndPeck.Models;

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
        /// Build a Grid-mode hint session covering the given bounds (physical screen
        /// coordinates), regardless of which monitor the foreground window is on. Used to
        /// generate one grid per monitor for monitor cycling.
        /// </summary>
        HintSession EnumGridHintsForBounds(IntPtr handle, Rect bounds);
    }
}
