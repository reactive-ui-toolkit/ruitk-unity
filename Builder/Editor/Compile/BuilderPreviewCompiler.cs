#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Ruitk.EditorSupport.HMR;

namespace Ruitk.Builder
{
    /// <summary>Per-run outcome of <see cref="BuilderPreviewCompiler.CompileDirty"/>:
    /// the focus file's result plus every failure and every skipped dependent —
    /// nothing about a compile round is silent (UB-15).</summary>
    internal sealed class BuilderCompileSummary
    {
        public HmrCompileResult FocusResult;
        public readonly List<(string Path, string Error)> Failures =
            new List<(string, string)>();
        public readonly List<(string Path, string BlockedBy)> Skipped =
            new List<(string, string)>();
    }

    /// <summary>
    /// The builder's OWN hot-swap compiler instance (plan §2.2: the controller's
    /// instance compiles disk content; this one compiles workspace buffers via
    /// the VE-05 SourceOverlay seam — two instances coexist safely, families
    /// converge after Save because registration is global last-write-wins).
    ///
    /// Dirty buffers compile per-file in import-graph order (§4.3b): an
    /// imported peer compiles before its importer so the peer's assembly is in
    /// this instance's cross-ref registry when the importer compiles.
    /// </summary>
    internal sealed class BuilderPreviewCompiler : IDisposable
    {
        private UitkxHmrCompiler _compiler;
        private BuilderWorkspace _workspace;
        private string _initError;

        public event Action<string, bool, string> CompileFinished;

        public string InitError => _initError;

        public bool EnsureReady(BuilderWorkspace workspace)
        {
            _workspace = workspace;
            if (_compiler != null)
                return true;
            if (_initError != null)
                return false;

            var compiler = new UitkxHmrCompiler();
            if (!compiler.TryInitialize(out string error))
            {
                compiler.Dispose();
                _initError = error;
                return false;
            }
            compiler.SourceOverlay = ReadBuffer;
            _compiler = compiler;
            return true;
        }

        /// <summary>
        /// Compiles every dirty session, dependencies before dependents.
        /// UB-15: a failure no longer aborts the whole loop — only the FAILED
        /// file's downstream dependents are skipped (compiling them would just
        /// cascade against a stale peer assembly); independent siblings still
        /// compile, and every outcome is reported in the summary instead of
        /// dying as a null return.
        /// </summary>
        /// <summary>The buffer each module was last COMPILED from. Under the
        /// save-only contract a module stays dirty until Save, so the dirty set only
        /// grows as the user works - and every debounced keystroke was recompiling
        /// all of it, whether or not that module had been touched. On Unity 6.5 each
        /// of those is an external csc process, so the editor got measurably slower
        /// with every module added to an unsaved tree.</summary>
        private readonly Dictionary<string, string> _compiledFrom =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The assembly each module was last successfully BUILT into.
        ///
        /// The preview has to render the CURRENT build, and it cannot work out
        /// which that is by scanning loaded assemblies: every hot swap loads
        /// another one carrying the same [UitkxSource] path, so a scan finds an
        /// arbitrary one - in practice the oldest still loaded. That is why leaving
        /// a component and coming back showed an earlier render, and why it stuck:
        /// the module needs no rebuild, so nothing came along to correct it.
        ///
        /// The compiler produced these assemblies, so it is the only thing that
        /// knows which is current. It says so rather than letting the pane guess.</summary>
        private readonly Dictionary<string, Assembly> _built =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        public Assembly BuiltAssemblyFor(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }
            return _built.TryGetValue(full, out var asm) ? asm : null;
        }

        public BuilderCompileSummary CompileDirty(string focusFile)
        {
            if (_compiler == null || _workspace == null)
                return null;

            // What needs rebuilding is what has changed since it was last BUILT -
            // not what is unsaved. Those differ in a case the owner hit head-on:
            // type a label, then type it back to what is on disk, and the module
            // becomes CLEAN. Keyed on dirtiness it left the batch, nothing
            // recompiled, and the preview went on showing the edit that had been
            // taken back (UB-194).
            var dirty = new Dictionary<string, BuilderModule>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in _workspace.Modules)
            {
                string full = Path.GetFullPath(session.FilePath);
                if (!_compiledFrom.TryGetValue(full, out string built)
                    || !string.Equals(built, session.BufferText, StringComparison.Ordinal))
                    dirty[full] = session;
            }
            if (dirty.Count == 0)
                return null;

            var summary = new BuilderCompileSummary();
            string focusFull = Path.GetFullPath(focusFile ?? "");

            // The preview shows ONE component. Compiling every dirty module in the
            // workspace meant the editor got slower with each module the user
            // added - and under the save-only contract they are ALL dirty, for the
            // whole session. Only the focused module and what it imports can affect
            // what is on screen, so only those are built.
            RestrictToFocusClosure(dirty, focusFull);
            var failedRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var recompiled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in OrderByImports(dirty, out var dependencies))
            {
                string blockedBy = null;
                if (dependencies.TryGetValue(path, out var deps))
                {
                    foreach (string dep in deps)
                    {
                        if (failedRoots.TryGetValue(dep, out string root))
                        {
                            blockedBy = root;
                            break;
                        }
                    }
                }
                if (blockedBy != null)
                {
                    failedRoots[path] = blockedBy;
                    summary.Skipped.Add((path, blockedBy));
                    CompileFinished?.Invoke(path, false,
                        "skipped — depends on failed " + Path.GetFileName(blockedBy));
                    continue;
                }
                // Nothing to do for a module whose own text has not moved and none
                // of whose imports were rebuilt this round: its swap assembly is
                // still current. Walking in import order is what makes the second
                // half of that test valid - every dependency has been decided
                // before its dependents are.
                bool textMoved = !_compiledFrom.TryGetValue(path, out string lastText)
                    || !string.Equals(lastText, dirty[path].BufferText, StringComparison.Ordinal);
                bool dependencyRebuilt = false;
                if (!textMoved && dependencies.TryGetValue(path, out var upstream))
                {
                    foreach (string dep in upstream)
                    {
                        if (recompiled.Contains(dep))
                        {
                            dependencyRebuilt = true;
                            break;
                        }
                    }
                }
                if (!textMoved && !dependencyRebuilt)
                    continue;

                var result = _compiler.Compile(path);
                recompiled.Add(path);
                if (result.Success)
                {
                    _compiledFrom[path] = dirty[path].BufferText;
                    if (result.LoadedAssembly != null)
                        _built[path] = result.LoadedAssembly;
                }
                else
                {
                    _compiledFrom.Remove(path);
                    _built.Remove(path);
                }
                CompileFinished?.Invoke(path, result.Success, result.Error);
                if (string.Equals(focusFull, path, StringComparison.OrdinalIgnoreCase))
                    summary.FocusResult = result;
                if (!result.Success)
                {
                    failedRoots[path] = path;
                    summary.Failures.Add((path, result.Error));
                }
            }
            return summary;
        }

        /// <summary>Drops every dirty module the focused one cannot reach through
        /// its imports. A module nobody in the preview refers to cannot change what
        /// the preview shows, and building it is pure cost - paid on every debounced
        /// keystroke, through an external csc process on Unity 6.5.</summary>
        /// <summary>Every dirty module that reaches <paramref name="target"/>
        /// through its imports, directly or not.</summary>
        private List<string> ImportersOf(
            Dictionary<string, BuilderModule> dirty, string target)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target };
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var pair in dirty)
                {
                    if (seen.Contains(pair.Key))
                        continue;
                    foreach (string dep in ResolveImports(pair.Key, pair.Value))
                    {
                        if (!seen.Contains(dep))
                            continue;
                        seen.Add(pair.Key);
                        found.Add(pair.Key);
                        grew = true;
                        break;
                    }
                }
            }
            return found;
        }

        private void RestrictToFocusClosure(
            Dictionary<string, BuilderModule> dirty, string focusFull)
        {
            if (dirty.Count <= 1 || !dirty.ContainsKey(focusFull))
                return;

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { focusFull };
            var queue = new Queue<string>();
            queue.Enqueue(focusFull);

            // A module with no visual of its own is never what the preview is
            // showing - a COMPONENT that imports it is. Clicking a style entry to
            // edit it moves the focus onto that style, so a forward-only walk
            // dropped the very component on screen and it stopped updating until
            // something moved the focus back (UB-190).
            if (dirty.TryGetValue(focusFull, out var focusModule)
                && focusModule.Kind != BuilderNodeKind.Component)
            {
                foreach (string importer in ImportersOf(dirty, focusFull))
                    if (keep.Add(importer))
                        queue.Enqueue(importer);
            }
            while (queue.Count > 0)
            {
                string path = queue.Dequeue();
                if (!dirty.TryGetValue(path, out var session))
                    continue;
                foreach (string dep in ResolveImports(path, session))
                {
                    if (dirty.ContainsKey(dep) && keep.Add(dep))
                        queue.Enqueue(dep);
                }
            }

            if (keep.Count == dirty.Count)
                return;
            var drop = new List<string>();
            foreach (string path in dirty.Keys)
                if (!keep.Contains(path))
                    drop.Add(path);
            foreach (string path in drop)
                dirty.Remove(path);
        }

        public void Dispose()
        {
            _compiler?.Dispose();
            _compiler = null;
            _workspace = null;
            _compiledFrom.Clear();
            _built.Clear();
        }

        /// <summary>The unsaved-buffer overlay the HMR compiler reads every .uitkx
        /// through. The path is CANONICALISED first, and that is the whole point.
        ///
        /// The compiler resolves an import target with
        /// ImportResolver.MapSpecifierToPath, which builds its answer by joining
        /// strings with FORWARD slashes; every session in the workspace is keyed by
        /// a Path.GetFullPath path, which on Windows uses backslashes. The lookup
        /// therefore missed for every import target, UitkxSourceExists fell through
        /// to File.Exists, and that is false for a module the builder has never
        /// saved. The import was silently dropped, no alias was emitted for it, and
        /// the component failed to compile with CS0103 on the alias name - so a
        /// component and a style module both created in the builder could not
        /// preview together until they had been saved.</summary>
        private string ReadBuffer(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }
            return _workspace?.TryGet(full)?.BufferText;
        }

        /// <summary>Topological order over the dirty set: imported peers first.
        /// Only relative specifiers resolve (the cross-tree <c>~/</c> form points
        /// outside the open tree); cycles fall back to insertion order. The
        /// dirty-set dependency edges are retained for the caller's
        /// skip-downstream-of-failure pass.</summary>
        private List<string> OrderByImports(
            Dictionary<string, BuilderModule> dirty,
            out Dictionary<string, List<string>> dependencies)
        {
            var order = new List<string>(dirty.Count);
            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var deps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            void Visit(string path)
            {
                if (state.TryGetValue(path, out int s) && s != 0)
                    return;
                state[path] = 1;
                var mine = new List<string>();
                foreach (string dep in ResolveImports(path, dirty[path]))
                {
                    if (!dirty.ContainsKey(dep))
                        continue;
                    mine.Add(dep);
                    if (!state.TryGetValue(dep, out int ds) || ds == 0)
                        Visit(dep);
                }
                deps[path] = mine;
                state[path] = 2;
                order.Add(path);
            }

            foreach (string path in dirty.Keys)
                Visit(path);
            dependencies = deps;
            return order;
        }

        private IEnumerable<string> ResolveImports(string path, BuilderModule session)
        {
            var resolved = new List<string>();
            try
            {
                var parsed = BuilderLanguage.Parse(session.BufferText, path);
                foreach (var import in parsed.Directives.Imports)
                {
                    string spec = import.Specifier;
                    if (string.IsNullOrEmpty(spec) || spec.StartsWith("@", StringComparison.Ordinal))
                        continue;
                    // One resolver for the whole builder: compile order and the
                    // edges the canvas draws must agree about what an import
                    // points at, or a module compiles before what it depends on.
                    string candidate = BuilderGraphService.MapSpecifier(path, spec);
                    if (candidate != null)
                        resolved.Add(candidate);
                }
            }
            catch
            {
                // A buffer that cannot parse contributes no ordering edges; the
                // compile itself reports the parse failure with full diagnostics.
            }
            return resolved;
        }
    }
}
#endif
