#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Ruitk.Builder
{
    /// <summary>
    /// Resolves a .NET runtime able to host the net8.0 LSP server, following the
    /// repo's canonical tool chain (CLAUDE.md, PublishUtility precedent):
    /// <c>$RUITK_DOTNET</c> → <c>.ruitk-local.json</c> <c>dotnetPath</c> →
    /// Unity's bundled runtime (only when its shared framework is .NET 8+ —
    /// Unity 6000.0–6000.3 bundle 6.0.x, 6000.4+ bundle 8.x) → <c>dotnet</c> on
    /// PATH / standard install roots. Failure returns null and
    /// <see cref="FailureMessage"/> names every rung.
    /// </summary>
    internal static class RuitkDotnetLocator
    {
        private const string EnvVar = "RUITK_DOTNET";

        public static string FailureMessage =>
            "No .NET 8+ runtime found to host the UITKX language server. Probed, in order: "
            + "the RUITK_DOTNET environment variable, .ruitk-local.json { \"dotnetPath\": … } "
            + "at the package root, Unity's bundled runtime (needs a shared Microsoft.NETCore.App 8+, "
            + "present from Unity 6000.4), and dotnet on PATH / standard install roots. "
            + "Install the .NET 8 runtime or set one of the overrides.";

        public static string Resolve()
        {
            string fromEnv = Environment.GetEnvironmentVariable(EnvVar);
            if (Accept(fromEnv, "$" + EnvVar, out string env))
                return env;

            if (Accept(ReadLocalConfigDotnet(), ".ruitk-local.json dotnetPath", out string local))
                return local;

            if (TryBundled(out string bundled))
                return bundled;

            if (TrySystem(out string system))
                return system;

            return null;
        }

        private static bool Accept(string candidate, string source, out string accepted)
        {
            accepted = null;
            if (string.IsNullOrEmpty(candidate))
                return false;
            if (!File.Exists(candidate))
            {
                Debug.LogWarning($"[RUITK Builder] {source} points at '{candidate}' which does not exist - skipping.");
                return false;
            }
            accepted = candidate;
            return true;
        }

        [Serializable]
        private sealed class LocalConfig
        {
            public string dotnetPath;
        }

        private static string ReadLocalConfigDotnet()
        {
            try
            {
                if (!Ruitk.EditorSupport.RuitkPackagePaths.TryGetRoot(out string root))
                    return null;
                string path = Path.Combine(root, ".ruitk-local.json");
                if (!File.Exists(path))
                    return null;
                var cfg = JsonUtility.FromJson<LocalConfig>(File.ReadAllText(path));
                return cfg?.dotnetPath;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Unity's own runtime (the one HMR uses for external csc) hosts the server
        /// only when its bundled shared framework is 8+; the check reads the actual
        /// shared/Microsoft.NETCore.App version folders rather than trusting the
        /// editor version.
        /// </summary>
        private static bool TryBundled(out string dotnetPath)
        {
            dotnetPath = null;
            try
            {
                string editorDir = Path.GetDirectoryName(EditorApplication.applicationPath);
                if (editorDir == null)
                    return false;
                string exe = Path.Combine(editorDir, "Data", "NetCoreRuntime",
                    Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet");
                if (!File.Exists(exe))
                    return false;

                string sharedDir = Path.Combine(
                    editorDir, "Data", "NetCoreRuntime", "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(sharedDir))
                    return false;
                foreach (string dir in Directory.GetDirectories(sharedDir))
                {
                    string name = Path.GetFileName(dir);
                    if (Version.TryParse(name, out var v) && v.Major >= 8)
                    {
                        dotnetPath = exe;
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySystem(out string dotnetPath)
        {
            var candidates = new List<string>();
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                // Standard install roots are legitimate in discovery code
                // (machine-paths gate R1; FindDotnet precedent in UitkxTestRunnerWindow).
                candidates.Add(@"C:\Program Files\dotnet\dotnet.exe");
                candidates.Add(@"C:\Program Files (x86)\dotnet\dotnet.exe");
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(pf))
                    candidates.Add(Path.Combine(pf, "dotnet", "dotnet.exe"));
            }
            else
            {
                candidates.Add("/usr/local/share/dotnet/dotnet");
                candidates.Add("/usr/lib/dotnet/dotnet");
                candidates.Add("/usr/bin/dotnet");
            }

            foreach (string c in candidates)
            {
                if (File.Exists(c))
                {
                    dotnetPath = c;
                    return true;
                }
            }

            // Let the OS resolve PATH: spawning plain "dotnet" is the final rung.
            dotnetPath = PathHasDotnet() ? "dotnet" : null;
            return dotnetPath != null;
        }

        private static bool PathHasDotnet()
        {
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            char sep = Application.platform == RuntimePlatform.WindowsEditor ? ';' : ':';
            string exeName = Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet";
            foreach (string dir in pathVar.Split(sep))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir.Trim(), exeName)))
                        return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// Locates the server DLL: <c>Server~/</c> (shipped UPM layout) →
        /// <c>Server/</c> (Asset Store layout) → the dev repo's committed VS Code
        /// server folder. Null when absent.
        /// </summary>
        public static string ResolveServerDll()
        {
            if (!Ruitk.EditorSupport.RuitkPackagePaths.TryGetRoot(out string root))
                return null;

            string[] candidates =
            {
                Path.Combine(root, "Server~", "UitkxLanguageServer.dll"),
                Path.Combine(root, "Server", "UitkxLanguageServer.dll"),
                Path.Combine(root, "ide-extensions~", "vscode", "server", "UitkxLanguageServer.dll"),
            };
            foreach (string c in candidates)
            {
                if (File.Exists(c))
                    return c;
            }
            return null;
        }
    }
}
#endif
