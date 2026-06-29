using System;
using System.Windows;
using HuntAndPeck.NativeMethods;

namespace HuntAndPeck.Models
{
    /// <summary>
    /// A synthetic hint at a fixed screen position with no underlying UI element. Used in
    /// grid mode: typing its label moves the cursor onto the point so the user can nudge
    /// to a nearby target and click manually. Instant -- no UI Automation tree walk.
    /// </summary>
    public class PointHint : Hint
    {
        private readonly Point _screenPoint;

        public PointHint(IntPtr owningWindow, Rect bounds, Point screenPoint)
            : base(owningWindow, bounds)
        {
            _screenPoint = screenPoint;
        }

        /// <summary>Move the cursor onto this point (there is nothing to invoke).</summary>
        public override void Invoke()
        {
            MoveCursor();
        }

        public override void MoveMouseToCenter()
        {
            MoveCursor();
        }

        private void MoveCursor()
        {
            User32.SetCursorPos((int)_screenPoint.X, (int)_screenPoint.Y);
        }
    }
}
