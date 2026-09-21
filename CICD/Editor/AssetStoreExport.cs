using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ruitk.CICD
{
    /// <summary>
    /// Headless Asset Store package export (Tier A of Plans~/ASSET_STORE_PUBLISHING_PLAN.md).
    ///
    /// Runs inside a SHELL project whose Assets/ReactiveUIToolkit holds the store-shaped
    /// content (the dist omit-list applied, Samples kept visible, plus CICD/ so this method
    /// exists to be called):
    ///
    ///   Unity -batchmode -nographics -quit -projectPath &lt;shell&gt;
    ///         -executeMethod Ruitk.CICD.AssetStoreExport.Run
    ///         [-exportOut &lt;absolute .unitypackage path&gt;]
    ///
    /// CICD/ itself never ships: it is excluded from the collected asset list, and the
    /// guard rails below turn any packaging surprise into a red CI run instead of a store
    /// rejection two review-days later. Script compile errors abort batchmode before this
    /// method runs, so a package that does not compile on the floor Unity version can never
    /// export — that IS the validation the store reviewers apply first.
    /// </summary>
    internal static class AssetStoreExport
    {
        /// <summary>
        /// DELIBERATELY A LITERAL — do not route this through <c>RuitkPackagePaths</c>.
        ///
        /// Three reasons, in order of weight:
        /// <list type="number">
        /// <item>It is an ASSET-DATABASE path, not a filesystem path. It is consumed by
        ///   <c>AssetDatabase.FindAssets(searchInFolders:)</c> and by <c>StartsWith</c> prefix tests on
        ///   other asset paths. <c>RuitkPackagePaths</c> returns an absolute filesystem path, which is
        ///   the wrong currency here.</item>
        /// <item>It is a CONTRACT with <c>publish.yml</c>, which hand-builds the shell project at
        ///   <c>STORE_DIR="shell/Assets/ReactiveUIToolkit"</c>. Both sides must name the same location;
        ///   deriving one side would let them drift apart silently.</item>
        /// <item>Asserting the layout IS the job. The <c>RequirePrefix</c> guard rails below exist to
        ///   turn a packaging surprise into a red CI run. A resolver that adapts to whatever layout it
        ///   finds would defeat exactly that check — and since the shell project always has the package
        ///   under <c>Assets/</c> (layout a), rung 1 would return null anyway and rungs 2-3 would be a
        ///   more elaborate way to arrive back at this same string.</item>
        /// </list>
        /// </summary>
        private const string PackageRoot = "Assets/ReactiveUIToolkit";

        public static void Run()
        {
            try
            {
                string outPath = ArgAfter("-exportOut");
                if (string.IsNullOrEmpty(outPath))
                {
                    outPath = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "ReactiveUIToolkit.unitypackage"
                    );
                }

                var paths = AssetDatabase
                    .FindAssets(string.Empty, new[] { PackageRoot })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Distinct()
                    .Where(p =>
                        !p.StartsWith(PackageRoot + "/CICD", StringComparison.OrdinalIgnoreCase)
                    )
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToArray();

                if (paths.Length == 0)
                {
                    Fail("no assets found under " + PackageRoot);
                    return;
                }

                // Must-ship guard rails: a store install is broken without these.
                RequirePrefix(paths, PackageRoot + "/Runtime");
                RequirePrefix(paths, PackageRoot + "/Shared");
                RequirePrefix(paths, PackageRoot + "/Signals");
                RequirePrefix(paths, PackageRoot + "/Editor");
                RequirePrefix(paths, PackageRoot + "/Analyzers");

                // Must-NOT-ship guard rails.
                if (paths.Any(p => p.Contains("publisher-secrets")))
                {
                    Fail("publisher-secrets leaked into the export set");
                    return;
                }
                if (paths.Any(p => p.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
                {
                    Fail("a .pdb leaked into the export set (omit-list not applied?)");
                    return;
                }
                if (paths.Any(p => p.EndsWith("/CLAUDE.md", StringComparison.OrdinalIgnoreCase)))
                {
                    Fail("repo-internal files leaked into the export set (pathsToOmitFromStore not applied?)");
                    return;
                }

                string outDir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(outDir))
                {
                    Directory.CreateDirectory(outDir);
                }

                AssetDatabase.ExportPackage(paths, outPath, ExportPackageOptions.Default);

                if (!File.Exists(outPath))
                {
                    Fail("ExportPackage produced no file at " + outPath);
                    return;
                }

                long size = new FileInfo(outPath).Length;
                Debug.Log(
                    $"[AssetStoreExport] exported {paths.Length} assets ({size / 1024} KB) -> {outPath}"
                );
                Debug.Log("[AssetStoreExport] OK");
            }
            catch (Exception ex)
            {
                Debug.LogError("[AssetStoreExport] FAILED: " + ex);
                EditorApplication.Exit(1);
            }
        }

        private static void RequirePrefix(string[] paths, string prefix)
        {
            if (!paths.Any(p => p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
            {
                Fail("required content missing from export set: " + prefix);
            }
        }

        private static void Fail(string message)
        {
            Debug.LogError("[AssetStoreExport] FAILED: " + message);
            EditorApplication.Exit(1);
        }

        private static string ArgAfter(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }
    }
}
