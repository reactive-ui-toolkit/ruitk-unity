using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Ruitk.Language
{
    /// <summary>
    /// Canonical Unity asset-path resolution rule, shared by every consumer that turns a
    /// bare/relative path written in a <c>.uitkx</c> file (e.g. <c>@uss "styles.uss"</c>,
    /// <c>Asset&lt;Texture2D&gt;("icon.png")</c>) into a Unity project-relative path.
    ///
    /// The rule: a path already rooted at <c>Assets/</c> or <c>Packages/</c> is absolute and
    /// passed through unchanged; every other path (bare <c>"styles.uss"</c> or explicitly
    /// relative <c>"./styles.uss"</c> / <c>"../shared/styles.uss"</c>) is resolved relative to
    /// the directory containing the <c>.uitkx</c> file, with <c>.</c>/<c>..</c> segments
    /// collapsed.
    ///
    /// Before this existed, four independent consumers disagreed on bare-path semantics
    /// (uitkx-dir-relative vs. as-is-unresolved vs. project-root-relative) — the editor could
    /// show no error while the build emitted an unresolvable path, or HMR's USS dependency map
    /// could miss a file entirely (see FINAL_AUDIT_UITKX_FINDINGS.md, finding H-03).
    ///
    /// <c>Editor/HMR</c> cannot reference this type directly (its asmdef only references
    /// <c>Ruitk.Shared</c>/<c>Ruitk.Runtime</c> — the language-lib is consumed via
    /// reflection against the committed analyzer DLL, never a normal assembly reference). Its
    /// HMR-side mirror must be kept byte-for-byte identical to this algorithm; see
    /// <c>UitkxHmrController.HmrAssetPathUtil</c>.
    /// </summary>
    public static class AssetPathUtil
    {
        /// <summary>
        /// Resolves <paramref name="rawPath"/> against <paramref name="uitkxDir"/> per the rule
        /// above. <paramref name="uitkxDir"/> should be a Unity project-relative directory (e.g.
        /// <c>"Assets/UI"</c>), typically obtained from <see cref="GetAssetDir"/>.
        /// </summary>
        /// <summary>The UI source root a <c>~/</c> asset path resolves against (engine default).</summary>
        public const string DefaultRoot = "Assets";

        public static string ResolveAssetPath(string uitkxDir, string rawPath, string? root = null)
        {
            if (string.IsNullOrEmpty(rawPath))
                return rawPath;

            // ~/ (root alias, import/export grammar, leg 3) resolves against the UI source root.
            // Engine default is "Assets"; callers that walk uitkx.config.json pass its "root" here
            // (nearest-config-wins, see UitkxConfig.LoadRoot). Null → the default.
            if (rawPath.StartsWith("~/", StringComparison.Ordinal))
                return Collapse((string.IsNullOrEmpty(root) ? DefaultRoot : root) + "/" + rawPath.Substring(2));

            if (rawPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                rawPath.StartsWith("Packages/", StringComparison.Ordinal))
                return rawPath;

            string combined = string.IsNullOrEmpty(uitkxDir) ? rawPath : uitkxDir + "/" + rawPath;
            return Collapse(combined);
        }

        /// <summary>Collapse <c>.</c>/<c>..</c>/empty segments in a forward-slashed path.</summary>
        private static string Collapse(string combined)
        {
            var parts = combined.Replace('\\', '/').Split('/');
            var stack = new List<string>();
            foreach (var p in parts)
            {
                if (p == "." || p == "") continue;
                if (p == ".." && stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                else if (p != "..")
                    stack.Add(p);
            }
            return string.Join("/", stack);
        }

        /// <summary>
        /// Extracts the Unity project-relative directory (e.g. <c>"Assets/UI"</c>) containing
        /// the file at <paramref name="filePath"/> (an absolute OS path). For a file inside a
        /// UPM package (no <c>Assets/</c> segment), walks up to the nearest <c>package.json</c>
        /// and returns the Unity asset-path form <c>"Packages/&lt;package-name&gt;/&lt;dir&gt;"</c> —
        /// the package NAME from the manifest, never the physical folder name, because Unity
        /// asset paths are name-keyed while embedded package folders are free-form. Returns
        /// <c>null</c> when neither applies (e.g. a test-environment temp path).
        /// </summary>
        public static string? GetAssetDir(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return null;

            string normalized = filePath.Replace('\\', '/');
            int assetsIdx = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (assetsIdx < 0)
            {
                if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    int lastSlash = normalized.LastIndexOf('/');
                    return lastSlash > 0 ? normalized.Substring(0, lastSlash) : "Assets";
                }

                if (TryGetPackageContext(filePath, out string packageRootAbs, out string packageName))
                {
                    string rootNorm = packageRootAbs.Replace('\\', '/').TrimEnd('/');
                    string rel = normalized.Length > rootNorm.Length
                        ? normalized.Substring(rootNorm.Length).TrimStart('/')
                        : string.Empty;
                    int relSlash = rel.LastIndexOf('/');
                    string relDir = relSlash >= 0 ? rel.Substring(0, relSlash) : string.Empty;
                    return relDir.Length == 0
                        ? "Packages/" + packageName
                        : "Packages/" + packageName + "/" + relDir;
                }

                return null;
            }

            string assetPath = normalized.Substring(assetsIdx + 1);
            int dirSlash = assetPath.LastIndexOf('/');
            return dirSlash >= 0 ? assetPath.Substring(0, dirSlash) : "Assets";
        }

        private static readonly Regex s_packageNameRe = new Regex(
            "\"name\"\\s*:\\s*\"([^\"]+)\"",
            RegexOptions.Compiled);

        private static readonly ConcurrentDictionary<string, (string Root, string Name)?> s_packageCtxByDir =
            new ConcurrentDictionary<string, (string Root, string Name)?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Walks up from <paramref name="filePath"/> to the nearest <c>package.json</c> and
        /// returns the package's physical root directory plus its manifest <c>"name"</c>.
        /// Directory-cached (a build/IDE session resolves the same folders thousands of times);
        /// call <see cref="InvalidatePackageContextCache"/> after a manifest rename.
        /// </summary>
        public static bool TryGetPackageContext(string filePath, out string packageRootAbs, out string packageName)
        {
            packageRootAbs = string.Empty;
            packageName = string.Empty;
            if (string.IsNullOrEmpty(filePath))
                return false;

            string? dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); }
            catch { return false; }

            string? probe = dir;
            while (!string.IsNullOrEmpty(probe))
            {
                if (s_packageCtxByDir.TryGetValue(probe, out var cached))
                {
                    if (cached == null)
                        return false;
                    packageRootAbs = cached.Value.Root;
                    packageName = cached.Value.Name;
                    CacheRange(dir, probe, cached);
                    return true;
                }

                string manifest = Path.Combine(probe, "package.json");
                if (File.Exists(manifest))
                {
                    string name;
                    try
                    {
                        var m = s_packageNameRe.Match(File.ReadAllText(manifest));
                        name = m.Success ? m.Groups[1].Value : string.Empty;
                    }
                    catch
                    {
                        name = string.Empty;
                    }

                    (string, string)? ctx = name.Length > 0 ? (probe, name) : ((string, string)?)null;
                    CacheRange(dir, probe, ctx);
                    if (ctx == null)
                        return false;
                    packageRootAbs = probe;
                    packageName = name;
                    return true;
                }

                probe = Path.GetDirectoryName(probe);
            }

            CacheRange(dir, null, null);
            return false;
        }

        /// <summary>Drops the package-manifest directory cache (manifest renamed/moved).</summary>
        public static void InvalidatePackageContextCache() => s_packageCtxByDir.Clear();

        private static void CacheRange(string? fromDir, string? foundAt, (string Root, string Name)? ctx)
        {
            string? d = fromDir;
            while (d != null && d.Length > 0)
            {
                s_packageCtxByDir[d] = ctx;
                if (string.Equals(d, foundAt, StringComparison.OrdinalIgnoreCase))
                    return;
                d = Path.GetDirectoryName(d);
            }
        }

        /// <summary>
        /// Extracts the Unity project root (the folder containing <c>Assets/</c>) from an
        /// absolute file path. Returns <c>null</c> when <c>Assets/</c> is not found.
        /// </summary>
        public static string? GetProjectRoot(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return null;

            string normalized = filePath.Replace('\\', '/');
            int assetsIdx = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            return assetsIdx >= 0 ? normalized.Substring(0, assetsIdx) : null;
        }
    }
}
