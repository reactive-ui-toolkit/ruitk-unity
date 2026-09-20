#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Ruitk.EditorSupport
{
    /// <summary>
    /// Finds the editor's own bundled .NET runtime and Roslyn compiler.
    ///
    /// <para>Two callers need this and both used to compute it themselves as
    /// <c>Path.GetDirectoryName(EditorApplication.applicationPath) + "/Data"</c> — the
    /// WINDOWS install shape. macOS puts the same content under
    /// <c>Unity.app/Contents</c>, so HMR could not start there at all and the Builder's
    /// language-server launcher silently fell through to a system runtime. Filename
    /// fallbacks (<c>dotnet.exe</c> then <c>dotnet</c>) papered over the extension and
    /// left the LAYOUT wrong, which is why it read as fixed.</para>
    ///
    /// <para>Resolution follows the repo's tool chain (CLAUDE.md, "Machine-local paths"):
    /// <c>$RUITK_UNITY_DATA</c> → <c>.ruitk-local.json</c> <c>unityDataPath</c> →
    /// <see cref="EditorApplication.applicationContentsPath"/>, which is Unity's own
    /// answer and correct on every platform → the historical <c>&lt;editor dir&gt;/Data</c>,
    /// kept as a rung so Windows cannot regress. Each root is then probed for the
    /// subdirectory that actually holds <c>NetCoreRuntime</c>.</para>
    ///
    /// <para>Nothing here is hardcoded per-OS: the candidates are enumerated and the
    /// first that EXISTS wins, so a layout we have not seen is a matter of adding one
    /// string. On exhaustion the caller gets every path probed — the previous error
    /// named a single path, which is why the bug report had to read our source to
    /// explain itself.</para>
    /// </summary>
    public static class UnityBundledRuntime
    {
        private const string EnvVar = "RUITK_UNITY_DATA";

        /// <summary>Subdirectories of a content root that have been seen to hold the scripting payload.</summary>
        private static readonly string[] s_relativeRoots =
        {
            "",
            "Data",
            Path.Combine("Resources", "Scripting"),
        };

        /// <summary>
        /// The directory containing <c>NetCoreRuntime</c>, plus the host executable inside it.
        /// <paramref name="probed"/> lists every path considered, in order, for the error message.
        /// </summary>
        public static bool TryFindScriptingRoot(
            out string scriptingRoot,
            out string dotnetHost,
            out IReadOnlyList<string> probed
        )
        {
            var attempted = new List<string>();
            probed = attempted;
            scriptingRoot = null;
            dotnetHost = null;

            foreach (string root in CandidateContentRoots())
            {
                foreach (string relative in s_relativeRoots)
                {
                    string candidate = relative.Length == 0 ? root : Path.Combine(root, relative);
                    foreach (string exe in HostExecutableNames())
                    {
                        string host = Path.Combine(candidate, "NetCoreRuntime", exe);
                        attempted.Add(host);
                        if (File.Exists(host))
                        {
                            scriptingRoot = candidate;
                            dotnetHost = host;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The bundled Roslyn <c>csc.dll</c>. Through 6000.4 it sits in a dedicated
        /// <c>DotNetSdkRoslyn</c> folder; 6000.5 removed that and ships Roslyn inside the
        /// full bundled SDK under a VERSION-NUMBERED directory, so the version segment is
        /// enumerated rather than hardcoded and the highest wins.
        ///
        /// Every candidate root is tried, not just the one that yielded the runtime host:
        /// nothing guarantees the two live under the same subdirectory on a layout we have
        /// not inspected, and trying the rest costs a handful of File.Exists calls.
        /// </summary>
        public static bool TryFindBundledCsc(
            string preferredRoot,
            out string cscPath,
            out IReadOnlyList<string> probed
        )
        {
            var attempted = new List<string>();
            probed = attempted;
            cscPath = null;

            foreach (string root in RootsPreferring(preferredRoot))
            {
                string legacy = Path.Combine(root, "DotNetSdkRoslyn", "csc.dll");
                attempted.Add(legacy);
                if (File.Exists(legacy))
                {
                    cscPath = legacy;
                    return true;
                }

                string sdkRoot = Path.Combine(root, "DotNetSdk", "sdk");
                attempted.Add(Path.Combine(sdkRoot, "<version>", "Roslyn", "bincore", "csc.dll"));
                if (!Directory.Exists(sdkRoot))
                {
                    continue;
                }

                string best = null;
                Version bestVersion = null;
                foreach (string dir in Directory.GetDirectories(sdkRoot))
                {
                    string candidate = Path.Combine(dir, "Roslyn", "bincore", "csc.dll");
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }
                    Version.TryParse(Path.GetFileName(dir), out Version version);
                    if (best == null || (version != null && (bestVersion == null || version > bestVersion)))
                    {
                        best = candidate;
                        bestVersion = version;
                    }
                }
                if (best != null)
                {
                    cscPath = best;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The highest <c>Microsoft.NETCore.App</c> major version the bundled host can run,
        /// or 0 when the shared framework folder is absent. Unity 6000.0-6000.3 bundle 6.0.x
        /// and 6000.4+ bundle 8.x, but the actual folders are read rather than the editor
        /// version trusted.
        /// </summary>
        public static int BundledSharedFrameworkMajor(string scriptingRoot)
        {
            try
            {
                string sharedDir = Path.Combine(
                    scriptingRoot,
                    "NetCoreRuntime",
                    "shared",
                    "Microsoft.NETCore.App"
                );
                if (!Directory.Exists(sharedDir))
                {
                    return 0;
                }
                int best = 0;
                foreach (string dir in Directory.GetDirectories(sharedDir))
                {
                    if (Version.TryParse(Path.GetFileName(dir), out Version version) && version.Major > best)
                    {
                        best = version.Major;
                    }
                }
                return best;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>One line per probed path, for an error a reader can act on.</summary>
        public static string DescribeProbe(IReadOnlyList<string> probed)
        {
            if (probed == null || probed.Count == 0)
            {
                return "  (nothing probed)";
            }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < probed.Count; i++)
            {
                sb.Append("  ").Append(probed[i]);
                if (i < probed.Count - 1)
                {
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        public static string OverrideHint =>
            "Override with the $" + EnvVar + " environment variable or .ruitk-local.json "
            + "{ \"unityDataPath\": \"<the directory holding NetCoreRuntime>\" } at the package root.";

        private static IEnumerable<string> RootsPreferring(string preferredRoot)
        {
            if (!string.IsNullOrEmpty(preferredRoot))
            {
                yield return preferredRoot;
            }
            foreach (string root in CandidateContentRoots())
            {
                foreach (string relative in s_relativeRoots)
                {
                    string candidate = relative.Length == 0 ? root : Path.Combine(root, relative);
                    if (candidate != preferredRoot && Directory.Exists(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }

        private static IEnumerable<string> CandidateContentRoots()
        {
            string fromEnv = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrEmpty(fromEnv))
            {
                yield return fromEnv;
            }

            string fromConfig = ReadLocalConfigDataPath();
            if (!string.IsNullOrEmpty(fromConfig))
            {
                yield return fromConfig;
            }

            string contents = EditorApplication.applicationContentsPath;
            if (!string.IsNullOrEmpty(contents))
            {
                yield return contents;
            }

            string editorDir = null;
            try
            {
                editorDir = Path.GetDirectoryName(EditorApplication.applicationPath);
            }
            catch
            {
                editorDir = null;
            }
            if (!string.IsNullOrEmpty(editorDir))
            {
                yield return editorDir;
            }
        }

        private static IEnumerable<string> HostExecutableNames()
        {
            yield return "dotnet.exe";
            yield return "dotnet";
        }

        [Serializable]
        private sealed class LocalConfig
        {
            public string unityDataPath;
        }

        private static string ReadLocalConfigDataPath()
        {
            try
            {
                if (!RuitkPackagePaths.TryGetRoot(out string root))
                {
                    return null;
                }
                string path = Path.Combine(root, ".ruitk-local.json");
                if (!File.Exists(path))
                {
                    return null;
                }
                return JsonUtility.FromJson<LocalConfig>(File.ReadAllText(path))?.unityDataPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
#endif
