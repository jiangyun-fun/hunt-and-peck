using System.Windows;

namespace HuntAndPeck.Models
{
    /// <summary>
    /// One group-view box: the first-char label group's key char and the union of its
    /// member hints' bounding rectangles (overlay-relative coordinates, same space the
    /// hint pills render in). Immutable value type; see <see cref="Services.GroupViewService"/>.
    /// </summary>
    public readonly struct GroupHintBox
    {
        /// <summary>The group's label char (the first char of every member label).</summary>
        public char Key { get; }

        /// <summary>Union of the member hints' bounding rectangles (tight; the canvas pads it visually).</summary>
        public Rect Bounds { get; }

        public GroupHintBox(char key, Rect bounds)
        {
            Key = key;
            Bounds = bounds;
        }
    }
}
