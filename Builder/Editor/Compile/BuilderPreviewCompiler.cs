#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
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
        public BuilderCompileSummary CompileDirty(string focusFile)
        {
            if (_compiler == null || _workspace == null)
                return null;

            var dirty = new Dictionary<string, BuilderDocumentSession>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in _workspace.Sessions)
            {
                if (session.IsDirty)
                    dirty[Path.GetFullPath(session.FilePath)] = session;
            }
            if (dirty.Count == 0)
                return null;

            var summary = new BuilderCompileSummary();
            string focusFull = Path.GetFullPath(focusFile ?? "");
            var failedRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                var result = _compiler.Compile(path);
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

        public void Dispose()
        {
            _compiler?.Dispose();
            _compiler = null;
            _workspace = null;
        }

        private string ReadBuffer(string path)
        {
            var session = _workspace?.TryGet(path);
            return session?.BufferText;
        }

        /// <summary>Topological order over the dirty set: imported peers first.
        /// Only relative specifiers resolve (the cross-tree <c>~/</c> form points
        /// outside the open tree); cycles fall back to insertion order. The
        /// dirty-set dependency edges are retained for the caller's
        /// skip-downstream-of-failure pass.</summary>
        private List<string> OrderByImports(
            Dictionary<string, BuilderDocumentSession> dirty,
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

        private IEnumerable<string> ResolveImports(string path, BuilderDocumentSession session)
        {
            var resolved = new List<string>();
            try
            {
                var parsed = BuilderLanguage.Parse(session.BufferText, path);
                string dir = Path.GetDirectoryName(path) ?? "";
                foreach (var import in parsed.Directives.Imports)
                {
                    string spec = import.Specifier;
                    if (string.IsNullOrEmpty(spec) || spec.StartsWith("@", StringComparison.Ordinal))
                        continue;
                    if (!spec.StartsWith(".", StringComparison.Ordinal))
                        continue;
                    string candidate = Path.GetFullPath(Path.Combine(dir, spec + ".uitkx"));
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
