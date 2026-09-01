using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Roslyn in-process compilation (reflection handles)
// Uses Microsoft.CodeAnalysis.CSharp 4.3.1 netstandard2.0 loaded at runtime

namespace Ruitk.EditorSupport.HMR
{
    /// <summary>
    /// Compiles a .uitkx file in-process:
    ///   1. Parse via Ruitk.Language.dll (loaded at runtime, Roslyn-free)
    ///   2. Emit C# via built-in HMR emitter
    ///   3. Compile via in-process Roslyn (fast) or external csc.dll (fallback)
    ///   4. Load via Assembly.Load
    /// </summary>
    internal sealed class UitkxHmrCompiler : IDisposable
    {
        // ── File-read retry policy ───────────────────────────────────────────
        // When a .uitkx (or .cs) save fires the FileSystemWatcher, the
        // originating editor often still holds the file with an exclusive
        // write lock for a few milliseconds. A naive File.ReadAllText then
        // throws IOException ("Sharing violation"), the compile is aborted
        // and the user's edit silently never reaches Roslyn — visible in
        // logs as repeated edits to a child component having zero effect
        // while edits to the parent (whose save happens to land after the
        // lock release) work fine.
        //
        // ReadTextWithRetry opens with FileShare.ReadWrite|Delete (so we
        // cooperate with the editor instead of fighting it) and retries
        // with a short exponential-ish backoff on IOException /
        // UnauthorizedAccessException. Total worst-case wait ~480ms which
        // is well under the FileWatcher debounce window.
        private const int FileReadMaxAttempts = 8;

        internal static string ReadTextWithRetry(string path)
        {
            int delayMs = 5;
            IOException lastIo = null;
            UnauthorizedAccessException lastUa = null;
            for (int attempt = 1; attempt <= FileReadMaxAttempts; attempt++)
            {
                try
                {
                    using (
                        var fs = new FileStream(
                            path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            bufferSize: 4096,
                            useAsync: false
                        )
                    )
                    using (var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true))
                    {
                        var text = sr.ReadToEnd();
                        if (attempt > 1)
                        {
                            Debug.Log(
                                $"[HMR] ReadTextWithRetry: succeeded on attempt {attempt}/{FileReadMaxAttempts} for '{path}' (editor lock cleared)."
                            );
                        }
                        return text;
                    }
                }
                catch (IOException ex)
                {
                    lastIo = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastUa = ex;
                }
                System.Threading.Thread.Sleep(delayMs);
                if (delayMs < 120)
                    delayMs *= 2;
            }
            // Out of retries — surface the original exception so the caller's
            // try/catch path (HandleCompileFailure → re-queue) still runs.
            Debug.LogWarning(
                $"[HMR] ReadTextWithRetry: gave up after {FileReadMaxAttempts} attempts for '{path}'. Editor lock did not release in time; compile will be re-queued."
            );
            if (lastIo != null)
                throw lastIo;
            if (lastUa != null)
                throw lastUa;
            // Defensive — both null shouldn't be reachable but keep the
            // contract: caller always gets a string or an exception.
            throw new IOException($"ReadTextWithRetry exhausted attempts for '{path}'.");
        }

        /// <summary>
        /// Optional overlay for unsaved editor buffers (the RUITK Builder compiles
        /// buffer content that never touched disk). Every <c>.uitkx</c> read on the
        /// compile pipeline consults it FIRST: a non-null return is used verbatim,
        /// null falls through to disk. Companion <c>.cs</c> reads are never
        /// overlaid. Existence checks for <c>.uitkx</c> go through
        /// <see cref="UitkxSourceExists"/> so overlay-only files (created in the
        /// builder, not yet saved) resolve as import targets.
        /// </summary>
        internal Func<string, string> SourceOverlay { get; set; }

        /// <summary>Where module truth comes from. Defaults to the filesystem, which
        /// is exactly what HMR wants and what every caller had before this existed.
        /// The builder replaces it with one backed by its in-memory tree.
        ///
        /// The point of it being a FIELD rather than a set of remembered rules: an
        /// overlay that is not passed cannot be enforced, and three defects in one
        /// wave were call sites that forgot to consult it (ISO-3 in
        /// Plans~/BUILDER_ISOLATION_PLAN.md).</summary>
        internal IModuleSource Modules { get; set; } = FileSystemModuleSource.Instance;

        /// <summary>Optional diagnostic sink. What a swap unit INLINES is the one
        /// fact that decides whether an edit to an imported module can be seen,
        /// and it was invisible from outside - which is what made UB-203 take four
        /// rounds of guessing.</summary>
        internal Action<string> Trace { get; set; }


        /// <summary>Hands the unsaved-buffer overlay to the language lib, which
        /// resolves import targets of its own when it computes the using aliases an
        /// import implies. Done per compile rather than once, so it cannot depend on
        /// whether the overlay was set before or after initialisation.</summary>
        private void PublishSourceOverlay()
        {
            if (_importScopeOverlay == null)
                return;
            try
            {
                _importScopeOverlay.SetValue(null, SourceOverlay);
            }
            catch (Exception)
            {
                // An older Language.dll, or a field of another shape: the compile
                // still runs, it just cannot see unsaved import targets.
            }
        }

        private string ReadUitkxText(string path)
        {
            // SourceOverlay is still consulted first so an existing caller that sets
            // only the overlay keeps working; it is now an adapter ONTO the module
            // source rather than a parallel truth (ISO-A).
            string overlay = SourceOverlay?.Invoke(path);
            return overlay ?? (Modules ?? FileSystemModuleSource.Instance).ReadText(path);
        }

        private bool UitkxSourceExists(string path)
        {
            if (SourceOverlay?.Invoke(path) != null)
                return true;
            return (Modules ?? FileSystemModuleSource.Instance).Exists(path);
        }

        /// <summary>The companion set for a module: siblings sharing its name
        /// prefix. A directory glob cannot answer this for an unsaved tree, where
        /// the companion has no file to enumerate (ISO-1).</summary>
        private IEnumerable<string> CompanionSiblings(string directory, string prefix) =>
            (Modules ?? FileSystemModuleSource.Instance).SiblingsWithPrefix(directory, prefix);

        // ── Loaded pipeline assembly ──────────────────────────────────────────
        private Assembly _languageAsm;

        // ── Cached reflection handles ─────────────────────────────────────────
        private MethodInfo _directiveParse;
        private MethodInfo _uitkxParse;
        private MethodInfo _canonicalLower;
        private MethodInfo _parseFragment; // optional — see H-04
        private MethodInfo _findJsxBlockRanges;
        private MethodInfo _findBareJsxRanges;
        private MethodInfo _findLhsStartForLogicalAnd;
        // §7 — shared language-lib functions so HMR computes the SAME qualified hook family
        // key ({EffectiveNs}.{Container}::{name}) the source generator does. Optional (tolerate
        // an older Language.dll): when null, HMR falls back to the file's raw namespace.
        private MethodInfo _effectiveNamespaceResolve; // EffectiveNamespace.Resolve(bool, string, string, bool) — 4-arg mode-aware overload (U-01)
        private MethodInfo _uiSourceRootDir;           // EffectiveNamespace.UiSourceRootDir(string)
        private MethodInfo _importResolverMap;         // ImportResolver.MapSpecifierToPath(string, string, string, out string)
        // §6.2/§6.3 parity — ImportScopeFacts.ComputeInjectedUsingPayloads(DirectiveSet, string):
        // the using lines a file's imports imply (cross-folder hook containers + module/component
        // type aliases with EFFECTIVE namespaces). Optional (older Language.dll → skip).
        private MethodInfo _importScopePayloads;
        private FieldInfo _importScopeOverlay;
        // U-03 bridges — ImportScopeFacts.ComputeImportedMemberBridgeLines(DirectiveSet, string):
        // rendered `internal static …` forwarding lines for aliased/default member imports,
        // byte-identical to the SG's ExportsEmitter shapes. Optional (older Language.dll → skip).
        private MethodInfo _importedMemberBridgeLines;
        // U-05/U-03 tag maps (audit H3) — ImportScopeFacts.ComputeStarImportNamespaces /
        // ComputeImportAliasTypeMap: dotted-tag + renamed/default component tag resolution for
        // the hot emitter, disk-parse twin of the SG's BuildImportAliasTagMaps. Optional.
        private MethodInfo _starImportNamespacesFn;
        private MethodInfo _importAliasTypeMapFn;
        private Type _parseDiagnosticType;

        // ── Reference cache (built once per session) ──────────────────────────
        private List<string> _referenceLocations;
        private int _swapCounter;
        private string _dotnetPath;
        private string _cscPath;
        private string _tempDir;

        // ── In-process Roslyn compilation ─────────────────────────────────────
        private bool _roslynLoaded;
        private Assembly _roslynCSharpAsm;
        private Assembly _roslynCommonAsm;

        // Cached reflection: CSharpSyntaxTree.ParseText(string, CSharpParseOptions)
        private MethodInfo _parseText;

        // Cached reflection: CSharpCompilation.Create(string, IEnumerable<SyntaxTree>, IEnumerable<MetadataReference>, CSharpCompilationOptions)
        private MethodInfo _compilationCreate;

        // Cached: CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, ...)
        private object _compilationOptions;

        // Cached: CSharpParseOptions(languageVersion: Latest)
        private object _parseOptions;

        // Cached: MetadataReference[] built from _referenceLocations + cross-refs
        private object[] _metadataReferences;

        // Cached: MethodInfo for MetadataReference.CreateFromFile(string)
        private MethodInfo _createFromFile;

        // Cached: MethodInfo for CSharpCompilation.Emit(Stream)
        private MethodInfo _emitToStream;

        // Cached: incremental compilation API handles
        private MethodInfo _compilationRemoveSyntaxTrees;
        private MethodInfo _compilationAddSyntaxTrees;
        private MethodInfo _compilationAddReferences;
        private MethodInfo _compilationWithAssemblyName;

        // ── Incremental compilation cache ─────────────────────────────────
        // Cached Compilation per component for incremental reuse
        private readonly Dictionary<string, object> _cachedCompilations = new(
            StringComparer.OrdinalIgnoreCase
        );

        // Cached SyntaxTree[] per component (to know what to remove)
        private readonly Dictionary<string, object[]> _cachedSyntaxTrees = new(
            StringComparer.OrdinalIgnoreCase
        );

        // Track genuinely-new component count to detect cross-ref changes
        private int _lastGenuineComponentCount;

        // ── Cross-ref MetadataReference cache ─────────────────────────────
        // Cached MetadataReference per cross-component DLL path
        private readonly Dictionary<string, object> _crossRefCache = new(
            StringComparer.OrdinalIgnoreCase
        );

        // ── Per-asmdef reference filtering ────────────────────────────────
        // _referenceLocations is built once from AppDomain.GetAssemblies() and
        // therefore contains EVERY loaded assembly. Handing that whole set to
        // Roslyn for an HMR compile of a file owned by asmdef X causes CS0433
        // duplicate-type errors whenever the project legitimately defines the
        // same type name in two non-cross-referencing asmdefs (e.g. a user
        // component `AppButton` in Assembly-CSharp and a sample `AppButton`
        // in Ruitk.Samples — Unity's normal compile never sees both
        // because Assembly-CSharp.csproj does not reference Samples). The
        // fix mirrors Unity's per-asmdef reference closure via
        // CompilationPipeline.GetAssemblies(...).allReferences. Caches are
        // populated lazily on first use per asmdef and cleared in Reset().
        private readonly Dictionary<string, HashSet<string>> _allowedRefsByAsmdef = new(
            StringComparer.Ordinal
        );
        private readonly Dictionary<string, object[]> _filteredMetaRefsByAsmdef = new(
            StringComparer.Ordinal
        );
        private readonly Dictionary<string, List<string>> _filteredRefLocsByAsmdef = new(
            StringComparer.Ordinal
        );

        // Map of loaded Roslyn dependency DLLs for AssemblyResolve
        private readonly Dictionary<string, Assembly> _roslynDeps = new(
            StringComparer.OrdinalIgnoreCase
        );

        // ── HMR assembly registry (component → DLL path for cross-references) ─
        private readonly Dictionary<string, string> _hmrAssemblyPaths = new(
            StringComparer.OrdinalIgnoreCase
        );

        // Member-only new-mode files: full uitkx path → registry key ({ns}.__Exports).
        // Lets a file-delete/rename event evict every registration of the dead path
        // (EvictFileRegistration) without re-parsing a file that no longer exists.
        private readonly Dictionary<string, string> _memberRegistryKeysByFile = new(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>
        /// The component names the compile in flight is DEFINING, so their own
        /// previously-built swap assemblies are not also referenced.
        ///
        /// The single-file path expressed this as "skip self", comparing against
        /// one componentName. A UNION compile defines several components at once
        /// and is keyed by a synthetic batch name, so "self" matched nothing and
        /// every member's previous assembly was referenced while the same types
        /// were being recompiled - CS0433 on '__Exports' and on the component
        /// types, every time. Defining and referencing the same type is the
        /// error; the number of names is the only thing that changed.
        /// </summary>
        private readonly HashSet<string> s_definingNow = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        /// <summary>True when <paramref name="name"/> is being rebuilt by the
        /// compile in flight and must not be cross-referenced.</summary>
        private bool IsDefinedByThisCompile(string name, string componentName) =>
            s_definingNow.Contains(name)
            || name.Equals(componentName, StringComparison.OrdinalIgnoreCase);

        // Components that are genuinely new (not in any pre-existing assembly).
        // Only these need cross-references; existing components would cause CS0433.
        private readonly HashSet<string> _genuinelyNewComponents = new(
            StringComparer.OrdinalIgnoreCase
        );

        private bool _initialized;
        private string _initError;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Paths of all HMR-compiled assemblies currently on disk, keyed by component name.
        /// Used by the controller to know when new components become available.
        /// </summary>
        public IReadOnlyDictionary<string, string> HmrAssemblyPaths => _hmrAssemblyPaths;

        public bool TryInitialize(out string error)
        {
            if (_initialized)
            {
                error = _initError;
                return _initError == null;
            }
            _initialized = true;

            try
            {
                LoadLanguageDll();
                CacheReflectionHandles();
                FindCompilerPaths();
                BuildReferenceList();
                _tempDir = Path.Combine(Path.GetTempPath(), "UitkxHmr");
                Directory.CreateDirectory(_tempDir);

                // Clean up stale DLLs from previous sessions (may have been
                // locked by LoadFrom and couldn't be deleted on Dispose)
                CleanStaleTempFiles();

                // Try to load Roslyn for in-process compilation (non-fatal)
                TryLoadRoslyn();

                error = null;
                _initError = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"[HMR] Compiler init failed: {ex.Message}";
                _initError = error;
                Debug.LogError(error);
                return false;
            }
        }

        /// <summary>
        /// Compile a .uitkx file and return the loaded assembly + component name.
        /// </summary>
        public HmrCompileResult Compile(string uitkxPath, string[] companionCsFiles = null)
        {
            _directivesThisCompile.Clear();
            var result = new HmrCompileResult();
            var sw = Stopwatch.StartNew();
            PublishSourceOverlay();

            try
            {
                string source = ReadUitkxText(uitkxPath);

                // ── 1. Parse directives ──────────────────────────────────────
                var stepSw = Stopwatch.StartNew();
                var diagList = CreateDiagnosticList();
                var directives = InvokeWithDefaults(
                    _directiveParse,
                    null,
                    source,
                    uitkxPath,
                    diagList,
                    true
                );

                if (directives == null)
                {
                    result.Error = "DirectiveParser returned null";
                    return result;
                }

                string componentName = (string)GetProp(directives, "ComponentName");
                // EFFECTIVE namespace, not the raw parsed value: raw is a parser default
                // for stamp-less files, and everything downstream that resolves types by
                // this value (swappers matching Type.FullName, controller diagnostics)
                // must aim at the namespace the REAL build emitted into.
                string ns = ComputeEffectiveNs(directives, uitkxPath);
                result.Namespace = ns ?? string.Empty;

                if (string.IsNullOrEmpty(componentName))
                {
                    // ── Hook/module file path ────────────────────────────────
                    return CompileHookModuleFile(directives, diagList, uitkxPath, sw, result);
                }

                result.ComponentName = componentName;

                // ── 2. Parse AST ─────────────────────────────────────────────
                // All 6 args explicit: source, filePath, directives, diagnostics,
                // validateSingleRoot=false, lineOffset=0. Passing 0 explicitly
                // (instead of letting InvokeWithDefaults pad it) silences the
                // drift warning while preserving the correct freestanding-file
                // semantics. If UitkxParser.Parse ever gains a 7th param, the
                // drift warning fires loud-and-clear so HMR can be updated.
                var astNodes = InvokeWithDefaults(
                    _uitkxParse,
                    null,
                    source,
                    uitkxPath,
                    directives,
                    diagList,
                    false,
                    0
                );
                stepSw.Stop();
                result.ParseMs = stepSw.Elapsed.TotalMilliseconds;

                // H-01: never emit from an error-recovered AST — see TryGetParseErrorMessage.
                if (TryGetParseErrorMessage(diagList, uitkxPath, out string parseErrorMsg))
                {
                    result.Error = parseErrorMsg;
                    return result;
                }

                // ── 3. Canonical lowering + Emit C# ──────────────────────────
                stepSw = Stopwatch.StartNew();
                var lowered = InvokeWithDefaults(
                    _canonicalLower,
                    null,
                    directives,
                    astNodes,
                    uitkxPath
                );

                // Build a delegate that can parse standalone JSX fragments.
                // Used by the emitter to splice embedded JSX in setup code.
                HmrCSharpEmitter.MarkupParseFunc parseMarkup = (jsxText, path, startLine) =>
                    ParseMarkupFragment(jsxText, path, startLine);

                // Phase 1: scanner delegates so the emitter can detect JSX
                // literals embedded inside arbitrary C# expressions
                // (ternaries, lambdas, attribute values, etc.). When the
                // language-lib build pre-dates Phase 1 these resolve to null
                // and the emitter falls back to non-splicing behavior.
                HmrCSharpEmitter.FindJsxRangesFunc findJsxBlockRanges =
                    _findJsxBlockRanges == null
                        ? null
                        : (src, s, e) =>
                            (System.Collections.IEnumerable)
                                _findJsxBlockRanges.Invoke(null, new object[] { src, s, e });
                HmrCSharpEmitter.FindJsxRangesFunc findBareJsxRanges =
                    _findBareJsxRanges == null
                        ? null
                        : (src, s, e) =>
                            (System.Collections.IEnumerable)
                                _findBareJsxRanges.Invoke(null, new object[] { src, s, e });

                // Phase 1.5: LHS walker for `cond && <Tag/>` desugar. Same
                // null-fallback pattern as the range scanners above.
                HmrCSharpEmitter.FindLhsStartFunc findLhsStartForLogicalAnd =
                    _findLhsStartForLogicalAnd == null
                        ? null
                        : (src, ss, ae) =>
                            (int)
                                _findLhsStartForLogicalAnd.Invoke(
                                    null,
                                    new object[] { src, ss, ae }
                                );

                // Effective namespace + the self family key the emitted ModuleInitializer will
                // Register under — EXACTLY HmrCSharpEmitter's selfKey rule (ComputeEffectiveNs
                // never returns null, so the emitter's `_ns` is always this value). Carried on the
                // result so the controller can DIAGNOSE a zero-swap (key mismatch vs not-mounted).
                // `ns` above is already the same effective value; alias for readability.
                string effectiveNs = ns;
                result.FamilyKey = string.IsNullOrEmpty(effectiveNs)
                    ? componentName
                    : effectiveNs + "." + componentName;

                string csharp = HmrCSharpEmitter.Emit(
                    directives,
                    lowered,
                    uitkxPath,
                    parseMarkup,
                    findJsxBlockRanges,
                    findBareJsxRanges,
                    findLhsStartForLogicalAnd,
                    effectiveNs,
                    BuildHookFamilyKeyMap(directives, uitkxPath),
                    BuildComponentFqnMap(directives, uitkxPath),
                    InvokeTagMap(_starImportNamespacesFn, directives, uitkxPath),
                    InvokeTagMap(_importAliasTypeMapFn, directives, uitkxPath)
                );
                stepSw.Stop();
                result.EmitMs = stepSw.Elapsed.TotalMilliseconds;

                // ── 4. Compile ───────────────────────────────────────────────
                stepSw = Stopwatch.StartNew();
                var sources = new List<string> { csharp };

                // Inline hot copies of member files this compile cannot otherwise
                // reference (mid-session created/renamed import targets and same-stem
                // companions).
                EmitCompanionUitkxSources(directives, uitkxPath, componentName, sources);

                // §6.2/§6.3 parity via the shared language-lib ImportScopeFacts: cross-folder
                // imported hook containers PLUS type aliases for imported modules/components that
                // live in another (effective) namespace — exactly what the SG injects, so a
                // hot-swapped unit sees the same scope the real build does. Without this, a
                // component whose C# body references an imported module (`SidebarItem`) or an
                // imported component's type (`MetricDisplay.MetricType`) compiles in the full
                // build but fails the HMR compile with CS0246 the moment namespaces are
                // path-derived. Optional handle: an older Language.dll simply skips (pre-0.8.1
                // behavior); same-folder companions are already inlined above (same namespace →
                // the helper's same-ns guard skips them too).
                sources[0] = WithImportScopeUsings(sources[0], directives, uitkxPath, ns);

                // New-mode component files (ES-modules campaign, U-02): the file's own members
                // (values/utils/hooks) and any imported-member bridges live on {ns}.__Exports.
                // Referencing the PROJECT assembly's container is not enough (audit H1/M2):
                // non-exported members are `internal` there (cross-assembly CS0122 on every hot
                // edit of the mainline component+private-value pattern), and a member edited in
                // the same save would stay STALE. So emit a HOT copy of the file's __Exports
                // into this compilation — the same-assembly precedent legacy companion inlining
                // set — and bind the component to it bare-name (the source-defined type shadows
                // the project one; CS0436 is warning-tier).
                {
                    if (!string.IsNullOrEmpty(ns))
                    {
                        bool hasMembers = GetItems(GetProp(directives, "MemberDeclarations")).Count > 0;
                        bool hasAliasedImports = false;
                        if (!hasMembers)
                            foreach (var imp in GetItems(GetProp(directives, "Imports")))
                            {
                                if (GetProp(imp, "IsDefault") is bool idf && idf) { hasAliasedImports = true; break; }
                                foreach (var a in GetItems(GetProp(imp, "Aliases")))
                                    if (a != null) { hasAliasedImports = true; break; }
                                if (hasAliasedImports) break;
                            }
                        if (hasMembers || hasAliasedImports)
                        {
                            string ownExports = HmrHookEmitter.EmitExports(
                                directives, uitkxPath,
                                effectiveNs: ns,
                                hookKeyMap: BuildHookFamilyKeyMap(directives, uitkxPath),
                                bridgeLines: ComputeBridgeLines(directives, uitkxPath));
                            // The using is only valid when the hot unit actually CARRIES a
                            // __Exports (members or real bridges) — a same-name default import
                            // produces neither, and a dangling using is CS0234 (field find;
                            // mirrors the SG's emitsOwnExports gate).
                            if (!string.IsNullOrEmpty(ownExports))
                            {
                                if (_importScopePayloads != null)
                                {
                                    try
                                    {
                                        var ownPayloads = _importScopePayloads.Invoke(
                                            null, new object[] { directives, uitkxPath }) as System.Collections.IEnumerable;
                                        if (ownPayloads != null)
                                        {
                                            var ownLines = new List<string>();
                                            foreach (object p in ownPayloads)
                                                if (p is string payload && payload.Length > 0)
                                                    ownLines.Add(payload);
                                            if (ownLines.Count > 0)
                                                ownExports = InjectUsings(
                                                    ownExports, ns, ownLines);
                                        }
                                    }
                                    catch { }
                                }
                                sources.Add(ownExports);
                                sources[0] = InjectUsings(
                                    sources[0], ns,
                                    new List<string> { $"static {ns}.__Exports" });
                            }
                        }
                    }
                }

                if (companionCsFiles != null)
                {
                    foreach (var csFile in companionCsFiles)
                    {
                        if (File.Exists(csFile))
                            sources.Add(ReadTextWithRetry(csFile));
                    }
                }

                // ── Rank 2: pick up new .cs files anywhere in the asmdef ─────
                // When the user adds a fresh helper .cs (not in the same folder
                // as the .uitkx) and references it before Unity recompiles, the
                // helper is invisible to HMR by default. NewCsFileDiscovery
                // scans the asmdef for .cs files newer than the project DLL's
                // mtime whose primary type-name is not yet in AppDomain, and
                // adds them as additional source trees. Type-name dedupe avoids
                // CS0101 against the project DLL.
                TryIncludeNewAsmdefCsFiles(uitkxPath, companionCsFiles, sources);

                var asm = CompileSources(sources.ToArray(), componentName, uitkxPath, out string compileError);
                stepSw.Stop();
                result.CompileMs = stepSw.Elapsed.TotalMilliseconds;

                if (asm == null)
                {
                    result.Error = compileError;
                    return result;
                }

                result.LoadedAssembly = asm;
                result.Success = true;

                // Track whether this component is genuinely new (first-time check only).
                // Existing components (already in a project assembly) must NOT be added
                // as cross-references or they cause CS0433 duplicate-type errors.
                // FQN match (component-namespace + name) — see Issue 8 in the bug plan.
                if (!_genuinelyNewComponents.Contains(componentName))
                    CheckIfGenuinelyNew(componentName, ns);
            }
            catch (Exception ex)
            {
                result.IsInfrastructureError = IsInfrastructureException(ex);
                result.Error = ex is TargetInvocationException tie
                    ? $"{tie.InnerException?.GetType().Name}: {tie.InnerException?.Message ?? ex.Message}\n{tie.InnerException?.StackTrace}"
                    : $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            }
            finally
            {
                sw.Stop();
                result.TotalMs = sw.Elapsed.TotalMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// Per-file build artifacts collected by <see cref="BuildComponentArtifacts"/>.
        /// </summary>
        private sealed class ComponentBuildArtifacts
        {
            public string UitkxPath;
            public string ComponentName;
            public string Namespace;
            public string FullyQualifiedName;
            public string EmittedComponentSource;
            public List<string> CompanionUitkxSources = new();
            public List<string> CompanionCsSources = new();
            public HashSet<string> CompanionUitkxPathsConsumed = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );
            public double ParseMs;
            public double EmitMs;
            public string Error;
        }

        /// <summary>
        /// Parse + lower + emit a single component .uitkx file into a build
        /// artifact bundle. Mirrors the per-file emit work that <see cref="Compile"/>
        /// inlines, factored out so the union-compile path can reuse it.
        /// Returns null on parse failure with <c>error</c> populated.
        ///
        /// Hook/module-only files (no <c>component</c> keyword) are out of scope
        /// — callers must route them through <see cref="CompileHookModuleFile"/>.
        /// </summary>

        /// <summary>
        /// Applies the imported-scope <c>using</c> aliases to an emitted unit -
        /// the cross-folder hook containers and the type aliases for imported
        /// modules and components living in another effective namespace, exactly
        /// what the source generator injects.
        ///
        /// Shared by BOTH compile paths on purpose. It used to live inline in the
        /// single-file path only, so the UNION path emitted parents with no alias
        /// for their own children and every union compile died on
        /// <c>CS0103: The name LeftSide does not exist in the current context</c> -
        /// then fell back to per-file, which is why the union looked like it was
        /// never running. Two emission paths that must agree is exactly the drift
        /// this repo keeps parity tests for.
        ///
        /// Returns the source unchanged when the language library is older than
        /// the handle (pre-0.8.1 behaviour) or anything throws: a missing alias
        /// degrades to the previous compile error, never to a crash.
        /// </summary>
        private string WithImportScopeUsings(
            string emitted, object directives, string uitkxPath, string ns)
        {
            if (_importScopePayloads == null || string.IsNullOrEmpty(emitted))
                return emitted;
            try
            {
                var payloads = _importScopePayloads.Invoke(
                    null, new object[] { directives, uitkxPath }) as System.Collections.IEnumerable;
                if (payloads == null)
                    return emitted;
                var already = new HashSet<string>(System.StringComparer.Ordinal);
                var aliasLines = new List<string>();
                foreach (object p in payloads)
                {
                    if (p is string payload && payload.Length > 0 && already.Add(payload))
                        aliasLines.Add(payload);
                }
                return aliasLines.Count > 0 ? InjectUsings(emitted, ns, aliasLines) : emitted;
            }
            catch
            {
                // Graceful: no injected import scope - matches pre-0.8.1 HMR behavior.
                return emitted;
            }
        }

        private ComponentBuildArtifacts BuildComponentArtifacts(
            string uitkxPath,
            string[] companionCsFiles,
            out string error
        )
        {
            error = null;
            var artifacts = new ComponentBuildArtifacts { UitkxPath = uitkxPath };

            try
            {
                string source = ReadUitkxText(uitkxPath);
                var stepSw = Stopwatch.StartNew();

                var diagList = CreateDiagnosticList();
                var directives = InvokeWithDefaults(
                    _directiveParse,
                    null,
                    source,
                    uitkxPath,
                    diagList,
                    true
                );
                if (directives == null)
                {
                    error = "DirectiveParser returned null";
                    return null;
                }

                string componentName = (string)GetProp(directives, "ComponentName");
                // EFFECTIVE namespace — FullyQualifiedName below drives the trampoline
                // swapper's type lookup, which must match the REAL build's FQN; the raw
                // parsed value is a parser default for stamp-less files and aims batch/
                // cascade swaps at a type that does not exist.
                string ns = ComputeEffectiveNs(directives, uitkxPath);

                if (string.IsNullOrEmpty(componentName))
                {
                    // Hook/module-only file — caller routes these separately.
                    error = "Hook/module file not eligible for union compile";
                    return null;
                }

                artifacts.ComponentName = componentName;
                artifacts.Namespace = ns ?? string.Empty;
                artifacts.FullyQualifiedName = string.IsNullOrEmpty(ns)
                    ? componentName
                    : ns + "." + componentName;

                var astNodes = InvokeWithDefaults(
                    _uitkxParse,
                    null,
                    source,
                    uitkxPath,
                    directives,
                    diagList,
                    false,
                    0
                );
                stepSw.Stop();
                artifacts.ParseMs = stepSw.Elapsed.TotalMilliseconds;

                // H-01: never emit from an error-recovered AST — see TryGetParseErrorMessage.
                if (TryGetParseErrorMessage(diagList, uitkxPath, out string parseErrorMsg))
                {
                    error = parseErrorMsg;
                    return null;
                }

                stepSw = Stopwatch.StartNew();
                var lowered = InvokeWithDefaults(
                    _canonicalLower,
                    null,
                    directives,
                    astNodes,
                    uitkxPath
                );

                // Build the parse delegates that the emitter needs for JSX
                // splice handling. Identical to the per-file path; the
                // delegates are pure functions over text + the language-lib
                // reflection handles, so sharing them across batch members is
                // safe (no per-file state inside).
                HmrCSharpEmitter.MarkupParseFunc parseMarkup = (jsxText, path, startLine) =>
                    ParseMarkupFragment(jsxText, path, startLine);
                HmrCSharpEmitter.FindJsxRangesFunc findJsxBlockRanges =
                    _findJsxBlockRanges == null
                        ? null
                        : (src, s, e) =>
                            (System.Collections.IEnumerable)
                                _findJsxBlockRanges.Invoke(null, new object[] { src, s, e });
                HmrCSharpEmitter.FindJsxRangesFunc findBareJsxRanges =
                    _findBareJsxRanges == null
                        ? null
                        : (src, s, e) =>
                            (System.Collections.IEnumerable)
                                _findBareJsxRanges.Invoke(null, new object[] { src, s, e });
                HmrCSharpEmitter.FindLhsStartFunc findLhsStartForLogicalAnd =
                    _findLhsStartForLogicalAnd == null
                        ? null
                        : (src, ss, ae) =>
                            (int)
                                _findLhsStartForLogicalAnd.Invoke(
                                    null,
                                    new object[] { src, ss, ae }
                                );

                string csharp = HmrCSharpEmitter.Emit(
                    directives,
                    lowered,
                    uitkxPath,
                    parseMarkup,
                    findJsxBlockRanges,
                    findBareJsxRanges,
                    findLhsStartForLogicalAnd,
                    ComputeEffectiveNs(directives, uitkxPath),
                    BuildHookFamilyKeyMap(directives, uitkxPath),
                    BuildComponentFqnMap(directives, uitkxPath)
                );

                // Companion .uitkx pickup. EmitCompanionUitkxSources writes into
                // the sources list; we tee into our per-file bundle and remember
                // which companion paths we consumed so the batch can dedupe
                // shared style/hook files across members.
                var companionInline = new List<string>();
                EmitCompanionUitkxSources(
                    directives,
                    uitkxPath,
                    componentName,
                    companionInline
                );

                // Track which companion paths were inlined. The companion file
                // discovery is path-based (same dir, prefix match) so the same
                // path appears once per parent component file.
                var compDir = Path.GetDirectoryName(uitkxPath);
                if (compDir != null)
                {
                    string prefix = componentName + ".";
                    // A module the builder is holding as a pending buffer can
                    // sit in a directory that does not exist on disk yet (a new
                    // component owns a fresh folder, and so does a rename). An
                    // absent directory HAS no companions - that is an answer,
                    // not a failure - but the unguarded scan threw
                    // DirectoryNotFoundException on every debounced recompile,
                    // which killed the preview and made typing crawl.
                    foreach (var file in CompanionSiblings(compDir, prefix))
                    {
                        if (!string.Equals(file, uitkxPath, StringComparison.OrdinalIgnoreCase))
                            artifacts.CompanionUitkxPathsConsumed.Add(Path.GetFullPath(file));
                    }
                }
                artifacts.CompanionUitkxSources = companionInline;

                // Same import-scope aliases the single-file path applies. Without
                // them a parent in this batch cannot name its own children.
                artifacts.EmittedComponentSource =
                    WithImportScopeUsings(csharp, directives, uitkxPath, ns);

                if (companionCsFiles != null)
                {
                    foreach (var csFile in companionCsFiles)
                    {
                        if (File.Exists(csFile))
                            artifacts.CompanionCsSources.Add(ReadTextWithRetry(csFile));
                    }
                }

                stepSw.Stop();
                artifacts.EmitMs = stepSw.Elapsed.TotalMilliseconds;
                return artifacts;
            }
            catch (Exception ex)
            {
                error = ex is TargetInvocationException tie
                    ? $"{tie.InnerException?.GetType().Name}: {tie.InnerException?.Message ?? ex.Message}"
                    : $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Public entry point for the union compile. Caller is expected to
        /// provide already-resolved component .uitkx files (companion / hook
        /// files routed individually). When the batch is empty or contains a
        /// single eligible file, falls through to <see cref="Compile"/> to
        /// preserve the well-tested single-file path.
        ///
        /// Failure semantics: on guard or compile failure the returned result
        /// has <see cref="HmrBatchCompileResult.OverallSuccess"/> = false and
        /// <see cref="HmrBatchCompileResult.FallbackReason"/> populated. The
        /// controller MUST then invoke <see cref="Compile"/> per file so the
        /// user-facing error surface (CS0117 etc.) is not swallowed by the
        /// union path. See §5.2.1 of TECH_DEBT_20_21_22_RESOLUTION_PLAN.md.
        /// </summary>
        public HmrBatchCompileResult CompileBatch(
            IReadOnlyList<string> uitkxPaths,
            IReadOnlyDictionary<string, string[]> companionCsByPath = null
        )
        {
            var result = new HmrBatchCompileResult { BatchSize = uitkxPaths?.Count ?? 0 };

            if (uitkxPaths == null || uitkxPaths.Count == 0)
            {
                result.OverallError = "Empty batch";
                return result;
            }

            // Single-file batch → reuse the existing well-tested path. No
            // union compile is needed; caller still gets a HmrBatchCompileResult
            // back with one PerFileResults entry so the controller's swap loop
            // works uniformly.
            if (uitkxPaths.Count == 1)
            {
                string only = uitkxPaths[0];
                string[] companions =
                    companionCsByPath != null && companionCsByPath.TryGetValue(only, out var c)
                        ? c
                        : null;
                var single = Compile(only, companions);
                result.PerFileResults.Add(single);
                result.OverallSuccess = single.Success;
                result.OverallError = single.Error;
                result.UnionAssembly = single.LoadedAssembly;
                result.TotalMs = single.TotalMs;
                return result;
            }

            var sw = Stopwatch.StartNew();

            try
            {
                // ── 1. Per-file build (parse + lower + emit) ───────────────
                var artifactsByPath = new Dictionary<string, ComponentBuildArtifacts>(
                    StringComparer.OrdinalIgnoreCase
                );
                foreach (var path in uitkxPaths)
                {
                    var art = BuildComponentArtifacts(
                        path,
                        companionCsByPath != null && companionCsByPath.TryGetValue(path, out var c)
                            ? c
                            : null,
                        out string artErr
                    );
                    if (art == null)
                    {
                        // One file can't be union-compiled (parse error or
                        // hook/module-only). Bail to per-file. Don't try to
                        // partially batch — the cascade semantics expect all
                        // files to participate.
                        result.FallbackReason =
                            $"BuildComponentArtifacts failed for {Path.GetFileName(path)}: {artErr}";
                        result.OverallError = result.FallbackReason;
                        return result;
                    }
                    artifactsByPath[path] = art;
                }

                // ── 2. Pre-compile guard: unique (Namespace, ComponentName) ──
                string guardError = ValidateBatchUniqueness(artifactsByPath.Values);
                if (guardError != null)
                {
                    result.FallbackReason = $"Pre-compile guard failed: {guardError}";
                    result.OverallError = result.FallbackReason;
                    return result;
                }

                // ── 3. Aggregate sources (with companion dedupe) ───────────
                var allSources = new List<string>();
                var consumedCompanionPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var consumedCompanionCsTexts = new HashSet<string>(StringComparer.Ordinal);

                foreach (var art in artifactsByPath.Values)
                {
                    allSources.Add(art.EmittedComponentSource);

                    // Dedupe shared companion .uitkx emissions (e.g. a
                    // shared theme module across two components in the
                    // batch). We track BY PATH on the parent companion
                    // file rather than by emitted text because the emit
                    // is path-bound (file headers, line directives).
                    if (
                        art.CompanionUitkxSources.Count > 0
                        && art.CompanionUitkxPathsConsumed.Count > 0
                    )
                    {
                        var newOnes = new List<string>(art.CompanionUitkxSources.Count);
                        int idx = 0;
                        foreach (var compPath in art.CompanionUitkxPathsConsumed)
                        {
                            if (idx >= art.CompanionUitkxSources.Count)
                                break;
                            if (consumedCompanionPaths.Add(compPath))
                                newOnes.Add(art.CompanionUitkxSources[idx]);
                            idx++;
                        }
                        // Defensive fallback: if the path/source order
                        // assumption above is off, fall back to text dedupe.
                        if (
                            newOnes.Count == 0
                            && art.CompanionUitkxPathsConsumed.Count
                                < art.CompanionUitkxSources.Count
                        )
                        {
                            foreach (var s in art.CompanionUitkxSources)
                                if (consumedCompanionCsTexts.Add(s))
                                    allSources.Add(s);
                        }
                        else
                        {
                            allSources.AddRange(newOnes);
                        }
                    }

                    // Companion .cs sources — dedupe by raw text (file path
                    // isn't tracked through to here, but identical companion
                    // files would produce identical reads anyway).
                    foreach (var cs in art.CompanionCsSources)
                        if (consumedCompanionCsTexts.Add(cs))
                            allSources.Add(cs);
                }

                // ── 4. Rank 2 — new asmdef .cs pickup (once per batch) ────
                // The batch is asmdef-scoped by construction (controller
                // cascades within an asmdef). Run NewCsFileDiscovery once
                // against the first member's asmdef and add the union of new
                // .cs files. Same best-effort wrapping as single-file path.
                TryIncludeNewAsmdefCsFiles(
                    uitkxPaths[0],
                    /* alreadyIncluded */null,
                    allSources
                );

                // ── 5. Roslyn compile ──────────────────────────────────────
                // Use a deterministic batch key derived from the LAST file in
                // the batch (which is the "root" save per the cascade walker's
                // dependents-first / root-last ordering).
                string rootBase = Path.GetFileNameWithoutExtension(
                    uitkxPaths[uitkxPaths.Count - 1]
                );
                string batchKey = $"batch_{rootBase}_{uitkxPaths.Count}";

                // Invalidate cached compilations for every batch member —
                // the next per-file compile must rebuild from scratch since
                // the cached SyntaxTrees belong to a different compile shape.
                foreach (var art in artifactsByPath.Values)
                {
                    _cachedCompilations.Remove(art.ComponentName);
                    _cachedSyntaxTrees.Remove(art.ComponentName);
                }

                var compileSw = Stopwatch.StartNew();
                string compileErrorLocal;
                // Declare what this compile defines, so the cross-ref loops do not
                // reference the members' own previous assemblies back into it.
                s_definingNow.Clear();
                foreach (var art in artifactsByPath.Values)
                    s_definingNow.Add(art.ComponentName);
                Assembly asm;
                try
                {
                    asm = CompileSources(
                        allSources.ToArray(), batchKey, uitkxPaths[0], out compileErrorLocal);
                }
                finally
                {
                    // Cleared unconditionally: a stale set would silently drop a
                    // legitimate cross-reference from the NEXT compile.
                    s_definingNow.Clear();
                }
                string compileError = compileErrorLocal;
                compileSw.Stop();
                double compileMs = compileSw.Elapsed.TotalMilliseconds;

                if (asm == null)
                {
                    result.OverallError =
                        $"[HMR] Union compile failed ({uitkxPaths.Count} files): {compileError}";
                    result.FallbackReason =
                        "Roslyn errors in union compile — falling back to per-file";
                    return result;
                }

                // ── 6. Post-compile guard: assembly identity ───────────────
                string postGuardError = ValidateBatchAssemblyIdentity(asm, artifactsByPath.Values);
                if (postGuardError != null)
                {
                    result.UnionAssembly = asm;
                    result.OverallError = postGuardError;
                    result.FallbackReason =
                        $"Post-compile assembly-identity guard failed: {postGuardError}";
                    Debug.LogWarning(
                        "[HMR] union-compile sanity check failed — falling back. " + postGuardError
                    );
                    return result;
                }

                // ── 7. Build per-file results ──────────────────────────────
                foreach (var path in uitkxPaths)
                {
                    var art = artifactsByPath[path];
                    var perFile = new HmrCompileResult
                    {
                        Success = true,
                        ComponentName = art.ComponentName,
                        Namespace = art.Namespace ?? string.Empty,
                        // The union path used to leave this null, so a batch build
                        // reported no family and the one diagnostic that separates
                        // "key mismatch" from "not mounted" went dark (UB-205).
                        // FullyQualifiedName is built from the same effective
                        // namespace the emitter's selfKey uses, so it IS the key.
                        FamilyKey = art.FullyQualifiedName,
                        LoadedAssembly = asm,
                        ParseMs = art.ParseMs,
                        EmitMs = art.EmitMs,
                        CompileMs = compileMs / uitkxPaths.Count,
                        TotalMs = (sw.ElapsedMilliseconds * 1.0) / uitkxPaths.Count,
                    };
                    result.PerFileResults.Add(perFile);

                    // Register the union DLL under every batch component's
                    // name so future single-file cross-refs see it.
                    bool hadOld = _hmrAssemblyPaths.TryGetValue(art.ComponentName, out var oldDll);
                    bool pathChanged = !hadOld
                        || !string.Equals(oldDll, asm.Location, StringComparison.OrdinalIgnoreCase);
                    if (pathChanged)
                    {
                        // Invalidate this member's cached cross-ref MetadataReference —
                        // it points at the OLD DLL. The single-file paths do this at
                        // ~2397/2693; the batch path used to omit it, so an importer
                        // compiled after a union recompile bound the member's stale
                        // (now-deleted) DLL — CS0117/CS0246 on added members, silent
                        // staleness on changed values, or CS0433 duplicate types.
                        _crossRefCache.Remove(art.ComponentName);
                        if (hadOld && File.Exists(oldDll))
                        {
                            try
                            {
                                File.Delete(oldDll);
                            }
                            catch
                            { /* may be locked */
                            }
                        }
                    }
                    _hmrAssemblyPaths[art.ComponentName] = asm.Location;

                    if (!_genuinelyNewComponents.Contains(art.ComponentName))
                        CheckIfGenuinelyNew(art.ComponentName, art.Namespace);
                }

                result.UnionAssembly = asm;
                result.OverallSuccess = true;

                Debug.Log($"[HMR] union: {uitkxPaths.Count} files, {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                result.OverallError = ex is TargetInvocationException tie
                    ? $"{tie.InnerException?.GetType().Name}: {tie.InnerException?.Message ?? ex.Message}"
                    : $"{ex.GetType().Name}: {ex.Message}";
                result.FallbackReason = "Union compile threw — falling back to per-file";
            }
            finally
            {
                sw.Stop();
                result.TotalMs = sw.Elapsed.TotalMilliseconds;
            }

            return result;
        }

        // ── Rank 5 — pre/post-compile guards ─────────────────────────────────

        /// <summary>
        /// Pre-compile guard: every component in the batch must own a unique
        /// (Namespace, ComponentName) tuple. Two components colliding on the
        /// FQN inside one Roslyn compile would surface as <c>CS0260</c> or
        /// <c>CS0101</c> at emit time, but bailing here gives the controller a
        /// clean signal to fall back per-file with a useful diagnostic. Pure
        /// static method — unit-testable without Roslyn/Unity dependencies.
        /// </summary>
        internal static string ValidateBatchUniquenessImpl(
            IEnumerable<(string Namespace, string ComponentName, string FilePath)> components
        )
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (ns, name, path) in components)
            {
                string fqn = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                if (seen.TryGetValue(fqn, out var firstPath))
                {
                    return $"Two batch members emit type '{fqn}': "
                        + $"'{Path.GetFileName(firstPath)}' and "
                        + $"'{Path.GetFileName(path)}'. Cannot union-compile.";
                }
                seen[fqn] = path;
            }
            return null;
        }

        private static string ValidateBatchUniqueness(
            IEnumerable<ComponentBuildArtifacts> artifacts
        )
        {
            return ValidateBatchUniquenessImpl(
                artifacts.Select(a => (a.Namespace, a.ComponentName, a.UitkxPath))
            );
        }

        /// <summary>
        /// Post-compile guard: every batch component's expected type FQN must
        /// resolve to the union assembly we just loaded. If the type isn't
        /// found (emit-time culling), or worse — points at a DIFFERENT
        /// assembly (e.g. project DLL win race), the union swap would bind
        /// the wrong delegate. Bail and let per-file flow expose the issue.
        /// </summary>
        private static string ValidateBatchAssemblyIdentity(
            Assembly unionAssembly,
            IEnumerable<ComponentBuildArtifacts> artifacts
        )
        {
            foreach (var art in artifacts)
            {
                Type t = unionAssembly.GetType(art.FullyQualifiedName, throwOnError: false);
                if (t == null)
                    return $"Type '{art.FullyQualifiedName}' missing from union assembly.";
                if (t.Assembly != unionAssembly)
                    return $"Type '{art.FullyQualifiedName}' resolved to "
                        + $"'{t.Assembly.GetName().Name}' instead of union assembly.";
            }
            return null;
        }

        /// <summary>
        /// Compiles a member-only .uitkx file (no component declaration).
        /// Uses <see cref="HmrHookEmitter.EmitExports"/> to generate the C# source and
        /// compiles it the same way as component files.
        /// </summary>
        private HmrCompileResult CompileHookModuleFile(
            object directives,
            object diagList,
            string uitkxPath,
            Stopwatch sw,
            HmrCompileResult result
        )
        {
            try
            {
                // H-01: same error-first gate as the component path — a malformed member
                // signature must not silently emit from a recovered directive set.
                if (TryGetParseErrorMessage(diagList, uitkxPath, out string parseErrorMsg))
                {
                    result.Error = parseErrorMsg;
                    return result;
                }

                result.IsHookModuleFile = true;

                // Member-only file (ES-modules campaign, U-02): every member lives on
                // the per-file __Exports container — one emit covers values (static swap),
                // utils (module-method delegate swap by name), and hooks (__{name}_body +
                // SwapHooks against container "__Exports").
                result.HookContainerClass = "__Exports";

                string exNs = ComputeEffectiveNs(directives, uitkxPath);
                // Registry identity (rename-flow field find): the literal key "__Exports"
                // collided EVERY member-only file onto one _hmrAssemblyPaths slot — each
                // member compile deleted the previous file's DLL, hijacked its cached
                // compilation, and invalidated its cross-ref. The key is the container FQN
                // {ns}.__Exports: unique per file (new-mode namespaces are file-keyed),
                // stable across edits, and directly usable as the genuinely-new probe FQN.
                // Only the REGISTRY identity changes — the container TYPE NAME stays
                // "__Exports" (SwapHooks resolves the type by HookContainerClass).
                string exKey = string.IsNullOrEmpty(exNs) ? "__Exports" : exNs + ".__Exports";
                result.ComponentName = exKey;
                var exStepSw = Stopwatch.StartNew();
                string exportsCSharp = HmrHookEmitter.EmitExports(
                    directives, uitkxPath,
                    effectiveNs: exNs,
                    hookKeyMap: BuildHookFamilyKeyMap(directives, uitkxPath),
                    bridgeLines: ComputeBridgeLines(directives, uitkxPath));
                exStepSw.Stop();
                result.EmitMs = exStepSw.Elapsed.TotalMilliseconds;

                if (string.IsNullOrEmpty(exportsCSharp))
                {
                    result.Error = "No member declarations found";
                    return result;
                }

                // Import payloads (audit H4): the SG rewrites the unit's usings through
                // ResolveInjectedUsings before ExportsEmitter runs — without the same
                // payloads here, a member body referencing an imported member/module
                // (`padding = spacing`) compiles in the full build but CS0103s on every
                // hot edit of the file. Same shared-ImportScopeFacts route as the
                // component path above.
                if (_importScopePayloads != null)
                {
                    try
                    {
                        var exPayloads = _importScopePayloads.Invoke(
                            null, new object[] { directives, uitkxPath }) as System.Collections.IEnumerable;
                        if (exPayloads != null)
                        {
                            var exLines = new List<string>();
                            foreach (object p in exPayloads)
                                if (p is string payload && payload.Length > 0)
                                    exLines.Add(payload);
                            if (exLines.Count > 0)
                                exportsCSharp = InjectUsings(
                                    exportsCSharp, exNs, exLines);
                        }
                    }
                    catch
                    {
                        // Graceful: no injected import scope — matches the component path.
                    }
                }
                bool anyHookMember = false;
                foreach (var m in GetItems(GetProp(directives, "MemberDeclarations")))
                    if (string.Equals(GetProp(m, "Kind")?.ToString(), "Hook", StringComparison.Ordinal))
                    { anyHookMember = true; break; }
                result.HasHooks = anyHookMember;

                exStepSw = Stopwatch.StartNew();
                var exAsm = CompileSources(
                    new[] { exportsCSharp }, exKey, uitkxPath, out string exCompileError);
                exStepSw.Stop();
                result.CompileMs = exStepSw.Elapsed.TotalMilliseconds;
                if (exAsm == null)
                {
                    result.Error = exCompileError;
                    return result;
                }
                result.LoadedAssembly = exAsm;

                // Value/util/hook propagation contract for member files (rename flow, c):
                // consumers bind this file's members either through the PROJECT assembly
                // ({ns}.__Exports compiled before the session → SwapModuleStatics copies
                // the hot statics onto the project type in place, no consumer recompile
                // needed) or — for files created/renamed mid-session, where no project
                // type exists — through THIS hot DLL via the cross-ref registry. The
                // designed propagation path for the latter is the controller's import
                // fan-out: each importer recompiles fresh against the LATEST DLL
                // registered under exKey and re-renders. That path only exists with the
                // registration below — without the genuinely-new mark, BuildCrossRefs
                // never hands this DLL to any importer compile.
                _memberRegistryKeysByFile[NormalizeRegistryPath(uitkxPath)] = exKey;
                // Null namespace arg: exKey IS the container FQN, so the probe FQN equals
                // the registry key (CheckIfGenuinelyNew concatenates ns + "." + name when
                // a namespace is passed, which would double-qualify here).
                if (!_genuinelyNewComponents.Contains(exKey))
                    CheckIfGenuinelyNew(exKey, null);

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.IsInfrastructureError = IsInfrastructureException(ex);
                result.Error = ex is System.Reflection.TargetInvocationException tie
                    ? $"{tie.InnerException?.GetType().Name}: {tie.InnerException?.Message ?? ex.Message}\n{tie.InnerException?.StackTrace}"
                    : $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            }
            finally
            {
                sw.Stop();
                result.TotalMs = sw.Elapsed.TotalMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// Discovers companion .uitkx files (e.g. Foo.style.uitkx, Foo.hooks.uitkx)
        /// beside the component file plus the component's file-import targets, and
        /// inlines a hot __Exports copy for any member file no referenceable assembly
        /// carries yet (mid-session created/renamed files). Cross-file member scope
        /// itself is import-driven (ImportScopeFacts payloads at the call sites).
        /// </summary>
        private void EmitCompanionUitkxSources(
            object componentDirectives,
            string uitkxPath,
            string componentName,
            List<string> sources
        )
        {
            var dir = Path.GetDirectoryName(uitkxPath);
            if (dir == null)
                return;

            // Candidate set = name-glob companions (ComponentName.*.uitkx) UNION the
            // component's file-import targets. The real build compiles EVERY .uitkx in
            // the asmdef together, so an imported module/hook file is always visible to
            // it; the hot unit must inline the same set or a module whose FILE NAME does
            // not match the component (e.g. a companion renamed to SomeOtherFile.style
            // .uitkx and consumed via `import { SomeOtherFile } from "./SomeOtherFile
            // .style"`) resolves nowhere mid-session — the real assembly has not
            // compiled it (reload locked) and the glob never picks it up. Single-level:
            // the imported file's own imports are not walked (parity with the glob,
            // which is also non-recursive).
            var candidateFiles = new List<string>();
            var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string prefix = componentName + ".";
            // Same reason as the companion scan above: a pending module may
            // have no directory on disk, which simply means no candidates.
            foreach (var f in CompanionSiblings(dir, prefix))
                if (seenCandidates.Add(Path.GetFullPath(f)))
                    candidateFiles.Add(f);
            if (componentDirectives != null && _importResolverMap != null)
            {
                try
                {
                    string importerDir = (Path.GetDirectoryName(uitkxPath) ?? uitkxPath).Replace('\\', '/');
                    string rootDir = importerDir;
                    if (_uiSourceRootDir != null)
                    {
                        try { rootDir = (_uiSourceRootDir.Invoke(null, new object[] { uitkxPath }) as string) ?? importerDir; }
                        catch { }
                    }
                    foreach (var imp in GetItems(GetProp(componentDirectives, "Imports")))
                    {
                        string specifier = (string)GetProp(imp, "Specifier");
                        if (string.IsNullOrEmpty(specifier))
                            continue;
                        string targetFile = null;
                        try
                        {
                            var args = new object[] { importerDir, specifier, rootDir, null };
                            targetFile = _importResolverMap.Invoke(null, args) as string;
                        }
                        catch { }
                        if (string.IsNullOrEmpty(targetFile) || !UitkxSourceExists(targetFile))
                            continue;
                        if (seenCandidates.Add(Path.GetFullPath(targetFile)))
                            candidateFiles.Add(targetFile);
                    }
                }
                catch { /* import-target discovery is best-effort */ }
            }

            foreach (var file in candidateFiles)
            {
                // Skip the component file itself
                if (string.Equals(
                        Path.GetFullPath(file),
                        Path.GetFullPath(uitkxPath),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    string companionSource = ReadUitkxText(file);
                    var diagList = CreateDiagnosticList();
                    var companionDir = InvokeWithDefaults(
                        _directiveParse,
                        null,
                        companionSource,
                        file,
                        diagList,
                        true
                    );
                    if (companionDir == null)
                        continue;

                    // An imported COMPONENT file is a candidate now too (import-target
                    // discovery above) but is never inlined — component types resolve via
                    // the real assembly + alias payloads.
                    if (!string.IsNullOrEmpty((string)GetProp(companionDir, "ComponentName")))
                        continue;

                    // Member-file inlining (field find — the copy-rename-to-style scenario):
                    // a file CREATED mid-session has no {ns}.__Exports in the project
                    // assembly at all (reload locked) — the injected using dangles into
                    // CS0234. Inline the target's __Exports into the hot unit ONLY while no
                    // assembly THIS compilation can reference carries the container: the
                    // project assemblies always can, and the target's own hot DLL can once
                    // it is registered genuinely-new (that is exactly the set BuildCrossRefs
                    // hands to Roslyn — always the LATEST DLL for that file). The previous
                    // gate probed the whole AppDomain, so the target's own FIRST hot compile
                    // turned inlining off permanently while nothing referenced its DLL — the
                    // rename-flow dead end (CS0234 on every importer recompile). A companion
                    // with no member declarations carries nothing to inline (its markup/type
                    // declarations resolve via the real assembly).
                    if (GetItems(GetProp(companionDir, "MemberDeclarations")).Count > 0)
                    {
                        string newModeNs = ComputeEffectiveNs(companionDir, file);
                        string exportsFqn = newModeNs + ".__Exports";
                        // An OVERLAID companion is one the caller is holding a live
                        // buffer for, and then the project assembly's copy is stale
                        // BY CONSTRUCTION - so the gate below, which asks whether the
                        // container already exists somewhere referenceable, answers
                        // the wrong question. For a style module saved once and
                        // edited ever since, that answer is always yes: the importer
                        // bound to the SAVED exports and every unsaved edit to the
                        // style was invisible in the preview, at any value, which is
                        // why it never looked like a staleness bug (UB-203).
                        //
                        // Only the builder sets an overlay; the HMR controller's own
                        // instance leaves it null and keeps the original gate.
                        bool overlaid = SourceOverlay?.Invoke(file) != null;
                        if (!string.IsNullOrEmpty(newModeNs)
                            && (overlaid
                                || (!TypeExistsInProjectAssemblies(exportsFqn)
                                    && !HotExportsAvailable(exportsFqn))))
                        {
                            string inlined = HmrHookEmitter.EmitExports(
                                companionDir, file,
                                effectiveNs: newModeNs,
                                hookKeyMap: BuildHookFamilyKeyMap(companionDir, file),
                                bridgeLines: ComputeBridgeLines(companionDir, file));
                            if (!string.IsNullOrEmpty(inlined))
                            {
                                if (_importScopePayloads != null)
                                {
                                    try
                                    {
                                        var tp = _importScopePayloads.Invoke(
                                            null, new object[] { companionDir, file }) as System.Collections.IEnumerable;
                                        if (tp != null)
                                        {
                                            var tl = new List<string>();
                                            foreach (object p in tp)
                                                if (p is string s && s.Length > 0) tl.Add(s);
                                            if (tl.Count > 0)
                                                inlined = InjectUsings(inlined, newModeNs, tl);
                                        }
                                    }
                                    catch { }
                                }
                                sources.Add(inlined);
                                Trace?.Invoke(
                                    "inlined " + Path.GetFileName(file) + " into "
                                    + Path.GetFileName(uitkxPath)
                                    + (overlaid ? " (live buffer)" : " (not in any assembly)"));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[HMR] Failed to process companion {Path.GetFileName(file)}: {ex.Message}"
                    );
                }
            }
        }

        /// <summary>
        /// Clears per-session caches without releasing the expensive Roslyn handles
        /// and MetadataReferences. Call this between HMR start/stop cycles.
        /// </summary>
        public void Reset()
        {
            _cachedCompilations.Clear();
            _cachedSyntaxTrees.Clear();
            _crossRefCache.Clear();
            _hmrAssemblyPaths.Clear();
            _memberRegistryKeysByFile.Clear();
            _genuinelyNewComponents.Clear();
            _allowedRefsByAsmdef.Clear();
            _filteredMetaRefsByAsmdef.Clear();
            _filteredRefLocsByAsmdef.Clear();
            _lastGenuineComponentCount = 0;
            _swapCounter = 0;

            // Clean up temp DLLs from this session
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                try
                {
                    foreach (var file in Directory.GetFiles(_tempDir))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        { /* locked by LoadFrom — will be cleaned next time */
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Drops the cached (incremental) compilation for the component / hook-container
        /// owned by <paramref name="uitkxPath"/> so its NEXT compile rebuilds FRESH, thereby
        /// re-applying cross-references. The import reverse-edge fan-out
        /// (<see cref="UitkxHmrController.FanOutToImporters"/>) calls this before recompiling
        /// each importer of a changed dependency: the incremental path
        /// (<see cref="TryBuildIncremental"/>) reuses a cached compilation's references
        /// verbatim, and the cache-miss signal is consumed by the FIRST importer (it
        /// repopulates <c>_crossRefCache</c>), so later importers in the same cascade would
        /// otherwise reuse a compilation bound to the dependency's stale DLL. Forcing fresh
        /// here is strictly safe — at worst it forgoes incremental reuse for this one compile,
        /// and it only fires on the (rare) dependency-change path, never on a normal
        /// single-component edit. Best-effort: a parse failure leaves the cache untouched.
        /// </summary>
        public void InvalidateCompilationForFile(string uitkxPath)
        {
            if (string.IsNullOrEmpty(uitkxPath))
                return;
            try
            {
                string source = ReadUitkxText(uitkxPath);
                var diag = CreateDiagnosticList();
                var directives = InvokeWithDefaults(_directiveParse, null, source, uitkxPath, diag, true);
                if (directives == null)
                    return;
                string componentName = (string)GetProp(directives, "ComponentName");
                if (string.IsNullOrEmpty(componentName))
                {
                    // Member-only files cache under their per-file registry key
                    // ({ns}.__Exports) — mirror of CompileHookModuleFile's exKey.
                    string invNs = ComputeEffectiveNs(directives, uitkxPath);
                    componentName = string.IsNullOrEmpty(invNs)
                        ? "__Exports" : invNs + ".__Exports";
                }
                if (!string.IsNullOrEmpty(componentName))
                {
                    _cachedCompilations.Remove(componentName);
                    _cachedSyntaxTrees.Remove(componentName);
                }
            }
            catch { /* best-effort — fresh vs incremental self-corrects on the emit result */ }
        }

        /// <summary>
        /// Drops every registration keyed by <paramref name="uitkxPath"/>'s member-file
        /// identity after the file is deleted or renamed away. Without this, a renamed
        /// member file's OLD key ({old-ns}.__Exports) would keep its DLL registered as a
        /// cross-reference for every later compile in the session. The loaded assembly
        /// itself stays (already-bound consumers keep rendering); only the registry entries
        /// and the on-disk DLL go. Keyed via <see cref="_memberRegistryKeysByFile"/> because
        /// the dead file can no longer be parsed for its namespace.
        /// </summary>
        public void EvictFileRegistration(string uitkxPath)
        {
            if (string.IsNullOrEmpty(uitkxPath))
                return;
            string norm = NormalizeRegistryPath(uitkxPath);
            if (!_memberRegistryKeysByFile.TryGetValue(norm, out string key))
                return;
            _memberRegistryKeysByFile.Remove(norm);
            _cachedCompilations.Remove(key);
            _cachedSyntaxTrees.Remove(key);
            _crossRefCache.Remove(key);
            // Removing from the genuinely-new set shifts the count against
            // _lastGenuineComponentCount, which makes TryBuildIncremental rebuild
            // dependents fresh — exactly right after a reference disappears.
            _genuinelyNewComponents.Remove(key);
            if (_hmrAssemblyPaths.TryGetValue(key, out string dll))
            {
                _hmrAssemblyPaths.Remove(key);
                try
                {
                    File.Delete(dll);
                }
                catch
                { /* may be locked by LoadFrom — cleaned on next session */
                }
            }
        }

        private static string NormalizeRegistryPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        public void Dispose()
        {
            Reset();

            _languageAsm = null;
            if (_roslynLoaded)
                AppDomain.CurrentDomain.AssemblyResolve -= RoslynAssemblyResolve;

            // Try to remove the temp directory itself (succeeds only if empty)
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                try
                {
                    Directory.Delete(_tempDir, false);
                }
                catch { }
            }
        }

        // ── Initialization helpers ────────────────────────────────────────────

        private void CleanStaleTempFiles()
        {
            try
            {
                foreach (var file in Directory.GetFiles(_tempDir))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    { /* still locked — leave for next time */
                    }
                }
            }
            catch { }
        }

        private void LoadLanguageDll()
        {
            string analyzersDir = FindAnalyzersDirectory();

            // System.Collections.Immutable is a dependency of Language.dll
            string immutablePath = Path.Combine(analyzersDir, "System.Collections.Immutable.dll");
            if (File.Exists(immutablePath))
            {
                // Only load if not already present
                bool alreadyLoaded = AppDomain
                    .CurrentDomain.GetAssemblies()
                    .Any(a => a.GetName().Name == "System.Collections.Immutable");
                if (!alreadyLoaded)
                    Assembly.LoadFrom(immutablePath);
            }

            string langPath = Path.Combine(analyzersDir, "Ruitk.Language.dll");
            if (!File.Exists(langPath))
                throw new FileNotFoundException(
                    $"Ruitk.Language.dll not found at {langPath}"
                );

            _languageAsm = Assembly.LoadFrom(langPath);
        }

        private void CacheReflectionHandles()
        {
            // DirectiveParser
            var dpType = _languageAsm.GetType("Ruitk.Language.Parser.DirectiveParser");
            if (dpType == null)
                throw new TypeLoadException("DirectiveParser not found");
            _directiveParse = dpType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static);
            if (_directiveParse == null)
                throw new MissingMethodException("DirectiveParser.Parse not found");

            // Phase 1 scanner methods (used to detect JSX literals embedded in
            // arbitrary C# expressions at HMR time, mirroring the SG path).
            // Optional — older Language.dll builds may lack these; HMR falls
            // back to pre-Phase-1 behavior (no expression splice) when missing.
            _findJsxBlockRanges = dpType.GetMethod(
                "FindJsxBlockRanges",
                BindingFlags.Public | BindingFlags.Static
            );
            _findBareJsxRanges = dpType.GetMethod(
                "FindBareJsxRanges",
                BindingFlags.Public | BindingFlags.Static
            );
            // Phase 1.5 LHS walker for `cond && <Tag/>` desugar. Same
            // optional-resolution pattern — when missing, the HMR splicer
            // falls back to emitting raw `&&` JSX (which the user's compiler
            // surfaces as CS0019 at the right line).
            _findLhsStartForLogicalAnd = dpType.GetMethod(
                "FindLhsStartForLogicalAnd",
                BindingFlags.Public | BindingFlags.Static
            );

            // §7 shared functions (optional — older Language.dll may lack them).
            // ES-modules campaign (U-01): the DLL now carries TWO Resolve overloads (3-arg
            // legacy + 4-arg mode-aware), so a bare name-only GetMethod throws
            // AmbiguousMatchException. Bind the 4-arg overload BY PARAMETER TYPES; a DLL that
            // has EffectiveNamespace but NOT the 4-arg overload is a stale committed
            // Analyzers/Ruitk.Language.dll — fail loudly (a silent 3-arg fallback would
            // put every migrated file's HMR family key in the WRONG namespace, the exact
            // split-brain class of bug the effective-namespace seam exists to prevent).
            var effNsType = _languageAsm.GetType("Ruitk.Language.EffectiveNamespace");
            if (effNsType != null)
            {
                _effectiveNamespaceResolve = effNsType.GetMethod(
                    "Resolve",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(bool), typeof(string), typeof(string), typeof(bool) },
                    modifiers: null);
                if (_effectiveNamespaceResolve == null
                    && effNsType.GetMethod(
                        "Resolve",
                        BindingFlags.Public | BindingFlags.Static,
                        binder: null,
                        types: new[] { typeof(bool), typeof(string), typeof(string) },
                        modifiers: null) != null)
                {
                    throw new MissingMethodException(
                        "EffectiveNamespace.Resolve(bool, string, string, bool) not found — the committed "
                        + "Analyzers/Ruitk.Language.dll predates the ES-modules namespace overload. "
                        + "Rebuild it with scripts/build-generator.ps1 and restart Unity.");
                }
            }
            _uiSourceRootDir = effNsType?.GetMethod("UiSourceRootDir", BindingFlags.Public | BindingFlags.Static);
            _importResolverMap = _languageAsm
                .GetType("Ruitk.Language.ImportResolver")
                ?.GetMethod("MapSpecifierToPath", BindingFlags.Public | BindingFlags.Static);
            _importScopePayloads = _languageAsm
                .GetType("Ruitk.Language.ImportScopeFacts")
                ?.GetMethod("ComputeInjectedUsingPayloads", BindingFlags.Public | BindingFlags.Static);
            // ImportScopeFacts resolves each import TARGET itself to work out the
            // using alias it implies, and it read those targets straight off disk -
            // so a module held only as an editor buffer could not be an import
            // target and no alias was emitted for it. Optional: an older
            // Language.dll simply has no such field.
            _importScopeOverlay = _languageAsm
                .GetType("Ruitk.Language.ImportScopeFacts")
                ?.GetField("SourceOverlay", BindingFlags.Public | BindingFlags.Static);
            _importedMemberBridgeLines = _languageAsm
                .GetType("Ruitk.Language.ImportScopeFacts")
                ?.GetMethod("ComputeImportedMemberBridgeLines", BindingFlags.Public | BindingFlags.Static);
            _starImportNamespacesFn = _languageAsm
                .GetType("Ruitk.Language.ImportScopeFacts")
                ?.GetMethod("ComputeStarImportNamespaces", BindingFlags.Public | BindingFlags.Static);
            _importAliasTypeMapFn = _languageAsm
                .GetType("Ruitk.Language.ImportScopeFacts")
                ?.GetMethod("ComputeImportAliasTypeMap", BindingFlags.Public | BindingFlags.Static);

            // ParseDiagnostic type (for creating List<ParseDiagnostic>)
            _parseDiagnosticType = _languageAsm.GetType("Ruitk.Language.ParseDiagnostic");
            if (_parseDiagnosticType == null)
                throw new TypeLoadException("ParseDiagnostic type not found");

            // UitkxParser
            var upType = _languageAsm.GetType("Ruitk.Language.Parser.UitkxParser");
            if (upType == null)
                throw new TypeLoadException("UitkxParser not found");
            _uitkxParse = upType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static);
            if (_uitkxParse == null)
                throw new MissingMethodException("UitkxParser.Parse not found");

            // CanonicalLowering
            var clType = _languageAsm.GetType("Ruitk.Language.Lowering.CanonicalLowering");
            if (clType == null)
                throw new TypeLoadException("CanonicalLowering not found");
            _canonicalLower = clType.GetMethod(
                "LowerToRenderRoots",
                BindingFlags.Public | BindingFlags.Static
            );
            if (_canonicalLower == null)
                throw new MissingMethodException("CanonicalLowering.LowerToRenderRoots not found");

            // H-04: UitkxParser.ParseFragment (standalone JSX snippet parsing) replaced
            // the fragile synthetic-header-prepend trick previously used to parse
            // embedded JSX fragments. Required since 0.16.0: the legacy synthetic header
            // used the removed wrapper grammar, and HMR ships in the same package as the
            // committed Language.dll — a missing ParseFragment means a stale DLL, same
            // loud-failure class as the EffectiveNamespace overload check above.
            _parseFragment = upType.GetMethod("ParseFragment", BindingFlags.Public | BindingFlags.Static);
            if (_parseFragment == null)
                throw new MissingMethodException(
                    "UitkxParser.ParseFragment not found — the committed Analyzers/Ruitk.Language.dll "
                    + "predates the fragment parser. Rebuild it with scripts/build-generator.ps1 "
                    + "and restart Unity.");
        }

        private object CreateDiagnosticList()
        {
            var listType = typeof(List<>).MakeGenericType(_parseDiagnosticType);
            return Activator.CreateInstance(listType);
        }

        /// <summary>
        /// H-04: parses a standalone JSX fragment via <c>UitkxParser.ParseFragment</c>
        /// (required — bind time fails loudly on a stale Language.dll without it).
        /// </summary>
        private IList ParseMarkupFragment(string jsxText, string path, int startLine)
        {
            var diags = CreateDiagnosticList();
            var nodes = InvokeWithDefaults(_parseFragment, null, jsxText, path, startLine, diags);
            return GetItems(nodes);
        }

        /// <summary>
        /// H-01: HMR previously parsed a file, filled <c>diagList</c> with whatever
        /// DirectiveParser/UitkxParser reported, and then emitted C# from the
        /// error-recovered AST regardless of severity — unlike the SourceGenerator
        /// pipeline (<c>UitkxPipeline.cs</c>), which converts every Error into a
        /// <c>#line</c>/<c>#error</c> and stops. A save with a syntax error would
        /// either produce a cryptic csc wall from the recovered AST's garbage output,
        /// or — worse — valid C# that hot-swapped the WRONG UI silently. This gate
        /// mirrors the SG pipeline's error-first policy for HMR.
        /// </summary>
        /// <returns>
        /// <c>true</c> and a formatted, human-readable message (one line per error,
        /// each "  CODE Lline: message") when <paramref name="diagList"/> contains at
        /// least one <c>ParseSeverity.Error</c> entry; otherwise <c>false</c>.
        /// </returns>
        private bool TryGetParseErrorMessage(object diagList, string uitkxPath, out string errorMessage)
        {
            errorMessage = null;
            if (diagList is not IEnumerable enumerable)
                return false;

            var lines = new List<string>();
            foreach (var diag in enumerable)
            {
                var severity = GetProp(diag, "Severity");
                if (severity == null)
                    continue;
                // ParseSeverity.Error == 0 (first enum member — see ParseDiagnostic.cs).
                if (Convert.ToInt32(severity) != 0)
                    continue;

                string code = (GetProp(diag, "Code") as string) ?? "";
                int line = Convert.ToInt32(GetProp(diag, "SourceLine") ?? 0);
                string message = (GetProp(diag, "Message") as string) ?? "";
                lines.Add($"  {code} L{line}: {message}");
            }

            if (lines.Count == 0)
                return false;

            errorMessage =
                $"[HMR] {uitkxPath} has {lines.Count} parse error(s):\n" + string.Join("\n", lines);
            return true;
        }

        private void BuildReferenceList()
        {
            _referenceLocations = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location))
                    continue;
                if (!seen.Add(asm.Location))
                    continue;
                if (!File.Exists(asm.Location))
                    continue;
                _referenceLocations.Add(asm.Location);
            }
        }

        // ── Per-asmdef reference filtering ────────────────────────────────
        //
        // Returns the full set of DLL paths (case-insensitive, normalized)
        // that the target asmdef is allowed to reference, computed from
        // UnityEditor.Compilation.CompilationPipeline. Includes the asmdef's
        // own output DLL so types defined in that asmdef but not part of the
        // HMR compilation unit still resolve.
        //
        // Returns null when the asmdef is unknown to Unity or when the
        // pipeline API fails — caller must fall back to the unfiltered
        // reference list (preserving pre-fix behavior in degraded cases).
        private HashSet<string> GetAllowedRefsForAsmdef(string asmdefName)
        {
            if (string.IsNullOrEmpty(asmdefName))
                return null;
            if (_allowedRefsByAsmdef.TryGetValue(asmdefName, out var cached))
                return cached;

            HashSet<string> allowed = null;
            try
            {
                var asms = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(
                    UnityEditor.Compilation.AssembliesType.Editor
                );

                UnityEditor.Compilation.Assembly target = null;
                if (asms != null)
                {
                    for (int i = 0; i < asms.Length; i++)
                    {
                        if (
                            asms[i] != null
                            && string.Equals(asms[i].name, asmdefName, StringComparison.Ordinal)
                        )
                        {
                            target = asms[i];
                            break;
                        }
                    }
                }

                if (target != null)
                {
                    allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (!string.IsNullOrEmpty(target.outputPath))
                        allowed.Add(NormalizePath(target.outputPath));

                    if (target.allReferences != null)
                    {
                        for (int i = 0; i < target.allReferences.Length; i++)
                        {
                            var r = target.allReferences[i];
                            if (string.IsNullOrEmpty(r))
                                continue;
                            allowed.Add(NormalizePath(r));
                        }
                    }

                    // ── CS0433 shadow-rule ─────────────────────────────────
                    // Normal Unity compile of asmdef X has X's types as
                    // in-source (current compilation) and ref DLLs as
                    // references; Roslyn silently prefers current-compilation
                    // types over referenced ones, so duplicate FQNs across
                    // X.dll and a referenced DLL never raise CS0433 in normal
                    // compile.
                    //
                    // HMR compiles only the changed .uitkx into a fresh tiny
                    // assembly and references X.dll itself — so the duplicate
                    // suddenly lives across two referenced DLLs and CS0433
                    // fires. To preserve parity with normal compile we drop
                    // any non-owning ref whose public type FQN set intersects
                    // the owning DLL's. The owning asmdef wins, matching the
                    // same-assembly-shadows-referenced rule.
                    if (!string.IsNullOrEmpty(target.outputPath))
                    {
                        string ownPath = NormalizePath(target.outputPath);
                        var ownTypes = ReadPublicTypeFqns(ownPath);
                        if (ownTypes != null && ownTypes.Count > 0)
                        {
                            var toRemove = new List<string>();
                            foreach (var path in allowed)
                            {
                                if (string.Equals(path, ownPath, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                var refTypes = ReadPublicTypeFqns(path);
                                if (refTypes == null || refTypes.Count == 0)
                                    continue;
                                bool collides = false;
                                foreach (var t in refTypes)
                                {
                                    if (ownTypes.Contains(t)) { collides = true; break; }
                                }
                                if (collides)
                                    toRemove.Add(path);
                            }
                            foreach (var r in toRemove)
                            {
                                allowed.Remove(r);
                                Debug.Log(
                                    $"[HMR] Excluding reference '{SafeFileName(r)}' from asmdef "
                                    + $"'{asmdefName}' compile — public type FQN conflict with "
                                    + $"owning DLL (mirrors Unity normal-compile shadowing)."
                                );
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[HMR] Could not compute allowed references for asmdef '{asmdefName}' "
                        + $"via CompilationPipeline; falling back to unfiltered references. "
                        + $"({ex.Message})"
                );
            }

            _allowedRefsByAsmdef[asmdefName] = allowed; // null => no filtering
            return allowed;
        }

        private static string NormalizePath(string p)
        {
            try
            {
                return Path.GetFullPath(p);
            }
            catch
            {
                return p;
            }
        }

        // Returns the per-asmdef MetadataReference[] for an HMR compilation
        // whose source file lives at <paramref name="uitkxPath"/>. Mirrors
        // exactly the reference set Unity itself uses to compile the owning
        // asmdef (CompilationPipeline.GetAssemblies(...).allReferences plus
        // the asmdef's own output DLL). Falls back to the unfiltered
        // _metadataReferences when the asmdef is unknown to Unity.
        //
        // CRITICAL: we materialize MetadataReferences directly from Unity's
        // allReferences paths rather than intersecting with
        // AppDomain.CurrentDomain.GetAssemblies() locations, because the
        // AppDomain location for core BCL DLLs (mscorlib at
        // MonoBleedingEdge/...) differs from the path Unity advertises in
        // allReferences (netstandard.dll under Data/NetStandard/...). A path
        // intersection silently drops every BCL ref and the compile fails
        // with CS0518 ("Predefined type 'System.Object' is not defined").
        private object[] GetFilteredMetaRefs(string uitkxPath)
        {
            string asmdef = AsmdefResolver.OwningAsmdefName(uitkxPath);
            if (string.IsNullOrEmpty(asmdef))
                return _metadataReferences;

            if (_filteredMetaRefsByAsmdef.TryGetValue(asmdef, out var cached))
                return cached;

            var allowed = GetAllowedRefsForAsmdef(asmdef);
            object[] result;
            if (allowed == null || allowed.Count == 0)
            {
                result = _metadataReferences;
            }
            else
            {
                var refs = new List<object>(allowed.Count);
                foreach (var loc in allowed)
                {
                    if (string.IsNullOrEmpty(loc) || !File.Exists(loc))
                        continue;
                    try
                    {
                        refs.Add(InvokeWithDefaults(_createFromFile, null, loc));
                    }
                    catch
                    {
                        // Skip assemblies Roslyn can't read (native, corrupt, etc.)
                    }
                }
                result = refs.ToArray();
            }
            _filteredMetaRefsByAsmdef[asmdef] = result;
            return result;
        }

        // Per-asmdef reference DLL paths for an external csc.dll compilation.
        // Same source-of-truth as GetFilteredMetaRefs: Unity's allReferences.
        private List<string> GetFilteredRefLocations(string uitkxPath)
        {
            string asmdef = AsmdefResolver.OwningAsmdefName(uitkxPath);
            if (string.IsNullOrEmpty(asmdef))
                return _referenceLocations;

            if (_filteredRefLocsByAsmdef.TryGetValue(asmdef, out var cached))
                return cached;

            var allowed = GetAllowedRefsForAsmdef(asmdef);
            List<string> result;
            if (allowed == null || allowed.Count == 0)
            {
                result = _referenceLocations;
            }
            else
            {
                result = new List<string>(allowed.Count);
                foreach (var loc in allowed)
                {
                    if (string.IsNullOrEmpty(loc) || !File.Exists(loc))
                        continue;
                    result.Add(loc);
                }
            }
            _filteredRefLocsByAsmdef[asmdef] = result;
            return result;
        }

        private static string SafeFileName(string p)
        {
            try { return Path.GetFileName(p); }
            catch { return p; }
        }

        // ── Public type FQN scanner (CS0433 shadow rule) ──────────────────
        // Returns the set of public top-level type FQNs exported by the
        // assembly at <paramref name="dllPath"/>. We resolve via the already-
        // loaded AppDomain assembly that matches the DLL's filename (simple
        // name) — every asmdef DLL Unity references for editor compilation
        // is already loaded into the editor's AppDomain by definition, so
        // this avoids needing System.Reflection.Metadata (which is not in
        // the editor asmdef reference closure) or Mono.Cecil.
        //
        // Returns null when the assembly cannot be located or enumerated;
        // callers MUST treat null as "could not classify" and skip filtering
        // (better to leave a ref in than wrongly drop one). Cache keyed by
        // path + assembly identity so a Unity rebuild (new Assembly instance)
        // invalidates naturally.
        private static readonly Dictionary<string, (Assembly asm, HashSet<string> fqns)> s_typeFqnCache =
            new Dictionary<string, (Assembly, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> ReadPublicTypeFqns(string dllPath)
        {
            if (string.IsNullOrEmpty(dllPath))
                return null;

            string simpleName;
            try
            {
                string fn = Path.GetFileName(dllPath);
                simpleName = fn.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? fn.Substring(0, fn.Length - 4)
                    : fn;
            }
            catch { return null; }
            if (string.IsNullOrEmpty(simpleName))
                return null;

            Assembly match = null;
            try
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < loaded.Length; i++)
                {
                    var a = loaded[i];
                    if (a == null) continue;
                    var name = a.GetName().Name;
                    if (string.Equals(name, simpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        match = a;
                        break;
                    }
                }
            }
            catch { /* fall through */ }

            if (match == null)
                return null;

            lock (s_typeFqnCache)
            {
                if (s_typeFqnCache.TryGetValue(dllPath, out var entry) && ReferenceEquals(entry.asm, match))
                    return entry.fqns;
            }

            HashSet<string> set = null;
            try
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                // GetExportedTypes() returns public top-level + public-nested.
                // Filter to top-level only (nested types cannot collide as
                // standalone identifiers in CS0433 contexts).
                var types = match.GetExportedTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    var t = types[i];
                    if (t == null || t.IsNested) continue;
                    string fqn = t.FullName;
                    if (string.IsNullOrEmpty(fqn)) continue;
                    set.Add(fqn);
                }
            }
            catch
            {
                // Reflection-load issues — leave set null so caller skips.
                set = null;
            }

            lock (s_typeFqnCache)
            {
                s_typeFqnCache[dllPath] = (match, set);
            }
            return set;
        }

        private void CheckIfGenuinelyNew(string componentName, string expectedNamespace)
        {
            // Scan loaded assemblies (excluding HMR assemblies) for a type matching
            // the component's *fully-qualified* name. If none found, the component
            // is genuinely new and needs to be added as a cross-reference for
            // dependents.
            //
            // FQN match (rather than the bare-name match used pre-fix) prevents a
            // false "exists" hit when an unrelated assembly happens to declare a
            // public type literally named e.g. `App` or `Page` — which would
            // suppress cross-reference registration and break dependents at
            // compile time. See Plans~/PRETTY_UI_HMR_BUGS.md Issue 8.
            string expectedFqn = string.IsNullOrEmpty(expectedNamespace)
                ? componentName
                : expectedNamespace + "." + componentName;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location))
                    continue;
                if (asm.GetName().Name.StartsWith("hmr_", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    foreach (var type in asm.GetExportedTypes())
                    {
                        // Match by FQN (case-sensitive — namespaces and type names
                        // are case-sensitive in C# regardless of source casing).
                        if (string.Equals(type.FullName, expectedFqn, StringComparison.Ordinal))
                            return; // exists in a pre-existing assembly — NOT new
                    }
                }
                catch { } // ReflectionTypeLoadException, etc.
            }
            _genuinelyNewComponents.Add(componentName);
        }

        // ── In-process Roslyn loading ─────────────────────────────────────────

        private void TryLoadRoslyn()
        {
            try
            {
                string nugetBase = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget",
                    "packages"
                );

                // DLLs we need to load (order matters: deps before dependents)
                var dllSpecs = new[]
                {
                    (
                        "system.runtime.compilerservices.unsafe",
                        "6.0.0",
                        "System.Runtime.CompilerServices.Unsafe"
                    ),
                    ("system.collections.immutable", "6.0.0", "System.Collections.Immutable"),
                    ("system.reflection.metadata", "5.0.0", "System.Reflection.Metadata"),
                    ("system.text.encoding.codepages", "6.0.0", "System.Text.Encoding.CodePages"),
                };

                // Register AssemblyResolve handler for version redirects
                AppDomain.CurrentDomain.AssemblyResolve += RoslynAssemblyResolve;

                foreach (var (pkg, ver, name) in dllSpecs)
                {
                    string dllPath = Path.Combine(
                        nugetBase,
                        pkg,
                        ver,
                        "lib",
                        "netstandard2.0",
                        $"{name}.dll"
                    );
                    if (!File.Exists(dllPath))
                    {
                        Debug.Log(
                            $"[HMR] Roslyn dep not found: {dllPath} — falling back to external compiler"
                        );
                        return;
                    }

                    // Skip if a compatible version is already loaded
                    var loaded = AppDomain
                        .CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == name);
                    if (loaded != null)
                    {
                        _roslynDeps[name] = loaded;
                        continue;
                    }

                    var asm = Assembly.LoadFrom(dllPath);
                    _roslynDeps[name] = asm;
                }

                // Load Roslyn Common then CSharp
                string commonPath = Path.Combine(
                    nugetBase,
                    "microsoft.codeanalysis.common",
                    "4.3.1",
                    "lib",
                    "netstandard2.0",
                    "Microsoft.CodeAnalysis.dll"
                );
                string csharpPath = Path.Combine(
                    nugetBase,
                    "microsoft.codeanalysis.csharp",
                    "4.3.1",
                    "lib",
                    "netstandard2.0",
                    "Microsoft.CodeAnalysis.CSharp.dll"
                );

                if (!File.Exists(commonPath) || !File.Exists(csharpPath))
                {
                    Debug.Log(
                        $"[HMR] Roslyn DLLs not found in NuGet cache — falling back to external compiler"
                    );
                    return;
                }

                _roslynCommonAsm = Assembly.LoadFrom(commonPath);
                _roslynDeps["Microsoft.CodeAnalysis"] = _roslynCommonAsm;

                _roslynCSharpAsm = Assembly.LoadFrom(csharpPath);
                _roslynDeps["Microsoft.CodeAnalysis.CSharp"] = _roslynCSharpAsm;

                // Cache reflection handles for the compilation API
                CacheRoslynHandles();
                BuildMetadataReferences();

                _roslynLoaded = true;
                Debug.Log("[HMR] In-process Roslyn compiler loaded successfully");
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[HMR] Failed to load in-process Roslyn, will use external compiler: {ex.Message}"
                );
                _roslynLoaded = false;
            }
        }

        private Assembly RoslynAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var requestedName = new AssemblyName(args.Name);
            if (_roslynDeps.TryGetValue(requestedName.Name, out var asm))
                return asm;
            return null;
        }

        private void CacheRoslynHandles()
        {
            // CSharpParseOptions — Default maps to the latest stable C# this Roslyn build
            // knows; the language version is pinned to Unity's just below.
            var parseOptionsType = _roslynCSharpAsm.GetType(
                "Microsoft.CodeAnalysis.CSharp.CSharpParseOptions"
            );
            var defaultProp = parseOptionsType.GetProperty(
                "Default",
                BindingFlags.Public | BindingFlags.Static
            );
            _parseOptions = defaultProp.GetValue(null);

            // Pin the SAME language version Unity compiles the project with, so the
            // hot compile cannot accept code the next real compile rejects. Default
            // maps to the newest C# this Roslyn build knows, which is strictly more
            // permissive than Unity's setting.
            var withLanguageVersion = parseOptionsType.GetMethod(
                "WithLanguageVersion",
                BindingFlags.Public | BindingFlags.Instance
            );
            var facts = _roslynCSharpAsm.GetType(
                "Microsoft.CodeAnalysis.CSharp.LanguageVersionFacts"
            );
            var tryParse = facts?.GetMethod(
                "TryParse",
                BindingFlags.Public | BindingFlags.Static
            );
            if (withLanguageVersion != null && tryParse != null)
            {
                var args = new object[] { ProjectLanguageVersion, null };
                bool parsed = false;
                try { parsed = (bool)tryParse.Invoke(null, args); }
                catch (Exception) { parsed = false; }
                if (parsed)
                    _parseOptions = withLanguageVersion.Invoke(_parseOptions, new[] { args[1] });
                else
                    Debug.LogWarning(
                        "[HMR] Could not parse language version '" + ProjectLanguageVersion
                            + "' - the in-process compile will accept newer C# than Unity does.");
            }

            // ── Define UNITY_EDITOR (and Unity's full editor-define list when
            //    available) so companion .cs `#if UNITY_EDITOR` blocks compile
            //    with the same semantics as the project's Unity-Editor build.
            string[] preprocessorSymbols = ResolveEditorPreprocessorSymbols();
            var withSymbolsMethod = parseOptionsType.GetMethod(
                "WithPreprocessorSymbols",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(IEnumerable<string>) },
                null
            );
            if (withSymbolsMethod != null)
            {
                _parseOptions = withSymbolsMethod.Invoke(
                    _parseOptions,
                    new object[] { (IEnumerable<string>)preprocessorSymbols }
                );
            }
            else
            {
                Debug.LogWarning(
                    "[HMR] Roslyn CSharpParseOptions.WithPreprocessorSymbols(IEnumerable<string>) "
                        + "not found — UNITY_EDITOR will be undefined in HMR builds, breaking the "
                        + "trampoline on prior HMR DLLs (brand-new components will not hot-swap "
                        + "until a domain reload)."
                );
            }

            // CSharpSyntaxTree.ParseText(string text, CSharpParseOptions options, ...)
            // Roslyn ships multiple overloads of ParseText. The canonical one we want
            // has all-optional tail (so we can pass just the text + parse options) —
            // a non-deterministic First() pick can land on a (string,string,...)
            // overload that breaks our call shape. PickAllOptionalTailOverload makes
            // the discovery deterministic across Roslyn versions.
            var syntaxTreeType = _roslynCSharpAsm.GetType(
                "Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree"
            );
            _parseText = PickAllOptionalTailOverload(
                syntaxTreeType,
                "ParseText",
                typeof(string),
                BindingFlags.Public | BindingFlags.Static
            );
            RegisterSilentDrift(_parseText);

            // CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullable, optimize, ...)
            var outputKindType = _roslynCommonAsm.GetType("Microsoft.CodeAnalysis.OutputKind");
            object dllOutputKind = Enum.ToObject(outputKindType, 2); // DynamicallyLinkedLibrary = 2

            var compOptsType = _roslynCSharpAsm.GetType(
                "Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions"
            );
            // The constructor has ~20 optional parameters; find it by first param being OutputKind
            var compOptsCtor = compOptsType
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .First(c =>
                    c.GetParameters().Length > 0
                    && c.GetParameters()[0].ParameterType == outputKindType
                );
            var ctorParams = compOptsCtor.GetParameters();
            var ctorArgs = new object[ctorParams.Length];
            ctorArgs[0] = dllOutputKind;
            for (int i = 1; i < ctorParams.Length; i++)
            {
                if (ctorParams[i].HasDefaultValue)
                {
                    var def = ctorParams[i].DefaultValue;
                    ctorArgs[i] = def is System.DBNull ? null : def;
                }
                else
                {
                    ctorArgs[i] = ctorParams[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(ctorParams[i].ParameterType)
                        : null;
                }
            }
            _compilationOptions = compOptsCtor.Invoke(ctorArgs);

            // Enable nullable: WithNullableContextOptions(NullableContextOptions.Enable)
            var nullableType = _roslynCommonAsm.GetType(
                "Microsoft.CodeAnalysis.NullableContextOptions"
            );
            if (nullableType != null)
            {
                object enableVal = Enum.ToObject(nullableType, 2); // Enable = 2
                var withNullable = compOptsType.GetMethod("WithNullableContextOptions");
                if (withNullable != null)
                    _compilationOptions = withNullable.Invoke(
                        _compilationOptions,
                        new[] { enableVal }
                    );
            }

            // Enable optimizations
            var optimizationLevel = _roslynCommonAsm.GetType(
                "Microsoft.CodeAnalysis.OptimizationLevel"
            );
            if (optimizationLevel != null)
            {
                object releaseVal = Enum.ToObject(optimizationLevel, 1); // Release = 1
                var withOpt = compOptsType.GetMethod("WithOptimizationLevel");
                if (withOpt != null)
                    _compilationOptions = withOpt.Invoke(_compilationOptions, new[] { releaseVal });
            }

            // CSharpCompilation.Create(string asmName, IEnumerable<SyntaxTree>?,
            //                          IEnumerable<MetadataReference>?, CSharpCompilationOptions?)
            // The 4-arg form is the canonical all-optional-tail overload today; using
            // the picker keeps us safe if Roslyn ever ships a sibling 4-arg shape.
            var compilationType = _roslynCSharpAsm.GetType(
                "Microsoft.CodeAnalysis.CSharp.CSharpCompilation"
            );
            _compilationCreate = PickAllOptionalTailOverload(
                compilationType,
                "Create",
                typeof(string),
                BindingFlags.Public | BindingFlags.Static
            );
            RegisterSilentDrift(_compilationCreate);

            // MetadataReference.CreateFromFile(string path, MetadataReferenceProperties, DocumentationProvider)
            var metaRefType = _roslynCommonAsm.GetType("Microsoft.CodeAnalysis.MetadataReference");
            _createFromFile = PickAllOptionalTailOverload(
                metaRefType,
                "CreateFromFile",
                typeof(string),
                BindingFlags.Public | BindingFlags.Static
            );
            RegisterSilentDrift(_createFromFile);

            // Compilation.Emit(Stream peStream, Stream pdbStream = null, ...) —
            // critical: Roslyn has multiple Emit overloads where param[1] is
            // a non-defaulted Stream (e.g. metadataPEStream variants). The old
            // First() pick was non-deterministic across runtimes; that's the
            // root cause of the Issue 3 "missing required argument 'pdbStream'"
            // failure. PickAllOptionalTailOverload guarantees we pick the
            // canonical overload where pdbStream and everything after it
            // have compile-time defaults.
            var baseCompilationType = _roslynCommonAsm.GetType(
                "Microsoft.CodeAnalysis.Compilation"
            );
            _emitToStream = PickAllOptionalTailOverload(
                baseCompilationType,
                "Emit",
                typeof(Stream),
                BindingFlags.Public | BindingFlags.Instance
            );
            RegisterSilentDrift(_emitToStream);

            // Incremental compilation handles:
            // Compilation.RemoveSyntaxTrees(params SyntaxTree[])
            _compilationRemoveSyntaxTrees = baseCompilationType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name == "RemoveSyntaxTrees" && m.GetParameters().Length == 1
                );

            // Compilation.AddSyntaxTrees(params SyntaxTree[])
            _compilationAddSyntaxTrees = baseCompilationType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "AddSyntaxTrees" && m.GetParameters().Length == 1);

            // Compilation.AddReferences(params MetadataReference[])
            _compilationAddReferences = baseCompilationType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "AddReferences" && m.GetParameters().Length == 1);

            // Compilation.WithAssemblyName(string)
            _compilationWithAssemblyName = compilationType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name == "WithAssemblyName"
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(string)
                );
        }

        private void BuildMetadataReferences()
        {
            var refs = new List<object>();
            foreach (var loc in _referenceLocations)
            {
                try
                {
                    refs.Add(InvokeWithDefaults(_createFromFile, null, loc));
                }
                catch
                {
                    // Skip assemblies Roslyn can't read (native, corrupt, etc.)
                }
            }
            _metadataReferences = refs.ToArray();
        }

        // ── Compiler discovery ────────────────────────────────────────────────

        private void FindCompilerPaths()
        {
            // Unity Editor path: EditorApplication.applicationPath → .../Unity.exe
            string editorDir = Path.GetDirectoryName(EditorApplication.applicationPath);
            string dataDir = Path.Combine(editorDir, "Data");

            _dotnetPath = Path.Combine(dataDir, "NetCoreRuntime", "dotnet.exe");
            if (!File.Exists(_dotnetPath))
            {
                // macOS/Linux fallback
                _dotnetPath = Path.Combine(dataDir, "NetCoreRuntime", "dotnet");
            }
            if (!File.Exists(_dotnetPath))
                throw new FileNotFoundException(
                    $"dotnet runtime not found at {Path.Combine(dataDir, "NetCoreRuntime")}"
                );

            _cscPath = FindBundledCsc(dataDir);
        }

        /// <summary>
        /// Locates the editor's bundled Roslyn compiler across Unity layouts.
        /// Through 6000.4 it lives in a dedicated <c>Data/DotNetSdkRoslyn</c> folder;
        /// 6000.5 removed that folder and ships Roslyn inside the full bundled .NET
        /// SDK under a VERSION-NUMBERED directory (e.g.
        /// <c>Data/DotNetSdk/sdk/8.0.318/Roslyn/bincore/csc.dll</c>), so the SDK
        /// segment is enumerated rather than hardcoded — highest version wins when
        /// several are present. The existing <c>NetCoreRuntime</c> host runs either
        /// csc (verified against 6000.5.6f1: Roslyn 4.10.0 under the bundled host).
        /// </summary>
        private static string FindBundledCsc(string dataDir)
        {
            string legacy = Path.Combine(dataDir, "DotNetSdkRoslyn", "csc.dll");
            if (File.Exists(legacy))
                return legacy;

            string sdkRoot = Path.Combine(dataDir, "DotNetSdk", "sdk");
            if (Directory.Exists(sdkRoot))
            {
                string bestCsc = null;
                Version bestVer = null;
                foreach (string dir in Directory.GetDirectories(sdkRoot))
                {
                    string candidate = Path.Combine(dir, "Roslyn", "bincore", "csc.dll");
                    if (!File.Exists(candidate))
                        continue;
                    Version.TryParse(Path.GetFileName(dir), out Version ver);
                    if (bestCsc == null
                        || (ver != null && (bestVer == null || ver > bestVer)))
                    {
                        bestCsc = candidate;
                        bestVer = ver;
                    }
                }
                if (bestCsc != null)
                    return bestCsc;
            }

            throw new FileNotFoundException(
                "Roslyn csc.dll not found — probed "
                    + legacy
                    + " (Unity <= 6000.4 layout) and "
                    + Path.Combine(sdkRoot, "<version>", "Roslyn", "bincore", "csc.dll")
                    + " (Unity 6000.5+ layout)"
            );
        }

        // ── Compilation ───────────────────────────────────────────────────────

        // Local polyfill for ModuleInitializerAttribute. Required because the
        // HMR DLL emits `[ModuleInitializer]` (see HmrCSharpEmitter companion
        // class emission) and references the project's main asmdef which only
        // exposes the SG-emitted polyfill as `internal` -- so the HMR DLL's
        // attribute reference fails with CS0122 "inaccessible due to its
        // protection level". Adding a local copy in the HMR compilation unit
        // makes Roslyn bind the attribute to the in-compilation type (same
        // FQN) before consulting referenced assemblies, sidestepping the
        // visibility issue without changing the main-asmdef polyfill's
        // accessibility (which would risk ambiguity once Unity moves to a
        // TFM that ships the real attribute).
        //
        // Guarded by #if !NET5_0_OR_GREATER so a future Unity TFM that ships
        // the real attribute does not produce a duplicate type error.
        private const string ModuleInitializerPolyfillSource =
            "// <auto-generated — HMR ModuleInitializerAttribute polyfill />\n"
            + "#if !NET5_0_OR_GREATER\n"
            + "namespace System.Runtime.CompilerServices\n"
            + "{\n"
            + "    [global::System.AttributeUsage(global::System.AttributeTargets.Method, Inherited = false)]\n"
            + "    internal sealed class ModuleInitializerAttribute : global::System.Attribute { }\n"
            + "}\n"
            + "#endif\n";

        private static string[] PrependPolyfills(string[] sources)
        {
            var combined = new string[sources.Length + 1];
            combined[0] = ModuleInitializerPolyfillSource;
            System.Array.Copy(sources, 0, combined, 1, sources.Length);
            return combined;
        }

        private Assembly CompileSources(
            string[] sources,
            string componentName,
            string ownerUitkxPath,
            out string error
        )
        {
            _swapCounter++;
            error = null;

            // Inject the ModuleInitializerAttribute polyfill into every HMR
            // compile so `[ModuleInitializer]` resolves against an in-compilation
            // type (visible from this assembly) instead of the referenced main
            // asmdef's `internal` copy. See ModuleInitializerPolyfillSource for
            // the full rationale.
            sources = PrependPolyfills(sources);

            // ── Fast path: in-process Roslyn ──────────────────────────────────
            if (_roslynLoaded)
            {
                var asm = InProcessCompile(sources, componentName, ownerUitkxPath, out error);
                if (asm != null || error == null)
                    return asm;
                // Infrastructure failures (the exception path, e.g. the NuGet-cache
                // Roslyn binding against a BCL Unity already loaded — MissingMethod
                // on 6000.5's newer System.Reflection.Metadata) are permanent for
                // the session: latch off in-process so every subsequent save goes
                // straight to external csc without re-failing + re-warning.
                if (error.Contains("In-process Roslyn error"))
                {
                    _roslynLoaded = false;
                    Debug.LogWarning(
                        "[HMR] In-process Roslyn is incompatible with this editor's loaded "
                            + $"assemblies — using the external csc for the rest of the session. ({error})"
                    );
                }
                else
                {
                    Debug.LogWarning($"[HMR] In-process compile failed, trying external: {error}");
                }
                error = null;
            }

            // ── Slow path: external dotnet csc.dll ────────────────────────────
            return ExternalCompile(sources, componentName, ownerUitkxPath, out error);
        }

        private Assembly InProcessCompile(
            string[] sources,
            string componentName,
            string ownerUitkxPath,
            out string error
        )
        {
            error = null;

            try
            {
                // Parse source texts into SyntaxTrees
                var treesList = new List<object>(sources.Length);
                foreach (var src in sources)
                {
                    var tree = InvokeWithDefaults(_parseText, null, src, _parseOptions);
                    treesList.Add(tree);
                }

                var newTrees = treesList.ToArray();

                // Build cross-component references using cache
                var crossRefs = BuildCrossRefs(componentName, out bool depRefsChanged);

                // HMR reverse-edge invalidation (plan §8), compiler half.
                // When a dependency B recompiles it removes its own entry from
                // _crossRefCache (see the DLL-path-changed branch below) and
                // re-registers a new DLL path. BuildCrossRefs then rebuilds B's
                // MetadataReference on the resulting cache miss and reports it via
                // depRefsChanged. The incremental path (TryBuildIncremental) only
                // swaps syntax trees — it never re-applies references — so a cached
                // compilation for THIS component would keep binding B's *stale* DLL.
                // Drop the cache here so this component rebuilds fresh against B's
                // new metadata. The controller fans a change out to its importers;
                // this guarantees each importer's recompile actually sees the change.
                // (Cold cache is unaffected: there is no cached compilation to drop.)
                if (depRefsChanged && _cachedCompilations.ContainsKey(componentName))
                {
                    _cachedCompilations.Remove(componentName);
                    _cachedSyntaxTrees.Remove(componentName);
                }

                string asmName = $"hmr_{componentName}_{_swapCounter}";

                // ── Try incremental compilation first ─────────────────────────
                object compilation = TryBuildIncremental(
                    componentName,
                    newTrees,
                    crossRefs,
                    asmName
                );

                // ── Fallback: fresh Compilation.Create ────────────────────────
                if (compilation == null)
                    compilation = BuildFreshCompilation(newTrees, crossRefs, asmName, ownerUitkxPath);

                // Emit to MemoryStream and write DLL to disk
                string outputDll = Path.Combine(
                    _tempDir,
                    $"hmr_{componentName}_{_swapCounter}.dll"
                );

                using (var ms = new MemoryStream())
                {
                    var emitResult = InvokeWithDefaults(_emitToStream, compilation, ms);

                    bool success = (bool)
                        emitResult
                            .GetType()
                            .GetProperty("Success", BindingFlags.Public | BindingFlags.Instance)
                            .GetValue(emitResult);

                    if (!success)
                    {
                        // If we used incremental and it failed, retry with a fresh build
                        if (_cachedCompilations.ContainsKey(componentName))
                        {
                            _cachedCompilations.Remove(componentName);
                            _cachedSyntaxTrees.Remove(componentName);
                            compilation = BuildFreshCompilation(newTrees, crossRefs, asmName, ownerUitkxPath);

                            ms.SetLength(0);
                            emitResult = InvokeWithDefaults(_emitToStream, compilation, ms);
                            success = (bool)
                                emitResult
                                    .GetType()
                                    .GetProperty(
                                        "Success",
                                        BindingFlags.Public | BindingFlags.Instance
                                    )
                                    .GetValue(emitResult);
                        }

                        if (!success)
                        {
                            // Extract diagnostics
                            var diagnostics = (IEnumerable)
                                emitResult
                                    .GetType()
                                    .GetProperty(
                                        "Diagnostics",
                                        BindingFlags.Public | BindingFlags.Instance
                                    )
                                    .GetValue(emitResult);

                            var errors = new List<string>();
                            foreach (var diag in diagnostics)
                            {
                                var severity = diag.GetType()
                                    .GetProperty(
                                        "Severity",
                                        BindingFlags.Public | BindingFlags.Instance
                                    )
                                    .GetValue(diag);
                                // DiagnosticSeverity.Error = 3
                                if (Convert.ToInt32(severity) == 3)
                                    errors.Add("  " + diag.ToString());
                            }

                            error =
                                errors.Count > 0
                                    ? $"[HMR] Compilation failed for {componentName}:\n{string.Join("\n", errors)}"
                                    : $"[HMR] Compilation failed for {componentName}: unknown error";
                            return null;
                        }
                    }

                    // Write directly from MemoryStream to disk (avoids byte[] copy)
                    using (var fs = new FileStream(outputDll, FileMode.Create, FileAccess.Write))
                    {
                        ms.Position = 0;
                        ms.CopyTo(fs);
                    }
                }

                // Cache the successful compilation and trees for incremental reuse
                _cachedCompilations[componentName] = compilation;
                _cachedSyntaxTrees[componentName] = newTrees;
                _lastGenuineComponentCount = _genuinelyNewComponents.Count;

                // Register on disk for cross-component references; replace previous version
                string oldDll = null;
                if (_hmrAssemblyPaths.TryGetValue(componentName, out oldDll) && oldDll != outputDll)
                {
                    // Invalidate cached cross-ref for the old DLL
                    _crossRefCache.Remove(componentName);
                    try
                    {
                        File.Delete(oldDll);
                    }
                    catch
                    { /* may be locked by LoadFrom — cleaned on next session */
                    }
                }
                _hmrAssemblyPaths[componentName] = outputDll;

                // Use LoadFrom (memory-mapped) instead of Load(byte[]) to avoid
                // copying the PE image into the managed heap
                var loadedAsm = Assembly.LoadFrom(outputDll);

                // Force every module's [ModuleInitializer] to run NOW. The CLR
                // only fires <Module>.cctor lazily on first member access from
                // the loaded module, and the downstream swap pipeline only
                // touches member-bearing /module/ types — never the synthetic
                // companion class that carries [ModuleInitializer]. Result
                // before this call: a freshly compiled component whose render
                // body never gets published to its Family (Register never
                // fires) so the parent renders the fallback placeholder and
                // the user sees nothing change on screen. See
                // ApplySuccessfulCompileResult's comment claiming
                // "the freshly compiled assembly's [ModuleInitializer] has
                // already run during the Roslyn-emit Assembly.Load above" --
                // that assumption is what this call now upholds.
                ForceRunModuleInitializers(loadedAsm);

                // Force GC to reclaim dead SyntaxTrees, EmitResult, MemoryStream etc.
                // before Mono's lazy GC decides to expand the heap
                GC.Collect(2, GCCollectionMode.Optimized);

                return loadedAsm;
            }
            catch (TargetInvocationException tie)
            {
                error =
                    $"[HMR] In-process Roslyn error: {tie.InnerException?.Message ?? tie.Message}";
                return null;
            }
            catch (Exception ex)
            {
                error = $"[HMR] In-process Roslyn error: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Build cross-component MetadataReferences using cache.
        /// Only creates new references when the underlying DLL path changes.
        /// </summary>
        private List<object> BuildCrossRefs(string componentName, out bool anyRefRebuilt)
        {
            anyRefRebuilt = false;
            var crossRefs = new List<object>();
            foreach (var kvp in _hmrAssemblyPaths)
            {
                if (IsDefinedByThisCompile(kvp.Key, componentName))
                    continue;
                if (!_genuinelyNewComponents.Contains(kvp.Key))
                    continue;
                if (!File.Exists(kvp.Value))
                    continue;

                // Check cache: keyed by component name, invalidated when DLL path changes
                if (_crossRefCache.TryGetValue(kvp.Key, out var cached))
                {
                    crossRefs.Add(cached);
                }
                else
                {
                    try
                    {
                        var metaRef = InvokeWithDefaults(_createFromFile, null, kvp.Value);
                        _crossRefCache[kvp.Key] = metaRef;
                        crossRefs.Add(metaRef);
                        // A referenced component's MetadataReference had to be rebuilt —
                        // i.e. its DLL path changed since we last cached it. Signals the
                        // caller that any cached compilation for THIS component now holds
                        // a stale reference and must be rebuilt fresh (see call site).
                        anyRefRebuilt = true;
                    }
                    catch { }
                }
            }
            return crossRefs;
        }

        /// <summary>
        /// Try to reuse a cached Compilation for this component by swapping
        /// SyntaxTrees and updating references incrementally.
        /// Returns null if incremental is not possible.
        /// </summary>
        private object TryBuildIncremental(
            string componentName,
            object[] newTrees,
            List<object> crossRefs,
            string asmName
        )
        {
            // Incremental API handles must all be available
            if (
                _compilationRemoveSyntaxTrees == null
                || _compilationAddSyntaxTrees == null
                || _compilationWithAssemblyName == null
            )
                return null;

            // Must have a cached compilation for this component
            if (!_cachedCompilations.TryGetValue(componentName, out var cached))
                return null;

            // If new cross-refs appeared since last compile, invalidate all caches
            // because every compilation needs the new reference
            if (_genuinelyNewComponents.Count != _lastGenuineComponentCount)
            {
                _cachedCompilations.Clear();
                _cachedSyntaxTrees.Clear();
                return null;
            }

            try
            {
                // Remove old syntax trees
                if (
                    _cachedSyntaxTrees.TryGetValue(componentName, out var oldTrees)
                    && oldTrees.Length > 0
                )
                {
                    var syntaxTreeBaseType = _roslynCommonAsm.GetType(
                        "Microsoft.CodeAnalysis.SyntaxTree"
                    );
                    var oldArray = Array.CreateInstance(syntaxTreeBaseType, oldTrees.Length);
                    for (int i = 0; i < oldTrees.Length; i++)
                        oldArray.SetValue(oldTrees[i], i);
                    cached = _compilationRemoveSyntaxTrees.Invoke(
                        cached,
                        new object[] { oldArray }
                    );
                }

                // Add new syntax trees
                {
                    var syntaxTreeBaseType = _roslynCommonAsm.GetType(
                        "Microsoft.CodeAnalysis.SyntaxTree"
                    );
                    var newArray = Array.CreateInstance(syntaxTreeBaseType, newTrees.Length);
                    for (int i = 0; i < newTrees.Length; i++)
                        newArray.SetValue(newTrees[i], i);
                    cached = _compilationAddSyntaxTrees.Invoke(cached, new object[] { newArray });
                }

                // Update assembly name
                cached = _compilationWithAssemblyName.Invoke(cached, new object[] { asmName });

                return cached;
            }
            catch
            {
                // Incremental failed — caller will use fresh Create
                _cachedCompilations.Remove(componentName);
                _cachedSyntaxTrees.Remove(componentName);
                return null;
            }
        }

        /// <summary>
        /// Build a fresh CSharpCompilation from scratch (original behavior).
        /// </summary>
        private object BuildFreshCompilation(
            object[] newTrees,
            List<object> crossRefs,
            string asmName,
            string ownerUitkxPath
        )
        {
            var baseRefs = GetFilteredMetaRefs(ownerUitkxPath);
            var allRefs = new List<object>(baseRefs.Length + crossRefs.Count);
            for (int i = 0; i < baseRefs.Length; i++)
                allRefs.Add(baseRefs[i]);
            allRefs.AddRange(crossRefs);

            var syntaxTreeBaseType = _roslynCommonAsm.GetType("Microsoft.CodeAnalysis.SyntaxTree");
            var treesArray = Array.CreateInstance(syntaxTreeBaseType, newTrees.Length);
            for (int i = 0; i < newTrees.Length; i++)
                treesArray.SetValue(newTrees[i], i);

            var metaRefType = _roslynCommonAsm.GetType("Microsoft.CodeAnalysis.MetadataReference");
            var refsArray = Array.CreateInstance(metaRefType, allRefs.Count);
            for (int i = 0; i < allRefs.Count; i++)
                refsArray.SetValue(allRefs[i], i);

            return _compilationCreate.Invoke(
                null,
                new object[] { asmName, treesArray, refsArray, _compilationOptions }
            );
        }

        private Assembly ExternalCompile(
            string[] sources,
            string componentName,
            string ownerUitkxPath,
            out string error
        )
        {
            error = null;

            // Write sources to temp files
            var sourceFiles = new List<string>();
            for (int i = 0; i < sources.Length; i++)
            {
                string path = Path.Combine(_tempDir, $"hmr_{componentName}_{i}.cs");
                File.WriteAllText(path, sources[i]);
                sourceFiles.Add(path);
            }

            string outputDll = Path.Combine(_tempDir, $"hmr_{componentName}_{_swapCounter}.dll");

            // Build response file for csc
            string rspPath = Path.Combine(_tempDir, $"hmr_{componentName}.rsp");
            using (var rsp = new StreamWriter(rspPath, false))
            {
                rsp.WriteLine("-target:library");
                rsp.WriteLine($"-out:\"{outputDll}\"");
                rsp.WriteLine($"-langversion:{ProjectLanguageVersion}");
                rsp.WriteLine("-nowarn:0105,0436,8600,8601,8602,8603,8604");
                rsp.WriteLine("-nullable:enable");
                rsp.WriteLine("-deterministic");
                rsp.WriteLine("-optimize+");
                // Same preprocessor symbols the in-process path sets — WITHOUT
                // them every `#if UNITY_EDITOR` region in the emitted source is
                // preprocessed OUT, including the __UitkxRefresh companion whose
                // [ModuleInitializer] publishes the new render body to its
                // Family: the hot assembly then loads and swaps delegates but no
                // fiber ever refreshes (field find: first session forced onto
                // this path by the 6000.5 in-process Roslyn breakage).
                foreach (string sym in ResolveEditorPreprocessorSymbols())
                    rsp.WriteLine($"-define:{sym}");
                foreach (var loc in GetFilteredRefLocations(ownerUitkxPath))
                    rsp.WriteLine($"-reference:\"{loc}\"");
                // Add previously HMR-compiled assemblies for cross-component resolution (new components only)
                foreach (var kvp in _hmrAssemblyPaths)
                {
                    // Skip everything this compile DEFINES - "self" for a
                    // single-file build, every member for a union.
                    if (IsDefinedByThisCompile(kvp.Key, componentName))
                        continue;
                    // Only add cross-refs for genuinely new components;
                    // existing ones are already referenced and would cause CS0433
                    if (!_genuinelyNewComponents.Contains(kvp.Key))
                        continue;
                    if (File.Exists(kvp.Value))
                        rsp.WriteLine($"-reference:\"{kvp.Value}\"");
                }
                foreach (var sf in sourceFiles)
                    rsp.WriteLine($"\"{sf}\"");
            }

            // Invoke: dotnet csc.dll @response.rsp
            var psi = new ProcessStartInfo
            {
                FileName = _dotnetPath,
                Arguments = $"exec \"{_cscPath}\" \"@{rspPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _tempDir,
            };

            string stdout,
                stderr;
            int exitCode;

            using (var proc = Process.Start(psi))
            {
                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                exitCode = proc.ExitCode;
            }

            if (exitCode != 0 || !File.Exists(outputDll))
            {
                // Parse errors from stdout (csc writes diagnostics to stdout)
                string output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
                var errorLines = output
                    .Split('\n')
                    .Where(l => l.Contains(": error "))
                    .Select(l => "  " + l.Trim())
                    .ToArray();

                error =
                    errorLines.Length > 0
                        ? $"[HMR] Compilation failed for {componentName}:\n{string.Join("\n", errorLines)}"
                        : $"[HMR] Compilation failed for {componentName}:\n{output}";
                return null;
            }

            // Register on disk for cross-component references; replace previous version
            if (
                _hmrAssemblyPaths.TryGetValue(componentName, out string oldDll)
                && oldDll != outputDll
            )
            {
                _crossRefCache.Remove(componentName);
                try
                {
                    File.Delete(oldDll);
                }
                catch
                { /* may be locked by LoadFrom */
                }
            }
            _hmrAssemblyPaths[componentName] = outputDll;

            // Use LoadFrom (memory-mapped) instead of Load(byte[])
            var loadedAsm = Assembly.LoadFrom(outputDll);

            // Same rationale as the in-process path -- force module
            // initializers to run so per-component Register calls publish
            // the new render body to its Family before the swap pipeline
            // touches the assembly.
            ForceRunModuleInitializers(loadedAsm);

            // Force GC to reclaim dead allocations before Mono expands the heap
            GC.Collect(2, GCCollectionMode.Optimized);

            return loadedAsm;
        }

        // Explicitly fire <Module>.cctor for every module in the loaded HMR
        // assembly. RuntimeHelpers.RunModuleConstructor is the documented way
        // to deterministically execute a module's initializer; the CLR
        // de-duplicates internally so calling this when the cctor already ran
        // is a no-op. Without this call, Assembly.LoadFrom defers <Module>.cctor
        // until the first reflection access that touches a member of a type
        // in the module -- which never happens for synthetic companion types
        // that exist solely to carry [ModuleInitializer] for Family.Register.
        private static void ForceRunModuleInitializers(Assembly asm)
        {
            if (asm == null) return;
            Module[] modules;
            try { modules = asm.GetModules(); }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[HMR] Could not enumerate modules of HMR assembly to force "
                    + $"ModuleInitializer execution: {ex.Message}"
                );
                return;
            }
            foreach (var mod in modules)
            {
                if (mod == null) continue;
                try
                {
                    System.Runtime.CompilerServices.RuntimeHelpers
                        .RunModuleConstructor(mod.ModuleHandle);
                }
                catch (Exception ex)
                {
                    // A ModuleInitializer throwing should surface so the user
                    // can fix it; swallow only the meta-failure of "could not
                    // run cctor" itself, which is virtually never recoverable.
                    Debug.LogWarning(
                        $"[HMR] Failed to run ModuleInitializer for '{mod.Name}': "
                        + $"{ex.Message}"
                    );
                }
            }
        }

        // ── Utility ───────────────────────────────────────────────────────────

        /// <summary>
        /// Absolute path of the package's <c>Analyzers/</c> directory (which holds
        /// <c>Ruitk.Language.dll</c>) — in EVERY install layout.
        ///
        /// <para>This used to probe only under <c>Application.dataPath</c>: it walked up a
        /// FIXED three levels from an AssetDatabase hit and then re-rooted the result at the project
        /// folder, plus two <c>Assets/</c>-only fallbacks. A UPM git-URL install (the primary channel,
        /// where the package physically lives in <c>Library/PackageCache/com.reactiveuitoolkit@&lt;hash&gt;</c>)
        /// exhausted all three probes and threw <see cref="DirectoryNotFoundException"/>, so Hot Module
        /// Reload could not work at all. It now goes through the one layout-agnostic resolver,
        /// <see cref="RuitkPackagePaths"/>.</para>
        ///
        /// <para>READ-ONLY use, deliberately: nothing in HMR writes under the package root — every
        /// emitted <c>.cs</c>/<c>.rsp</c>/<c>.dll</c> goes to <c>%TEMP%/UitkxHmr</c> (see
        /// <c>_tempDir</c>). That is what makes a read-only PackageCache install legal here.</para>
        /// </summary>
        private static string FindAnalyzersDirectory()
        {
            // Layout-agnostic: PackageInfo (UPM / embedded / file:) → asset-database sentinel
            // walk-up (Assets/, folder-name-agnostic) → legacy Assets/ReactiveUIToolkit.
            bool rootResolved = RuitkPackagePaths.TryGetRoot(out string packageRoot);
            if (rootResolved)
            {
                string analyzersDir = Path.Combine(packageRoot, "Analyzers");
                if (Directory.Exists(analyzersDir))
                    return analyzersDir;
            }

            // Rescue for an oddly-shaped tree (package root unresolvable, or present but missing its
            // Analyzers/): the pre-existing Assets-wide scan. Only ever reached on the path that would
            // otherwise throw, so it costs nothing in the happy path.
            try
            {
                foreach (
                    var candidate in Directory.GetDirectories(
                        Application.dataPath,
                        "Analyzers",
                        SearchOption.AllDirectories
                    )
                )
                {
                    if (File.Exists(Path.Combine(candidate, "Ruitk.Language.dll")))
                        return candidate;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[HMR] Assets-wide Analyzers/ scan failed: " + ex.Message
                );
            }

            throw new DirectoryNotFoundException(
                "Cannot find the Analyzers/ directory containing Ruitk.Language.dll — Hot Module "
                + "Reload cannot run without it.\n"
                + (rootResolved
                    ? "The package root resolved to '" + packageRoot + "', but '"
                      + Path.Combine(packageRoot, "Analyzers")
                      + "' does not exist and no Analyzers/ holding Ruitk.Language.dll was found under "
                      + Application.dataPath
                      + ". Analyzers/ is a must-ship folder, so the install looks incomplete."
                    : RuitkPackagePaths.FailureMessage)
            );
        }

        /// <summary>
        /// Adds using payloads to an emitted unit, INSIDE the namespace block
        /// (global::-qualified) — file-keyed namespaces make sibling file stems
        /// enclosing-namespace members, which shadow file-level using-aliases, and
        /// inside-namespace usings resolve RELATIVE to the enclosing namespaces
        /// (mirrors the SG emitters' M7 move). A unit without a resolvable namespace
        /// falls back to the file-top prepend.
        /// </summary>
        internal static string InjectUsings(
            string source, string ns, List<string> payloads)
        {
            if (payloads == null || payloads.Count == 0)
                return source;
            if (string.IsNullOrEmpty(ns))
            {
                var sbTop = new System.Text.StringBuilder();
                foreach (var p in payloads)
                    sbTop.AppendLine($"using {p};");
                return sbTop.ToString() + source;
            }
            string marker = "namespace " + ns;
            int at = source.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                int brace = source.IndexOf('{', at);
                if (brace >= 0)
                {
                    int insertAt = brace + 1;
                    var sbIns = new System.Text.StringBuilder();
                    sbIns.Append('\n');
                    foreach (var p in payloads)
                        sbIns.Append("    using ").Append(GlobalizeUsingPayload(p)).Append(";\n");
                    return source.Substring(0, insertAt) + sbIns + source.Substring(insertAt);
                }
            }
            // Fallback: no namespace marker found — file-top prepend (still legal C#).
            var sbFb = new System.Text.StringBuilder();
            foreach (var p in payloads)
                sbFb.AppendLine($"using {p};");
            return sbFb.ToString() + source;
        }

        /// <summary>Mirror of language-lib's ImportScopeFacts.GlobalizeUsingPayload (Editor/HMR
        /// cannot reference language-lib): rewrites a using payload for emission INSIDE a
        /// namespace block — inside-namespace usings resolve RELATIVE to enclosing namespaces,
        /// so payloads must be global::-qualified. Keep in lockstep (pinned by contract test).</summary>
        internal static string GlobalizeUsingPayload(string payload)
        {
            string p = (payload ?? string.Empty).Trim();
            if (p.StartsWith("static ", StringComparison.Ordinal))
            {
                string rest = p.Substring("static ".Length).TrimStart();
                return rest.StartsWith("global::", StringComparison.Ordinal)
                    ? p : "static global::" + rest;
            }
            int eq = p.IndexOf('=');
            if (eq > 0)
            {
                string left = p.Substring(0, eq).TrimEnd();
                string right = p.Substring(eq + 1).TrimStart();
                return right.StartsWith("global::", StringComparison.Ordinal)
                    ? p : left + " = global::" + right;
            }
            return p.StartsWith("global::", StringComparison.Ordinal) ? p : "global::" + p;
        }

        internal static object GetProp(object obj, string name)
        {
            if (obj == null)
                return null;
            return obj.GetType()
                .GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(obj);
        }

        internal static IList GetItems(object immutableArray)
        {
            if (immutableArray == null)
                return Array.Empty<object>();
            // A DEFAULT ImmutableArray<T> (declared but never initialized — e.g.
            // DirectiveSet.MemberDeclarations on a markup-only file) throws
            // InvalidOperationException from GetEnumerator(). Typed consumers guard with
            // IsDefaultOrEmpty; this reflective mirror must too, or the first hot-reload of a
            // plain component crashes in BuildHookFamilyKeyMap (found by RUNTIME-V testing).
            var isDefaultProp = immutableArray.GetType()
                .GetProperty("IsDefault", BindingFlags.Public | BindingFlags.Instance);
            if (isDefaultProp?.GetValue(immutableArray) is bool isDefault && isDefault)
                return Array.Empty<object>();
            // ImmutableArray<T> implements IEnumerable<T>; cast to non-generic IEnumerable
            // and materialize to list for indexed access.
            var items = new List<object>();
            foreach (var item in (IEnumerable)immutableArray)
                items.Add(item);
            return items;
        }

        // ── §7: path-qualified hook family keys (mirror of UitkxPipeline) ─────
        // HMR must emit the SAME {EffectiveNs}.{Container}::{HookName} key the source
        // generator does, because the runtime matches a producer id to a consumer key by
        // ordinal string equality — and one side may have been emitted by the SG (full
        // compile) and the other by HMR (edit). Parity is by SHARED language-lib functions
        // (EffectiveNamespace.Resolve / UiSourceRootDir, ImportResolver.MapSpecifierToPath),
        // reached by reflection, plus the container-name algorithm all worlds keep in sync.

        /// <summary>The file's effective namespace via the shared resolver; raw fallback if absent.
        /// Always FILE-keyed (isNewMode: true) — the 4-arg overload is bound by parameter types
        /// and the legacy folder-keyed mode died with the wrapper grammar (0.16.0); the flag
        /// survives on the resolver for the migration codemod's before-snapshot.</summary>
        internal string ComputeEffectiveNs(object directives, string filePath)
        {
            string rawNs = (string)GetProp(directives, "Namespace") ?? string.Empty;
            if (_effectiveNamespaceResolve == null || directives == null)
                return rawNs;
            try
            {
                bool hasExplicit = GetProp(directives, "HasExplicitNamespace") is bool b && b;
                var r = _effectiveNamespaceResolve.Invoke(
                    null, new object[] { hasExplicit, rawNs, filePath, true });
                return (r as string) ?? rawNs;
            }
            catch { return rawNs; }
        }

        /// <summary>Bound-tag-name → the imported component's fully-qualified name,
        /// resolved through the IMPORT rather than by scanning loaded assemblies.
        ///
        /// The emitter's fallback (ResolveComponentFqn) takes the first loaded type
        /// whose SIMPLE name matches the tag, which is only correct while no two
        /// trees share a component name. Open a second tree with a LeftSide in it and
        /// the consumer publishes GetFamily against the OTHER tree's key, so the
        /// registry hands back a stranger's component - correctly, because that is
        /// what was asked for (UB-223).
        ///
        /// Resolution is importer-relative and tested through UitkxSourceExists, not
        /// File.Exists: an unsaved sibling is not on disk by design, and a disk-gated
        /// check cannot see it. Names with no import are left out, so hand-written
        /// components (router types in the package) still reach the assembly scan.</summary>
        internal Dictionary<string, string> BuildComponentFqnMap(object directives, string filePath)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (directives == null || _importResolverMap == null)
                return map;

            var imports = GetItems(GetProp(directives, "Imports"));
            if (imports.Count == 0)
                return map;

            string importerDir = (Path.GetDirectoryName(filePath) ?? filePath).Replace('\\', '/');
            string rootDir = importerDir;
            if (_uiSourceRootDir != null)
            {
                try { rootDir = (_uiSourceRootDir.Invoke(null, new object[] { filePath }) as string) ?? importerDir; }
                catch { }
            }

            foreach (var imp in imports)
            {
                string specifier = (string)GetProp(imp, "Specifier");
                if (string.IsNullOrEmpty(specifier))
                    continue;
                string targetFile = null;
                try
                {
                    var args = new object[] { importerDir, specifier, rootDir, null };
                    targetFile = _importResolverMap.Invoke(null, args) as string;
                }
                catch { }
                if (string.IsNullOrEmpty(targetFile) || !UitkxSourceExists(targetFile))
                    continue;

                object targetDs = ParseDirectivesForFile(targetFile);
                if (targetDs == null)
                    continue;
                string targetNs = ComputeEffectiveNs(targetDs, targetFile);

                // The target's component is NOT in MemberDeclarations. ParseResult is
                // explicit about it: a VirtualNode-returning declaration classifies as
                // DeclKind.Component but is parsed into ComponentDeclaration, and the
                // directives surface it as ComponentName. Reading MemberDeclarations
                // here built an always-empty map, which silently left the assembly scan
                // in charge and fixed nothing.
                string targetComponent = GetProp(targetDs, "ComponentName") as string;
                if (string.IsNullOrEmpty(targetComponent))
                    continue;

                var names = GetItems(GetProp(imp, "Names"));
                var aliases = GetItems(GetProp(imp, "Aliases"));
                for (int k = 0; k < names.Count; k++)
                {
                    string nm = names[k] as string;
                    if (string.IsNullOrEmpty(nm)
                        || !string.Equals(nm, targetComponent, StringComparison.Ordinal))
                        continue;
                    string bound = k < aliases.Count ? (aliases[k] as string) ?? nm : nm;
                    map[bound] = string.IsNullOrEmpty(targetNs)
                        ? nm
                        : targetNs + "." + nm;
                }
            }
            if (Trace != null)
            {
                var report = new System.Text.StringBuilder("[RUITK Builder] compile: child components of ")
                    .Append(Path.GetFileName(filePath));
                if (map.Count == 0)
                    report.Append(" -> (none resolved through imports; the assembly scan decides)");
                else
                    foreach (var pair in map)
                        report.Append("\n    ").Append(pair.Key)
                              .Append("  ->  ").Append(pair.Value);
                Trace(report.ToString());
            }
            return map;
        }

        /// <summary>Bare-hook-name → qualified family key, matching UitkxPipeline.BuildHookFamilyKeyMap.</summary>
        internal Dictionary<string, string> BuildHookFamilyKeyMap(object directives, string filePath)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (directives == null)
                return map;
            string ns = ComputeEffectiveNs(directives, filePath);

            // Same-file hooks live in MemberDeclarations (Kind == Hook) on the per-file
            // __Exports container. Enum compared by name — the DeclKind type lives in the
            // reflected Language.dll.
            var memberDecls = GetItems(GetProp(directives, "MemberDeclarations"));
            foreach (var m in memberDecls)
            {
                if (!string.Equals(GetProp(m, "Kind")?.ToString(), "Hook", StringComparison.Ordinal))
                    continue;
                string name = (string)GetProp(m, "Name");
                if (!string.IsNullOrEmpty(name))
                    map[name] = ns + ".__Exports::" + name;
            }

            // Imported hooks → resolve each specifier to its target file, qualify with the
            // TARGET's effective namespace + container.
            if (_importResolverMap != null)
            {
                var imports = GetItems(GetProp(directives, "Imports"));
                if (imports.Count > 0)
                {
                    string importerDir = (Path.GetDirectoryName(filePath) ?? filePath).Replace('\\', '/');
                    string rootDir = importerDir;
                    if (_uiSourceRootDir != null)
                    {
                        try { rootDir = (_uiSourceRootDir.Invoke(null, new object[] { filePath }) as string) ?? importerDir; }
                        catch { }
                    }
                    foreach (var imp in imports)
                    {
                        string specifier = (string)GetProp(imp, "Specifier");
                        if (string.IsNullOrEmpty(specifier))
                            continue;
                        var names = GetItems(GetProp(imp, "Names"));
                        string targetFile = null;
                        try
                        {
                            var args = new object[] { importerDir, specifier, rootDir, null };
                            targetFile = _importResolverMap.Invoke(null, args) as string;
                        }
                        catch { }
                        if (string.IsNullOrEmpty(targetFile) || !UitkxSourceExists(targetFile))
                            continue;
                        object targetDs = ParseDirectivesForFile(targetFile);
                        string targetNs = targetDs != null
                            ? ComputeEffectiveNs(targetDs, targetFile) : string.Empty;
                        // M1 parity with UitkxPipeline.BuildHookFamilyKeyMap: only names that are
                        // actually EXPORTED HOOKS of the target get keys (values/components must
                        // not pollute the map), and a rename-on-import binds the BOUND name to a
                        // key carrying the target's ORIGINAL hook name — that is what the
                        // producer registered, and the runtime matches by ordinal equality.
                        var targetHookNames = new HashSet<string>(StringComparer.Ordinal);
                        if (targetDs != null)
                        {
                            foreach (var m in GetItems(GetProp(targetDs, "MemberDeclarations")))
                                if (string.Equals(GetProp(m, "Kind")?.ToString(), "Hook", StringComparison.Ordinal)
                                    && GetProp(m, "IsExported") is bool me && me
                                    && GetProp(m, "Name") is string mn && !string.IsNullOrEmpty(mn))
                                    targetHookNames.Add(mn);
                        }
                        var aliases = GetItems(GetProp(imp, "Aliases"));
                        for (int k = 0; k < names.Count; k++)
                        {
                            string nm = names[k] as string;
                            if (string.IsNullOrEmpty(nm) || !targetHookNames.Contains(nm))
                                continue;
                            string bound = k < aliases.Count ? (aliases[k] as string) ?? nm : nm;
                            map[bound] = targetNs + ".__Exports::" + nm;
                        }
                    }
                }
            }
            return map;
        }

        /// <summary>Bridge lines for aliased/default member imports via the shared
        /// ImportScopeFacts.ComputeImportedMemberBridgeLines (reflection seam; older
        /// Language.dll or any failure → null, EmitExports skips bridges gracefully).</summary>
        internal IReadOnlyList<string> ComputeBridgeLines(object directives, string filePath)
        {
            if (_importedMemberBridgeLines == null || directives == null)
                return null;
            try
            {
                var raw = _importedMemberBridgeLines.Invoke(
                    null, new object[] { directives, filePath }) as System.Collections.IEnumerable;
                if (raw == null)
                    return null;
                var lines = new List<string>();
                foreach (object o in raw)
                    if (o is string s && s.Length > 0)
                        lines.Add(s);
                return lines.Count > 0 ? lines : null;
            }
            catch { return null; }
        }

        /// <summary>True when <paramref name="fullTypeName"/> resolves in a PROJECT (non-HMR)
        /// assembly — the "does the real build already carry this generated container" probe
        /// for mid-session-created files (assembly reload is locked during an HMR session, so
        /// a brand-new file's SG output is never compiled until the session ends). Session-hot
        /// hmr_* assemblies are excluded: their types are only referenceable through the
        /// cross-ref registry, which <see cref="HotExportsAvailable"/> checks instead.</summary>
        private static bool TypeExistsInProjectAssemblies(string fullTypeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic)
                    continue;
                if (asm.GetName().Name.StartsWith("hmr_", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (asm.GetType(fullTypeName, throwOnError: false) != null)
                        return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>True when a member file's hot __Exports DLL is bindable by the NEXT
        /// compile: registered under its container-FQN key, marked genuinely new, and still
        /// on disk — the exact criteria <see cref="BuildCrossRefs"/> applies.</summary>
        private bool HotExportsAvailable(string exportsFqn)
        {
            return _genuinelyNewComponents.Contains(exportsFqn)
                && _hmrAssemblyPaths.TryGetValue(exportsFqn, out string dll)
                && File.Exists(dll);
        }

        /// <summary>Invokes one of the shared ImportScopeFacts tag-map helpers (audit H3).
        /// Dictionary&lt;string,string&gt; is a BCL type, so the reflected result casts
        /// directly; null on older Language.dll or any failure.</summary>
        private IReadOnlyDictionary<string, string> InvokeTagMap(
            MethodInfo fn, object directives, string filePath)
        {
            if (fn == null || directives == null)
                return null;
            try
            {
                var map = fn.Invoke(null, new object[] { directives, filePath })
                    as IReadOnlyDictionary<string, string>;
                return map != null && map.Count > 0 ? map : null;
            }
            catch { return null; }
        }

        /// <summary>Parses a target hook file just to compute its effective namespace.</summary>
        private string ComputeEffectiveNsForFile(string targetFile)
        {
            var ds = ParseDirectivesForFile(targetFile);
            return ds != null ? ComputeEffectiveNs(ds, targetFile) : string.Empty;
        }

        /// <summary>Parses a target file's directives (reflected DirectiveSet), null on failure.</summary>
        /// <summary>Parsed directives for import targets, for the life of ONE
        /// compile.
        ///
        /// The hook-family map and the component-FQN map walk the same imports, and
        /// each read and parsed every target independently - so adding the second
        /// map doubled a cost paid on every save, HMR included. Within a single
        /// compile a target cannot change underneath us, so one parse per target is
        /// the right number. Cleared per compile rather than held: a stale directive
        /// set is exactly the class of bug this campaign spent the day removing.</summary>
        private readonly Dictionary<string, object> _directivesThisCompile =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private object ParseDirectivesForFile(string targetFile)
        {
            if (string.IsNullOrEmpty(targetFile))
                return null;
            if (_directivesThisCompile.TryGetValue(targetFile, out object cached))
                return cached;
            object parsed = null;
            try
            {
                string src = ReadUitkxText(targetFile);
                var diag = CreateDiagnosticList();
                parsed = InvokeWithDefaults(_directiveParse, null, src, targetFile, diag, true);
            }
            catch { parsed = null; }
            _directivesThisCompile[targetFile] = parsed;
            return parsed;
        }

        /// <summary>Maps bare custom-hook keys to their qualified form via <paramref name="map"/>.</summary>
        internal static string[] QualifyHookKeys(string[] bareKeys, IReadOnlyDictionary<string, string> map)
        {
            if (bareKeys == null || bareKeys.Length == 0 || map == null || map.Count == 0)
                return bareKeys ?? Array.Empty<string>();
            var result = new string[bareKeys.Length];
            for (int i = 0; i < bareKeys.Length; i++)
                result[i] = map.TryGetValue(bareKeys[i], out var q) ? q : bareKeys[i];
            return result;
        }

        // ── Defensive reflection invoker ─────────────────────────────────────
        // Tracks per-MethodInfo whether we've already warned about silent
        // padding, so a single drift is reported once per session instead of
        // flooding the console on every .uitkx save.
        private static readonly HashSet<MethodInfo> _paddedMethodWarnings = new();
        private static readonly object _paddedMethodWarningsLock = new();

        // ── Drift-warning suppression set ────────────────────────────────────
        // Roslyn-targeted MethodInfos registered here are *intentionally*
        // called with a short positional arg list whose tail is filled from
        // compile-time defaults (the canonical "all-optional-tail" overload
        // shape returned by PickAllOptionalTailOverload). Drift warnings for
        // those calls are pure noise. Language-library calls are NOT in this
        // set so genuine API drift in the parser/lowering pipeline still
        // surfaces loud-and-clear on the very first save.
        private static readonly HashSet<MethodInfo> _silentDriftMethods = new();
        private static readonly object _silentDriftMethodsLock = new();

        private static void RegisterSilentDrift(MethodInfo m)
        {
            if (m == null)
                return;
            lock (_silentDriftMethodsLock)
                _silentDriftMethods.Add(m);
        }

        /// <summary>
        /// Locates the canonical "all-optional-tail" overload of a reflected
        /// method. Filters by <paramref name="name"/> and the type of parameter
        /// 0 (<paramref name="firstParamType"/>), then prefers the SHORTEST
        /// overload where every parameter from index 1 onward has
        /// <c>HasDefaultValue == true</c>. Falls back to the shortest matching
        /// overload regardless of optionality if no all-optional-tail candidate
        /// exists.
        /// <para>
        /// Critical for Roslyn discovery: <see cref="System.Linq.Enumerable.First{TSource}(System.Collections.Generic.IEnumerable{TSource}, Func{TSource, bool})"/>
        /// has no documented ordering guarantee across runtime versions, and
        /// <summary>
        /// Returns the preprocessor symbols Roslyn should define when
        /// compiling user .uitkx / companion .cs sources for HMR. Mirrors
        /// Unity's editor-side compile so the SG-emitted trampoline (and any
        /// user <c>#if UNITY_EDITOR</c> blocks) survive.
        /// <para>
        /// Strategy: ask <see cref="UnityEditor.Compilation.CompilationPipeline"/>
        /// for the editor-target Assembly-CSharp-Editor defines (Unity adds
        /// <c>UNITY_EDITOR</c>, version pragmas, scripting-backend pragmas
        /// etc. there). Fall back to a minimum viable set of just
        /// <c>UNITY_EDITOR</c> if the API is unavailable or returns nothing.
        /// </para>

        // ── Project language version ──────────────────────────────────────────
        //
        // The hot compile MUST use the language version Unity compiles the
        // project with. It used to pass "latest", which is strictly more
        // permissive: the preview accepted code that the next real compile
        // rejected, and said nothing. C# 10's inferred delegate type is the
        // one that surfaced it -
        //
        //     var handler = () => { ... };   // fine at latest, CS8773 at 9.0
        //
        // - so a component previewed clean and then failed to build on Save.
        // A preview that accepts more than the compiler is not a preview.
        //
        // Unity owns the answer: every assembly's ScriptCompilerOptions carries
        // the version Unity itself passes to Roslyn. Asked once and cached; the
        // fallback matches every Unity this package supports (floor 6000.2).
        private static string s_languageVersion;

        private const string LanguageVersionFallback = "9.0";

        internal static string ProjectLanguageVersion
        {
            get
            {
                if (s_languageVersion != null)
                    return s_languageVersion;
                s_languageVersion = QueryUnityLanguageVersion() ?? LanguageVersionFallback;
                return s_languageVersion;
            }
        }

        private static string QueryUnityLanguageVersion()
        {
            try
            {
                var assemblies = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(
                    UnityEditor.Compilation.AssembliesType.Editor
                );
                if (assemblies == null)
                    return null;
                foreach (var assembly in assemblies)
                {
                    string version = assembly?.compilerOptions?.LanguageVersion;
                    if (!string.IsNullOrEmpty(version))
                        return version;
                }
            }
            catch (Exception)
            {
                // Unity has moved this before. A wrong-but-conservative version is
                // recoverable; throwing out of the compile path is not.
            }
            return null;
        }
        /// </summary>
        private static string[] ResolveEditorPreprocessorSymbols()
        {
            HashSet<string> symbols = new HashSet<string>(StringComparer.Ordinal)
            {
                "UNITY_EDITOR",
            };

            try
            {
                var asms = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(
                    UnityEditor.Compilation.AssembliesType.Editor
                );
                if (asms != null)
                {
                    foreach (var asm in asms)
                    {
                        if (asm?.defines == null)
                            continue;
                        for (int i = 0; i < asm.defines.Length; i++)
                            symbols.Add(asm.defines[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[HMR] Could not enumerate Unity editor defines via CompilationPipeline; "
                        + $"falling back to UNITY_EDITOR-only. ({ex.Message})"
                );
            }

            var result = new string[symbols.Count];
            symbols.CopyTo(result);
            return result;
        }

        /// <summary>
        /// Picks the overload whose parameters past <paramref name="firstParamType"/>
        /// are all optional. Used for resolving Roslyn surface methods in a
        /// version-tolerant way without locking to a particular API generation.
        /// <para>
        /// <c>Compilation.Emit</c> in particular ships multiple <c>(Stream, …)</c>
        /// overloads where parameter 1 is a non-defaulted <c>Stream</c>. A
        /// non-deterministic First() pick on those silently fails at invoke time.
        /// </para>
        /// </summary>
        private static MethodInfo PickAllOptionalTailOverload(
            Type declaringType,
            string name,
            Type firstParamType,
            BindingFlags flags
        )
        {
            if (declaringType == null)
                return null;

            MethodInfo allOptionalBest = null;
            int allOptionalBestLen = int.MaxValue;
            MethodInfo anyMatchBest = null;
            int anyMatchBestLen = int.MaxValue;

            foreach (var m in declaringType.GetMethods(flags))
            {
                if (m.Name != name)
                    continue;
                var ps = m.GetParameters();
                if (ps.Length == 0)
                    continue;
                if (ps[0].ParameterType != firstParamType)
                    continue;

                if (ps.Length < anyMatchBestLen)
                {
                    anyMatchBest = m;
                    anyMatchBestLen = ps.Length;
                }

                bool tailAllOptional = true;
                for (int i = 1; i < ps.Length; i++)
                {
                    if (!ps[i].HasDefaultValue)
                    {
                        tailAllOptional = false;
                        break;
                    }
                }
                if (!tailAllOptional)
                    continue;
                if (ps.Length < allOptionalBestLen)
                {
                    allOptionalBest = m;
                    allOptionalBestLen = ps.Length;
                }
            }

            return allOptionalBest ?? anyMatchBest;
        }

        /// <summary>
        /// Invokes a reflective method, padding short argument arrays with each
        /// parameter's compile-time default value when the language library has
        /// gained new optional parameters since this HMR build was shipped.
        /// Surfaces silent API drift via a one-time <c>LogWarning</c> per
        /// <see cref="MethodInfo"/>, and throws a clear <see cref="ArgumentException"/>
        /// when the mismatch is irrecoverable (too many args, or a non-optional
        /// parameter is missing).
        ///
        /// <para>The <paramref name="target"/> argument is the receiver for
        /// instance methods, or <c>null</c> for static methods. It is mandatory
        /// (rather than defaulted) so that overload resolution cannot silently
        /// shift a <c>string</c> argument into the receiver slot — a class of
        /// bug that previously hid behind two competing <c>params</c> overloads.</para>
        ///
        /// <para>Methods registered via <see cref="RegisterSilentDrift"/> skip
        /// the warning (see field comments for rationale).</para>
        /// </summary>
        private static object InvokeWithDefaults(
            MethodInfo method,
            object target,
            params object[] args
        )
        {
            if (method == null)
                throw new ArgumentNullException(nameof(method));
            args ??= Array.Empty<object>();

            var parameters = method.GetParameters();
            if (args.Length == parameters.Length)
                return method.Invoke(target, args);

            if (args.Length > parameters.Length)
            {
                throw new ArgumentException(
                    $"[HMR] {method.DeclaringType?.Name}.{method.Name}: HMR passed "
                        + $"{args.Length} args but the loaded language library declares "
                        + $"{parameters.Length} parameter(s). The HMR compiler must be updated."
                );
            }

            // args.Length < parameters.Length — pad missing tail with defaults.
            var padded = new object[parameters.Length];
            Array.Copy(args, padded, args.Length);
            for (int i = args.Length; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (!p.HasDefaultValue)
                {
                    throw new ArgumentException(
                        $"[HMR] {method.DeclaringType?.Name}.{method.Name}: missing "
                            + $"required argument '{p.Name}' (position {i}). The HMR compiler "
                            + $"is out of sync with the loaded language library."
                    );
                }
                padded[i] = p.DefaultValue;
            }

            bool firstWarning;
            lock (_paddedMethodWarningsLock)
                firstWarning = _paddedMethodWarnings.Add(method);
            bool isSilent;
            lock (_silentDriftMethodsLock)
                isSilent = _silentDriftMethods.Contains(method);
            if (firstWarning && !isSilent)
            {
                Debug.LogWarning(
                    $"[HMR] {method.DeclaringType?.Name}.{method.Name}: HMR passed "
                        + $"{args.Length} args but the loaded language library declares "
                        + $"{parameters.Length} parameter(s); padded missing tail with "
                        + $"compile-time defaults. Update the HMR compiler to pass the "
                        + $"new arguments explicitly."
                );
            }

            return method.Invoke(target, padded);
        }

        /// <summary>
        /// Classifies whether an exception caught inside the HMR compile pipeline
        /// represents an infrastructure failure (HMR plumbing is broken) as opposed
        /// to a user-authored .uitkx error. Infrastructure failures cause the
        /// controller to log one error and self-disable; user errors continue to
        /// follow the warn + retry cascade flow.
        /// </summary>
        internal static bool IsInfrastructureException(Exception ex)
        {
            if (ex == null)
                return false;
            // Unwrap reflection wrapper.
            if (ex is TargetInvocationException tie && tie.InnerException != null)
                ex = tie.InnerException;
            return ex is TargetParameterCountException
                || ex is MissingMethodException
                || ex is MissingFieldException
                || ex is TypeLoadException
                || ex is ReflectionTypeLoadException
                || ex is BadImageFormatException;
        }

        // ── Rank 2: new .cs pickup ───────────────────────────────────────────

        // Cache: asmdef name → last-known project DLL mtime. Used to filter
        // .cs files older than the DLL (already compiled into AppDomain).
        // Refreshed every Compile call cheaply via File.GetLastWriteTimeUtc.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            string,
            DateTime
        > s_asmdefDllMtimeCache = new();

        /// <summary>
        /// Rank 2 helper: appends fresh asmdef-scoped .cs files (whose primary
        /// type-name isn't yet in AppDomain) to <paramref name="sources"/>.
        /// Fully best-effort — any failure swallows quietly and the compile
        /// proceeds without the extra trees, matching pre-Rank-2 behavior.
        /// </summary>
        private void TryIncludeNewAsmdefCsFiles(
            string uitkxPath,
            string[] alreadyIncludedCs,
            List<string> sources
        )
        {
            try
            {
                string asmdef = AsmdefResolver.OwningAsmdefName(uitkxPath);
                if (string.IsNullOrEmpty(asmdef))
                    return;

                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

                // Resolve the project DLL mtime for this asmdef. If we can't
                // find it (e.g. first cold compile), use DateTime.MinValue so
                // every .cs is considered "newer" — type-name dedupe still
                // prevents CS0101 against any types Unity DID load.
                DateTime dllMtime = ResolveAsmdefDllMtime(projectRoot, asmdef);

                ICollection<string> already = alreadyIncludedCs ?? Array.Empty<string>();
                var extra = NewCsFileDiscovery.FindForAsmdef(
                    asmdef,
                    projectRoot,
                    dllMtime,
                    already
                );

                foreach (var path in extra)
                {
                    try
                    {
                        sources.Add(ReadTextWithRetry(path));
                    }
                    catch
                    { /* per-file IO error — skip silently */
                    }
                }
            }
            catch
            {
                // Never let the discovery pass fail the entire compile.
            }
        }

        private static DateTime ResolveAsmdefDllMtime(string projectRoot, string asmdef)
        {
            if (s_asmdefDllMtimeCache.TryGetValue(asmdef, out var cached))
            {
                // Refresh if the DLL has been updated since we cached.
                string dllPathCached = Path.Combine(
                    projectRoot,
                    "Library",
                    "ScriptAssemblies",
                    asmdef + ".dll"
                );
                try
                {
                    var fresh = File.GetLastWriteTimeUtc(dllPathCached);
                    if (fresh != cached)
                    {
                        s_asmdefDllMtimeCache[asmdef] = fresh;
                        return fresh;
                    }
                    return cached;
                }
                catch
                {
                    return cached;
                }
            }

            string dllPath = Path.Combine(
                projectRoot,
                "Library",
                "ScriptAssemblies",
                asmdef + ".dll"
            );
            DateTime mtime;
            try
            {
                mtime = File.GetLastWriteTimeUtc(dllPath);
            }
            catch
            {
                mtime = DateTime.MinValue;
            }

            s_asmdefDllMtimeCache[asmdef] = mtime;
            return mtime;
        }
    }

    // ── Result ────────────────────────────────────────────────────────────────

    internal sealed class HmrCompileResult
    {
        public bool Success;
        public string Error;
        public string ComponentName;

        /// <summary>
        /// Declared namespace from the .uitkx file's <c>@namespace</c> directive
        /// (empty string when omitted). Threaded from <see cref="ComponentBuildArtifacts.Namespace"/>
        /// so swappers can FQN-resolve types without a non-deterministic
        /// <c>GetTypes().FirstOrDefault().Namespace</c> probe (which picks Roslyn's
        /// embedded <c>Microsoft.CodeAnalysis.EmbeddedAttribute</c> based on metadata
        /// ordering).
        /// </summary>
        public string Namespace;

        /// <summary>
        /// The self family key the emitted ModuleInitializer Registers under
        /// (<c>{EffectiveNs}.{ComponentName}</c>) — mirrors HmrCSharpEmitter's selfKey exactly.
        /// Component files only. Lets the controller diagnose a zero-swap precisely: the key must
        /// match the one the PROJECT assembly registered at load, or Register takes the create
        /// path and no fiber ever refreshes.
        /// </summary>
        public string FamilyKey;

        public Assembly LoadedAssembly;

        /// <summary>
        /// True when this result is from a hook/module .uitkx file
        /// (as opposed to a component file). The controller uses this
        /// to route to the hook delegate swapper instead of the
        /// component fiber swapper.
        /// </summary>
        public bool IsHookModuleFile;

        /// <summary>
        /// True when this hook/module file actually declared one or more
        /// <c>hook</c>s (i.e. the hook emitter produced a container class).
        /// A module-ONLY file (only <c>module</c> bodies, no hooks) leaves this
        /// false, so the controller skips <see cref="UitkxHmrDelegateSwapper.SwapHooks"/>
        /// — which would otherwise log a spurious "Could not find hook container"
        /// warning and no-op — and instead fires the global re-render itself
        /// after the module-method swap.
        /// </summary>
        public bool HasHooks;

        /// <summary>
        /// Container class name for hook files (e.g. "CounterHooks").
        /// Used by the delegate swapper to find the static fields.
        /// </summary>
        public string HookContainerClass;

        // Per-step timing (milliseconds)
        public double ParseMs;
        public double EmitMs;
        public double CompileMs;
        public double SwapMs;
        public double TotalMs;

        public string TimingBreakdown =>
            $"Parse: {ParseMs:F1}ms | Emit: {EmitMs:F1}ms | Compile: {CompileMs:F1}ms | Swap: {SwapMs:F1}ms | Total: {TotalMs:F1}ms";

        /// <summary>
        /// True when the failure is caused by HMR infrastructure drift
        /// (reflection signature mismatch against the language library, missing
        /// type/method, or assembly load failure) rather than a user-authored
        /// .uitkx error. Triggers a one-time error log + self-disable in the
        /// controller — restart Unity or click Start in the HMR window after
        /// rebuilding the language library.
        /// </summary>
        public bool IsInfrastructureError;
    }

    // ── Batch (Rank 5 — per-SCC union) result ────────────────────────────────

    /// <summary>
    /// Result of a single union-compile call covering N <c>.uitkx</c> files.
    /// All files in the batch share <see cref="UnionAssembly"/>; per-file
    /// <see cref="HmrCompileResult"/> entries in <see cref="PerFileResults"/>
    /// each point at the same loaded assembly but carry per-file component
    /// metadata so the controller can run the existing swap pipeline once
    /// per file without re-emitting.
    ///
    /// On failure (pre-compile guard tripped, Roslyn errors, or post-compile
    /// guard tripped) <see cref="OverallSuccess"/> is <c>false</c>,
    /// <see cref="OverallError"/> carries the user-facing reason, and
    /// <see cref="FallbackReason"/> tells the controller why per-file compile
    /// must be used as the safety net (per §5.2.1 of the Tech Debt plan:
    /// "loud regression preferred over silent wrong-IL").
    /// </summary>
    internal sealed class HmrBatchCompileResult
    {
        public bool OverallSuccess;
        public string OverallError;
        public string FallbackReason;
        public Assembly UnionAssembly;
        public List<HmrCompileResult> PerFileResults = new();
        public double TotalMs;
        public int BatchSize;
    }
}
