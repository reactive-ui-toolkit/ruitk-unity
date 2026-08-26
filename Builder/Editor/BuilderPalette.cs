#if UNITY_EDITOR
using UnityEngine;

namespace Ruitk.Builder
{
    /// <summary>
    /// The single source of truth for the builder chrome's colors. Every pane
    /// had grown its own copy of the same literals (window shell, library pane,
    /// preview pane, code field, canvas drawing); one drifted value there is a
    /// visible seam between panes, so the values live here once.
    ///
    /// Values that only LOOK like these (the legend's rounded 0.31/0.76/0.97,
    /// the search menu's 0.165/0.165/0.19) are deliberately NOT folded in —
    /// they are different colors and collapsing them would change pixels.
    /// </summary>
    internal static class BuilderPalette
    {
        public static readonly Color Panel = new Color(0.137f, 0.137f, 0.161f);
        public static readonly Color Panel2 = new Color(0.165f, 0.165f, 0.192f);
        public static readonly Color Line = new Color(0.227f, 0.227f, 0.267f);
        public static readonly Color Text = new Color(0.839f, 0.839f, 0.863f);
        public static readonly Color Dim = new Color(0.545f, 0.545f, 0.588f);
        public static readonly Color Accent = new Color(0.310f, 0.765f, 0.969f);

        /// <summary>The selection gold, the same one the canvas rings a selected
        /// card with (#ffd54f). Defined once so the canvas and the library cannot
        /// disagree about what "selected" looks like.</summary>
        public static readonly Color Select = new Color(1.000f, 0.835f, 0.310f);
        public static readonly Color Ground = new Color(0.090f, 0.090f, 0.106f);
        public static readonly Color Transparent = new Color(0f, 0f, 0f, 0f);

        public static readonly Color ComponentTint = new Color(0.498f, 0.859f, 0.792f);
        public static readonly Color HookTint = new Color(0.506f, 0.780f, 0.518f);
        public static readonly Color StyleTint = new Color(0.808f, 0.576f, 0.847f);
        public static readonly Color UtilTint = new Color(1.000f, 0.718f, 0.302f);

        public static readonly Color UsageEdge = new Color(0.361f, 0.545f, 0.690f);
        public static readonly Color HookEdge = new Color(0.427f, 0.659f, 0.435f);
        public static readonly Color StyleEdge = new Color(0.647f, 0.467f, 0.702f);
    }
}
#endif
