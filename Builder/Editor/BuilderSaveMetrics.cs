#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Ruitk.Builder
{
    /// <summary>
    /// VE-06 measurement instrumentation: every builder save batch and every
    /// domain reload log with timestamps, so a validation session produces the
    /// acceptance numbers (N edits -> 0 reloads; 1 save -> 1 reload; abort -> 0;
    /// HMR-active save -> 0) straight from the console without manual counting.
    /// </summary>
    [InitializeOnLoad]
    internal static class BuilderSaveMetrics
    {
        static BuilderSaveMetrics()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (SessionState.GetBool("Ruitk.Builder.SaveMetrics.Armed", false))
                {
                    SessionState.SetBool("Ruitk.Builder.SaveMetrics.Armed", false);
                    Debug.Log("[RUITK Builder][VE-06] domain reload following the save batch "
                        + $"(t={EditorApplication.timeSinceStartup:F1}s)");
                }
            };
        }

        public static void RecordSaveBatch(int filesWritten, bool hmrActive)
        {
            Debug.Log($"[RUITK Builder][VE-06] save batch: {filesWritten} file(s), "
                + $"HMR {(hmrActive ? "ACTIVE (expect 0 reloads)" : "off (expect 1 reload)")}, "
                + $"t={EditorApplication.timeSinceStartup:F1}s");
            SessionState.SetBool("Ruitk.Builder.SaveMetrics.Armed", filesWritten > 0 && !hmrActive);
        }
    }
}
#endif
