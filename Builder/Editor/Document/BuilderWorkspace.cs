#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Ruitk.EditorSupport.HMR;
using UnityEditor;
using UnityEngine;

namespace Ruitk.Builder
{
    /// <summary>One pending folder move. Serialized with the window, so a domain
    /// reload cannot lose a rename the user has not saved yet.</summary>
    [Serializable]
    public sealed class BuilderFolderMove
    {
        public string From;
        public string To;
    }

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

        /// <summary>Folders that are moving. A component that owns its folder
        /// takes the folder with it when renamed, and that is ONE operation on
        /// disk, not one per file: moving the children individually would write
        /// each anew and trash the original, churning every child GUID and
        /// stranding everything in the folder the builder does not manage -
        /// companion .cs, .uss, sub-folders. Pending like every other edit.</summary>
        [SerializeField] private List<BuilderFolderMove> _pendingFolderMoves =
            new List<BuilderFolderMove>();

        public event Action Changed;

        public IReadOnlyCollection<BuilderDocumentSession> Sessions => _sessions.Values;

        public IReadOnlyList<string> PendingDeletes => _pendingDeletes;

        public bool IsPendingDelete(string filePath) =>
            filePath != null && _pendingDeletes.Contains(Path.GetFullPath(filePath));

        /// <summary>True when a file that is still ON DISK must not be shown: it
        /// is either marked for deletion, or it is the location a module has
        /// MOVED out of. Both are pending intents, so the file is really there
        /// and anything built from disk - the module graph above all - would
        /// otherwise render a card for a module that is no longer at that
        /// path.</summary>
        public bool IsHiddenOnDisk(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;
            if (IsPendingDelete(filePath))
                return true;
            string full = Path.GetFullPath(filePath);
            foreach (var s in _sessions.Values)
                if (s.IsMoved && string.Equals(
                        Path.GetFullPath(s.OriginalDiskPath), full,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            foreach (var move in _pendingFolderMoves)
                if (full.StartsWith(move.From + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public IReadOnlyList<BuilderFolderMove> PendingFolderMoves => _pendingFolderMoves;

        /// <summary>Records a folder moving and re-paths every open session inside
        /// it. Callers open the folder's modules first, so the model holds the
        /// whole subtree and the canvas follows it across the move; Save projects
        /// the move itself as a single directory operation.</summary>
        public bool MoveFolder(string oldDir, string newDir)
        {
            if (string.IsNullOrEmpty(oldDir) || string.IsNullOrEmpty(newDir))
                return false;
            string from = Path.GetFullPath(oldDir);
            string to = Path.GetFullPath(newDir);
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                return false;
            if (IsReadOnlyLocation(from))
                return false;

            // A folder already on its way somewhere is REDIRECTED rather than
            // moved again, so undoing a rename cancels the move instead of
            // queueing a second one back the other way. This is also what lets a
            // move be taken back at all: the destination is the folder's own
            // original location, which of course still exists on disk.
            BuilderFolderMove existing = null;
            foreach (var move in _pendingFolderMoves)
            {
                if (string.Equals(move.To, from, StringComparison.OrdinalIgnoreCase))
                {
                    existing = move;
                    break;
                }
            }
            // Only the way BACK is exempt from the collision check - that folder
            // is the one this move came from, so of course it is still there.
            bool returningHome = existing != null &&
                string.Equals(existing.From, to, StringComparison.OrdinalIgnoreCase);
            if (!returningHome && Directory.Exists(to))
                return false;

            string prefix = from + Path.DirectorySeparatorChar;
            var inside = new List<BuilderDocumentSession>();
            foreach (var session in _sessions.Values)
                if (Path.GetFullPath(session.FilePath)
                        .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    inside.Add(session);
            foreach (var session in inside)
            {
                string rel = Path.GetFullPath(session.FilePath).Substring(prefix.Length);
                Reindex(session.FilePath, Path.Combine(to, rel), session);
            }

            if (existing != null)
            {
                existing.To = to;
                if (string.Equals(existing.From, existing.To, StringComparison.OrdinalIgnoreCase))
                    _pendingFolderMoves.Remove(existing);
            }
            else if (Directory.Exists(from))
            {
                _pendingFolderMoves.Add(new BuilderFolderMove { From = from, To = to });
            }
            // A folder that is not on disk has nothing to move: a module the user
            // has only just created lives in a directory no one has written yet.
            // Re-pathing its sessions is the whole job - Save creates the
            // directory when it writes them. Queueing a move for it would fail
            // the whole save on a directory that never existed.
            Changed?.Invoke();
            return true;
        }

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
                if (_pendingDeletes.Count > 0 || _pendingFolderMoves.Count > 0)
                    return true;
                foreach (var s in _sessions.Values)
                    if ((s.IsDirty || s.IsMoved) && !s.IsReadOnly)
                        return true;
                return false;
            }
        }

        /// <summary>Looks a session up by path. A null path is NOT FOUND, not an
        /// error - Dictionary throws ArgumentNullException on a null key, and with
        /// an empty workspace the focus is legitimately null, so every caller that
        /// passed it straight through blew up on a lookup whose honest answer is
        /// simply "nothing".</summary>
        public BuilderDocumentSession TryGet(string filePath) =>
            string.IsNullOrEmpty(filePath) ? null
                : _sessions.TryGetValue(filePath, out var s) ? s : null;

        /// <summary>Lookup by STABLE identity. The path map above is an index
        /// that a rename rewrites; this is the handle that never moves.</summary>
        public BuilderDocumentSession ById(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            foreach (var s in _sessions.Values)
                if (string.Equals(s.Id, id, StringComparison.Ordinal))
                    return s;
            return null;
        }

        /// <summary>Re-files a session under a new path. The session OBJECT is
        /// preserved - that is the whole point: its id, undo history and
        /// recorded line-ending flavor belong to the module, not to its
        /// location.</summary>
        private void Reindex(string oldPath, string newPath, BuilderDocumentSession session)
        {
            _sessions.Remove(oldPath);
            session.FilePath = newPath;
            _sessions[newPath] = session;
        }

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

            bool onDisk = File.Exists(filePath);
            string raw = onDisk ? File.ReadAllText(filePath) : string.Empty;
            var session = BuilderDocumentSession.Open(
                filePath, raw, IsReadOnlyLocation(filePath), onDisk);
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
        /// <summary>Whether a module can be created at this path. THE one rule, so
        /// the name prompt and the creation itself cannot disagree.
        ///
        /// A path whose deletion is still PENDING counts as available: the user
        /// deleted it and is putting something back, and Save has not run, so
        /// nothing is committed either way. Without this, deleting a module made its
        /// NAME unusable for the rest of the session - the session went on occupying
        /// the path invisibly, and the only symptom was "already exists" about a
        /// module that was no longer on the canvas.</summary>
        public bool IsPathAvailable(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;
            if (IsPendingDelete(filePath))
                return true;
            return !_sessions.ContainsKey(filePath) && !File.Exists(filePath);
        }

        public BuilderDocumentSession CreateNew(
            string filePath, string initialBuffer, bool needsLocation = false)
        {
            if (!IsPathAvailable(filePath))
                return null;

            // Re-creating over a module whose deletion is still pending takes the
            // deletion back and replaces the buffer, rather than adding a SECOND
            // session for the same path. That also avoids a save ordering hazard:
            // writes happen before deletions, so a fresh session at a path already
            // queued for deletion would be written and then trashed.
            if (_sessions.TryGetValue(filePath, out var revived))
            {
                UnmarkForDeletion(filePath);
                revived.NeedsLocation = needsLocation;
                if (!revived.IsReadOnly)
                    revived.ApplyEdit(BuilderDocumentSession.NormalizeLf(initialBuffer));
                Changed?.Invoke();
                return revived;
            }

            var session = BuilderDocumentSession.CreateNew(filePath, initialBuffer);
            session.NeedsLocation = needsLocation;
            _sessions[filePath] = session;
            Changed?.Invoke();
            return session;
        }

        /// <summary>Moves a never-saved session to its real path (UB-113: a tree
        /// begun with no folder picks one at Save). Only never-saved sessions
        /// move — a session with a disk file behind it would leave that file
        /// orphaned.</summary>
        public bool Relocate(string oldPath, string newPath)
        {
            var session = TryGet(oldPath);
            if (session == null || !session.IsNewFile || _sessions.ContainsKey(newPath))
                return false;
            Reindex(oldPath, newPath, session);
            // It has a home now, so it is writable.
            session.NeedsLocation = false;
            Changed?.Invoke();
            return true;
        }


        /// <summary>UB-124: moves a module to a new path as a PENDING change.
        /// <para>A never-saved module has no file behind it, so it simply
        /// relocates. A SAVED one cannot be moved on disk without breaking the
        /// save-only contract, so the rename is expressed with the two pending
        /// mechanisms that already exist: a new session carrying the text at the
        /// new path, and a deletion mark on the old one. Save then writes the
        /// new file and trashes the old in the same batch; Abort drops both, and
        /// undo reverses both because the ledger records them as a creation and
        /// a deletion.</para></summary>
        public bool Rename(string oldPath, string newPath)
        {
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath))
                return false;
            if (_sessions.ContainsKey(newPath))
                return false;
            var session = TryGet(oldPath);
            // A file at the destination normally means the rename would clobber
            // something. The one exception is a move being taken BACK: the file
            // never left its original location, because a move is only projected
            // at Save - so the module returning to its own OriginalDiskPath is
            // finding its file, not overwriting a stranger.
            bool returningHome = session != null &&
                string.Equals(session.OriginalDiskPath, newPath, StringComparison.OrdinalIgnoreCase);
            if (!returningHome && File.Exists(newPath))
                return false;
            if (session == null || session.IsReadOnly)
                return false;
            if (session.IsNewFile)
                return Relocate(oldPath, newPath);

            // The module MOVES. Replacing it with a fresh session at the new
            // path threw away its undo history and its recorded line-ending
            // flavor, and made a rename look like an unrelated creation plus
            // deletion to everything downstream. The session IS the module;
            // only its path changes, and OriginalDiskPath still names the file
            // Save has to retire.
            Reindex(oldPath, newPath, session);
            Changed?.Invoke();
            return true;
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

        /// <summary>Paths with no file behind them YET - a module the user has
        /// created, and the new location of one they have moved. The canvas needs
        /// them because its inventory of what exists still starts from disk, even
        /// though the structure it draws comes from the modules themselves.</summary>
        public IEnumerable<string> PendingNewFiles
        {
            get
            {
                foreach (var s in _sessions.Values)
                    if (s.IsNewFile || s.IsMoved)
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
            if (!HasPendingWork())
                return 0;

            List<BuilderDocumentSession> dirty = null;

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

                // Folders move BEFORE anything is written. Each one carries its
                // whole contents, so every session inside it is already at its new
                // location on disk and only what the user actually edited still
                // needs writing - which is why the dirty set is computed after.
                createdAssets |= ApplyFolderMoves();

                dirty = new List<BuilderDocumentSession>();
                foreach (var s in _sessions.Values)
                    if ((s.IsDirty || s.IsMoved) && !s.IsReadOnly && !s.NeedsLocation
                        && !IsPendingDelete(s.FilePath))
                        dirty.Add(s);

                foreach (var s in dirty)
                {
                    string dir = Path.GetDirectoryName(s.FilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    // A never-saved module becomes a real asset in this batch,
                    // and a plain File.WriteAllText is invisible to Unity until
                    // something imports it (UB-111).
                    createdAssets |= s.IsNewFile || s.IsMoved;
                    string text = s.UsedCrlf ? s.BufferText.Replace("\n", "\r\n") : s.BufferText;
                    File.WriteAllText(s.FilePath, text);
                    // A move writes the new file and retires the old one in the
                    // same batch, so the module is never present twice.
                    if (s.IsMoved)
                        RetireFile(s.OriginalDiskPath);
                    s.MarkClean(s.BufferText);
                }

                // Deletions land in the same batch, and only here. The asset is
                // moved to the trash rather than erased, so even a CONFIRMED and
                // SAVED delete stays recoverable outside the builder.
                foreach (string path in _pendingDeletes)
                {
                    _sessions.Remove(path);
                    RetireFile(path);
                }
            }
            finally
            {
                suppressor?.Dispose();
            }

            int deleted = _pendingDeletes.Count;
            _pendingDeletes.Clear();
            int written = dirty?.Count ?? 0;
            // Outside the reload suppressor: importing is what makes a new file
            // an asset with a .meta, and it must not run while the lock is held.
            if (createdAssets)
                AssetDatabase.Refresh();
            Changed?.Invoke();
            return written + deleted;
        }

        /// <summary>True when Save has anything at all to do. Kept separate from
        /// HasUnsavedChanges, which answers the window's prompt and does not care
        /// about the NeedsLocation and pending-delete filters Save applies.</summary>
        private bool HasPendingWork()
        {
            if (_pendingDeletes.Count > 0 || _pendingFolderMoves.Count > 0)
                return true;
            foreach (var s in _sessions.Values)
                if ((s.IsDirty || s.IsMoved) && !s.IsReadOnly && !s.NeedsLocation
                    && !IsPendingDelete(s.FilePath))
                    return true;
            return false;
        }

        /// <summary>Projects the pending folder moves. One directory operation per
        /// folder, through the AssetDatabase where there is one, so every child
        /// keeps its GUID and its meta file. Afterwards each session inside a moved
        /// folder is told where its file went, which is what stops the write loop
        /// below from treating a child as relocated and rewriting it.</summary>
        private bool ApplyFolderMoves()
        {
            if (_pendingFolderMoves.Count == 0)
                return false;
            bool any = false;
            // Each move leaves the queue AS IT LANDS. Clearing the whole queue at
            // the end would, if a later move failed, leave an earlier one that had
            // already happened on disk queued for a retry that could only fail -
            // its source is gone.
            while (_pendingFolderMoves.Count > 0)
            {
                var move = _pendingFolderMoves[0];
                if (Directory.Exists(move.From))
                {
                    string fromAsset = ToAssetPath(move.From);
                    string toAsset = ToAssetPath(move.To);
                    if (fromAsset != null && toAsset != null)
                    {
                        string error = AssetDatabase.MoveAsset(fromAsset, toAsset);
                        if (!string.IsNullOrEmpty(error))
                            throw new IOException(
                                "could not move " + fromAsset + " to " + toAsset + ": " + error);
                    }
                    else
                    {
                        Directory.Move(move.From, move.To);
                    }
                    RebaseDiskPaths(move.From, move.To);
                    any = true;
                }
                _pendingFolderMoves.RemoveAt(0);
            }
            return any;
        }

        private void RebaseDiskPaths(string from, string to)
        {
            string prefix = Path.GetFullPath(from) + Path.DirectorySeparatorChar;
            foreach (var s in _sessions.Values)
            {
                if (string.IsNullOrEmpty(s.OriginalDiskPath))
                    continue;
                string origin = Path.GetFullPath(s.OriginalDiskPath);
                if (!origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                s.OriginalDiskPath = Path.Combine(to, origin.Substring(prefix.Length));
            }
        }

        /// <summary>Takes one file out of the project. Trash rather than erase,
        /// so even a confirmed, saved removal stays recoverable outside the
        /// builder; a path outside Assets/Packages has no asset to trash.</summary>
        private static void RetireFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            string assetPath = ToAssetPath(path);
            if (assetPath != null)
                AssetDatabase.MoveAssetToTrash(assetPath);
            else if (File.Exists(path))
                File.Delete(path);
        }

        /// <summary>Discards every dirty buffer back to disk state; never-saved
        /// sessions close. Pending deletions are discarded too — an aborted
        /// session must leave the tree exactly as it found it.</summary>
        public int AbortAll()
        {
            int reverted = _pendingDeletes.Count + _pendingFolderMoves.Count;
            _pendingDeletes.Clear();
            _pendingFolderMoves.Clear();
            var toRemove = new List<string>();
            var moves = new List<BuilderDocumentSession>();
            foreach (var s in _sessions.Values)
            {
                bool moved = s.IsMoved;
                if (moved)
                {
                    // The file never moved on disk, so undoing the move is just
                    // pointing the session back at it.
                    moves.Add(s);
                }
                if (!s.IsDirty && !moved)
                    continue;
                reverted++;
                if (!s.IsDirty)
                    continue;
                if (s.IsNewFile)
                    toRemove.Add(s.FilePath);
                else
                    s.BufferText = s.DiskText;
            }
            foreach (var s in moves)
                Reindex(s.FilePath, s.OriginalDiskPath, s);
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
            {
                if (s == null || string.IsNullOrEmpty(s.FilePath))
                    continue;
                // Sessions serialized by an earlier build carry no identity.
                if (string.IsNullOrEmpty(s.Id))
                    s.Id = BuilderDocumentSession.NewId();
                _sessions[s.FilePath] = s;
            }
        }
    }
}
#endif
