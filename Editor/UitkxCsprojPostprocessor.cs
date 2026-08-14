#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

namespace Ruitk.Editor
{
    /// <summary>
    /// Injects &lt;AdditionalFiles&gt; entries for every .uitkx file into each
    /// Unity-generated .csproj so that:
    ///
    ///   1. Unity's own Roslyn compilation pipeline sees the .uitkx files as
    ///      AdditionalTexts and passes them to <c>UitkxGenerator</c> via
    ///      <c>IIncrementalGenerator.AdditionalTextsProvider</c>.
    ///
    ///   2. IDEs (Rider, VS, VS Code) also receive the AdditionalFiles entries,
    ///      which the future UITKX language server extension will use for
    ///      IntelliSense (Phase 7).
    ///
    /// WHY THIS IS NEEDED
    /// ──────────────────
    /// Unity's .csproj files are generated, not hand-edited.  We cannot use
    /// Directory.Build.props because Unity does not run MSBuild — it calls
    /// Roslyn directly using configuration derived from the generated .csproj.
    /// The only supported way to inject additional items is through this callback.
    ///
    /// WHAT IT DOES
    /// ────────────
    /// For each .csproj Unity regenerates (triggered by any asset change),
    /// this method scans the Assets folder for *.uitkx files and appends one
    /// <c>&lt;AdditionalFiles Include="..." /&gt;</c> element per file.
    /// Files inside folders ending with "~" (Unity-ignored folders such as
    /// SourceGenerator~) are excluded — those are source/tooling files that
    /// are not part of any Unity assembly.
    /// </summary>
    public sealed class UitkxCsprojPostprocessor : AssetPostprocessor
    {
        // Called by Unity after every .csproj regeneration.
        // Return the (possibly modified) XML string; Unity writes it to disk.
        private static string OnGeneratedCSProject(string path, string content)
        {
            try
            {
                string[] uitkxPaths = FindUitkxFiles();

                if (uitkxPaths.Length == 0)
                    return content;

                // Parse the existing csproj XML.
                // Unity generates standard MSBuild XML with the msbuild namespace.
                XDocument doc = XDocument.Parse(content);
                XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";

                // Build a single <ItemGroup> with one <AdditionalFiles> per .uitkx file.
                var itemGroup = new XElement(ns + "ItemGroup");

                foreach (string uitkxPath in uitkxPaths)
                {
                    // XDocument.Parse can fail if the content has an XML declaration
                    // with encoding. Use an absolute, back-slash path so MSBuild and
                    // Roslyn's AdditionalFiles mechanism can locate the file.
                    string absolutePath = Path.GetFullPath(uitkxPath).Replace('/', '\\');

                    itemGroup.Add(
                        new XElement(
                            ns + "AdditionalFiles",
                            new XAttribute("Include", absolutePath)
                        )
                    );
                }

                doc.Root!.Add(itemGroup);

                // Reconstruct the XML string, preserving the declaration Unity uses
                // so we don't accidentally change the encoding Unity expects.
                // XDocument.ToString() omits the declaration, so we prepend it.
                var sb = new StringBuilder();
                if (doc.Declaration != null)
                {
                    sb.AppendLine(doc.Declaration.ToString());
                }
                sb.Append(doc.Root!.ToString());
                return sb.ToString();
            }
            catch (Exception ex)
            {
                // Never crash Unity's project generation — log and return original.
                Debug.LogError(
                    $"[Ruitk] UitkxCsprojPostprocessor failed to inject "
                        + $"AdditionalFiles into '{path}': {ex.Message}"
                );
                return content;
            }
        }

        /// <summary>
        /// Returns the absolute paths of every .uitkx file under <c>Assets/</c> and every
        /// writable (embedded/local) package root, excluding files inside Unity-ignored
        /// "~" directories. GEN-3: package-resident .uitkx were previously absent from the
        /// generated csprojs, so IDE (dotnet) compiles fell back to the generator's
        /// slower asmdef-root disk scan. Injecting them everywhere is safe for the same
        /// reason the Assets/ injection is: the generator filters AdditionalTexts by
        /// asmdef ownership per compilation (<c>UitkxPipeline.IsOwnedByCompilation</c>),
        /// so no csproj generates types for files it does not own.
        /// </summary>
        private static string[] FindUitkxFiles()
        {
            var roots = new System.Collections.Generic.List<string>();

            string assetsRoot = Path.GetFullPath("Assets");
            if (Directory.Exists(assetsRoot))
                roots.Add(assetsRoot);

            foreach (var pkg in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (pkg.source != UnityEditor.PackageManager.PackageSource.Embedded &&
                    pkg.source != UnityEditor.PackageManager.PackageSource.Local)
                    continue;
                if (!string.IsNullOrEmpty(pkg.resolvedPath) && Directory.Exists(pkg.resolvedPath))
                    roots.Add(pkg.resolvedPath);
            }

            var result = new System.Collections.Generic.List<string>();
            foreach (string root in roots)
            {
                string[] all;
                try
                {
                    all = Directory.GetFiles(root, "*.uitkx", SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    continue;
                }

                // Filter out any paths whose directory segments carry a "~" suffix
                // (e.g. SourceGenerator~, Samples~). Those are Unity-ignored tooling
                // folders — their files must never reach the compiler as AdditionalFiles.
                foreach (string p in all)
                {
                    if (!IsInsideIgnoredFolder(p))
                        result.Add(p);
                }
            }

            return result.ToArray();
        }

        private static bool IsInsideIgnoredFolder(string absolutePath)
        {
            // Split on both separator styles for safety.
            string[] parts = absolutePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }
            );

            foreach (string part in parts)
            {
                if (part.EndsWith("~", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
#endif
