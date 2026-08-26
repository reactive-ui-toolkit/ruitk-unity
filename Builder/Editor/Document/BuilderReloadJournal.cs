#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace Ruitk.Builder
{
    /// <summary>
    /// The backstop for unsaved work: the tree, dumped to JSON outside the
    /// project's assets, so it survives what serialization cannot.
    ///
    /// Everything else guarding the domain-reload path lowers the CHANCE of
    /// losing a buffer; none of it recovers the work once it is gone, and none
    /// of it survives the process dying - which is how the owner lost an editor
    /// on 2026-08-22, with unsaved modules open, to a Unity fatal error.
    ///
    /// The journal exists ONLY while there is unsaved work: nothing writes it
    /// for a clean tree, and it is cleared when the work is saved, aborted,
    /// restored or discarded. So the file being there means exactly one thing -
    /// work existed that never reached disk - and the restore offer needs no
    /// other evidence to be sure it is not noise.
    ///
    /// It lives under UserSettings/, which is outside Assets: the Asset Database
    /// never sees it, so no import, no .meta, and nothing for the source
    /// generator to compile (the failure mode UB-120 catalogues).
    /// </summary>
    internal static class BuilderReloadJournal
    {
        [Serializable]
        private sealed class Payload
        {
            public string SavedAt;
            public int Modules;
            public BuilderTree Tree;
        }

        private static string JournalPath => Path.GetFullPath(Path.Combine(
            Application.dataPath, "..", "UserSettings", "RuitkBuilderTree.json"));

        /// <summary>Writes the tree when it holds unsaved work.
        ///
        /// A clean tree is NOT evidence that an older journal has been dealt
        /// with - it is usually a different tree, freshly loaded - so this never
        /// clears. Clearing is a decision: the work was saved, aborted, restored,
        /// or explicitly discarded. Deleting here instead would have destroyed a
        /// crashed session's only copy the moment the user opened the builder on
        /// some other file, before anyone was ever asked about it.</summary>
        public static void Capture(BuilderWorkspace workspace)
        {
            if (workspace == null || !workspace.HasUnsavedChanges)
                return;
            var payload = new Payload
            {
                SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Modules = workspace.Modules.Count,
                Tree = workspace.Tree,
            };
            try
            {
                string path = JournalPath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonUtility.ToJson(payload, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RUITK Builder] could not write the reload journal: " + ex.Message);
            }
        }

        /// <summary>What the journal holds, without restoring it - enough to ask
        /// the user whether they want it back.</summary>
        public static bool TryPeek(out int modules, out string savedAt)
        {
            modules = 0;
            savedAt = null;
            var payload = Read();
            if (payload?.Tree == null || payload.Tree.Modules.Count == 0)
                return false;
            modules = payload.Modules;
            savedAt = payload.SavedAt;
            return true;
        }

        public static bool TryRestore(BuilderWorkspace workspace)
        {
            var payload = Read();
            if (workspace == null || payload?.Tree == null || payload.Tree.Modules.Count == 0)
                return false;
            workspace.AdoptTree(payload.Tree);
            Clear();
            return true;
        }

        public static void Clear()
        {
            try
            {
                string path = JournalPath;
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
                // A journal that cannot be deleted is offered once more and
                // declined once more; losing the file is not worth a stack trace.
            }
        }

        private static Payload Read()
        {
            try
            {
                string path = JournalPath;
                if (!File.Exists(path))
                    return null;
                return JsonUtility.FromJson<Payload>(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
#endif
