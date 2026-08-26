#if UNITY_EDITOR
using System;

namespace Ruitk.Builder
{
    /// <summary>
    /// The house naming convention: which modules belong to the same COMPONENT.
    ///
    /// A component and the style and hook modules named after it are one family -
    /// <c>NewComponent</c>, <c>newComponent.style</c>, <c>useNewComponent.hooks</c> -
    /// and a family lives in one folder. This is what decides where a new module
    /// is BORN; it is not an invariant, and nothing re-places a module that has
    /// been put somewhere deliberately.
    ///
    /// Util modules are deliberately outside it. They have no suffix - a util is
    /// a plain <c>.uitkx</c> classified by what it exports - so there is no name
    /// to match on, and a util named for its component would collide with the
    /// component's own file on a case-insensitive filesystem.
    /// </summary>
    internal static class BuilderNaming
    {
        /// <summary>The family a module belongs to, in canonical form.
        ///
        /// A hook is named for what it DOES - <c>useNewComponent</c> - so the
        /// <c>use</c> prefix is stripped before comparing; a style is the
        /// component's name with a lowered first letter. Both reduce to the
        /// component's own name, which is the family.</summary>
        public static string FamilyOf(BuilderNodeKind kind, string name)
        {
            string bare = name ?? string.Empty;
            if (kind == BuilderNodeKind.Hook
                && bare.Length > 3
                && bare.StartsWith("use", StringComparison.Ordinal)
                && char.IsUpper(bare[3]))
            {
                bare = bare.Substring(3);
            }
            return bare.Length == 0
                ? bare
                : char.ToLowerInvariant(bare[0]) + bare.Substring(1);
        }

        /// <summary>Whether two module names name the same family. Compared
        /// case-insensitively: <c>newComponent</c> and <c>NewComponent</c> are the
        /// same name by every reading a person would give them.</summary>
        public static bool SameFamily(
            BuilderNodeKind aKind, string aName, BuilderNodeKind bKind, string bName)
        {
            string a = FamilyOf(aKind, aName);
            string b = FamilyOf(bKind, bName);
            return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>How closely two folders are related, as the length of the path
        /// they share. Used to pick the NEAREST component when more than one in
        /// the tree carries the family name.</summary>
        public static int SharedPrefixLength(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return 0;
            string x = a.Replace('\\', '/');
            string y = b.Replace('\\', '/');
            int n = Math.Min(x.Length, y.Length);
            int shared = 0;
            for (int i = 0; i < n; i++)
            {
                if (char.ToLowerInvariant(x[i]) != char.ToLowerInvariant(y[i]))
                    break;
                if (x[i] == '/')
                    shared = i;
            }
            return shared;
        }
    }
}
#endif
