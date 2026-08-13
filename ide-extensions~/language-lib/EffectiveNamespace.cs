using System;
using System.IO;

namespace Ruitk.Language
{
    /// <summary>
    /// Resolves a <c>.uitkx</c> file's EFFECTIVE namespace (import/export grammar, §4) — the single
    /// place this is decided, so the source generator, the peer-container tables, and the HMR
    /// compiler all compute the IDENTICAL value for a file. That byte-parity is load-bearing for §7:
    /// hook family keys are <c>{EffectiveNamespace}.{Container}::{HookName}</c>, and a producer id
    /// (emitted by one world) must string-match a consumer key (possibly emitted by the other).
    ///
    /// Rule: an explicit <c>@namespace</c> wins; otherwise the namespace is path-derived
    /// (<see cref="NamespaceDerivation.Derive"/>) anchored at the nearest owning <c>.asmdef</c>,
    /// else the configured project root.
    /// </summary>
    public static class EffectiveNamespace
    {
        /// <summary>
        /// The effective namespace for <paramref name="filePath"/> given its parsed
        /// <paramref name="rawNamespace"/> and whether that came from an explicit <c>@namespace</c>.
        /// Legacy (folder-keyed) form — delegates to the 4-arg overload with
        /// <c>fileKeyed: false</c>. KEPT with this exact 3-arg shape for source compatibility;
        /// note the HMR reflection seam binds the 4-arg overload BY PARAMETER TYPES
        /// (U-01 signature rule — a bare name-only GetMethod would now be ambiguous).
        /// </summary>
        public static string? Resolve(bool hasExplicitNamespace, string? rawNamespace, string filePath)
            => Resolve(hasExplicitNamespace, rawNamespace, filePath, fileKeyed: false);

        /// <summary>
        /// The effective namespace for <paramref name="filePath"/>, mode-aware (ES-modules
        /// campaign, U-01). <paramref name="fileKeyed"/> selects the derivation formula for
        /// stamp-less files: <c>false</c> → legacy folder-keyed (<see cref="NamespaceDerivation.Derive"/>,
        /// files in one folder share a namespace); <c>true</c> → file-keyed
        /// (<see cref="NamespaceDerivation.DeriveFileModule"/>, folder segments + sanitized file
        /// stem — every new-syntax file is its own module, G-01). Callers derive the flag from the
        /// parse's syntax mode (<c>!DirectiveSet.UsesLegacySyntax</c>). An explicit
        /// <c>@namespace</c> stamp wins in BOTH modes (unchanged escape hatch).
        /// </summary>
        public static string? Resolve(bool hasExplicitNamespace, string? rawNamespace, string filePath, bool fileKeyed)
        {
            if (hasExplicitNamespace)
                return rawNamespace;
            string? anchor = ResolveDerivationAnchor(filePath);
            string prefix = ResolveNamespacePrefix(filePath);
            string? derived = fileKeyed
                ? NamespaceDerivation.DeriveFileModule(filePath, anchor, prefix)
                : NamespaceDerivation.Derive(filePath, anchor, prefix);
            return derived ?? rawNamespace;
        }

        /// <summary>
        /// The root prefix for a path-derived namespace (namespace-import unification plan),
        /// most-specific WINS: the nearest <c>uitkx.config.json</c> <c>"namespacePrefix"</c>, else the
        /// owning <c>.asmdef</c>'s <c>"rootNamespace"</c> (Unity's own convention), else the
        /// <c>Ruitk.Uitkx</c> default. The asmdef <c>"name"</c> is DELIBERATELY not used — it
        /// would silently re-root every named-asmdef project. Every step is opt-in, so a project with
        /// neither config nor an asmdef rootNamespace keeps the exact legacy derivation.
        /// </summary>
        public static string ResolveNamespacePrefix(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath) ?? filePath;

            string? configured = UitkxConfig.LoadNamespacePrefix(dir);
            if (!string.IsNullOrEmpty(configured))
                return configured!;

            string? asmdefDir = FindOwningAsmdefDir(filePath);
            if (asmdefDir != null)
            {
                string? rootNs = ReadAsmdefRootNamespace(asmdefDir);
                if (!string.IsNullOrEmpty(rootNs))
                    return rootNs!;
            }

            return NamespaceDerivation.Root;
        }

        private static readonly System.Text.RegularExpressions.Regex s_asmdefRootNsRe =
            new System.Text.RegularExpressions.Regex(
                "\"rootNamespace\"\\s*:\\s*\"([^\"]*)\"",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The <c>"rootNamespace"</c> field of the <c>*.asmdef</c> in <paramref name="asmdefDir"/>
        /// (Unity 2020.2+), or <c>null</c> when the key is absent/empty.</summary>
        private static string? ReadAsmdefRootNamespace(string asmdefDir)
        {
            try
            {
                foreach (string asmdef in Directory.GetFiles(asmdefDir, "*.asmdef"))
                {
                    var m = s_asmdefRootNsRe.Match(File.ReadAllText(asmdef));
                    if (m.Success)
                    {
                        string v = m.Groups[1].Value.Trim();
                        if (v.Length > 0)
                            return v;
                    }
                }
            }
            catch { /* never crash on filesystem errors */ }
            return null;
        }

        /// <summary>
        /// The directory the path-derived namespace is anchored at: the nearest owning <c>.asmdef</c>
        /// directory, else — for a file in the default <c>Assembly-CSharp</c> (no asmdef anywhere) —
        /// the configured project root (<c>Assets</c> by default). <c>null</c> only when the file is
        /// under no resolvable project root at all.
        /// </summary>
        public static string? ResolveDerivationAnchor(string filePath)
        {
            string? asmdefDir = FindOwningAsmdefDir(filePath);
            if (asmdefDir != null)
                return asmdefDir;
            string? projectRoot = AssetPathUtil.GetProjectRoot(filePath);
            if (projectRoot == null)
                return null;
            string dir = Path.GetDirectoryName(filePath) ?? filePath;
            return (projectRoot + "/" + UitkxConfig.LoadRoot(dir)).Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>
        /// The UI source-root directory a <c>~/</c> specifier/asset path resolves against for
        /// <paramref name="filePath"/> — the project root plus the configured <c>"root"</c>
        /// (default <c>Assets</c>). Shared by the source generator and HMR so <c>~/</c> hook
        /// imports resolve to the SAME target file on both sides (§7 parity). <c>null</c> when the
        /// file is under no resolvable project root.
        /// </summary>
        public static string? UiSourceRootDir(string filePath)
        {
            string? projectRoot = AssetPathUtil.GetProjectRoot(filePath);
            if (projectRoot == null)
                return null;
            string dir = Path.GetDirectoryName(filePath) ?? filePath;
            return (projectRoot + "/" + UitkxConfig.LoadRoot(dir)).Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>Walks up from the file to the nearest directory containing a <c>*.asmdef</c>,
        /// stopping at (and including) the <c>Assets</c> boundary. <c>null</c> if none is found.</summary>
        public static string? FindOwningAsmdefDir(string uitkxFilePath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(uitkxFilePath);
                while (!string.IsNullOrEmpty(dir))
                {
                    if (Directory.GetFiles(dir, "*.asmdef").Length > 0)
                        return dir;

                    string dirName = Path.GetFileName(dir);
                    if (string.Equals(dirName, "Assets", StringComparison.OrdinalIgnoreCase))
                        break;

                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch
            {
                // Never crash on filesystem errors.
            }
            return null;
        }
    }
}
