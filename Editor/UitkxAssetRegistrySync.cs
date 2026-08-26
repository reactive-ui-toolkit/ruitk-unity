#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Ruitk.Core;
using UnityEditor;
using UnityEngine;

namespace Ruitk.Editor
{
    /// <summary>
    /// Populates the <see cref="UitkxAssetRegistry"/> ScriptableObject by scanning
    /// <c>.uitkx</c> files for <c>Asset&lt;T&gt;("path")</c>, <c>Ast&lt;T&gt;("path")</c>
    /// and <c>@uss "path"</c> references.
    ///
    /// <list type="bullet">
    ///   <item><b>Domain reload</b> — full rescan of all .uitkx files.</item>
    ///   <item><b>On save</b> — incremental update for changed files
    ///     (called by <see cref="UitkxChangeWatcher"/>).</item>
    /// </list>
    /// </summary>
    [InitializeOnLoad]
    internal static class UitkxAssetRegistrySync
    {
        private const string RegistryFolder = "Assets/Ruitk/Resources";
        private const string RegistryAssetPath = RegistryFolder + "/__uitkx_registry.asset";

        // 0.11.x location. The 0.12.0 rebrand moved the folder (ReactiveUITK -> Ruitk) but NOT
        // the asset name, and the runtime read is name-based
        // (Resources.Load<UitkxAssetRegistry>("__uitkx_registry")). If an upgraded project still
        // has the old asset, Resources.Load sees two same-named assets and resolves ambiguously —
        // when the stale one wins, every Asset<T>()/Ast<T>() and @uss lookup added after the
        // upgrade returns null, in the editor AND in player builds. Nothing self-heals it:
        // ClearRegistryIfExists only touches the new path. So: warn, loudly, once per reload.
        private const string LegacyRegistryAssetPath =
            "Assets/ReactiveUITK/Resources/__uitkx_registry.asset";

        private static bool s_warnedStaleRegistry;

        private static readonly Regex s_assetCallRe = new(
            @"(?:Asset|Ast)\s*<\s*(\w+)\s*>\s*\(\s*""([^""]+)""\s*\)",
            RegexOptions.Compiled);

        private static readonly Regex s_ussDirectiveRe = new(
            @"@uss\s+""([^""]+)""",
            RegexOptions.Compiled);

        static UitkxAssetRegistrySync()
        {
            EditorApplication.delayCall += FullRescan;
        }

        // ── Public API ───────────────────────────────────────────────

        /// <summary>
        /// Incremental sync: update registry entries for changed <c>.uitkx</c> files.
        /// Called by <see cref="UitkxChangeWatcher"/> on asset import.
        /// </summary>
        public static void SyncChangedFiles(string[] assetPaths)
        {
            bool anyUitkx = false;
            foreach (var p in assetPaths)
            {
                if (p.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                {
                    anyUitkx = true;
                    break;
                }
            }
            if (!anyUitkx) return;

            var registry = GetOrCreateRegistry();
            if (registry == null) return;

            bool dirty = false;
            string projectRoot = GetProjectRoot();

            foreach (var assetPath in assetPaths)
            {
                if (!assetPath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                    continue;

                string absPath = AssetPathToAbsolute(assetPath, projectRoot);
                if (absPath == null || !File.Exists(absPath)) continue;

                string content = File.ReadAllText(absPath);
                string uitkxDir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                var refs = ExtractAssetReferences(content, uitkxDir);

                foreach (var (key, resolvedAssetPath, typeName) in refs)
                {
                    var asset = LoadAssetTyped(resolvedAssetPath, typeName);
                    if (asset != null)
                    {
                        registry.Set(key, asset);
                        dirty = true;
                    }
                }
            }

            if (dirty)
            {
                EditorUtility.SetDirty(registry);
                AssetDatabase.SaveAssetIfDirty(registry);
            }
        }

        /// <summary>
        /// Full atomic rebuild of the registry from all <c>.uitkx</c> files under
        /// <c>Assets/</c> and every writable (embedded/local) package root — GEN-2:
        /// package-resident <c>Asset&lt;T&gt;()</c>/<c>@uss</c> references must register
        /// exactly like <c>Assets/</c>-resident ones.
        /// </summary>
        public static void FullRescan()
        {
            WarnIfStaleRegistryExists();

            var scanTargets = new List<(string absRoot, string assetRoot)>();
            string dataPath = Application.dataPath; // …/Assets
            scanTargets.Add((dataPath, "Assets"));

            foreach (var pkg in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (pkg.source != UnityEditor.PackageManager.PackageSource.Embedded &&
                    pkg.source != UnityEditor.PackageManager.PackageSource.Local)
                    continue;
                if (string.IsNullOrEmpty(pkg.resolvedPath) || !Directory.Exists(pkg.resolvedPath))
                    continue;
                scanTargets.Add((pkg.resolvedPath, "Packages/" + pkg.name));
            }

            var allEntries = new Dictionary<string, UnityEngine.Object>();
            bool anyFile = false;

            foreach (var (absRoot, assetRoot) in scanTargets)
            {
                string[] uitkxFiles;
                try
                {
                    uitkxFiles = Directory.GetFiles(absRoot, "*.uitkx", SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    continue; // folder not accessible (rare)
                }

                foreach (string absPath in uitkxFiles)
                {
                    string rel = absPath.Substring(absRoot.Length).Replace('\\', '/').TrimStart('/');
                    if (IsUnderTildeFolder(rel))
                        continue;
                    anyFile = true;

                    string assetPath = assetRoot + "/" + rel;
                    string content;
                    try { content = File.ReadAllText(absPath); }
                    catch { continue; }

                    string uitkxDir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                    var refs = ExtractAssetReferences(content, uitkxDir);

                    foreach (var (key, resolvedAssetPath, typeName) in refs)
                    {
                        var asset = LoadAssetTyped(resolvedAssetPath, typeName);
                        if (asset != null)
                            allEntries[key] = asset;
                    }
                }
            }

            if (!anyFile)
            {
                ClearRegistryIfExists();
                return;
            }

            if (allEntries.Count == 0)
            {
                ClearRegistryIfExists();
                return;
            }

            var registry = GetOrCreateRegistry();
            if (registry == null) return;

            var entries = new UitkxAssetRegistry.Entry[allEntries.Count];
            int i = 0;
            foreach (var kvp in allEntries)
                entries[i++] = new UitkxAssetRegistry.Entry { key = kvp.Key, asset = kvp.Value };

            registry.ReplaceAll(entries);
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssetIfDirty(registry);
        }

        // ── Image extensions handled by TextureImporter ───────────

        private static readonly HashSet<string> s_imageExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".psd",
            ".gif", ".tif", ".tiff", ".exr", ".hdr"
        };

        // ── Path parsing ─────────────────────────────────────────────

        private static List<(string key, string assetPath, string typeName)> ExtractAssetReferences(
            string content, string uitkxDir)
        {
            var result = new List<(string, string, string)>();

            foreach (Match m in s_ussDirectiveRe.Matches(content))
            {
                string rawPath = m.Groups[1].Value;
                string resolved = ResolvePath(uitkxDir, rawPath);
                result.Add((resolved, resolved, "StyleSheet"));
            }

            foreach (Match m in s_assetCallRe.Matches(content))
            {
                string typeName = m.Groups[1].Value;
                string rawPath = m.Groups[2].Value;
                string resolved = ResolvePath(uitkxDir, rawPath);
                result.Add((resolved, resolved, typeName));
            }

            return result;
        }

        /// <summary>
        /// Loads an asset with type-aware importer configuration.
        /// For image files, auto-configures the <see cref="TextureImporter"/>
        /// based on the requested type (Sprite vs Texture2D).
        /// </summary>
        private static UnityEngine.Object LoadAssetTyped(string assetPath, string typeName)
        {
            string ext = Path.GetExtension(assetPath);

            // Image files need importer configuration based on requested type
            if (s_imageExtensions.Contains(ext))
            {
                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer != null)
                {
                    if (string.Equals(typeName, "Sprite", StringComparison.Ordinal))
                    {
                        if (importer.textureType != TextureImporterType.Sprite)
                        {
                            importer.textureType = TextureImporterType.Sprite;
                            importer.spriteImportMode = SpriteImportMode.Single;
                            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                        }
                        return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                    }

                    if (string.Equals(typeName, "Texture2D", StringComparison.Ordinal))
                    {
                        if (importer.textureType == TextureImporterType.Sprite)
                        {
                            importer.textureType = TextureImporterType.Default;
                            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                        }
                        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    }
                }
            }

            return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
        }

        private static string ResolvePath(string uitkxDir, string rawPath)
        {
            if (rawPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                rawPath.StartsWith("Packages/", StringComparison.Ordinal))
                return rawPath;

            string combined = uitkxDir + "/" + rawPath;
            return NormalizePath(combined);
        }

        private static string NormalizePath(string path)
        {
            var parts = path.Replace('\\', '/').Split('/');
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

        // ── Registry SO management ───────────────────────────────────

        private static UitkxAssetRegistry GetOrCreateRegistry()
        {
            var registry = AssetDatabase.LoadAssetAtPath<UitkxAssetRegistry>(RegistryAssetPath);
            if (registry != null) return registry;

            string absFolder = Path.Combine(GetProjectRoot(), RegistryFolder.Replace('/', '\\'));
            if (!Directory.Exists(absFolder))
            {
                Directory.CreateDirectory(absFolder);
                AssetDatabase.Refresh();
            }

            registry = ScriptableObject.CreateInstance<UitkxAssetRegistry>();
            AssetDatabase.CreateAsset(registry, RegistryAssetPath);
            AssetDatabase.SaveAssets();
            return registry;
        }

        private static void ClearRegistryIfExists()
        {
            var registry = AssetDatabase.LoadAssetAtPath<UitkxAssetRegistry>(RegistryAssetPath);
            if (registry != null && registry.Entries.Count > 0)
            {
                registry.ReplaceAll(Array.Empty<UitkxAssetRegistry.Entry>());
                EditorUtility.SetDirty(registry);
                AssetDatabase.SaveAssetIfDirty(registry);
            }
        }

        /// <summary>
        /// 0.12.0 upgrade guard: the registry folder moved from <c>Assets/ReactiveUITK/Resources</c>
        /// to <c>Assets/Ruitk/Resources</c>, but <c>Resources.Load</c> looks the asset up BY NAME.
        /// A leftover 0.11.x registry therefore competes with the new one and can win, silently
        /// returning stale/null assets at runtime. Warn once per domain reload with the exact
        /// remedy — nothing else detects this.
        /// </summary>
        private static void WarnIfStaleRegistryExists()
        {
            if (s_warnedStaleRegistry) return;

            var stale = AssetDatabase.LoadAssetAtPath<UitkxAssetRegistry>(LegacyRegistryAssetPath);
            if (stale == null) return;

            s_warnedStaleRegistry = true;
            Debug.LogWarning(
                "[UITKX] A stale 0.11.x asset registry is still present at "
                    + $"'{LegacyRegistryAssetPath}'. The 0.12.0 rebrand moved it to "
                    + $"'{RegistryAssetPath}', but Resources.Load resolves it BY NAME "
                    + "(\"__uitkx_registry\"), so two same-named registries now compete and the "
                    + "stale one can win — Asset<T>()/Ast<T>() and @uss lookups added or changed "
                    + "since the upgrade would then return null, in the editor and in player "
                    + "builds. FIX: delete the whole 'Assets/ReactiveUITK' folder (and its .meta). "
                    + "It also holds the obsolete UITKX_GeneratorTrigger.g.cs. "
                    + "See MIGRATION-0.12.md, 'Upgrade steps'.");
        }

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        /// <summary>
        /// Maps a Unity asset path to an absolute OS path. <c>Packages/…</c> paths resolve
        /// through <see cref="UnityEditor.PackageManager.PackageInfo"/> (embedded package
        /// folders need not match the package name; local packages can live outside the
        /// project). Returns <c>null</c> for package paths Unity cannot resolve.
        /// </summary>
        private static string AssetPathToAbsolute(string assetPath, string projectRoot)
        {
            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (info == null || string.IsNullOrEmpty(info.resolvedPath))
                    return null;
                string prefix = "Packages/" + info.name;
                string rel = assetPath.Length > prefix.Length
                    ? assetPath.Substring(prefix.Length).TrimStart('/')
                    : string.Empty;
                return Path.GetFullPath(Path.Combine(info.resolvedPath, rel));
            }
            return Path.Combine(projectRoot, assetPath);
        }

        private static bool IsUnderTildeFolder(string relativePath)
        {
            foreach (var segment in relativePath.Split('/'))
            {
                if (segment.EndsWith("~", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
#endif
