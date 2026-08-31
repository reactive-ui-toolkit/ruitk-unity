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
        /// <summary>Every module this round decided about, after the focus
        /// closure trimmed the batch. What is NOT here was never a candidate -
        /// which is the failure that cost three rounds of guessing, because a
        /// module missing from the batch looks exactly like one that compiled and
        /// changed nothing.</summary>
        public readonly System.Collections.Generic.List<string> Considered =
            new System.Collections.Generic.List<string>();

        /// <summary>Why each rebuilt module was rebuilt.</summary>
        public readonly System.Collections.Generic.Dictionary<string, string> Reasons =
            new System.Collections.Generic.Dictionary<string, string>(
                System.StringComparer.OrdinalIgnoreCase);
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

        /// <summary>Diagnostics from the compiler itself - what each swap unit
        /// inlined, which is what decides whether an edit to an imported module
        /// can be seen at all.</summary>
        public event Action<string> Trace;

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
            // ISO-B: the compiler now asks the TREE, not the disk. The overlay stays
            // as the read fast-path; this is what answers existence and companion
            // discovery, which the overlay never could.
            _moduleSource = new BuilderModuleSource(() => _workspace?.Tree);
            compiler.Modules = _moduleSource;
            compiler.Trace = message => Trace?.Invoke(message);
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
        /// <summary>Kept so a compile can report what it could not answer from the
        /// tree (ISO-G).</summary>
        private BuilderModuleSource _moduleSource;

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

        /// <summary>Bumped whenever <see cref="_built"/> changes. The split-assembly
        /// invariant can only be violated by a BUILD, so a round that follows no
        /// build and finds nothing dirty needs no closure walk to prove it is safe -
        /// and those rounds are the common case, because selecting a card asks for a
        /// compile whether or not anything changed.</summary>
        private int _builtGeneration;

        private int _splitCheckedGeneration = -1;
        private string _splitCheckedFocus;

        private void MarkBuilt(string path, Assembly asm)
        {
            _built[path] = asm;
            _builtGeneration++;
        }

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
            using var __perf = BuilderPerf.Measure("compile round");

            // ISO-G: a fresh log per compile, so the report below is about THIS
            // round rather than everything since the window opened.
            _moduleSource?.ResetFallThroughLog();

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
            // NOT an early return on an empty set: a closure can need rebuilding
            // with nothing dirty at all. Preview a parent, then a child on its
            // own, then the parent again - every buffer matches what it was built
            // from, yet the group is now spread across two assemblies and the
            // parent's props no longer match the child's body. The split check
            // below is the only thing that catches it, so it has to run first.

            // A module whose DEPENDENCY changed has to be rebuilt as well, and its
            // own text has not moved - so it is never a candidate on its own. The
            // loop below already knows this (see dependencyRebuilt) but can only
            // act on modules that are IN the batch. Without closing the set upward
            // first, editing a style rebuilt the style and nothing that uses it,
            // so the preview kept rendering the component against the old one
            // (UB-198).
            AddImportersOfChanged(dirty);

            var summary = new BuilderCompileSummary();
            string focusFull = Path.GetFullPath(focusFile ?? "");

            // Nothing dirty, no build since the last time this focus was checked:
            // the split-assembly invariant cannot have been broken in between,
            // because only a BUILD can break it. Selecting or dragging a card asks
            // for a compile round whether or not anything changed, so this is the
            // common path - and walking the closure here means resolving every
            // import to prove that nothing needs doing.
            if (dirty.Count == 0
                && _splitCheckedGeneration == _builtGeneration
                && string.Equals(_splitCheckedFocus, focusFull, StringComparison.OrdinalIgnoreCase))
                return null;

            // The preview shows ONE component. Compiling every dirty module in the
            // workspace meant the editor got slower with each module the user
            // added - and under the save-only contract they are ALL dirty, for the
            // whole session. Only the focused module and what it imports can affect
            // what is on screen, so only those are built.
            RestrictToFocusClosure(dirty, focusFull);

            // The closure the preview will actually render, dirty or not. The
            // same-assembly invariant is a property of THAT set, so it has to be
            // computed over the whole closure rather than over what happens to
            // have changed.
            var focusClosure = new Dictionary<string, BuilderModule>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var module in _workspace.Modules)
                focusClosure[Path.GetFullPath(module.FilePath)] = module;
            RestrictToFocusClosure(focusClosure, focusFull);
            ForceRebuildOnSplitAssembly(dirty, focusClosure);
            _splitCheckedGeneration = _builtGeneration;
            _splitCheckedFocus = focusFull;
            if (dirty.Count == 0)
                return null;

            summary.Considered.AddRange(dirty.Keys);
            var failedRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var recompiled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = OrderByImports(dirty, out var dependencies);

            // Components go in as ONE assembly; style/hook/util modules are not
            // eligible for the union and keep their own per-file path. Import
            // order is preserved on both sides, so a component still builds after
            // the style module it imports.
            var unionComponents = new List<string>();
            var inUnion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in ordered)
                if (dirty.TryGetValue(path, out var module)
                    && module.Kind == BuilderNodeKind.Component)
                {
                    unionComponents.Add(path);
                    inUnion.Add(path);
                }

            // The round's composition, unconditionally. Which modules were in the
            // batch decides whether a parent can see its children's props types at
            // all, and a round that quietly carried one module looks exactly like a
            // round that carried five.
            UnityEngine.Debug.Log(
                "[RUITK Builder] compile round: " + dirty.Count + " module(s), "
                + unionComponents.Count + " component(s), union "
                + (unionComponents.Count > 1 ? "eligible" : "SKIPPED (needs 2+ components)")
                + " - focus " + Path.GetFileName(focusFull));

            bool unionBuilt = false;
            if (unionComponents.Count > 1)
            {
                foreach (string path in ordered)
                {
                    if (inUnion.Contains(path))
                        continue;
                    var pre = _compiler.Compile(path);
                    recompiled.Add(path);
                    summary.Reasons[path] = "non-component dependency";
                    if (pre.Success)
                    {
                        _compiledFrom[path] = dirty[path].BufferText;
                        if (pre.LoadedAssembly != null)
                            MarkBuilt(path, pre.LoadedAssembly);
                    }
                    else
                    {
                        _compiledFrom.Remove(path);
                        _built.Remove(path);
                    _builtGeneration++;
                        failedRoots[path] = path;
                        summary.Failures.Add((path, pre.Error));
                    }
                    CompileFinished?.Invoke(path, pre.Success, pre.Error);
                    if (string.Equals(focusFull, path, StringComparison.OrdinalIgnoreCase))
                        summary.FocusResult = pre;
                }
                if (failedRoots.Count == 0)
                    unionBuilt = TryCompileAsUnion(
                        unionComponents, dirty, focusFull, summary);
            }
            if (unionBuilt)
            {
                ReportFallThrough();
                return summary;
            }

            foreach (string path in ordered)
            {
                if (recompiled.Contains(path))
                    continue;
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

                summary.Reasons[path] = textMoved ? "text changed" : "dependency rebuilt";
                var result = _compiler.Compile(path);
                recompiled.Add(path);
                // The family key is what a PARENT looks its children up by. A
                // consumer bakes the key from the child FQN it can see, and the
                // child registers under its own; both are derived from FILE paths,
                // so the two can disagree - and then the parent silently falls back
                // to the child body in the saved assembly, which is a stale render
                // that reports no error anywhere (UB-205).
                if (!string.IsNullOrEmpty(result?.FamilyKey))
                    Trace?.Invoke(
                        "built " + Path.GetFileName(path) + " as family " + result.FamilyKey);
                if (result.Success)
                {
                    _compiledFrom[path] = dirty[path].BufferText;
                    if (result.LoadedAssembly != null)
                        MarkBuilt(path, result.LoadedAssembly);
                }
                else
                {
                    _compiledFrom.Remove(path);
                    _built.Remove(path);
                    _builtGeneration++;
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
            ReportFallThrough();
            return summary;
        }

        /// <summary>ISO-G. A fall-through is not automatically wrong -- a
        /// hand-written module outside the open tree is a legitimate import
        /// target. It is wrong when the TREE should have known, which is the leak
        /// this campaign closed three times while it was invisible each time. For
        /// a tree with no outside imports this reads zero; anything else is
        /// named.</summary>
        private void ReportFallThrough()
        {
            var fellThrough = _moduleSource?.FellThroughToDisk;
            if (Trace == null || fellThrough == null || fellThrough.Count == 0)
                return;
            var report = new System.Text.StringBuilder(
                "[RUITK Builder] compile: asked DISK for " + fellThrough.Count
                + " module path(s) the tree could not answer");
            foreach (string path in fellThrough)
                report.Append("\n    ").Append(path);
            Trace(report.ToString());
        }

        /// <summary>
        /// Forces the WHOLE closure back into the batch when its members are not
        /// all living in the same assembly.
        ///
        /// A component's props class is a TYPE, and type identity is per-assembly.
        /// The generated body reads its props with <c>__rawProps as FooProps</c> —
        /// an `as`, not a cast — so when a parent constructs FooProps from assembly
        /// B and the body registered for Foo came from assembly A, the match fails,
        /// the null-coalesce hands the body a fresh FooProps, and the component
        /// renders with EVERY PROP DEFAULTED. Nothing throws and nothing is logged.
        ///
        /// That is reachable by focus order alone: preview the parent (one union
        /// assembly for the group), then preview a child on its own (its own
        /// assembly), then the parent again — at which point nothing is dirty, so
        /// without this check nothing rebuilds and the parent renders blank props.
        /// Same-assembly is therefore an INVARIANT of the closure, not an
        /// optimisation, and it is cheaper to verify than to debug.
        /// </summary>
        private void ForceRebuildOnSplitAssembly(
            Dictionary<string, BuilderModule> batch, Dictionary<string, BuilderModule> closure)
        {
            if (closure.Count < 2)
                return;
            Assembly shared = null;
            bool split = false;
            foreach (var pair in closure)
            {
                // COMPONENTS only. Style, hook and util modules are not
                // union-eligible and are compiled one assembly each BY DESIGN, so
                // their assembly always differs from the union's. Counting them
                // made every closure look permanently split, which forced a full
                // rebuild on every compile round - and since selecting or dragging
                // a card changes focus, that meant a full rebuild per click. The
                // invariant is about props-type identity BETWEEN COMPONENTS; a
                // style module shares no type with them and cannot break it.
                if (pair.Value.Kind != BuilderNodeKind.Component)
                    continue;
                if (!_built.TryGetValue(pair.Key, out var asm) || asm == null)
                {
                    // Never built, or built and failed: the normal dirty test
                    // already covers it.
                    continue;
                }
                if (shared == null)
                    shared = asm;
                else if (!ReferenceEquals(shared, asm))
                {
                    split = true;
                    break;
                }
            }
            if (!split)
                return;
            int forced = 0;
            foreach (var pair in closure)
            {
                if (pair.Value.Kind != BuilderNodeKind.Component)
                    continue;
                batch[pair.Key] = pair.Value;
                forced++;
            }
            Trace?.Invoke(
                "[RUITK Builder] compile: the focused closure's components were split "
                + "across assemblies - rebuilding " + forced
                + " as one unit so their props types match");
        }

        /// <summary>
        /// Compiles the batch's COMPONENTS as a single assembly.
        ///
        /// One assembly means one definition of each component and therefore one
        /// props type, which is the whole point: a parent that passes props to a
        /// child it was compiled beside can never be handed a type from a
        /// different assembly. It is also what a real Unity compile does — every
        /// component in an asmdef lands in one DLL — so this is parity rather
        /// than a trick.
        ///
        /// Returns false when the union declines (a parse failure, a duplicate
        /// (namespace, name), a Roslyn error); the caller then walks the
        /// well-tested per-file path, which is what surfaces the actual error to
        /// the user.
        /// </summary>
        private bool TryCompileAsUnion(
            List<string> componentPaths,
            Dictionary<string, BuilderModule> batch,
            string focusFull,
            BuilderCompileSummary summary)
        {
            if (componentPaths.Count < 2)
                return false;

            var batchResult = _compiler.CompileBatch(componentPaths);
            if (batchResult == null || !batchResult.OverallSuccess)
            {
                // Debug.Log, not Trace: a declined union is the difference between
                // "the parent sees its children's current props" and "it silently
                // does not", and routing that to a toggle nobody has on made it
                // invisible for a whole round of diagnosis. The console always
                // gets it; Trace adds the detail.
                string reason = batchResult?.FallbackReason
                    ?? batchResult?.OverallError
                    ?? "no result";
                UnityEngine.Debug.LogWarning(
                    "[RUITK Builder] union compile DECLINED for " + componentPaths.Count
                    + " component(s) - falling back to one assembly per module, so a parent "
                    + "may not see a child's current props type. Reason: " + reason);
                Trace?.Invoke("[RUITK Builder] compile: union declined - " + reason);
                return false;
            }

            for (int i = 0; i < componentPaths.Count; i++)
            {
                string path = componentPaths[i];
                var perFile = i < batchResult.PerFileResults.Count
                    ? batchResult.PerFileResults[i]
                    : null;
                summary.Reasons[path] = "union build";
                _compiledFrom[path] = batch[path].BufferText;
                if (batchResult.UnionAssembly != null)
                    MarkBuilt(path, batchResult.UnionAssembly);
                if (!string.IsNullOrEmpty(perFile?.FamilyKey))
                    Trace?.Invoke(
                        "built " + Path.GetFileName(path)
                        + " as family " + perFile.FamilyKey + " (union)");
                CompileFinished?.Invoke(path, true, null);
                if (string.Equals(focusFull, path, StringComparison.OrdinalIgnoreCase)
                    && perFile != null)
                    summary.FocusResult = perFile;
            }
            Trace?.Invoke(
                "[RUITK Builder] compile: union built " + componentPaths.Count
                + " components into one assembly");
            return true;
        }

        /// <summary>Drops every dirty module the focused one cannot reach through
        /// its imports. A module nobody in the preview refers to cannot change what
        /// the preview shows, and building it is pure cost - paid on every debounced
        /// keystroke, through an external csc process on Unity 6.5.</summary>
        /// <summary>Pulls in every module that reaches something already in the
        /// batch through its imports. Searched over the WHOLE tree, not over the
        /// batch, because the modules being added are by definition the ones that
        /// did not change and so are not in it yet.</summary>
        private void AddImportersOfChanged(Dictionary<string, BuilderModule> candidates)
        {
            var all = new Dictionary<string, BuilderModule>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in _workspace.Modules)
                all[Path.GetFullPath(module.FilePath)] = module;

            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var pair in all)
                {
                    if (candidates.ContainsKey(pair.Key))
                        continue;
                    foreach (string dep in ResolveImports(pair.Key, pair.Value))
                    {
                        if (!candidates.ContainsKey(dep))
                            continue;
                        candidates[pair.Key] = pair.Value;
                        grew = true;
                        break;
                    }
                }
            }
        }

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
        /// <summary>The overlay the compiler and the language lib both read.
        ///
        /// It delegates to the module source rather than answering separately, so
        /// there is ONE policy for what a module says: the tree, falling through to
        /// disk only for files the tree does not own. Two implementations of that
        /// rule is how they drift, and drift here is invisible until something
        /// renders the wrong component.</summary>
        private string ReadBuffer(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            return _moduleSource?.ReadText(path);
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
