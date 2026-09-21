using System;
using Ruitk.EditorSupport;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Ruitk.CICD
{
    /// <summary>
    /// Runs the SHIPPED bundled-runtime probe and reports what it found, so the question
    /// "does HMR start on this platform?" is answered by the code users run.
    ///
    /// <para>#255 was that HMR looked for the editor's bundled .NET in the Windows layout
    /// only, so it could not start on macOS. The fix probes instead of assuming. Proving
    /// that on a platform nobody here owns means running it there — and it has to be THIS
    /// code, not a re-implementation. A JavaScript copy of the same candidate grid was the
    /// first attempt, and it would have reported success while the real thing threw: the
    /// 0.21.0 <c>Path.Combine("&lt;version&gt;")</c> bug was an illegal-character exception
    /// that only Mono raises, on exactly this code path, and it killed the Builder on open.
    /// JavaScript has no opinion about that character. This does.</para>
    ///
    /// <code>
    /// Unity -batchmode -nographics -quit -projectPath &lt;any project&gt;
    ///       -executeMethod Ruitk.CICD.BundledRuntimeReport.Run
    /// </code>
    ///
    /// <para>Exits 0 when both the .NET host and the Roslyn compiler are found, 1 otherwise,
    /// and prints every path probed either way — a failure that does not say where it looked
    /// sends the next person to read our source, which is what the original #255 report had
    /// to do.</para>
    /// </summary>
    internal static class BundledRuntimeReport
    {
        /// <summary>
        /// Every probed path, each on its own PREFIXED line. <c>DescribeProbe</c> indents
        /// continuation lines without the tag, and CI greps for the tag - so an unprefixed
        /// list is invisible in the one situation it matters, a failure.
        /// </summary>
        private static string Probed(System.Collections.Generic.IReadOnlyList<string> probed)
        {
            if (probed == null || probed.Count == 0)
            {
                return "[RUNTIME-PROBE] probed: (nothing)";
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("[RUNTIME-PROBE] probed ").Append(probed.Count).Append(" path(s):");
            foreach (string path in probed)
            {
                sb.Append("\n[RUNTIME-PROBE]   ").Append(path);
            }
            return sb.ToString();
        }

        public static void Run()
        {
            try
            {
                Debug.Log(
                    "[RUNTIME-PROBE] platform=" + UnityEngine.Application.platform
                        + " unity=" + UnityEngine.Application.unityVersion
                        + "\n[RUNTIME-PROBE] applicationContentsPath=" + EditorApplication.applicationContentsPath
                        + "\n[RUNTIME-PROBE] applicationPath=" + EditorApplication.applicationPath);

                bool hostFound = UnityBundledRuntime.TryFindScriptingRoot(
                    out string scriptingRoot,
                    out string dotnetHost,
                    out var hostProbed);

                Debug.Log(
                    "[RUNTIME-PROBE] .NET host: " + (hostFound ? "FOUND " + dotnetHost : "NOT FOUND")
                        + "\n[RUNTIME-PROBE] scripting root: " + (scriptingRoot ?? "(none)")
                        + "\n" + Probed(hostProbed));

                if (!hostFound)
                {
                    Debug.LogError(
                        "[RUNTIME-PROBE] RESULT=FAILED - HMR and the Builder preview cannot start here.\n"
                            + UnityBundledRuntime.OverrideHint);
                    EditorApplication.Exit(1);
                    return;
                }

                bool cscFound = UnityBundledRuntime.TryFindBundledCsc(
                    scriptingRoot,
                    out string cscPath,
                    out var cscProbed);

                Debug.Log(
                    "[RUNTIME-PROBE] Roslyn csc: " + (cscFound ? "FOUND " + cscPath : "NOT FOUND")
                        + "\n" + Probed(cscProbed));

                if (!cscFound)
                {
                    Debug.LogError(
                        "[RUNTIME-PROBE] RESULT=FAILED - the .NET host was found but the bundled "
                            + "compiler was not, so HMR can start but cannot compile.\n"
                            + UnityBundledRuntime.OverrideHint);
                    EditorApplication.Exit(1);
                    return;
                }

                int major = UnityBundledRuntime.BundledSharedFrameworkMajor(scriptingRoot);
                Debug.Log(
                    "[RUNTIME-PROBE] bundled shared framework major: "
                        + (major == 0 ? "unknown" : major.ToString()));

                Debug.Log("[RUNTIME-PROBE] RESULT=OK");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                // A throw from the probe is the failure mode this exists to catch: the 0.21.0
                // regression surfaced exactly here, as an ArgumentException rather than a
                // not-found, and a caught-and-reported throw is the difference between a red
                // CI run and a user opening the Builder to a stack trace.
                Debug.LogError("[RUNTIME-PROBE] RESULT=THREW " + ex);
                EditorApplication.Exit(2);
            }
        }
    }
}
