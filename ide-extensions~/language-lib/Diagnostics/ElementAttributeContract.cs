using System.Collections.Generic;

namespace Ruitk.Language.Diagnostics
{
    /// <summary>
    /// What the analyzer knows about one element's attribute surface: every
    /// attribute the element accepts, and the subset a call site MUST supply.
    ///
    /// The two travel together because they are decided together. When a tag
    /// name has several declarants and the current file's view of it is
    /// ambiguous, <see cref="Known"/> fails open to the UNION of every
    /// declarant's props - attribute validation must never invent an
    /// unknown-attribute error - while <see cref="Required"/> fails open to
    /// EMPTY, because requiring a prop the other declarant does not have would
    /// invent the opposite error. Opposite directions from one decision, which
    /// is why they cannot be built independently of each other.
    /// </summary>
    public sealed class ElementAttributeContract
    {
        private static readonly IReadOnlyDictionary<string, string> NoneRequired =
            new Dictionary<string, string>(0);

        public ElementAttributeContract(
            IReadOnlyCollection<string>? known,
            IReadOnlyDictionary<string, string>? required = null
        )
        {
            Known = known;
            Required = required ?? NoneRequired;
        }

        /// <summary>
        /// Every attribute name the element accepts, or <c>null</c> when the
        /// caller does not know the full accepted set and the unknown-attribute
        /// check must be skipped for this element.
        ///
        /// Null is not the same as empty. The builder knows a component's
        /// DECLARED parameters from its own tree, and therefore which of them
        /// are required, without necessarily holding the element schema that
        /// says what else is accepted - and an incomplete accepted-set would
        /// manufacture an unknown-attribute error for an attribute that is
        /// perfectly legal.
        /// </summary>
        public IReadOnlyCollection<string>? Known { get; }

        /// <summary>
        /// Attribute name of each required prop, mapped to the name of the
        /// parameter that declares it (they differ when the parameter carries
        /// the leading-underscore unused marker: <c>_count</c> exposes
        /// <c>count</c>).
        /// </summary>
        public IReadOnlyDictionary<string, string> Required { get; }
    }
}
