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

        public event Action Changed;

        public IReadOnlyCollection<BuilderDocumentSession> Sessions => _sessions.Values;

        public bool HasUnsavedChanges
        {
            get
            {
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
                return existing;

            string raw = File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;
            var session = BuilderDocumentSession.Open(filePath, raw, IsReadOnlyLocation(filePath));
            _sessions[filePath] = session;
            Changed?.Invoke();
            return session;
        }

        public BuilderDocumentSession CreateNew(string filePath, string initialBuffer)
        {
            if (_sessions.ContainsKey(filePath) || File.Exists(filePath))
                throw new InvalidOperationException($"'{filePath}' already exists.");
            var session = BuilderDocumentSession.CreateNew(filePath, initialBuffer);
            _sessions[filePath] = session;
            Changed?.Invoke();
            return session;
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
                if (s.IsDirty && !s.IsReadOnly)
                    dirty.Add(s);
            if (dirty.Count == 0)
                return 0;

            bool hmrActive = UitkxHmrController.IsActive;
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
                    string text = s.UsedCrlf ? s.BufferText.Replace("\n", "\r\n") : s.BufferText;
                    File.WriteAllText(s.FilePath, text);
                    s.MarkClean(s.BufferText);
                }
            }
            finally
            {
                suppressor?.Dispose();
            }

            Changed?.Invoke();
            return dirty.Count;
        }

        /// <summary>Discards every dirty buffer back to disk state; never-saved sessions close.</summary>
        public int AbortAll()
        {
            int reverted = 0;
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
