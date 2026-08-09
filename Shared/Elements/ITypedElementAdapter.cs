using Ruitk.Props.Typed;
using UnityEngine.UIElements;

namespace Ruitk.Elements
{
    /// <summary>
    /// Typed props adapter — enables the typed pipeline for built-in host elements.
    /// All built-in adapters implement this via <see cref="BaseElementAdapter"/>.
    /// </summary>
    /// <remarks>
    /// Lives apart from <see cref="IElementAdapter"/> because it names
    /// <see cref="BaseProps"/> — the UI Toolkit typed-props surface — while the
    /// untyped adapter seam is host-agnostic. Splitting the files lets the
    /// host-agnostic test suite link the seam without the props tree.
    /// </remarks>
    public interface ITypedElementAdapter
    {
        /// <summary>
        /// Apply all typed properties on initial placement (no previous state).
        /// </summary>
        void ApplyTypedFull(VisualElement element, BaseProps props);

        /// <summary>
        /// Apply only the changed typed properties.
        /// </summary>
        void ApplyTypedDiff(VisualElement element, BaseProps prev, BaseProps next);
    }
}
