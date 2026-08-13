using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ruitk.EditorSupport.HMR
{
    /// <summary>
    /// Watches for .uitkx and companion .cs file changes using FileSystemWatcher.
    /// Debounces rapid saves and marshals events to the main thread.
    /// </summary>
    internal sealed class UitkxHmrFileWatcher : IDisposable
    {
        /// <summary>
        /// Fires on the main thread after debounce. Parameter is the .uitkx file path
        /// that changed (or the .uitkx associated with a changed companion .cs file).
        /// </summary>
        public event Action<string> OnUitkxChanged;

        /// <summary>
        /// Fires on the main thread after debounce when a .uss file is saved.
        /// Parameter is the absolute path to the changed .uss file.
        /// </summary>
        public event Action<string> OnUssChanged;

        /// <summary>
        /// Fires on the main thread after debounce when a .uitkx file is
        /// deleted or renamed away (FSW Deleted/Renamed old path, or the
        /// AssetPostprocessor's deleted/moved-from lists). Parameter is the
        /// absolute path of the file that no longer exists. Used by the
        /// controller to evict stale per-file state (retry queue, compiler
        /// registry, import edges). Suppressed when the file exists again by
        /// pump time — editors that save via delete-and-replace must not tear
        /// down live registrations on every save.
        /// </summary>
        public event Action<string> OnUitkxDeleted;

        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly Dictionary<string, int> _pendingChanges = new();
        private readonly Dictionary<string, int> _pendingUssChanges = new();
        private readonly Dictionary<string, int> _pendingDeletions = new();
        // Last-seen write time per .uitkx path (ticks). Field find: Unity's Mono FSW can
        // silently drop the FILE-level Changed for a save while still delivering the
        // DIRECTORY-level Changed (the folder's LastWrite bump) — observed live for a
        // mid-session-created file whose every save produced only directory events. The
        // directory event triggers a single-folder mtime scan against this map, recovering
        // the dropped save. Guarded by _lock (FSW threadpool + main thread).
        private readonly Dictionary<string, long> _lastSeenWriteTicks = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private bool _disposed;

        // Raw-event trace (EditorPrefs UITKX_HMR_VerboseWatcher, set through the
        // controller). Volatile field, not a property: read on the FSW threadpool
        // thread, written on the main thread.
        internal volatile bool TraceEnabled;

        private const int DebounceMs = 50; // 50ms debounce
        // Slow safety poll: catches a save even when BOTH the file-level and the
        // directory-level FSW events are dropped. Editor-only, session-only cost.
        private const int FullSweepMs = 2000;
        private int _lastFullSweepTick;
        private int _sweepRunning; // Interlocked reentrancy guard (threadpool sweep)
        private string[] _watchRoots = Array.Empty<string>();

        /// <summary>
        /// Starts watching every root in <paramref name="watchRoots"/> (one
        /// FileSystemWatcher each). Roots are the project's Assets/ folder PLUS every
        /// writable package location (embedded / local file: installs) — a .uitkx
        /// inside a package raises no event from an Assets/-only watcher, which left
        /// HMR structurally blind to package-resident files (the FSW is the PRIMARY
        /// event source during play mode, where Unity's asset refresh — and with it
        /// the AssetPostprocessor safety net — may be deferred by user preferences).
        /// </summary>
        public void Start(IReadOnlyList<string> watchRoots)
        {
            if (_watchers.Count > 0)
                Stop();

            var roots = new List<string>();
            foreach (var r in watchRoots)
                if (!string.IsNullOrEmpty(r) && Directory.Exists(r))
                    roots.Add(r);
            _watchRoots = roots.ToArray();
            _lastFullSweepTick = Environment.TickCount;

            foreach (string root in _watchRoots)
            {
                var watcher = new FileSystemWatcher
                {
                    Path = root,
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };

                watcher.Changed += OnFileSystemEvent;
                watcher.Created += OnFileSystemEvent;
                watcher.Deleted += (s, e) =>
                {
                    if (TraceEnabled)
                        Debug.Log($"[HMR][trace] FSW Deleted {e.FullPath}");
                    EnqueueDeletion(e.FullPath);
                };
                watcher.Renamed += (s, e) =>
                {
                    if (TraceEnabled)
                        Debug.Log($"[HMR][trace] FSW Renamed {e.OldFullPath} -> {e.FullPath}");
                    // A rename is a change of the NEW path plus a deletion of the OLD
                    // path — without the second half, a renamed member file's previous
                    // identity (registry entries, import edges) is never evicted.
                    EnqueueDeletion(e.OldFullPath);
                    OnFileSystemEvent(
                        s,
                        new FileSystemEventArgs(
                            WatcherChangeTypes.Changed,
                            Path.GetDirectoryName(e.FullPath),
                            Path.GetFileName(e.FullPath)
                        )
                    );
                };
                // Overflow visibility: Mono's FSW drops events silently when its 8 KB
                // buffer overflows (see the AssetPostprocessor comment below). Without
                // this handler a dropped save is indistinguishable from a save that
                // never happened. Subscribing an event does not touch the fragile FSW
                // configuration (Path/filters/EnableRaisingEvents stay exactly as-is).
                watcher.Error += (s, e) =>
                {
                    Debug.LogWarning(
                        "[HMR] File watcher error — OS file events may have been lost. "
                            + "If a save produced no '[HMR] Save:' line, re-save the file. "
                            + $"({e.GetException()?.Message ?? "unknown"})"
                    );
                };

                _watchers.Add(watcher);
            }

            // Seed the mtime map so the first directory-scan recovery pass has a baseline
            // (otherwise every pre-existing file would look "changed" on the first folder
            // event). One-time recursive enumeration at session start.
            try
            {
                lock (_lock)
                {
                    _lastSeenWriteTicks.Clear();
                    foreach (string root in _watchRoots)
                        foreach (var f in Directory.EnumerateFiles(
                                     root, "*.uitkx", SearchOption.AllDirectories))
                        {
                            try
                            {
                                _lastSeenWriteTicks[Path.GetFullPath(f)] =
                                    File.GetLastWriteTimeUtc(f).Ticks;
                            }
                            catch { }
                        }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HMR] Watch: mtime baseline scan failed ({ex.Message}) — dropped-event recovery degraded.");
            }

            // Pump pending changes on every editor update
            EditorApplication.update += PumpPendingChanges;

            // Register a redundant event source via Unity's AssetPostprocessor.
            // Mono's FileSystemWatcher silently drops events when its 8 KB
            // internal buffer overflows under save bursts on a deep Assets/
            // tree (every save also touches .meta files, etc.). The
            // AssetPostprocessor path is main-thread, never drops, and
            // _pendingChanges dedupes by path so redundant events from both
            // sources are harmless. Touching FSW config has proven fragile on
            // some Mono versions, so we keep FSW exactly as-is and add
            // AssetPostprocessor as a parallel safety net.
            UitkxHmrAssetPostprocessor.Register(this);
        }

        public void Stop()
        {
            EditorApplication.update -= PumpPendingChanges;

            UitkxHmrAssetPostprocessor.Unregister(this);

            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
            _watchRoots = Array.Empty<string>();

            lock (_lock)
            {
                _pendingChanges.Clear();
                _pendingUssChanges.Clear();
                _pendingDeletions.Clear();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
        }

        // ── FileSystemWatcher callback (threadpool thread) ────────────────────

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (TraceEnabled)
                Debug.Log($"[HMR][trace] FSW {e.ChangeType} {e.FullPath}");

            string ext = Path.GetExtension(e.FullPath);
            if (string.IsNullOrEmpty(ext))
            {
                // Directory-level Changed (the folder's LastWrite bump). Unity's Mono FSW
                // can drop the FILE event for the very save that caused this — scan the
                // folder's .uitkx mtimes and recover anything the OS never delivered.
                RecoverDroppedSavesInDirectory(e.FullPath);
                return;
            }

            // .uss file change
            if (ext.Equals(".uss", StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock)
                {
                    _pendingUssChanges[e.FullPath] = Environment.TickCount;
                }
                return;
            }

            string uitkxPath = null;

            if (ext.Equals(".uitkx", StringComparison.OrdinalIgnoreCase))
            {
                uitkxPath = e.FullPath;
            }
            else if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // Check if this is a companion .cs file (same directory has a .uitkx)
                uitkxPath = FindAssociatedUitkx(e.FullPath);
            }

            if (uitkxPath == null)
                return;

            // Use Environment.TickCount (thread-safe) instead of
            // EditorApplication.timeSinceStartup (main thread only).
            lock (_lock)
            {
                _pendingChanges[uitkxPath] = Environment.TickCount;
                RecordWriteTimeLocked(uitkxPath);
            }
        }

        // Caller holds _lock. Records the path's current on-disk write time so the
        // directory-scan recovery pass doesn't re-enqueue a save that arrived normally.
        private void RecordWriteTimeLocked(string uitkxPath)
        {
            try
            {
                _lastSeenWriteTicks[Path.GetFullPath(uitkxPath)] =
                    File.GetLastWriteTimeUtc(uitkxPath).Ticks;
            }
            catch { }
        }

        /// <summary>Single-folder (non-recursive) mtime sweep triggered by a directory-level
        /// FSW event: any .uitkx whose write time moved past the recorded baseline without a
        /// corresponding file-level event is a save the OS dropped — enqueue it and say so.</summary>
        private void RecoverDroppedSavesInDirectory(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                    return;
                foreach (var f in Directory.GetFiles(directoryPath, "*.uitkx"))
                    CheckFileForMissedWrite(f);
            }
            catch { /* recovery is best-effort; the normal event path is unaffected */ }
        }

        // Shared by the directory-event recovery scan and the slow full sweep.
        private void CheckFileForMissedWrite(string filePath)
        {
            string full;
            long ticks;
            try
            {
                full = Path.GetFullPath(filePath);
                ticks = File.GetLastWriteTimeUtc(filePath).Ticks;
            }
            catch { return; }

            bool missed;
            lock (_lock)
            {
                missed = (!_lastSeenWriteTicks.TryGetValue(full, out long seen) || ticks > seen)
                    && !_pendingChanges.ContainsKey(full);
                _lastSeenWriteTicks[full] = ticks;
                if (missed)
                    _pendingChanges[full] = Environment.TickCount;
            }
            if (missed)
                Debug.Log(
                    $"[HMR] Watch: recovered a save the OS never delivered — {Path.GetFileName(filePath)}");
        }

        // Push a synthetic change event from the AssetPostprocessor.
        // Routes through OnFileSystemEvent so all extension filtering,
        // companion-.cs mapping, USS handling, and dedupe behave identically
        // to the FileSystemWatcher path.
        internal void EnqueueAssetChange(string absolutePath)
        {
            if (_disposed || string.IsNullOrEmpty(absolutePath))
                return;
            OnFileSystemEvent(
                this,
                new FileSystemEventArgs(
                    WatcherChangeTypes.Changed,
                    Path.GetDirectoryName(absolutePath) ?? string.Empty,
                    Path.GetFileName(absolutePath)
                )
            );
        }

        // Push a deletion notification from the AssetPostprocessor. Routes
        // through the same debounced pending-deletion queue as the FSW
        // Deleted/Renamed events so the pump's exists-again guard applies
        // uniformly. Companion .cs deletions are intentionally ignored:
        // there's no per-.cs-path state to evict.
        internal void EnqueueAssetDeletion(string absolutePath)
        {
            if (_disposed)
                return;
            EnqueueDeletion(absolutePath);
        }

        // Shared deletion intake (FSW threadpool thread or main thread). Also
        // evicts any pending change entry for the path so a stale debounced
        // compile doesn't fire after the file is gone.
        private void EnqueueDeletion(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return;
            string ext = Path.GetExtension(absolutePath);
            if (string.IsNullOrEmpty(ext))
                return;
            if (!ext.Equals(".uitkx", StringComparison.OrdinalIgnoreCase))
                return;

            lock (_lock)
            {
                _pendingChanges.Remove(absolutePath);
                _pendingDeletions[absolutePath] = Environment.TickCount;
                try { _lastSeenWriteTicks.Remove(Path.GetFullPath(absolutePath)); } catch { }
            }
        }

        // ── Main thread pump ──────────────────────────────────────────────────

        private void PumpPendingChanges()
        {
            if (_disposed)
                return;

            int sweepNow = Environment.TickCount;
            if (sweepNow - _lastFullSweepTick >= FullSweepMs && _watchRoots.Length > 0
                && System.Threading.Interlocked.CompareExchange(ref _sweepRunning, 1, 0) == 0)
            {
                _lastFullSweepTick = sweepNow;
                string[] roots = _watchRoots;
                // Threadpool: a full watched-tree stat walk must never hitch the editor
                // frame. CheckFileForMissedWrite is lock-guarded and Debug.Log is
                // thread-safe; recovered paths surface via the normal debounced pump.
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        foreach (string root in roots)
                            foreach (var f in Directory.EnumerateFiles(
                                         root, "*.uitkx", SearchOption.AllDirectories))
                            {
                                if (_disposed)
                                    return;
                                CheckFileForMissedWrite(f);
                            }
                    }
                    catch { /* best-effort safety net */ }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref _sweepRunning, 0);
                    }
                });
            }

            List<string> ready = null;
            List<string> readyUss = null;
            List<string> readyDeleted = null;
            int now = Environment.TickCount;

            lock (_lock)
            {
                if (
                    _pendingChanges.Count == 0
                    && _pendingUssChanges.Count == 0
                    && _pendingDeletions.Count == 0
                )
                    return;

                foreach (var kvp in _pendingDeletions)
                {
                    if (now - kvp.Value >= DebounceMs)
                    {
                        readyDeleted ??= new List<string>();
                        readyDeleted.Add(kvp.Key);
                    }
                }

                if (readyDeleted != null)
                    foreach (var path in readyDeleted)
                        _pendingDeletions.Remove(path);

                foreach (var kvp in _pendingChanges)
                {
                    if (now - kvp.Value >= DebounceMs)
                    {
                        ready ??= new List<string>();
                        ready.Add(kvp.Key);
                    }
                }

                if (ready != null)
                    foreach (var path in ready)
                        _pendingChanges.Remove(path);

                foreach (var kvp in _pendingUssChanges)
                {
                    if (now - kvp.Value >= DebounceMs)
                    {
                        readyUss ??= new List<string>();
                        readyUss.Add(kvp.Key);
                    }
                }

                if (readyUss != null)
                    foreach (var path in readyUss)
                        _pendingUssChanges.Remove(path);
            }

            // Deletions first: a rename delivers deletion(old) + change(new) in the
            // same burst, and the old identity must be evicted before the new path's
            // compile re-registers its dependents. Exists-again guard: editors that
            // save via delete-and-replace surface a transient Deleted for a file
            // that is back on disk by pump time — that is a save, not a deletion.
            // A save it is: EnqueueDeletion cancelled any pending change for the
            // path, so discarding here would eat the whole save with zero events
            // (the mid-session member-file silence class). Re-route it as a change
            // unless one is already pending or matured in this pump.
            if (readyDeleted != null)
            {
                foreach (var path in readyDeleted)
                {
                    if (!File.Exists(path))
                    {
                        OnUitkxDeleted?.Invoke(path);
                        continue;
                    }
                    bool changePending;
                    lock (_lock)
                    {
                        changePending = _pendingChanges.ContainsKey(path);
                    }
                    bool changeMatured = false;
                    if (!changePending && ready != null)
                        foreach (var r in ready)
                            if (string.Equals(r, path, StringComparison.OrdinalIgnoreCase))
                            {
                                changeMatured = true;
                                break;
                            }
                    if (!changePending && !changeMatured)
                    {
                        Debug.Log(
                            $"[HMR] Watch: delete-and-replace save detected for "
                                + $"{Path.GetFileName(path)} — treating as change."
                        );
                        OnUitkxChanged?.Invoke(path);
                    }
                }
            }

            if (ready != null)
                foreach (var path in ready)
                    OnUitkxChanged?.Invoke(path);

            if (readyUss != null)
                foreach (var path in readyUss)
                    OnUssChanged?.Invoke(path);
        }

        // ── Companion file detection ──────────────────────────────────────────

        /// <summary>
        /// Given a .cs file path, find the associated .uitkx file in the same directory.
        /// Convention: MyComponent.styles.cs → MyComponent.uitkx (same folder).
        /// </summary>
        private static string FindAssociatedUitkx(string csFilePath)
        {
            string dir = Path.GetDirectoryName(csFilePath);
            if (dir == null)
                return null;

            // Skip generated files
            string fileName = Path.GetFileName(csFilePath);
            if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                return null;

            // Check if any .uitkx file exists in the same directory
            try
            {
                var uitkxFiles = Directory.GetFiles(dir, "*.uitkx");
                if (uitkxFiles.Length == 0)
                    return null;

                // If exactly one .uitkx, it's the companion target
                if (uitkxFiles.Length == 1)
                    return uitkxFiles[0];

                // Multiple .uitkx files: try matching by prefix
                string csBase = Path.GetFileNameWithoutExtension(csFilePath);
                // Strip additional extensions: MyComponent.styles → MyComponent
                int dotIdx = csBase.IndexOf('.');
                if (dotIdx > 0)
                    csBase = csBase.Substring(0, dotIdx);

                return uitkxFiles.FirstOrDefault(f =>
                        Path.GetFileNameWithoutExtension(f)
                            .Equals(csBase, StringComparison.OrdinalIgnoreCase)
                    ) ?? uitkxFiles[0]; // fallback to first
            }
            catch
            {
                return null;
            }
        }
    }
}
