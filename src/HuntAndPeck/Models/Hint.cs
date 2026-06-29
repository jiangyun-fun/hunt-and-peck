using System;
using System.Windows;
using HuntAndPeck.NativeMethods;
using UIAutomationClient;

namespace HuntAndPeck.Models
{
    /// <summary>
    /// Represents a hint that has 1 or more capabilities
    /// </summary>
    public abstract class Hint
    {
        /// <summary>
        /// Ctor
        /// </summary>
        /// <param name="owningWindow">The owning window</param>
        /// <param name="boundingRectangle">The bounding rectangle of the hint in owner window coordinates</param>
        protected Hint(IntPtr owningWindow, Rect boundingRectangle)
        {
            OwningWindow = owningWindow;
            BoundingRectangle = boundingRectangle;
        }

        /// <summary>
        /// The bounding rectangle for the hint in Window coordinates for the owning window.
        /// Internal set so the hint provider can refresh positions on a cache hit.
        /// </summary>
        public Rect BoundingRectangle { get; internal set; }

        /// <summary>
        /// The window handle of the owning window
        /// </summary>
        public IntPtr OwningWindow { get; private set; }

        /// <summary>
        /// The source automation element. Held so a cached hint can refresh its
        /// bounding rectangle (CurrentBoundingRectangle) without re-walking the tree.
        /// </summary>
        public IUIAutomationElement AutomationElement { get; set; }

        /// <summary>
        /// Invokes the hint
        /// </summary>
        public abstract void Invoke();

        /// <summary>
        /// Moves the mouse cursor to the center of this hint's element (its current
        /// screen bounding rectangle) without clicking. Used in MoveMouse mode, e.g.
        /// for targets whose UI Automation Invoke pattern does not fire.
        /// </summary>
        public virtual void MoveMouseToCenter()
        {
            var element = AutomationElement;
            if (element == null)
            {
                return;
            }

            try
            {
                var br = element.CurrentBoundingRectangle;
                User32.SetCursorPos((br.left + br.right) / 2, (br.top + br.bottom) / 2);
            }
            catch (Exception)
            {
                // Element may have gone.
            }
        }
    }
}
