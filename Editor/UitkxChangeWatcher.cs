#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Ruitk.EditorSupport.HMR;

namespace Ruitk.Editor
{
    /// <summary>
    /// Forces Unity to recompile the assembly that OWNS a changed .uitkx file, so
    /// the source generator re-runs and picks up the new content.
    ///
    /// WHY THIS IS NEEDED
    /// ──────────────────
    /// Unity only re-runs Roslyn (and therefore source generators) when a .cs file
    /// in an assembly changes. Saving a .uitkx alone does not recompile anything, so
    /// the generator never sees the updated content.
    ///
    /// HOW IT WORKS (fast, incremental — no CleanBuildCache stall)
    /// ──────────────────────────────────────────────────────────
    /// A .uitkx file belongs to whatever assembly its folder's .asmdef defines
    /// (e.g. Samples/*.uitkx → Ruitk.Samples; .uitkx with no ancestor
    /// .asmdef → the default Assembly-CSharp). To make THAT assembly recompile we
    /// write a tiny trigger .cs into its own folder and bump a value inside it. A real
    /// script change is exactly what a manual .cs edit does: Unity recompiles just that
    /// one assembly INCREMENTALLY (the analyzer/generator stays warm), and a genuine
    /// recompile re-reads the assembly's .uitkx files fresh from disk.
    ///
    /// The ancestor-asmdef walk covers Assets/ AND writable package layouts (embedded
    /// Packages/, local file: installs). It must: an Assets/-only walk classified every
    /// package-resident .uitkx as Assembly-CSharp, dirtied the wrong assembly, and left
    /// the package assembly's generated output stale on every edit (the field symptom:
    /// edit a sample in an embedded package, Unity compiles, Play shows the old UI).
    /// Immutable packages are excluded — read-only content cannot change, and the
    /// package cache must never be written to.
    ///
    /// This replaced two earlier approaches, both wrong:
    ///   • A single trigger .cs in Assembly-CSharp (the shipped behavior) dirties only
    ///     Assembly-CSharp — never the asmdef assembly a component actually lives in —
    ///     so asmdef-owned .uitkx edits produced STALE generated output until a full
    ///     reimport / Library clear / any .cs edit. (This was the regression: commit
    ///     3f41aa8 removed CleanBuildCache, which had masked the problem by force-
    ///     recompiling EVERY assembly — at the cost of a 30-40s cold-analyzer stall on
    ///     HMR Stop.)
    ///   • Reimporting the owning .asmdef ASSET (ImportAsset ForceUpdate) does not
    ///     recompile the assembly — reimporting an asmdef is not a script change — so
    ///     it left output just as stale.
    ///
    /// Writing an actual .cs into the owning assembly is the only thing that reliably
    /// forces the incremental recompile, and it keeps the analyzer warm (no clean).
    ///
    /// While HMR is active it hot-swaps .uitkx edits directly, so we skip the cold
    /// recompile entirely — it would fight the assembly-reload lock and, batched at
    /// HMR Stop, was the original source of the 30-40s stall.
    ///
    /// Work is deferred via <see cref="EditorApplication.delayCall"/> so it runs
    /// outside the import callback, avoiding a recursive import loop.
    /// </summary>
    public sealed class UitkxChangeWatcher : AssetPostprocessor
    {
        private const string TriggerFileName = "UITKX_GeneratorTrigger.g.cs";

        // Folder (asset path) used for .uitkx that belong to the default assembly
        // (no ancestor .asmdef → Assembly-CSharp). Created on demand. Chosen inside
        // the consuming project's Assets/ so it works whether the package lives under
        // Assets/ (dev) or is UPM-installed under Packages/ (read-only).
        private const string DefaultAssemblyFolderAsset = "Assets/Ruitk";

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths
        )
        {
            // The distinct set of owning-assembly FOLDERS (asset paths) to dirty, plus
            // whether any changed .uitkx belongs to the default (Assembly-CSharp) one.
            var owningFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyDefaultAssembly = false;
            bool anyUitkx = false;

            foreach (var arr in new[] { importedAssets, deletedAssets, movedAssets, movedFromAssetPaths })
            {
                foreach (string p in arr)
                {
                    if (!p.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                        continue;
                    anyUitkx = true;
                    string folder = FindOwningAsmdefFolder(p);
                    if (folder != null)
                        owningFolders.Add(folder);
                    else
                        anyDefaultAssembly = true;
                }
            }

            if (!anyUitkx)
                return;

            // Defer out of the import callback (avoids recursive import).
            EditorApplication.delayCall += () => TriggerRecompile(owningFolders, anyDefaultAssembly);

            // Sync asset registry entries for changed .uitkx files.
            var imported = importedAssets;
            EditorApplication.delayCall += () => UitkxAssetRegistrySync.SyncChangedFiles(imported);
        }

        private static void TriggerRecompile(HashSet<string> owningFolders, bool anyDefaultAssembly)
        {
            // HMR is active: it hot-swaps .uitkx edits live. A cold recompile here
            // would fight the assembly-reload lock and, batched, was the original
            // 30-40s HMR-Stop stall. Let HMR own the update.
            if (UitkxHmrController.IsActive)
                return;

            if (anyDefaultAssembly)
                WriteTrigger(DefaultAssemblyFolderAsset, createIfMissing: true);

            foreach (string folder in owningFolders)
                WriteTrigger(folder, createIfMissing: false);

            // Incremental recompile of just the dirtied assemblies — NOT
            // CleanBuildCache (that clears Roslyn's cache and cold-restarts every
            // analyzer, the 30-40s stall). The trigger .cs above is a real script
            // change, which is all Unity needs to recompile the owning assembly and
            // re-read its .uitkx fresh.
            CompilationPipeline.RequestScriptCompilation();
        }

        /// <summary>
        /// Writes/updates the trigger .cs inside <paramref name="folderAssetPath"/> so
        /// the assembly that owns that folder recompiles. The bumped <c>Stamp</c> value
        /// guarantees a genuine content change every save.
        /// </summary>
        private static void WriteTrigger(string folderAssetPath, bool createIfMissing)
        {
            try
            {
                string absDir = ResolveAssetFolderAbsolute(folderAssetPath);
                if (absDir == null)
                    return; // immutable package — never write into the cache
                if (!Directory.Exists(absDir))
                {
                    if (!createIfMissing)
                        return; // folder gone — nothing to do
                    Directory.CreateDirectory(absDir);
                }

                string absFile = Path.Combine(absDir, TriggerFileName);
                string content =
                    "// <auto-generated/> UITKX recompile trigger.\n"
                    + "// Rewritten by UitkxChangeWatcher on each .uitkx save to force Unity to\n"
                    + "// recompile THIS assembly so the source generator re-reads its .uitkx files.\n"
                    + "// Safe to delete; gitignored by default. Do not reference this type.\n"
                    + "namespace Ruitk.Generated\n"
                    + "{\n"
                    + "    internal static class UitkxRecompileTrigger\n"
                    + "    {\n"
                    + $"        internal const long Stamp = {DateTime.UtcNow.Ticks}L;\n"
                    + "    }\n"
                    + "}\n";
                File.WriteAllText(absFile, content);

                // ImportAsset tells Unity this .cs changed, scheduling the recompile.
                AssetDatabase.ImportAsset(
                    folderAssetPath + "/" + TriggerFileName,
                    ImportAssetOptions.ForceUpdate
                );
            }
            catch
            {
                // Best effort — a failed trigger write must never break asset import.
            }
        }

        /// <summary>
        /// Returns the asset-path folder of the nearest ancestor that contains an
        /// .asmdef (i.e. the root folder of the assembly that owns
        /// <paramref name="uitkxAssetPath"/>), or <c>null</c> when the file belongs to
        /// the default assembly (no .asmdef up to the Assets root → Assembly-CSharp).
        /// Handles both <c>Assets/</c>-rooted paths and WRITABLE package layouts
        /// (embedded <c>Packages/…</c> and local <c>file:</c> installs, resolved via
        /// <see cref="UnityEditor.PackageManager.PackageInfo"/>) — an Assets/-only walk
        /// silently classified every package-resident .uitkx as Assembly-CSharp, so the
        /// trigger dirtied the wrong assembly and edits produced STALE generated output
        /// until an unrelated .cs change. A .uitkx inside an IMMUTABLE package returns
        /// null (its content cannot change, and the cache must never be written to).
        /// </summary>
        private static string FindOwningAsmdefFolder(string uitkxAssetPath)
        {
            string dir = Path.GetDirectoryName(uitkxAssetPath)?.Replace('\\', '/');

            while (!string.IsNullOrEmpty(dir) && IsWithinAsmdefWalkRoot(dir))
            {
                string absDir = ResolveAssetFolderAbsolute(dir);
                if (absDir != null
                    && Directory.Exists(absDir)
                    && Directory.GetFiles(absDir, "*.asmdef", SearchOption.TopDirectoryOnly).Length > 0)
                {
                    return dir;
                }
                int slash = dir.LastIndexOf('/');
                dir = slash > 0 ? dir.Substring(0, slash) : null;
            }
            return null;
        }

        /// <summary>
        /// True while the ancestor walk is still inside a folder that can own an
        /// .asmdef: anywhere under (or at) <c>Assets</c>, or inside a package
        /// (<c>Packages/&lt;id&gt;</c> and below — the package root itself may hold the
        /// asmdef; the bare <c>Packages</c> folder cannot).
        /// </summary>
        private static bool IsWithinAsmdefWalkRoot(string dir)
        {
            return dir.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                || dir.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || (dir.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                    && dir.Length > "Packages/".Length);
        }

        /// <summary>
        /// Project-relative asset folder → absolute filesystem folder.
        /// <c>Assets/…</c> joins onto the project root. <c>Packages/…</c> resolves
        /// through <see cref="UnityEditor.PackageManager.PackageInfo"/> so embedded and
        /// local (<c>file:</c>) layouts map to their real location; immutable package
        /// sources return <c>null</c> — their content is read-only, so no trigger may
        /// (or needs to) be written there.
        /// </summary>
        private static string ResolveAssetFolderAbsolute(string folderAssetPath)
        {
            if (folderAssetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(folderAssetPath);
                if (package == null
                    || (package.source != UnityEditor.PackageManager.PackageSource.Embedded
                        && package.source != UnityEditor.PackageManager.PackageSource.Local))
                {
                    return null;
                }
                string rel = folderAssetPath.Length > package.assetPath.Length
                    ? folderAssetPath.Substring(package.assetPath.Length).TrimStart('/')
                    : string.Empty;
                return Path.GetFullPath(Path.Combine(
                    package.resolvedPath, rel.Replace('/', Path.DirectorySeparatorChar)));
            }

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, folderAssetPath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
#endif
