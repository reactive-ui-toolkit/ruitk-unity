#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Ruitk.EditorSupport.HMR;
using UnityEditor;
using UnityEngine;

namespace Ruitk.Builder
{
    /// <summary>
    /// The builder's document set for one open tree: per-file sessions, the
    /// save/abort orchestration, and the read-only policy. Owns the save-only
    /// disk contract (VE-D2): during editing nothing here writes; Save batches
    /// all dirty buffers under a reload suppressor (none when HMR is active —
    /// it already holds the locks) and lets the normal UitkxChangeWatcher
    /// pipeline run once for the batch.
    ///
    /// Serialization: Unity's window serializer cannot round-trip dictionaries,
    /// so sessions serialize through a parallel list (ISerializationCallbackReceiver)
    /// — external domain reloads (a user editing .cs in the IDE) must not lose
    /// unsaved buffers.
    /// </summary>
    [Serializable]
    public sealed class BuilderWorkspace : ISerializationCallbackReceiver
    {
        [NonSerialized]
        private Dictionary<string, BuilderDocumentSession> _sessions =
            new Dictionary<string, BuilderDocumentSession>(StringComparer.OrdinalIgnoreCase);

        [SerializeField] private List<BuilderDocumentSession> _serializedSessions = new List<BuilderDocumentSession>();

        /// <summary>UB-88: files the user has deleted in the builder but which
        /// are still on disk. Deleting used to hit `AssetDatabase` the instant
        /// it was asked for, which broke the save-only contract this class owns
        /// (VE-D2) in the one direction that could not be taken back — the owner
        /// lost two sample files to it. A deletion is now a PENDING intent like
        /// every other edit: the card leaves the canvas, nothing leaves the
        /// disk, and Save is what makes it real. Abort forgets it, and undo is
        /// just un-marking, so no asset is ever re-created and no GUID
        /// churns.</summary>
        [SerializeField] private List<string> _pendingDeletes = new List<string>();

        public event Action Changed;

        public IReadOnlyCollection<BuilderDocumentSession> Sessions => _sessions.Values;

        public IReadOnlyList<string> PendingDeletes => _pendingDeletes;

        public bool IsPendingDelete(string filePath) =>
            filePath != null && _pendingDeletes.Contains(Path.GetFullPath(filePath));

        /// <summary>Returns false when the file is read-only or already marked.</summary>
        public bool MarkForDeletion(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || IsReadOnlyLocation(filePath))
                return false;
            string full = Path.GetFullPath(filePath);
            if (_pendingDeletes.Contains(full))
                return false;
            _pendingDeletes.Add(full);
            Changed?.Invoke();
            return true;
        }

        public bool UnmarkForDeletion(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;
            if (!_pendingDeletes.Remove(Path.GetFullPath(filePath)))
                return false;
            Changed?.Invoke();
            return true;
        }

        public bool HasUnsavedChanges
        {
            get
            {
                if (_pendingDeletes.Count > 0)
                    return true;
                foreach (var s in _sessions.Values)
                    if (s.IsDirty && !s.IsReadOnly)
                        return true;
                return false;
            }
        }

        public BuilderDocumentSession TryGet(string filePath) =>
            _sessions.TryGetValue(filePath, out var s) ? s : null;

        /// <summary>
        /// Immutable-package detection done RIGHT (plan §4.3c): PackageInfo source,
        /// never the asmdef walk — its null return also means "default assembly",
        /// which would mark every Assets/ file read-only.
        /// </summary>
        public static bool IsReadOnlyLocation(string filePath)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string full = Path.GetFullPath(filePath).Replace('\\', '/');
            string assetsRoot = Application.dataPath.Replace('\\', '/');
            if (full.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            foreach (var pkg in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (string.IsNullOrEmpty(pkg.resolvedPath))
                    continue;
                string pkgRoot = Path.GetFullPath(pkg.resolvedPath).Replace('\\', '/');
                if (full.StartsWith(pkgRoot + "/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, pkgRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return pkg.source != UnityEditor.PackageManager.PackageSource.Embedded
                        && pkg.source != UnityEditor.PackageManager.PackageSource.Local;
                }
            }
            return true;
        }

        public BuilderDocumentSession Open(string filePath)
        {
            if (_sessions.TryGetValue(filePath, out var existing))
            {
                // Sessions survive domain reloads by design (unsaved buffers),
                // which also means they survive EXTERNAL file changes — a clean
                // session must re-check the disk or it serves stale text
                // forever (owner report 2026-08-16: repaired samples still
                // showed their old mojibake on open cards).
                if (!existing.IsDirty && File.Exists(filePath)
                    && existing.AdoptDiskText(File.ReadAllText(filePath)))
                    Changed?.Invoke();
                return existing;
            }

            string raw = File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;
            var session = BuilderDocumentSession.Open(filePath, raw, IsReadOnlyLocation(filePath));
            _sessions[filePath] = session;
            Changed?.Invoke();
            return session;
        }

        /// <summary>External-change sweep (asset imports): clean sessions adopt
        /// the new disk text; dirty sessions keep the user's unsaved buffer.
        /// Returns the full paths whose buffers changed.</summary>
        public List<string> ReloadCleanFromDisk(IEnumerable<string> fullPaths)
        {
            var changed = new List<string>();
            foreach (string path in fullPaths)
            {
                var session = TryGet(path);
                if (session == null || session.IsDirty || session.IsReadOnly || !File.Exists(path))
                    continue;
                if (session.AdoptDiskText(File.ReadAllText(path)))
                    changed.Add(path);
            }
            if (changed.Count > 0)
                Changed?.Invoke();
            return changed;
        }

        /// <summary>Opens a never-saved session for a module that does not exist
        /// on disk yet (UB-111). Returns null when something already claims the
        /// path — a redo replaying a create must not throw.</summary>
        public BuilderDocumentSession CreateNew(string filePath, string initialBuffer)
        {
            if (_sessions.ContainsKey(filePath) || File.Exists(filePath))
                return null;
            var session = BuilderDocumentSession.CreateNew(filePath, initialBuffer);
            _sessions[filePath] = session;
            Changed?.Invoke();
            return session;
        }

        /// <summary>Drops a never-saved session — undoing a create. Refuses to
        /// touch a session that has been saved, which is a real file and belongs
        /// to the deletion path instead.</summary>
        public bool DiscardNew(string filePath)
        {
            var session = TryGet(filePath);
            if (session == null || !session.IsNewFile)
                return false;
            _sessions.Remove(filePath);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Paths of modules that exist only as buffers so far. The
        /// canvas synthesises a card for each, since the module graph the cards
        /// come from is built from files on DISK.</summary>
        public IEnumerable<string> PendingNewFiles
        {
            get
            {
                foreach (var s in _sessions.Values)
                    if (s.IsNewFile)
                        yield return s.FilePath;
            }
        }

        public void ApplyEdit(string filePath, string newBufferLf)
        {
            var session = TryGet(filePath)
                ?? throw new InvalidOperationException($"no session for '{filePath}'");
            session.ApplyEdit(newBufferLf);
            Changed?.Invoke();
        }

        /// <summary>
        /// Writes every dirty buffer. HMR inactive: bracket the batch in a reload
        /// suppressor via <c>using</c> (a leaked lock freezes the domain — the
        /// bracket is not optional) so the deferred refresh imports everything in
        /// one pass → one trigger set → one script compilation. HMR active: no
        /// suppressor at all — HMR already holds both locks, and its FSW hot-swaps
        /// the saved files; the SG catch-up happens at HMR Stop (existing
        /// mechanism, untouched).
        /// </summary>
        public int SaveAll()
        {
            var dirty = new List<BuilderDocumentSession>();
            foreach (var s in _sessions.Values)
                if (s.IsDirty && !s.IsReadOnly && !IsPendingDelete(s.FilePath))
                    dirty.Add(s);
            if (dirty.Count == 0 && _pendingDeletes.Count == 0)
                return 0;

            bool hmrActive = UitkxHmrController.IsActive;
            bool createdAssets = false;
            AssemblyReloadSuppressor suppressor = null;
            try
            {
                if (!hmrActive)
                {
                    suppressor = new AssemblyReloadSuppressor();
                    suppressor.Lock();
                }

                foreach (var s in dirty)
                {
                    string dir = Path.GetDirectoryName(s.FilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    // A never-saved module becomes a real asset in this batch,
                    // and a plain File.WriteAllText is invisible to Unity until
                    // something imports it (UB-111).
                    createdAssets |= s.IsNewFile;
                    string text = s.UsedCrlf ? s.BufferText.Replace("\n", "\r\n") : s.BufferText;
                    File.WriteAllText(s.FilePath, text);
                    s.MarkClean(s.BufferText);
                }

                // Deletions land in the same batch, and only here. The asset is
                // moved to the trash rather than erased, so even a CONFIRMED and
                // SAVED delete stays recoverable outside the builder.
                foreach (string path in _pendingDeletes)
                {
                    _sessions.Remove(path);
                    string assetPath = ToAssetPath(path);
                    if (assetPath != null)
                        AssetDatabase.MoveAssetToTrash(assetPath);
                    else if (File.Exists(path))
                        File.Delete(path);
                }
            }
            finally
            {
                suppressor?.Dispose();
            }

            int deleted = _pendingDeletes.Count;
            _pendingDeletes.Clear();
            // Outside the reload suppressor: importing is what makes a new file
            // an asset with a .meta, and it must not run while the lock is held.
            if (createdAssets)
                AssetDatabase.Refresh();
            Changed?.Invoke();
            return dirty.Count + deleted;
        }

        /// <summary>Discards every dirty buffer back to disk state; never-saved
        /// sessions close. Pending deletions are discarded too — an aborted
        /// session must leave the tree exactly as it found it.</summary>
        public int AbortAll()
        {
            int reverted = _pendingDeletes.Count;
            _pendingDeletes.Clear();
            var toRemove = new List<string>();
            foreach (var s in _sessions.Values)
            {
                if (!s.IsDirty)
                    continue;
                reverted++;
                if (s.IsNewFile)
                    toRemove.Add(s.FilePath);
                else
                    s.BufferText = s.DiskText;
            }
            foreach (var path in toRemove)
                _sessions.Remove(path);
            if (reverted > 0)
                Changed?.Invoke();
            return reverted;
        }

        public void Close(string filePath)
        {
            if (_sessions.Remove(filePath))
                Changed?.Invoke();
        }

        /// <summary>Absolute path to the "Assets/…" or "Packages/…" form the
        /// AssetDatabase understands, or null when the file lives outside both
        /// (a tree opened from somewhere Unity does not index).</summary>
        private static string ToAssetPath(string fullPath)
        {
            string normalized = fullPath.Replace('\\', '/');
            int at = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                at = normalized.IndexOf("/Packages/", StringComparison.OrdinalIgnoreCase);
            return at < 0 ? null : normalized.Substring(at + 1);
        }

        // ── ISerializationCallbackReceiver ───────────────────────────────────

        public void OnBeforeSerialize()
        {
            _serializedSessions.Clear();
            foreach (var s in _sessions.Values)
                _serializedSessions.Add(s);
        }

        public void OnAfterDeserialize()
        {
            _sessions = new Dictionary<string, BuilderDocumentSession>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _serializedSessions)
                if (s != null && !string.IsNullOrEmpty(s.FilePath))
                    _sessions[s.FilePath] = s;
        }
    }
}
#endif
