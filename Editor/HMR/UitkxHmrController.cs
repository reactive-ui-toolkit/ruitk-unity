using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ruitk.EditorSupport.HMR
{
    /// <summary>
    /// Orchestrates the HMR lifecycle: file watching, compilation, delegate
    /// swapping, and assembly reload suppression.
    /// </summary>
    internal sealed class UitkxHmrController : IDisposable
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        private static UitkxHmrController s_instance;
        public static UitkxHmrController Instance => s_instance;
        public static bool IsActive => s_instance != null && s_instance._active;

        // ── Components ────────────────────────────────────────────────────────
        private readonly AssemblyReloadSuppressor _suppressor = new();
        private readonly UitkxHmrFileWatcher _watcher = new();
        private UitkxHmrCompiler _compiler;

        // ── State ─────────────────────────────────────────────────────────────
        private bool _active;
        private bool _previousRunInBackground;
        private int _swapCount;
        private int _errorCount;
        private string _lastComponentName;
        private float _lastSwapMs;
        private string _lastTimingBreakdown;
        private readonly List<string> _recentErrors = new();

        // ── Compile queue ────────────────────────────────────────────────────
        // Real FIFO queue (replaces the prior single-slot _queuedPath design,
        // which was effectively dead code — _compilationQueued was never set
        // to true and the single slot dropped concurrent saves). Used by the
        // dep-graph cascade (Rank 3 / Rank 4) which can produce N-file batches
        // that MUST drain in order so each consumer's cctor runs after its
        // dependency cctor produced the new value.
        //
        // Invariants:
        //   • At most one ProcessFileChange call is in flight (DrainQueue
        //     re-enters via EditorApplication.delayCall, never recurses).
        //   • Each path appears at most once in the queue at any time
        //     (deduped via _enqueued set).
        private readonly Queue<string> _compileQueue = new();
        private readonly HashSet<string> _enqueued = new(StringComparer.OrdinalIgnoreCase);
        private bool _compileInFlight;

        // Set the first time the compiler reports an infrastructure failure
        // (reflection signature mismatch, missing type/method, bad image, etc.).
        // HMR self-disables and refuses to spam the console with retries until
        // the user fixes the underlying language-library mismatch and restarts
        // HMR (or Unity).
        private bool _loggedInfrastructureFailure;

        // ── Pending retry state (auto-cascade for new-file dependencies) ────
        private readonly Dictionary<string, string> _pendingRetryPaths = new(
            StringComparer.OrdinalIgnoreCase
        );
        private bool _retryingPending;

        // ── USS → UITKX reverse dependency map ───────────────────────────────
        // Key = absolute .uss path (lower-case), Value = list of absolute .uitkx paths
        private readonly Dictionary<string, List<string>> _ussDependents = new(
            StringComparer.OrdinalIgnoreCase
        );

        // ── Import reverse-dependency map (import/export grammar, leg 3, §8) ───
        // Key = absolute imported .uitkx path (forward-slash), Value = set of absolute .uitkx paths
        // that `import { … } from` it. When an imported file (e.g. a module or hook file) changes,
        // its importers are recompiled so cross-file references pick up the new IL — the confirmed
        // correctness gap where the Family-handle indirection only covers hook CONSUMERS, not
        // module-member references. Built at Start() and kept fresh on every file event.
        private readonly Dictionary<string, HashSet<string>> _importDependents = new(
            StringComparer.OrdinalIgnoreCase
        );

        // Matches a preamble file-import line (single line), covering the full ES import
        // surface (ES-modules campaign, G-05): named `import { A, B as C } from "spec"`,
        // namespace `import * as X from "spec"`, and default `import X from "spec"`, with
        // either quote style (closing quote must match — backreference). The specifier is
        // group 2. The namespace-import form `import "@Ns"` has no `from` clause and
        // never matches.
        private static readonly Regex s_importLineRegex = new Regex(
            @"^\s*import\s*(?:\{[^}]*\}|\*\s*as\s+[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_]*)\s*from\s*([""'])([^""']+)\1",
            RegexOptions.Multiline | RegexOptions.CultureInvariant
        );

        // ── Settings ──────────────────────────────────────────────────────────
        public bool AutoStopOnPlayMode
        {
            get => EditorPrefs.GetBool("UITKX_HMR_AutoStopPlay", true);
            set => EditorPrefs.SetBool("UITKX_HMR_AutoStopPlay", value);
        }
        public bool ShowNotifications
        {
            get => EditorPrefs.GetBool("UITKX_HMR_ShowNotify", true);
            set => EditorPrefs.SetBool("UITKX_HMR_ShowNotify", value);
        }

        /// <summary>
        /// When true, a detected CLR rude edit on a module type (newly-added
        /// <c>static readonly</c> field) automatically triggers a domain
        /// reload via <c>EditorUtility.RequestScriptReload</c> on the next
        /// editor frame. Default true — adding a new field is otherwise
        /// invisible until the user manually reloads. Disable only if you
        /// want full manual control (a warning is still logged either way).
        /// </summary>
        public bool AutoReloadOnRudeEdit
        {
            get => EditorPrefs.GetBool("UITKX_HMR_AutoReloadOnRudeEdit", true);
            set => EditorPrefs.SetBool("UITKX_HMR_AutoReloadOnRudeEdit", value);
        }

        /// <summary>
        /// When true, the FileSystemWatcher logs every raw .uitkx / .uss / .cs
        /// event to the Console as <c>[HMR][trace] FSW ...</c>. Use this when a
        /// save appears to do nothing in HMR — if no trace line appears for
        /// your file, the OS itself isn't delivering the event (FSW buffer
        /// overflow, antivirus hook, OneDrive/symlink path, etc.) and the
        /// problem is upstream of HMR. Off by default; high noise.
        /// </summary>
        public bool VerboseWatcherTrace
        {
            get => EditorPrefs.GetBool("UITKX_HMR_VerboseWatcher", false);
            set
            {
                EditorPrefs.SetBool("UITKX_HMR_VerboseWatcher", value);
                _watcher.TraceEnabled = value;
            }
        }

        // ── Memory tracking ─────────────────────────────────────────────────
        private long _sessionBaselineMemory;

        // ── Public properties ─────────────────────────────────────────────
        public bool Active => _active;
        public int SwapCount => _swapCount;
        public int ErrorCount => _errorCount;
        public string LastComponentName => _lastComponentName;
        public float LastSwapMs => _lastSwapMs;
        public string LastTimingBreakdown => _lastTimingBreakdown;
        public IReadOnlyList<string> RecentErrors => _recentErrors;

        /// <summary>Current process working set in bytes (close to Task Manager).</summary>
#if UNITY_EDITOR_WIN
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern System.IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(
            System.IntPtr hProcess,
            out PROCESS_MEMORY_COUNTERS counters,
            uint size
        );

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential
        )]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public System.UIntPtr PeakWorkingSetSize;
            public System.UIntPtr WorkingSetSize;
            public System.UIntPtr QuotaPeakPagedPoolUsage;
            public System.UIntPtr QuotaPagedPoolUsage;
            public System.UIntPtr QuotaPeakNonPagedPoolUsage;
            public System.UIntPtr QuotaNonPagedPoolUsage;
            public System.UIntPtr PagefileUsage;
            public System.UIntPtr PeakPagefileUsage;
        }

        public static long CurrentMemoryBytes
        {
            get
            {
                var counters = new PROCESS_MEMORY_COUNTERS();
                counters.cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(counters);
                if (GetProcessMemoryInfo(GetCurrentProcess(), out counters, counters.cb))
                    return (long)(ulong)counters.WorkingSetSize;
                return 0;
            }
        }
#else
        public static long CurrentMemoryBytes =>
            UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()
            + UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
#endif

        /// <summary>Delta from HMR session start, in MB.</summary>
        public float SessionMemoryDeltaMB =>
            _active ? (CurrentMemoryBytes - _sessionBaselineMemory) / (1024f * 1024f) : 0f;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public bool Start(out string error)
        {
            if (_active)
            {
                error = null;
                return true;
            }

            // Reuse the compiler across start/stop cycles to avoid re-creating
            // the expensive Roslyn MetadataReferences (~150-200MB) each time.
            if (_compiler == null)
            {
                _compiler = new UitkxHmrCompiler();
                if (!_compiler.TryInitialize(out error))
                {
                    _compiler.Dispose();
                    _compiler = null;
                    return false;
                }
            }
            else
            {
                // Clear per-session caches from the previous HMR session
                _compiler.Reset();
            }

            // Lock assembly reloads
            _suppressor.Lock();

            // Start watching — Assets/ plus every writable package root (embedded /
            // local file: installs): a package-resident .uitkx raises no event from
            // an Assets/-only watcher, leaving HMR blind to it (GEN-1's HMR leg).
            _watcher.OnUitkxChanged += OnUitkxFileChanged;
            _watcher.OnUssChanged += OnUssFileChanged;
            _watcher.OnUitkxDeleted += OnUitkxFileDeleted;
            _watcher.TraceEnabled = VerboseWatcherTrace;
            var watchRoots = BuildWatchRoots();
            _watcher.Start(watchRoots);

            // UITKX Fast Refresh: wire the renderer-walk callback the
            // Refresh runtime needs to dispatch PerformRefresh. Doing it
            // here (instead of [InitializeOnLoadMethod]) keeps the editor
            // load path lazy — the provider is only set when HMR actually
            // starts.
            global::Ruitk.Refresh.RefreshRuntime.RegisterRootRendererProvider(
                EnumerateRootFibers);

            // Rebuild BOTH reverse-dependency maps on EVERY Start (field find: the old
            // only-if-empty guard ran a session on the previous session's graph — an
            // import or @uss edge edited while HMR was stopped, with no domain reload
            // in between, stayed invisible, so a member-file save fanned out to NOBODY:
            // zero logs, zero screen change; a domain reload "fixed" it by forcing a
            // fresh map. Both builders clear their map first and span the SAME roots
            // the watcher covers — an Assets/-only graph would give package files
            // empty fan-out edges; the cost is one read of each .uitkx at Start.
            BuildUssDependencyMap(watchRoots);
            BuildImportDependencyMap(watchRoots);

            // Hook lifecycle events
            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            // Set HMR state flag (read by Fiber reconciler for CanReuseFiber)
            HmrState.IsActive = true;

            // Keep Unity's update loop running when the editor loses focus so
            // FileSystemWatcher events are pumped to the main thread immediately.
            _previousRunInBackground = UnityEngine.Application.runInBackground;
            UnityEngine.Application.runInBackground = true;

            _active = true;
            _swapCount = 0;
            _errorCount = 0;
            _recentErrors.Clear();
            _pendingRetryPaths.Clear();
            _loggedInfrastructureFailure = false;
            _sessionBaselineMemory = CurrentMemoryBytes;

            s_instance = this;
            error = null;

            Debug.Log("[HMR] Started — assembly reload locked. Save a .uitkx file to hot-reload.");
            return true;
        }

        public void Stop()
        {
            if (!_active)
                return;
            _active = false;

            HmrState.IsActive = false;

            // Restore original runInBackground setting
            UnityEngine.Application.runInBackground = _previousRunInBackground;

            // Unhook events
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;

            // Stop watching
            _watcher.OnUitkxChanged -= OnUitkxFileChanged;
            _watcher.OnUssChanged -= OnUssFileChanged;
            _watcher.OnUitkxDeleted -= OnUitkxFileDeleted;
            _watcher.Stop();

            // Drop the path→asmdef cache so a fresh session re-resolves moved files
            // (previously done by the hook-container registry's Reset, removed with
            // the legacy grammar in 0.16.0).
            AsmdefResolver.InvalidateAll();

            _pendingRetryPaths.Clear();
            _compileQueue.Clear();
            _enqueued.Clear();
            _compileInFlight = false;
            // Keep _ussDependents across start/stop cycles — it's rebuilt
            // incrementally and re-scanning all .uitkx files is expensive.

            // Unlock assembly reloads (triggers pending compilation)
            _suppressor.Unlock();

            // Reset session caches but keep the compiler alive
            // (Roslyn MetadataReferences are expensive to rebuild)
            _compiler?.Reset();

            // Keep s_instance alive — the controller persists across
            // start/stop cycles to reuse the compiler.

            Debug.Log(
                $"[HMR] Stopped — {_swapCount} swap(s), {_errorCount} error(s). "
                    + "Assembly reload unlocked."
            );
        }

        public void Dispose()
        {
            Stop();
            _compiler?.Dispose();
            _compiler = null;
            _ussDependents.Clear();

            if (s_instance == this)
                s_instance = null;
        }

        // ── File change handler ───────────────────────────────────────────────

        private void OnUitkxFileChanged(string uitkxPath)
            => OnUitkxFileChanged(uitkxPath, fanOutToImporters: true);

        private void OnUitkxFileChanged(string uitkxPath, bool fanOutToImporters)
        {
            if (!_active)
            {
                if (VerboseWatcherTrace)
                    Debug.Log(
                        $"[HMR] Save ignored (HMR inactive): {Path.GetFileName(uitkxPath)}"
                    );
                return;
            }

            // Every file compiles itself — the legacy companion-parent redirect died
            // with the wrapper grammar (0.16.0); the parent is reached through the
            // import reverse-edge fan-out below instead.

            // ── UITKX Fast Refresh ─────────────────────────────────────────
            // The Family handle indirection means a single Register call — emitted by
            // the [ModuleInitializer] in the freshly compiled assembly — reaches every
            // hook CONSUMER regardless of whether their IL was recompiled in this round.
            // See Plans~/HMR_FAST_REFRESH_PLAN.md.
            bool queued = EnqueueCompile(uitkxPath);

            // Always-on routing trail: one line per user save (fan-out cascades log
            // through the Fan-out line instead). Proves the event reached the
            // controller and names the routing decision — the two facts that were
            // undiagnosable when a member-file save vanished silently.
            if (fanOutToImporters)
            {
                int importerCount =
                    _importDependents.TryGetValue(NormalizeImportPath(uitkxPath), out var deps)
                        ? deps.Count
                        : 0;
                // Routine save-trail line — verbose-gated (field-debugging tier; the
                // anomaly lines — recovered save, watcher error, evictions — stay on).
                if (VerboseWatcherTrace)
                    Debug.Log(
                    $"[HMR] Save: {Path.GetFileName(uitkxPath)} → self compile"
                        + $"; importers: {importerCount}"
                        + (queued ? string.Empty : " (already queued)")
                );
            }

            DrainCompileQueueIfIdle();

            // Keep this file's outgoing import edges fresh (its imports may have changed).
            RegisterImportDependencies(uitkxPath);

            // ── Reverse-edge invalidation (import/export grammar, §8) ──────
            // The Family indirection above does NOT cover module-member references or other
            // non-component export edits: an importer's IL still references the old symbols. Walk
            // the reverse import edges and recompile each importer so those references refresh.
            if (fanOutToImporters)
                FanOutToImporters(uitkxPath);
        }

        /// <summary>
        /// Recompile every <c>.uitkx</c> that (transitively) imports <paramref name="changedFile"/>
        /// so cross-file references to its declarations pick up the new IL (import/export grammar §8).
        /// Guarded against import cycles by a visited set; each importer is refreshed WITHOUT
        /// re-fanning (this BFS drives the transitivity).
        /// </summary>
        private void FanOutToImporters(string changedFile)
        {
            if (_importDependents.Count == 0)
                return;

            string start = NormalizeImportPath(changedFile);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
            var queue = new Queue<string>();
            queue.Enqueue(start);
            List<string> recompiled = null;

            while (queue.Count > 0)
            {
                string file = queue.Dequeue();
                if (!_importDependents.TryGetValue(file, out var importers))
                    continue;

                // Copy: OnUitkxFileChanged re-registers edges and may mutate the map mid-iteration.
                foreach (string importer in importers.ToList())
                {
                    if (visited.Add(importer))
                    {
                        queue.Enqueue(importer);
                        (recompiled ??= new List<string>()).Add(Path.GetFileName(importer));
                        // Force this importer to recompile FRESH so it rebinds the changed
                        // dependency's new DLL. Without this, the incremental path reuses the
                        // importer's cached compilation (which holds the dependency's stale
                        // reference) for every importer past the first in this cascade.
                        _compiler?.InvalidateCompilationForFile(importer);
                        OnUitkxFileChanged(importer, fanOutToImporters: false);
                    }
                }
            }

            if (recompiled != null && VerboseWatcherTrace)
                Debug.Log(
                    $"[HMR] Fan-out: {Path.GetFileName(changedFile)} → "
                        + $"{recompiled.Count} importer(s): {string.Join(", ", recompiled)}"
                );
        }

        // ── Compile queue plumbing ────────────────────────────────────────────

        // ── UITKX Fast Refresh: root-fiber enumeration ───────────────────────
        // Supplied to RefreshRuntime.RegisterRootRendererProvider in Start().
        // MountRegistry is the single registry every FiberRenderer self-joins
        // at construction — runtime RootRenderer, editor windows, uGUI mounts,
        // virtualized row renderers, and any future host, with no per-host
        // list to extend. Cheap to call: O(renderers); RefreshRuntime calls
        // it once per HMR cycle.
        private static IEnumerable<FiberNode> EnumerateRootFibers()
        {
            return MountRegistry.EnumerateRootFibers();
        }

        // Returns true when the path was newly queued, false when it was already
        // pending (deduped) — surfaced in the Save routing log line.
        private bool EnqueueCompile(string uitkxPath)
        {
            if (string.IsNullOrEmpty(uitkxPath))
                return false;
            if (!_enqueued.Add(uitkxPath))
                return false;
            _compileQueue.Enqueue(uitkxPath);
            return true;
        }

        private void DrainCompileQueueIfIdle()
        {
            if (_compileInFlight)
                return; // a delayCall is already pumping
            if (_compileQueue.Count == 0)
                return;

            _compileInFlight = true;
            try
            {
                // UITKX Fast Refresh — every save is a single-file compile.
                // The cascade walker and union-compile batch path were
                // removed when Family-handle indirection eliminated the
                // cross-DLL identity bug that those mechanisms existed to
                // work around (see Plans~/HMR_FAST_REFRESH_PLAN.md).
                //
                // H-02: dequeue exactly ONE item per invocation, not the whole
                // queue in a single editor tick. A shared .uss edited by many
                // components fans out to N queued compiles (see
                // OnUssFileChanged) — draining all N synchronously here froze
                // the editor for the duration of N Roslyn compiles. The
                // delayCall tail below already re-invokes this method on the
                // next tick whenever the queue is non-empty, so compiles still
                // land one per tick without blocking the frame.
                if (_compileQueue.Count > 0)
                {
                    string next = _compileQueue.Dequeue();
                    _enqueued.Remove(next);
                    ProcessFileChange(next);
                }
            }
            finally
            {
                _compileInFlight = false;
            }

            // Continue draining on the next editor tick if new saves arrived
            // while we were compiling (debounced file events route through
            // OnUitkxFileChanged → EnqueueCompile → DrainCompileQueueIfIdle).
            if (_compileQueue.Count > 0)
                EditorApplication.delayCall += DrainCompileQueueIfIdle;
        }

        private void ProcessFileChange(string uitkxPath)
        {
            string componentDir = Path.GetDirectoryName(uitkxPath);
            string componentBase = Path.GetFileNameWithoutExtension(uitkxPath);

            // Find companion .cs files scoped to this component only.
            // Only include files named <ComponentBase>.cs, <ComponentBase>.styles.cs, etc.
            // to avoid pulling in another component's companions from the same directory.
            string[] companionFiles = null;
            if (componentDir != null)
            {
                try
                {
                    string prefix = componentBase + ".";
                    companionFiles = Directory
                        .GetFiles(componentDir, "*.cs")
                        .Where(f =>
                        {
                            var fileName = Path.GetFileName(f);
                            return !f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                                && fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                        })
                        .ToArray();
                }
                catch
                { /* ignore IO errors */
                }
            }

            // Compile
            var result = _compiler.Compile(uitkxPath, companionFiles);

            if (result.Success)
            {
                ApplySuccessfulCompileResult(result, uitkxPath);

                // Remove from pending retries if this was a re-attempt
                _pendingRetryPaths.Remove(uitkxPath);

                // A successful compilation may unblock pending failures that
                // depend on this component. Schedule a retry pass on the next frame.
                if (_pendingRetryPaths.Count > 0 && !_retryingPending)
                    EditorApplication.delayCall += RetryPendingCompilations;
            }
            else
            {
                HandleCompileFailure(result, uitkxPath);
            }

            // The compile queue (set by OnUitkxFileChanged + cascade walker)
            // is drained by DrainCompileQueueIfIdle on subsequent editor ticks;
            // we don't re-queue from here. The historical single-slot
            // _compilationQueued / _queuedPath fields were dead code and have
            // been replaced by the real FIFO queue declared near the top of
            // this class.
        }

        // ── Extracted swap pipeline (shared by single-file and batch flows) ──

        /// <summary>
        /// Run the post-compile swap pipeline for a single successful result:
        /// module-static + module-method re-init, then component-family
        /// PerformRefresh (or hook delegate swap for hook/module files).
        /// </summary>
        private void ApplySuccessfulCompileResult(
            HmrCompileResult result, string uitkxPath)
        {
            // Sync asset references into cache (lightweight — no SO write)
            SyncAssetCacheForHmr(uitkxPath);

            // Update USS dependency map for this file
            RegisterUssDependencies(uitkxPath);

            // Re-bind `static readonly` module fields BEFORE delegate swap
            // so the new render delegate sees the new field values on its
            // first execution. Pre-fix, edits to a `module` declaration's
            // field initializers (e.g. removing a Style entry) had no
            // effect until the user exited Play mode and forced a full
            // assembly reload. See UitkxHmrModuleStaticSwapper for design.
            ModuleStaticSwapResult moduleStaticResult = ModuleStaticSwapResult.Empty;
            try
            {
                moduleStaticResult = UitkxHmrModuleStaticSwapper.SwapModuleStatics(
                    result.LoadedAssembly
                );
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    $"[HMR] Module-static re-init failed: {ex.Message}"
                );
            }
            int reInitedFields = moduleStaticResult.Copied;
            int addedFieldsDetected = moduleStaticResult.AddedFieldsDetected;

            // Re-bind module static-method __hmr_* delegate fields. Mirrors
            // the per-fiber render-delegate swap and the hook delegate swap,
            // but operates type-wide on every module type in the HMR assembly.
            int reInitedMethods = 0;
            try
            {
                reInitedMethods = UitkxHmrModuleMethodSwapper.SwapModuleMethods(
                    result.LoadedAssembly
                );
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    $"[HMR] Module-method re-init failed: {ex.Message}"
                );
            }

            // Swap delegates (timed)
            var swapSw = Stopwatch.StartNew();
            int swapped;
            if (result.IsHookModuleFile)
            {
                // Hook/module files: update static delegate fields + global re-render.
                // Namespace is carried through HmrCompileResult so we don't probe via
                // GetTypes().FirstOrDefault() (which picks Roslyn's embedded
                // Microsoft.CodeAnalysis.EmbeddedAttribute due to metadata ordering).
                string ns = result.Namespace;
                if (string.IsNullOrEmpty(ns))
                {
                    try
                    {
                        var containerType = result.LoadedAssembly.GetTypes()
                            .FirstOrDefault(t =>
                                t.Name == result.HookContainerClass
                                || t.Name == result.ComponentName);
                        ns = containerType?.Namespace;
                    }
                    catch { }
                }

                // Only swap hook delegates when the file actually declared
                // hooks. A module-ONLY file has no hook container, so calling
                // SwapHooks would just log a spurious "Could not find hook
                // container" warning and no-op.
                int hookSwaps = result.HasHooks
                    ? UitkxHmrDelegateSwapper.SwapHooks(
                        result.LoadedAssembly,
                        result.HookContainerClass,
                        ns
                    )
                    : 0;

                // Module-method AND module/member-static edits: the swappers above
                // rebound delegates / copied field values onto the project types, but
                // loaded consumers still hold render output produced with the OLD
                // values. SwapHooks fires the global re-render on its own success; a
                // module-only or value-only file never runs it, so fire it here.
                // Field copies were previously excluded, which left a project-resident
                // member file's value edit invisible until an unrelated re-render.
                if ((reInitedMethods > 0 || reInitedFields > 0) && hookSwaps == 0)
                    UitkxHmrDelegateSwapper.TriggerGlobalReRender();

                swapped = hookSwaps;

                // Drain Phase 1/3 dirty + force-remount queues populated by
                // the hook's RegisterHook ModuleInitializer (just kicked by
                // ForceRunModuleInitializers). PropagateHookSignatureChanges
                // walks s_reverseEdges to fan force-remount out to every
                // consumer; without this call, hook signature changes never
                // remount their consumers and useRef / useState state from
                // before the edit lingers across a signature change. The
                // delegate-swap re-render (TriggerGlobalReRender) is what
                // picks up body-only edits; PerformRefresh is what enforces
                // the state-reset for signature edits. Both run; the second
                // call is cheap when nothing is dirty.
                int refreshed = global::Ruitk.Refresh.RefreshRuntime.PerformRefresh();
                if (refreshed > swapped)
                    swapped = refreshed;

                // Surface a module-only or value-only swap in the [HMR] notification
                // even when no hook delegates or component families reported live
                // instances (the change was applied via the global re-render).
                if (swapped == 0 && reInitedMethods + reInitedFields > 0)
                    swapped = reInitedMethods + reInitedFields;

                // Always-on outcome line for the member/hook/module route when
                // NOTHING here had a live swap target (mid-session member files: no
                // project type to copy onto, no hooks, no methods). Without it a
                // successful compile of such a file is byte-for-byte silent —
                // indistinguishable from the save never arriving. Consumers pick the
                // new values up via the importer fan-out recompile logged separately.
                if (swapped == 0 && VerboseWatcherTrace)
                    Debug.Log(
                        $"[HMR] {result.ComponentName} compiled ({result.TotalMs:F0}ms) — "
                            + "no live swap target here; member/module changes reach "
                            + "consumers via the importer fan-out recompile."
                    );
            }
            else
            {
                // Component files: under the Family architecture, the
                // freshly compiled assembly's [ModuleInitializer] has
                // already run -- forced deterministically by
                // ForceRunModuleInitializers right after Assembly.LoadFrom
                // (the CLR fires <Module>.cctor lazily on first member
                // access, which never happens for the synthetic companion
                // type that carries [ModuleInitializer], so we kick it
                // explicitly via RuntimeHelpers.RunModuleConstructor).
                // Register has therefore atomically updated the Family's
                // Current delegate. PerformRefresh now walks live renderer
                // trees once, schedules a re-render for every fiber whose
                // Family was just updated, and resets state for fibers
                // whose hook signature changed. Single tree walk -- not
                // per-component-type.
                swapped = global::Ruitk.Refresh.RefreshRuntime.PerformRefresh();

                // A successful compile that refreshes NOTHING must explain itself — a silent
                // no-op is undiagnosable. Name the exact leg that returned zero: the family key
                // never matched (hot assembly registered a FRESH id → key mismatch between the
                // project build and the HMR emit), no dirty families at all (ModuleInitializer
                // didn't run / didn't Register), no live roots, or no mounted fiber of this family.
                if (swapped == 0)
                {
                    var stats = global::Ruitk.Refresh.RefreshRuntime.LastRefreshStats;
                    global::Ruitk.Refresh.RefreshRuntime.TryGetFamilyInfo(
                        result.FamilyKey, out bool famExists, out bool famEverUpdated);
                    string cause;
                    if (!famExists)
                        cause = "the family key was never registered at all — the hot assembly's "
                            + "ModuleInitializer likely did not run or did not Register";
                    else if (!famEverUpdated)
                        cause = "the hot assembly registered a FRESH family (create path, no dirty "
                            + "mark) — the family key does not match the one the project assembly "
                            + "registered at load (key mismatch between build and HMR emit)";
                    else if (stats.DirtyCount == 0)
                        cause = "the family updated but nothing was dirty at refresh time (already "
                            + "consumed by an earlier refresh this frame?)";
                    else if (!stats.ProviderWired)
                        cause = "the root-renderer provider was not wired (HMR session state)";
                    else if (stats.RootsWalked == 0)
                        cause = "no live root renderers — is the app in Play mode with this "
                            + "component mounted?";
                    else
                        cause = "no mounted fiber belongs to this family — the component isn't "
                            + "on screen (or its fibers carry no Family association)";
                    Debug.LogWarning(
                        $"[HMR] {result.ComponentName} compiled OK but 0 fibers refreshed. "
                        + $"family='{result.FamilyKey}' exists={famExists} everUpdated={famEverUpdated}; "
                        + $"dirty={stats.DirtyCount} providerWired={stats.ProviderWired} "
                        + $"rootsWalked={stats.RootsWalked}. Likely cause: {cause}."
                    );
                }
            }
            swapSw.Stop();
            result.SwapMs = swapSw.Elapsed.TotalMilliseconds;

            _swapCount++;
            _lastComponentName = result.ComponentName;
            _lastSwapMs = (float)result.TotalMs;
            _lastTimingBreakdown = result.TimingBreakdown;

            if (ShowNotifications && swapped > 0)
                Debug.Log(
                    $"[HMR] {result.ComponentName} updated ({result.TotalMs:F0}ms, "
                        + $"{swapped} instance(s)) — {result.TimingBreakdown}"
                        + (
                            reInitedFields > 0
                                ? $" | Module statics re-init: {reInitedFields}"
                                : string.Empty
                        )
                        + (
                            reInitedMethods > 0
                                ? $" | Module methods re-init: {reInitedMethods}"
                                : string.Empty
                        )
                );

            // Rude-edit handling: a newly-added `static readonly` field on
            // a module type cannot be added to the project-loaded type
            // (CLR seals type metadata). The swapper has already logged a
            // once-per-session warning. If the user opted in, request a
            // domain reload to materialise the new field everywhere.
            if (addedFieldsDetected > 0 && AutoReloadOnRudeEdit)
            {
                Debug.Log(
                    "[HMR] AutoReloadOnRudeEdit enabled — scheduling domain "
                        + $"reload to materialise {addedFieldsDetected} newly-added "
                        + "module field(s) on the project-loaded type(s)."
                );
                RequestDomainReloadSafe(
                    $"materialise {addedFieldsDetected} newly-added module field(s)"
                );
            }
        }

        /// <summary>
        /// Handle the failure branch of a per-file compile result (infrastructure
        /// detection, dependency auto-resolve, retry-queue maintenance). Extracted
        /// to keep ProcessFileChange small enough to reason about and so the
        /// batch fallback flow can route failures uniformly through here.
        /// </summary>
        private void HandleCompileFailure(HmrCompileResult result, string uitkxPath)
        {
            // Infrastructure failures (reflection signature mismatch, missing
            // type/method, etc.) mean HMR plumbing itself is broken — retrying
            // would fail forever. Log once with actionable text and self-disable.
            if (result.IsInfrastructureError)
            {
                if (!_loggedInfrastructureFailure)
                {
                    _loggedInfrastructureFailure = true;
                    _errorCount++;
                    _recentErrors.Add(result.Error);
                    if (_recentErrors.Count > 10)
                        _recentErrors.RemoveAt(0);
                    Debug.LogError(
                        "[HMR] Infrastructure failure — the loaded language "
                            + "library is incompatible with this HMR build. HMR has "
                            + "been disabled for this session. Restart Unity (or click "
                            + "Start in the HMR window) after rebuilding the language "
                            + "library.\n\nDetails: "
                            + result.Error
                    );
                }
                Stop();
                return;
            }

            // Try to auto-discover and compile missing dependencies.
            // CS0103 means an unresolved name — may be a .uitkx component
            // the FileWatcher missed.
            if (TryResolveMissingDependencies(result.Error, uitkxPath))
            {
                // Dependencies were compiled — retry will happen via
                // the cascade mechanism on the next frame.
                return;
            }

            // During auto-retry, suppress duplicate error logging
            if (!_retryingPending || !_pendingRetryPaths.ContainsKey(uitkxPath))
            {
                _errorCount++;
                _recentErrors.Add(result.Error);
                if (_recentErrors.Count > 10)
                    _recentErrors.RemoveAt(0);
                Debug.LogWarning(result.Error);
            }

            // If the file no longer exists (renamed away or deleted while
            // a stale event was in flight), don't enqueue a retry.
            if (!File.Exists(uitkxPath))
            {
                _pendingRetryPaths.Remove(uitkxPath);
                return;
            }
            _pendingRetryPaths[uitkxPath] = result.Error;
        }

        // ── Deletion handler ──────────────────────────────────────────────────

        /// <summary>
        /// Invoked synchronously on the main thread by the watcher when a
        /// .uitkx file is deleted or renamed away. Evicts the path from the
        /// pending-retry queue so subsequent <see cref="RetryPendingCompilations"/>
        /// passes don't try to re-read a file that no longer exists. Without
        /// this, a copy-rename-edit cycle (the user creates a new component
        /// by cloning an existing one) would leave a stale entry that throws
        /// FileNotFoundException on every retry pass forever.
        /// See Plans~/HMR_NEW_COMPONENT_LIVE_SWAP_PLAN.md §4.4.
        /// </summary>
        private void OnUitkxFileDeleted(string uitkxPath)
        {
            if (!_active)
                return;
            bool removedFromRetry = _pendingRetryPaths.Remove(uitkxPath);
            Debug.Log(
                $"[HMR] Deleted: {Path.GetFileName(uitkxPath)} — evicting per-file "
                    + "registrations"
                    + (removedFromRetry ? " (removed from retry queue)" : string.Empty)
                    + "."
            );

            // Rename/delete cleanup (rename-flow field find): drop every registration keyed
            // by the dead path so a renamed member file's OLD identity cannot linger — its
            // hot DLL as a cross-reference for later compiles, or its own outgoing import
            // edges. Reverse edges (consumers importing this path) deliberately stay: the
            // map is keyed by target path, so they become live again if the file is
            // re-created, and a consumer whose import line still names the dead file fails
            // its own compile with the right diagnostic.
            _compiler?.EvictFileRegistration(uitkxPath);
            string importerAbs = NormalizeImportPath(uitkxPath);
            foreach (var kv in _importDependents)
                kv.Value.Remove(importerAbs);
        }

        // ── USS change handler ────────────────────────────────────────────────

        private void OnUssFileChanged(string ussPath)
        {
            if (!_active)
                return;

            // Also update the cached StyleSheet in the registry so PropsApplier
            // picks up the new version on reconcile. Layout-aware: a package-resident
            // .uss maps to a "Packages/<id>/…" asset path, not "Assets/…".
            string assetRelative = AbsoluteToAssetPath(ussPath);
            if (assetRelative != null)
            {
                AssetDatabase.ImportAsset(assetRelative, ImportAssetOptions.ForceSynchronousImport);
                var sheet = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.StyleSheet>(
                    assetRelative
                );
                if (sheet != null)
                    UitkxAssetRegistry.InjectCacheEntry(assetRelative, sheet);
            }

            // Find all .uitkx files that reference this .uss and re-trigger HMR
            int dependentCount = 0;
            if (_ussDependents.TryGetValue(ussPath, out var dependents))
                dependentCount = dependents.Count;
            if (VerboseWatcherTrace)
                Debug.Log(
                    $"[HMR] Save: {Path.GetFileName(ussPath)} → "
                        + $"{dependentCount} dependent .uitkx file(s)"
                );
            if (dependents != null)
            {
                foreach (string uitkxPath in dependents)
                    OnUitkxFileChanged(uitkxPath);
            }
        }

        /// <summary>
        /// Scans all .uitkx files under assetsRoot for @uss directives and builds
        /// a reverse map: absolute .uss path → list of .uitkx paths that import it.
        /// </summary>
        private void BuildUssDependencyMap(IReadOnlyList<string> roots)
        {
            _ussDependents.Clear();
            try
            {
                foreach (string root in roots)
                    foreach (
                        string uitkxPath in Directory.EnumerateFiles(
                            root,
                            "*.uitkx",
                            SearchOption.AllDirectories
                        )
                    )
                    {
                        RegisterUssDependencies(uitkxPath);
                    }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HMR] Failed to build USS dependency map: {ex.Message}");
            }
        }

        /// <summary>
        /// Scans every <c>.uitkx</c> under each of <paramref name="roots"/> for <c>import</c> lines
        /// and builds the reverse map (imported file → importers), mirroring the USS map
        /// (import/export grammar §8). Called once at Start() with the watcher's own roots;
        /// individual files are refreshed on each edit.
        /// </summary>
        private void BuildImportDependencyMap(IReadOnlyList<string> roots)
        {
            _importDependents.Clear();
            try
            {
                foreach (string root in roots)
                    foreach (
                        string uitkxPath in Directory.EnumerateFiles(
                            root,
                            "*.uitkx",
                            SearchOption.AllDirectories
                        )
                    )
                    {
                        RegisterImportDependencies(uitkxPath);
                    }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HMR] Failed to build import dependency map: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-reads one <c>.uitkx</c> file's <c>import</c> lines and refreshes its outgoing edges in
        /// <see cref="_importDependents"/> (removing this importer from every target first, so a
        /// removed import doesn't leave a stale reverse edge).
        /// </summary>
        private void RegisterImportDependencies(string uitkxPath)
        {
            try
            {
                string importerAbs = NormalizeImportPath(uitkxPath);

                // Drop this importer's previous edges (its import set may have shrunk).
                foreach (var kv in _importDependents)
                    kv.Value.Remove(importerAbs);

                if (!File.Exists(uitkxPath))
                    return;

                string content = File.ReadAllText(uitkxPath);
                string importerDir = NormalizeImportPath(Path.GetDirectoryName(uitkxPath));

                foreach (Match m in s_importLineRegex.Matches(content))
                {
                    string target = ResolveImportSpecifier(importerDir, uitkxPath, m.Groups[2].Value);
                    if (target == null)
                        continue;
                    if (!_importDependents.TryGetValue(target, out var set))
                        _importDependents[target] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(importerAbs);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HMR] Failed to register import deps for {uitkxPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolves an import specifier to an absolute <c>.uitkx</c> path — a compile-verified mirror
        /// of language-lib's <c>ImportResolver.MapSpecifierToPath</c> (Editor/HMR cannot reference
        /// language-lib; see <see cref="HmrAssetPathUtil"/>). Relative (<c>./</c>, <c>../</c>) resolve
        /// against the importer dir; <c>~/</c> against the project's <c>Assets</c> root; engine-native
        /// and bare specifiers return <c>null</c> (never mapped). Extensionless → <c>.uitkx</c> implied.
        /// </summary>
        private static string ResolveImportSpecifier(string importerDir, string importerPath, string specifier)
        {
            if (string.IsNullOrEmpty(specifier))
                return null;

            string baseDir, rest;
            if (specifier.StartsWith("./", StringComparison.Ordinal) ||
                specifier.StartsWith("../", StringComparison.Ordinal))
            {
                baseDir = importerDir;
                rest = specifier;
            }
            else if (specifier.StartsWith("~/", StringComparison.Ordinal))
            {
                string norm = NormalizeImportPath(importerPath);
                int idx = norm.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    return null;
                baseDir = norm.Substring(0, idx) + "/Assets";
                rest = specifier.Substring(2);
            }
            else
            {
                return null; // engine-native (Assets/, Packages/, absolute) or bare → never mapped
            }

            var segs = new List<string>();
            foreach (string s in (baseDir + "/" + rest).Replace('\\', '/').Split('/'))
            {
                if (s.Length == 0 || s == ".")
                    continue;
                if (s == "..")
                {
                    if (segs.Count > 0)
                        segs.RemoveAt(segs.Count - 1);
                }
                else
                {
                    segs.Add(s);
                }
            }

            string path = string.Join("/", segs);
            if (path.Length == 0)
                return null;
            if (!path.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                path += ".uitkx";
            return path;
        }

        private static string NormalizeImportPath(string p)
            => (p ?? string.Empty).Replace('\\', '/').TrimEnd('/');

        /// <summary>
        /// Reads a single .uitkx file, extracts @uss directives, and registers
        /// the reverse mapping. Also called after every successful HMR compilation
        /// to keep the map up to date.
        /// </summary>
        // H-03: asset-path resolution previously disagreed across four consumers
        // (this method treated a bare path as project-root-relative; InjectIfResolved
        // below left it unresolved; the editor's DiagnosticsAnalyzer and the
        // SourceGenerator's CSharpEmitter both resolve it uitkx-dir-relative). The
        // canonical rule now lives once in language-lib's AssetPathUtil; Editor/HMR
        // cannot reference that assembly directly (its asmdef only references
        // Ruitk.Shared/Ruitk.Runtime — language-lib is consumed via
        // reflection against the committed analyzer DLL elsewhere in this feature).
        // HmrAssetPathUtil is a byte-for-byte mirror of AssetPathUtil's algorithm —
        // if you change one, change the other (see FINAL_AUDIT_UITKX_FINDINGS.md H-03).
        internal static class HmrAssetPathUtil
        {
            public static string ResolveAssetPath(string uitkxDir, string rawPath)
            {
                if (string.IsNullOrEmpty(rawPath))
                    return rawPath;

                // BYTE-FOR-BYTE MIRROR of AssetPathUtil.ResolveAssetPath (language-lib). ~/ (root
                // alias) resolves against the UI source root (engine default "Assets") then collapses.
                string combined;
                if (rawPath.StartsWith("~/", StringComparison.Ordinal))
                {
                    combined = "Assets/" + rawPath.Substring(2);
                }
                else
                {
                    if (rawPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                        rawPath.StartsWith("Packages/", StringComparison.Ordinal))
                        return rawPath;
                    combined = string.IsNullOrEmpty(uitkxDir) ? rawPath : uitkxDir + "/" + rawPath;
                }
                var parts = combined.Replace('\\', '/').Split('/');
                var stack = new List<string>();
                foreach (var p in parts)
                {
                    if (p == "." || p == "") continue;
                    if (p == ".." && stack.Count > 0)
                        stack.RemoveAt(stack.Count - 1);
                    else if (p != "..")
                        stack.Add(p);
                }
                return string.Join("/", stack);
            }

            public static string GetAssetDir(string filePath)
            {
                string normalized = (filePath ?? string.Empty).Replace('\\', '/');
                int assetsIdx = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
                if (assetsIdx < 0)
                {
                    if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    {
                        int lastSlash = normalized.LastIndexOf('/');
                        return lastSlash > 0 ? normalized.Substring(0, lastSlash) : "Assets";
                    }

                    // BYTE-FOR-BYTE MIRROR of AssetPathUtil.GetAssetDir's package branch
                    // (language-lib): package-resident files resolve to the Unity asset-path
                    // form "Packages/<manifest name>/<dir>" — never the physical folder name.
                    if (TryGetPackageContext(filePath, out string packageRootAbs, out string packageName))
                    {
                        string rootNorm = packageRootAbs.Replace('\\', '/').TrimEnd('/');
                        string rel = normalized.Length > rootNorm.Length
                            ? normalized.Substring(rootNorm.Length).TrimStart('/')
                            : string.Empty;
                        int relSlash = rel.LastIndexOf('/');
                        string relDir = relSlash >= 0 ? rel.Substring(0, relSlash) : string.Empty;
                        return relDir.Length == 0
                            ? "Packages/" + packageName
                            : "Packages/" + packageName + "/" + relDir;
                    }

                    return Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
                }

                string assetPath = normalized.Substring(assetsIdx + 1);
                int dirSlash = assetPath.LastIndexOf('/');
                return dirSlash >= 0 ? assetPath.Substring(0, dirSlash) : "Assets";
            }

            private static readonly Regex s_packageNameRe = new(
                "\"name\"\\s*:\\s*\"([^\"]+)\"",
                RegexOptions.Compiled);

            private static readonly Dictionary<string, (string Root, string Name)?> s_packageCtxByDir =
                new(StringComparer.OrdinalIgnoreCase);

            public static bool TryGetPackageContext(string filePath, out string packageRootAbs, out string packageName)
            {
                packageRootAbs = string.Empty;
                packageName = string.Empty;
                if (string.IsNullOrEmpty(filePath))
                    return false;

                string dir;
                try { dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); }
                catch { return false; }

                string probe = dir;
                while (!string.IsNullOrEmpty(probe))
                {
                    if (s_packageCtxByDir.TryGetValue(probe, out var cached))
                    {
                        if (cached == null)
                            return false;
                        packageRootAbs = cached.Value.Root;
                        packageName = cached.Value.Name;
                        CacheRange(dir, probe, cached);
                        return true;
                    }

                    string manifest = Path.Combine(probe, "package.json");
                    if (File.Exists(manifest))
                    {
                        string name;
                        try
                        {
                            var m = s_packageNameRe.Match(File.ReadAllText(manifest));
                            name = m.Success ? m.Groups[1].Value : string.Empty;
                        }
                        catch
                        {
                            name = string.Empty;
                        }

                        (string, string)? ctx = name.Length > 0 ? (probe, name) : ((string, string)?)null;
                        CacheRange(dir, probe, ctx);
                        if (ctx == null)
                            return false;
                        packageRootAbs = probe;
                        packageName = name;
                        return true;
                    }

                    probe = Path.GetDirectoryName(probe);
                }

                CacheRange(dir, null, null);
                return false;
            }

            public static void InvalidatePackageContextCache() => s_packageCtxByDir.Clear();

            private static void CacheRange(string fromDir, string foundAt, (string Root, string Name)? ctx)
            {
                string d = fromDir;
                while (!string.IsNullOrEmpty(d))
                {
                    s_packageCtxByDir[d] = ctx;
                    if (string.Equals(d, foundAt, StringComparison.OrdinalIgnoreCase))
                        return;
                    d = Path.GetDirectoryName(d);
                }
            }
        }

        /// <summary>
        /// Maps a resolved Unity asset path to an absolute OS path. <c>Packages/&lt;name&gt;/…</c>
        /// paths resolve through <see cref="UnityEditor.PackageManager.PackageInfo"/> (the
        /// physical folder of an embedded package need not match the package name, and local
        /// packages can live outside the project); everything else joins the project root.
        /// </summary>
        private static string AssetPathToAbsolute(string assetPath, string projectRoot)
        {
            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                {
                    string prefix = "Packages/" + info.name;
                    string rel = assetPath.Length > prefix.Length
                        ? assetPath.Substring(prefix.Length).TrimStart('/')
                        : string.Empty;
                    return Path.GetFullPath(Path.Combine(info.resolvedPath, rel));
                }
            }
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        private void RegisterUssDependencies(string uitkxPath)
        {
            try
            {
                string content = File.ReadAllText(uitkxPath);
                string uitkxDir = HmrAssetPathUtil.GetAssetDir(uitkxPath);
                string projectRoot = Path.GetFullPath(
                    Path.Combine(UnityEngine.Application.dataPath, "..")
                );

                foreach (Match m in s_ussDirectiveRe.Matches(content))
                {
                    string rawPath = m.Groups[1].Value;
                    string resolved = HmrAssetPathUtil.ResolveAssetPath(uitkxDir, rawPath);
                    string absoluteUss = AssetPathToAbsolute(resolved, projectRoot);

                    if (!_ussDependents.TryGetValue(absoluteUss, out var list))
                    {
                        list = new List<string>();
                        _ussDependents[absoluteUss] = list;
                    }
                    if (!list.Contains(uitkxPath))
                        list.Add(uitkxPath);
                }
            }
            catch
            { /* file may be locked or deleted */
            }
        }

        // ── Missing dependency discovery ──────────────────────────────────────

        private static readonly Regex s_cs0103Re = new(
            @"error CS0103: The name '(\w+)' does not exist",
            RegexOptions.Compiled
        );

        /// <summary>
        /// Parse CS0103 errors, search Assets/ for matching .uitkx files,
        /// compile them on-demand. Returns true if any dependencies were
        /// newly compiled (caller should retry via cascade).
        /// </summary>
        private bool TryResolveMissingDependencies(string errorText, string failedPath)
        {
            if (errorText == null || _compiler == null)
                return false;

            var matches = s_cs0103Re.Matches(errorText);
            if (matches.Count == 0)
                return false;

            bool anyCompiled = false;
            var searchRoots = BuildWatchRoots();

            foreach (Match m in matches)
            {
                string missingName = m.Groups[1].Value;

                // Already compiled in this HMR session?
                if (_compiler.HmrAssemblyPaths.ContainsKey(missingName))
                    continue;

                // Search for a matching .uitkx file on disk (same roots the watcher covers)
                string uitkxPath = null;
                foreach (string root in searchRoots)
                {
                    uitkxPath = FindUitkxByComponentName(root, missingName);
                    if (uitkxPath != null)
                        break;
                }
                if (
                    uitkxPath == null
                    || uitkxPath.Equals(failedPath, StringComparison.OrdinalIgnoreCase)
                )
                    continue;

                Debug.Log(
                    $"[HMR] Auto-discovered dependency: {missingName} → {Path.GetFileName(uitkxPath)}"
                );
                // H-06: this is a direct (re-entrant) ProcessFileChange call, not routed
                // through the compile queue — it technically violates the "at most one
                // ProcessFileChange in flight" invariant documented above (this method is
                // itself only ever reached from within an in-flight ProcessFileChange).
                // Safe today because auto-discovered dependencies are compiled
                // synchronously one at a time right here (no concurrent queue drain can
                // interleave); revisit if this path is ever made to fan out concurrently.
                ProcessFileChange(uitkxPath);

                if (_compiler.HmrAssemblyPaths.ContainsKey(missingName))
                    anyCompiled = true;
            }

            if (anyCompiled)
            {
                // Queue the failed file for retry now that deps are available
                _pendingRetryPaths[failedPath] = errorText;
                if (!_retryingPending)
                    EditorApplication.delayCall += RetryPendingCompilations;
            }

            return anyCompiled;
        }

        /// <summary>
        /// Absolute filesystem path → project asset path ("Assets/…" or
        /// "Packages/&lt;id&gt;/…"), or null when the file is in neither surface.
        /// Package paths resolve through <see cref="UnityEditor.PackageManager.PackageInfo"/>
        /// so embedded and local (<c>file:</c>) layouts both map correctly — the
        /// package's ASSET path uses its id, which need not equal its folder name.
        /// </summary>
        private static string AbsoluteToAssetPath(string absolutePath)
        {
            string full;
            try { full = Path.GetFullPath(absolutePath).Replace('\\', '/'); }
            catch { return null; }

            string assetsRoot = Path.GetFullPath(UnityEngine.Application.dataPath).Replace('\\', '/');
            if (full.StartsWith(assetsRoot + "/", StringComparison.OrdinalIgnoreCase))
                return "Assets" + full.Substring(assetsRoot.Length);

            try
            {
                foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (string.IsNullOrEmpty(package.resolvedPath))
                        continue;
                    string root = Path.GetFullPath(package.resolvedPath).Replace('\\', '/');
                    if (full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                        return package.assetPath + full.Substring(root.Length);
                }
            }
            catch { /* package manager unavailable — Assets/ answer above still served */ }

            return null;
        }

        /// <summary>
        /// The roots the HMR watcher (and dependency discovery) must cover: the
        /// project's Assets/ folder plus the resolved location of every WRITABLE
        /// package (embedded and local <c>file:</c> installs — the layouts whose
        /// .uitkx files a developer can actually edit). Immutable cache packages
        /// are excluded: their content cannot change mid-session.
        /// </summary>
        private static List<string> BuildWatchRoots()
        {
            var roots = new List<string>
            {
                Path.GetFullPath(UnityEngine.Application.dataPath),
            };
            try
            {
                foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (package.source != UnityEditor.PackageManager.PackageSource.Embedded
                        && package.source != UnityEditor.PackageManager.PackageSource.Local)
                        continue;
                    if (string.IsNullOrEmpty(package.resolvedPath))
                        continue;
                    string full = Path.GetFullPath(package.resolvedPath);
                    if (Directory.Exists(full) && !roots.Contains(full))
                        roots.Add(full);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[HMR] Package-root discovery failed ({ex.Message}) — watching Assets/ only.");
            }
            return roots;
        }

        /// <summary>
        /// Search a watch root recursively for a .uitkx file whose name matches
        /// the given component name (case-insensitive).
        /// </summary>
        private static string FindUitkxByComponentName(string assetsDir, string componentName)
        {
            string target = componentName + ".uitkx";
            try
            {
                foreach (
                    var file in Directory.EnumerateFiles(
                        assetsDir,
                        "*.uitkx",
                        SearchOption.AllDirectories
                    )
                )
                {
                    if (Path.GetFileName(file).Equals(target, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
            catch
            { /* IO errors during scan — skip */
            }
            return null;
        }

        // ── Auto-cascade retry ────────────────────────────────────────────────

        /// <summary>
        /// Retry compilation of previously-failed files. A successful compilation
        /// of component A may have registered a new assembly that unblocks
        /// component B which references A.
        /// </summary>
        private void RetryPendingCompilations()
        {
            if (!_active || _pendingRetryPaths.Count == 0)
                return;

            _retryingPending = true;
            try
            {
                var paths = new List<string>(_pendingRetryPaths.Keys);
                foreach (var path in paths)
                {
                    if (!_active)
                        break;
                    ProcessFileChange(path);
                }
            }
            finally
            {
                _retryingPending = false;
            }
        }

        // ── Asset cache sync for HMR ──────────────────────────────────────────

        private static readonly Regex s_assetCallRe = new Regex(
            @"(?:Asset|Ast)\s*<\s*(\w+)\s*>\s*\(\s*""([^""]+)""\s*\)",
            RegexOptions.Compiled
        );

        private static readonly Regex s_ussDirectiveRe = new Regex(
            @"@uss\s+""([^""]+)""",
            RegexOptions.Compiled
        );

        /// <summary>
        /// Lightweight HMR-safe asset cache sync: reads the .uitkx file, extracts
        /// asset references, resolves paths, and injects them directly into the
        /// static cache without writing to the SO.
        /// </summary>
        private static void SyncAssetCacheForHmr(string uitkxPath)
        {
            try
            {
                if (!File.Exists(uitkxPath))
                    return;
                string content = File.ReadAllText(uitkxPath);
                string assetDir = HmrAssetPathUtil.GetAssetDir(uitkxPath);

                foreach (Match m in s_ussDirectiveRe.Matches(content))
                    InjectIfResolved(assetDir, m.Groups[1].Value, "StyleSheet");

                foreach (Match m in s_assetCallRe.Matches(content))
                    InjectIfResolved(assetDir, m.Groups[2].Value, m.Groups[1].Value);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HMR] Asset cache sync failed: {ex.Message}");
            }
        }

        // ── Image extensions handled by TextureImporter ────────────────────────

        private static readonly HashSet<string> s_imageExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".tga",
            ".psd",
            ".gif",
            ".tif",
            ".tiff",
            ".exr",
            ".hdr",
        };

        private static void InjectIfResolved(string uitkxDir, string rawPath, string requestedType)
        {
            // H-03: bare paths ("styles.uss", no "./" prefix) previously passed through
            // unresolved here — see HmrAssetPathUtil's doc comment above RegisterUssDependencies.
            string resolved = HmrAssetPathUtil.ResolveAssetPath(uitkxDir, rawPath);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(resolved);

            // If the asset isn't in the database but exists on disk, import it first.
            // This handles files copied into the project during HMR (assemblies are
            // locked so auto-refresh is suppressed).
            if (asset == null)
            {
                string projectRoot = Application.dataPath;
                if (projectRoot.EndsWith("/Assets") || projectRoot.EndsWith("\\Assets"))
                    projectRoot = projectRoot.Substring(0, projectRoot.Length - 6);
                string diskPath = Path.Combine(
                    projectRoot,
                    resolved.Replace('/', Path.DirectorySeparatorChar)
                );

                if (File.Exists(diskPath))
                {
                    AssetDatabase.ImportAsset(resolved, ImportAssetOptions.ForceSynchronousImport);
                    asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(resolved);
                }
            }

            if (asset == null)
                return;

            // ── Auto-configure importer when requested type doesn't match ─────
            string ext = Path.GetExtension(resolved);

            if (s_imageExtensions.Contains(ext))
            {
                asset = ConfigureTextureImport(resolved, asset, requestedType);
            }

            if (asset != null)
                UitkxAssetRegistry.InjectCacheEntry(resolved, asset);
        }

        /// <summary>
        /// Ensures a texture asset's importer matches the requested type.
        /// If <paramref name="requestedType"/> is <c>Sprite</c> but the file is
        /// imported as <c>Texture2D</c> (or vice-versa), reconfigures the
        /// <see cref="TextureImporter"/> and reimports synchronously.
        /// </summary>
        private static UnityEngine.Object ConfigureTextureImport(
            string assetPath,
            UnityEngine.Object currentAsset,
            string requestedType
        )
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return currentAsset;

            if (string.Equals(requestedType, "Sprite", StringComparison.Ordinal))
            {
                if (importer.textureType != TextureImporterType.Sprite)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                }
                // Load the Sprite sub-asset (created by sprite import)
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                return sprite != null ? sprite : currentAsset;
            }

            if (string.Equals(requestedType, "Texture2D", StringComparison.Ordinal))
            {
                if (importer.textureType == TextureImporterType.Sprite)
                {
                    importer.textureType = TextureImporterType.Default;
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath) ?? currentAsset;
                }
                // Already a Texture2D-compatible import
                return currentAsset;
            }

            // Other requested types on image files — return as-is
            return currentAsset;
        }

        // ── Auto-stop hooks ───────────────────────────────────────────────────

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!AutoStopOnPlayMode)
                return;

            if (
                state == PlayModeStateChange.ExitingEditMode
                || state == PlayModeStateChange.ExitingPlayMode
            )
            {
                Debug.Log("[HMR] Auto-stopping due to play mode change.");
                Stop();
                UitkxHmrWindow.RepaintIfOpen();
            }
        }

        // ── Safe domain reload (Play-mode aware) ──────────────────────────────

        // Set while a domain reload has been requested but is being held until
        // the editor exits Play mode. Idempotent: multiple rude edits in the
        // same Play session coalesce into a single deferred reload.
        private bool _pendingReloadOnEditMode;
        private string _pendingReloadReason;

        /// <summary>
        /// Routes a script-reload request through a Play-mode guard.
        /// Firing <c>RequestScriptReload</c> while the editor is in Play mode
        /// produces a partial domain reload that leaves Play-mode
        /// MonoBehaviours with broken script references (observed as
        /// "The referenced script (Unknown) on this Behaviour is missing!").
        /// If we are in Play mode (or transitioning), the request is held
        /// until <c>PlayModeStateChange.EnteredEditMode</c> fires. Otherwise
        /// it dispatches via <c>delayCall</c> so the current HMR cycle
        /// completes cleanly first.
        /// </summary>
        private void RequestDomainReloadSafe(string reason)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (_pendingReloadOnEditMode)
                    return;
                _pendingReloadOnEditMode = true;
                _pendingReloadReason = reason;
                EditorApplication.playModeStateChanged += OnPlayModeChangedForDeferredReload;
                Debug.Log("[HMR] Domain reload deferred until exit of Play mode " + $"({reason}).");
                return;
            }
            string captured = reason;
            EditorApplication.delayCall += () => SafeRequestScriptReload(captured);
        }

        private void OnPlayModeChangedForDeferredReload(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode)
                return;
            EditorApplication.playModeStateChanged -= OnPlayModeChangedForDeferredReload;
            string reason = _pendingReloadReason;
            _pendingReloadOnEditMode = false;
            _pendingReloadReason = null;
            EditorApplication.delayCall += () => SafeRequestScriptReload(reason);
        }

        private static void SafeRequestScriptReload(string reason)
        {
            try
            {
                Debug.Log($"[HMR] Requesting domain reload ({reason}).");
                UnityEditor.EditorUtility.RequestScriptReload();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HMR] RequestScriptReload failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Separate build processor that delegates to the HMR controller.
    /// Unity requires IPreprocessBuildWithReport implementors to have a
    /// parameterless constructor it can instantiate.
    /// </summary>
    internal sealed class UitkxHmrBuildProcessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (UitkxHmrController.IsActive)
            {
                Debug.Log("[HMR] Auto-stopping due to build.");
                UitkxHmrController.Instance?.Stop();
            }
        }
    }
}
