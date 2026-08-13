using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Ruitk.Language.Parser;
using Ruitk.Router;

namespace Ruitk.SourceGenerator.Emitter
{
    /// <summary>
    /// Phase 3 — maps markup tag names to the V.* call pattern that the
    /// <see cref="CSharpEmitter"/> will generate.
    ///
    /// Strategy B (Roslyn symbol inspection):
    ///   1. Scans the <c>Ruitk.V</c> class at resolver-construction time to
    ///      build a lowercase tag-name → <see cref="TagResolution"/> map for all
    ///      built-in elements.
    ///   2. For PascalCase tags (function components), checks whether a type with that
    ///      name and an appropriate <c>Render</c> static method exists in the
    ///      compilation.  Emits <c>UITKX0008</c> if the type cannot be found.
    ///   3. Falls back to a hardcoded built-in map when the V class cannot be located
    ///      in the compilation (e.g. the shared assembly is not yet compiled).
    /// </summary>
    public sealed class PropsResolver
    {
        private readonly Compilation _compilation;

        /// <summary>
        /// Peer UITKX components from the same generator pass, scoped to the current
        /// compilation/assembly and carrying namespace-qualified identity.
        /// </summary>
        /// <summary>
        /// Peer components keyed by their metadata type name (e.g. "App.UI.Child").
        /// </summary>
        private readonly ImmutableDictionary<
            string,
            PeerComponentInfo
        > _peerComponentsByMetadataName;

        /// <summary>Lowercase tag → TagResolution for every V.* built-in.</summary>
        private readonly Dictionary<string, TagResolution> _builtinMap;

        // ── Roslyn type-name constants ────────────────────────────────────────
        private const string VTypeName = "Ruitk.V";
        private const string UTypeName = "Ruitk.Ugui.U";
        private const string VirtualNodeName = "VirtualNode";

        /// <summary>File backend ("ugui" or null for UI Toolkit) — selects the vocabulary.</summary>
        private readonly string? _backend;

        // ── Well-known component tag aliases ────────────────────────────────────
        // Single source of truth lives in Shared/Core/Router/RouterTagAliases.cs
        // (linked into this project via <Compile Include="..." Link="..." /> in
        // Ruitk.SourceGenerator.csproj).  The same alias set is consumed
        // by Editor/HMR/HmrCSharpEmitter.cs at runtime via the Shared assembly.
        private static readonly IReadOnlyDictionary<string, string> s_componentTagAliases =
            RouterTagAliases.Map;

        // ── Fallback hard-coded map (used when V type not resolvable) ─────────
        private static readonly IReadOnlyDictionary<string, TagResolution> s_fallbackMap =
            BuildFallbackMap();

        // ── Fallback hard-coded map for the ugui vocabulary (U not resolvable) ─
        private static readonly IReadOnlyDictionary<string, TagResolution> s_uguiFallbackMap =
            BuildUguiFallbackMap();

        // ── Import-alias tag maps (ES-modules campaign, U-05/U-03) ────────────
        // starImportNamespaces: `* as X` binding → target file's effective namespace, so a
        // dotted tag <X.Comp/> resolves as {ns}.Comp. importAliasTypeMap: bound component
        // name (default import / `as`-rename) → target metadata type name, so <Tile/> bound
        // by `import { Card as Tile }` resolves as the target's Card.
        private readonly IReadOnlyDictionary<string, string> _starImportNamespaces;
        private readonly IReadOnlyDictionary<string, string> _importAliasTypeMap;

        public PropsResolver(
            Compilation compilation,
            ImmutableArray<PeerComponentInfo>? peerComponents = null,
            IReadOnlyDictionary<string, string>? starImportNamespaces = null,
            IReadOnlyDictionary<string, string>? importAliasTypeMap = null,
            string? backend = null
        )
        {
            _compilation = compilation;
            _backend = backend;
            var resolvedPeerComponents = peerComponents ?? ImmutableArray<PeerComponentInfo>.Empty;
            _peerComponentsByMetadataName = resolvedPeerComponents.ToImmutableDictionary(
                p => p.MetadataTypeName,
                p => p,
                StringComparer.Ordinal
            );
            _starImportNamespaces = starImportNamespaces
                ?? System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;
            _importAliasTypeMap = importAliasTypeMap
                ?? System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;
            _builtinMap = BuildBuiltinMapFromCompilation(compilation, backend);
        }

        // ── Public API ────────────────────────────────────────────────────────

        // ── ref-as-prop routing ───────────────────────────────────────────────

        /// <summary>
        /// Outcome of a <see cref="TryGetRefParamPropName"/> look-up.
        /// </summary>
        internal enum RefParamLookupResult
        {
            /// <summary>No <c>Hooks.MutableRef&lt;T&gt;</c> parameter was found.</summary>
            None,

            /// <summary>Exactly one param found; <c>refPropName</c> is set.</summary>
            Found,

            /// <summary>Multiple params found; routing is ambiguous.</summary>
            Ambiguous,
        }

        /// <summary>
        /// Attempts to find the single unambiguous <c>Hooks.MutableRef&lt;T&gt;</c> parameter
        /// for <paramref name="componentTypeName"/> so that a bare <c>ref={x}</c> attribute
        /// can be routed to the correct Props property name.
        ///
        /// Two resolution paths are tried in order:
        /// <list type="number">
        ///   <item>Peer-UITKX path — resolves the visible peer component for the
        ///     current namespace/import set and scans its parsed
        ///     <see cref="FunctionParam"/> entries for <c>MutableRef&lt;T&gt;</c>.</item>
        ///   <item>Roslyn path — inspects the compiled Props type (<paramref name="propsTypeName"/>)
        ///     for public settable properties whose Roslyn type is <c>Hooks.MutableRef&lt;T&gt;</c>.</item>
        /// </list>
        /// </summary>
        /// <param name="componentTypeName">Simple or qualified C# type name of the component (e.g. "RefChild" or "global::App.UI.RefChild").</param>
        /// <param name="propsTypeName">Qualified or simple props type name, or <c>null</c> when the
        ///   component has no props class.</param>
        /// <param name="refPropName">PascalCase property name to emit (e.g. "InputRef").</param>
        /// <returns>The lookup outcome.</returns>
        internal RefParamLookupResult TryGetRefParamPropName(
            string componentTypeName,
            string? propsTypeName,
            ImmutableArray<string> searchNamespaces,
            out string? refPropName
        )
        {
            refPropName = null;

            // ── Path A: peer-UITKX component (same code-gen pass) ─────────────
            if (TryFindVisiblePeerComponent(componentTypeName, searchNamespaces, out var peer))
            {
                var mutableRefParams = peer
                    .FunctionParams.Where(fp => IsMutableRefTypeName(fp.Type))
                    .ToList();

                if (mutableRefParams.Count == 0)
                    return RefParamLookupResult.None;

                if (mutableRefParams.Count > 1)
                    return RefParamLookupResult.Ambiguous;

                // Exactly one — convert camelCase param name to PascalCase prop name
                // (PropSourceName: underscore-marked params keep their public prop).
                refPropName = ToPropName(mutableRefParams[0].PropSourceName);
                return RefParamLookupResult.Found;
            }

            // ── Path B: C# component — inspect compiled Props type ────────────
            if (propsTypeName == null)
                return RefParamLookupResult.None;

            var propNames = GetMutableRefPropertyNames(propsTypeName, searchNamespaces);
            if (propNames.Count == 0)
                return RefParamLookupResult.None;

            if (propNames.Count > 1)
                return RefParamLookupResult.Ambiguous;

            refPropName = propNames[0];
            return RefParamLookupResult.Found;
        }

        // ── ref-detection helpers ─────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> when <paramref name="typeName"/> (as it appears in a .uitkx
        /// function-style parameter list) denotes a <c>Ref&lt;T&gt;</c> or the deprecated
        /// <c>Hooks.MutableRef&lt;T&gt;</c> type.
        /// Handles optional nullable suffix and fully-qualified form.
        /// </summary>
        private static bool IsMutableRefTypeName(string typeName)
        {
            string stripped = typeName.TrimEnd('?').Trim();
            return stripped.StartsWith("Ref<", StringComparison.Ordinal)
                || stripped.StartsWith("Ruitk.Core.Ref<", StringComparison.Ordinal)
                || stripped.StartsWith("Hooks.MutableRef<", StringComparison.Ordinal) // [Obsolete] compat
                || stripped.StartsWith(
                    "Ruitk.Core.Hooks.MutableRef<",
                    StringComparison.Ordinal
                );
        }

        /// <summary>
        /// Converts a camelCase identifier to PascalCase — e.g. "inputRef" → "InputRef".
        /// No op if the first character is already uppercase.
        /// </summary>
        private static string ToPropName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            return char.IsUpper(name[0])
                ? name
                : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// Collects the names of all public settable properties on the given
        /// <paramref name="propsTypeName"/> that are typed as <c>Ref&lt;T&gt;</c> or the
        /// deprecated <c>Hooks.MutableRef&lt;T&gt;</c>, walking the inheritance chain.
        /// Returns an empty list when the type cannot be resolved by Roslyn.
        /// </summary>
        private List<string> GetMutableRefPropertyNames(
            string propsTypeName,
            ImmutableArray<string> searchNamespaces
        )
        {
            INamedTypeSymbol? typeSymbol = null;
            string normalizedPropsTypeName = NormalizeMetadataTypeName(propsTypeName);

            // Try the propsTypeName as-is (may be fully qualified via TryGetFuncComponentPropsTypeName).
            typeSymbol = _compilation.GetTypeByMetadataName(normalizedPropsTypeName);

            // Nested type variant for source-style names (e.g. Ns.Type.Props → Ns.Type+Props)
            if (typeSymbol == null)
                typeSymbol = _compilation.GetTypeByMetadataName(
                    ToSingleNestedMetadataTypeName(normalizedPropsTypeName)
                );

            // Try each @using namespace as a qualifier.
            if (typeSymbol == null)
            {
                foreach (var ns in searchNamespaces)
                {
                    typeSymbol = _compilation.GetTypeByMetadataName(
                        $"{ns}.{normalizedPropsTypeName}"
                    );
                    if (typeSymbol != null)
                        break;

                    // Nested type variant (e.g. RouterFunc+Props / ChildComp+ChildCompProps)
                    typeSymbol = _compilation.GetTypeByMetadataName(
                        ToSingleNestedMetadataTypeName($"{ns}.{normalizedPropsTypeName}")
                    );
                    if (typeSymbol != null)
                        break;
                }
            }

            if (typeSymbol == null)
                return new List<string>();

            var result = new List<string>();
            var current = typeSymbol;
            while (current != null && current.SpecialType != SpecialType.System_Object)
            {
                foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
                {
                    if (
                        member.DeclaredAccessibility == Accessibility.Public
                        && member.SetMethod != null
                        && IsRoslynMutableRefType(member.Type)
                    )
                    {
                        result.Add(member.Name);
                    }
                }
                current = current.BaseType;
            }
            return result;
        }

        private static string NormalizeMetadataTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return string.Empty;

            return typeName.StartsWith("global::", StringComparison.Ordinal)
                ? typeName.Substring("global::".Length)
                : typeName;
        }

        private static string ToSingleNestedMetadataTypeName(string sourceStyleTypeName)
        {
            int lastDot = sourceStyleTypeName.LastIndexOf('.');
            if (lastDot < 0)
                return sourceStyleTypeName;

            return sourceStyleTypeName.Substring(0, lastDot)
                + "+"
                + sourceStyleTypeName.Substring(lastDot + 1);
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="typeSymbol"/> is the top-level
        /// <c>Ref&lt;T&gt;</c> from <c>Ruitk.Core</c>, or the deprecated
        /// <c>Hooks.MutableRef&lt;T&gt;</c> type.
        /// Also matches nullable wrappers by unwrapping via Nullable.
        /// </summary>
        private static bool IsRoslynMutableRefType(ITypeSymbol typeSymbol)
        {
            // Unwrap Nullable<T> → T
            if (
                typeSymbol
                    is INamedTypeSymbol
                    {
                        ConstructedFrom: { SpecialType: SpecialType.System_Nullable_T }
                    }
                && typeSymbol is INamedTypeSymbol nullable
            )
            {
                typeSymbol = nullable.TypeArguments[0];
            }

            if (typeSymbol is not INamedTypeSymbol named || !named.IsGenericType)
                return false;

            var def = named.ConstructedFrom;

            // Match top-level Ref<T> in Ruitk.Core namespace (not nested)
            if (
                string.Equals(def.Name, "Ref", StringComparison.Ordinal)
                && def.ContainingType == null
                && string.Equals(
                    def.ContainingNamespace?.ToDisplayString(),
                    "Ruitk.Core",
                    StringComparison.Ordinal
                )
            )
                return true;

            // Match Hooks.MutableRef<T> — [Obsolete] backward compat
            return string.Equals(def.Name, "MutableRef", StringComparison.Ordinal)
                && string.Equals(def.ContainingType?.Name, "Hooks", StringComparison.Ordinal)
                && string.Equals(
                    def.ContainingNamespace?.ToDisplayString(),
                    "Ruitk.Core",
                    StringComparison.Ordinal
                );
        }

        /// <summary>
        /// Resolves <paramref name="tagName"/> (as it appears in markup) to a
        /// <see cref="TagResolution"/> describing how to emit the V.* call.
        /// </summary>
        /// <param name="tagName">Raw tag name from the .uitkx file.</param>
        /// <param name="usingNamespaces">
        ///   @using namespaces from the DirectiveSet, used when searching for
        ///   function-component types.
        /// </param>
        /// <param name="unknownDiagnostic">
        ///   When the tag cannot be verified, set to a <see cref="DiagnosticDescriptor"/>
        ///   (caller should create the <see cref="Diagnostic"/> with source location).
        /// </param>
        public TagResolution Resolve(
            string tagName,
            ImmutableArray<string> usingNamespaces,
            out DiagnosticDescriptor? unknownDiagnostic
        )
        {
            unknownDiagnostic = null;

            // Empty tag name = <></> fragment shorthand
            if (string.IsNullOrEmpty(tagName))
                return new TagResolution(
                    TagResolutionKind.Fragment,
                    "Fragment",
                    null,
                    AcceptsChildren: true
                );

            // "Fragment" tag = explicit fragment
            if (string.Equals(tagName, "Fragment", StringComparison.OrdinalIgnoreCase))
                return new TagResolution(
                    TagResolutionKind.Fragment,
                    "Fragment",
                    null,
                    AcceptsChildren: true
                );

            // ── Built-in lookup (lowercase match) ────────────────────────────
            if (_builtinMap.TryGetValue(tagName, out var builtinResolution))
                return builtinResolution;

            // ── Lowercase unknown → defer to C# compiler, emit UITKX0001 ─────
            if (!char.IsUpper(tagName[0]))
            {
                unknownDiagnostic = UitkxDiagnostics.UnknownElement;
                return new TagResolution(TagResolutionKind.Unknown, tagName, null, false);
            }

            // ── PascalCase → function component ──────────────────────────────
            // Apply well-known component tag aliases first so that short markup
            // names like <Router>, <Route>, <Link> transparently resolve to their
            // Func-suffixed implementation classes.
            string lookupTypeName = s_componentTagAliases.TryGetValue(tagName, out var aliased)
                ? aliased
                : tagName;

            // ── Import-alias resolution (ES-modules campaign) ────────────────
            // U-05 dotted tag: <X.Comp/> where X is a `* as X` binding → {targetNs}.Comp.
            // U-03 bound-name tag: <Tile/> where Tile is a default import or `as`-rename of
            // a component → the target's metadata type name. An unresolvable dotted head
            // falls through unchanged into the existing unknown-tag diagnostics path.
            int tagDot = lookupTypeName.IndexOf('.');
            if (tagDot > 0 && tagDot < lookupTypeName.Length - 1
                && _starImportNamespaces.TryGetValue(lookupTypeName.Substring(0, tagDot), out string? starNs))
            {
                lookupTypeName = starNs + "." + lookupTypeName.Substring(tagDot + 1);
            }
            else if (tagDot < 0 && _importAliasTypeMap.TryGetValue(lookupTypeName, out string? boundFqn))
            {
                lookupTypeName = boundFqn;
            }

            string resolvedTypeName = ResolveFuncComponentTypeName(
                lookupTypeName,
                usingNamespaces,
                out bool typeFound
            );
            if (!typeFound)
            {
                unknownDiagnostic = UitkxDiagnostics.UnknownComponent;
                // Still emit V.Func(LookupTypeName.Render, ...) — let C# compiler report missing type
            }

            string? funcPropsTypeName = TryGetFuncComponentPropsTypeName(
                lookupTypeName,
                resolvedTypeName,
                usingNamespaces
            );

            return new TagResolution(
                TagResolutionKind.FuncComponent,
                "Func",
                null,
                AcceptsChildren: true,
                FuncTypeName: resolvedTypeName,
                FuncPropsTypeName: funcPropsTypeName
            );
        }

        // GetPublicPropertyNames — used by CSharpEmitter for UITKX0109 attribute validation

        private static readonly string[] s_propsNamespaces =
        {
            "Ruitk.Props.Typed",
            "Ruitk.Ugui",
        };

        private static HashSet<string> CollectPropertyNames(INamedTypeSymbol type)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Walk up the inheritance chain so that properties declared in BaseProps
            // (and any other ancestor) are visible to attribute validation.
            var current = type;
            while (current != null && current.SpecialType != SpecialType.System_Object)
            {
                foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
                {
                    if (
                        member.DeclaredAccessibility == Accessibility.Public
                        && member.SetMethod != null
                    )
                        result.Add(member.Name);
                }
                current = current.BaseType;
            }
            return result;
        }

        /// <summary>
        /// Returns the PascalCase public settable property names for
        /// <paramref name="propsTypeName"/> (e.g. "LabelProps" → {"Text","Style","Name",…}).
        /// Used to validate attribute names and produce did-you-mean hints.
        /// Returns an empty set when the type cannot be found.
        /// </summary>
        public HashSet<string> GetPublicPropertyNames(string propsTypeName)
        {
            foreach (var ns in s_propsNamespaces)
            {
                var type = _compilation.GetTypeByMetadataName($"{ns}.{propsTypeName}");
                if (type != null)
                    return CollectPropertyNames(type);
            }
            return new HashSet<string>();
        }

        /// <summary>
        /// Like <see cref="GetPublicPropertyNames"/> but accepts a fully-qualified
        /// type name (e.g. <c>global::PrettyUi.App.Components.AppButtonProps</c>
        /// or <c>global::PrettyUi.App.Components.AppButton.AppButtonProps</c>) as
        /// produced by <see cref="TryGetFuncComponentPropsTypeName"/> for user
        /// components. Used by <c>EmitFuncComponent</c> to validate attribute
        /// names against the declared user-component prop set (UITKX0109).
        /// Returns an empty set when the type cannot be resolved.
        /// </summary>
        public HashSet<string> GetPublicPropertyNamesByQualifiedName(string qualifiedTypeName)
        {
            if (string.IsNullOrWhiteSpace(qualifiedTypeName))
                return new HashSet<string>();

            string normalized = NormalizeMetadataTypeName(qualifiedTypeName);

            // Try as-is (e.g. "Ns.AppButtonProps").
            var type = _compilation.GetTypeByMetadataName(normalized);
            if (type != null)
                return CollectPropertyNames(type);

            // Try nested-class form (e.g. "Ns.AppButton.AppButtonProps" → "Ns.AppButton+AppButtonProps").
            type = _compilation.GetTypeByMetadataName(ToSingleNestedMetadataTypeName(normalized));
            if (type != null)
                return CollectPropertyNames(type);

            // Same-pass peer fallback: the *Props class for a UITKX peer
            // component is generated in this same compilation pass and is NOT
            // yet present as an INamedTypeSymbol the first time the parent is
            // processed.  Match the qualified props-type name against any peer
            // whose SourceQualifiedPropsTypeName equals it and use its declared
            // FunctionParams as the authoritative property surface.
            // Without this, UITKX0109 would silently miss every cross-file
            // user-component reference on a clean build.
            string sourceQualified = qualifiedTypeName.StartsWith("global::", StringComparison.Ordinal)
                ? qualifiedTypeName
                : "global::" + qualifiedTypeName;
            foreach (var peer in _peerComponentsByMetadataName.Values)
            {
                if (peer.SourceQualifiedPropsTypeName == sourceQualified)
                {
                    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!peer.FunctionParams.IsDefaultOrEmpty)
                    {
                        foreach (var p in peer.FunctionParams)
                            result.Add(ToPropName(p.PropSourceName));
                    }
                    return result;
                }
            }

            return new HashSet<string>();
        }

        /// <summary>
        /// Returns the simple type name of the companion props class for a PascalCase
        /// function component (convention: "{ComponentName}Props"), or <c>null</c> when
        /// no such class is found in the compilation.
        ///
        /// When a props class is found the emitter uses the typed <c>V.Func&lt;TProps&gt;</c>
        /// overload; otherwise it falls back to the no-props <c>V.Func(TypeName.Render)</c> call.
        /// </summary>
        public string? TryGetFuncComponentPropsTypeName(
            string componentTypeName,
            string resolvedComponentTypeName,
            ImmutableArray<string> usingNamespaces
        )
        {
            string candidate = $"{componentTypeName}Props";

            // Same-pass UITKX peers must win over any stale compiled metadata with
            // the same simple name so that cross-namespace composition targets the
            // current source-generated component shape.
            if (
                TryFindVisiblePeerComponent(componentTypeName, usingNamespaces, out var peer)
                && peer.EmitsGeneratedProps
            )
            {
                return peer.SourceQualifiedPropsTypeName;
            }

            // Try unqualified (global namespace)
            if (TryGetTypeSymbol(candidate, out var topLevelProps))
                return ToSourceQualifiedTypeName(topLevelProps);

            // Try each @using namespace
            foreach (var ns in usingNamespaces)
            {
                if (TryGetTypeSymbol($"{ns}.{candidate}", out var namespacedTopLevelProps))
                    return ToSourceQualifiedTypeName(namespacedTopLevelProps);
            }

            // Support already-compiled function-style components from referenced
            // assemblies: their generated props are nested as TypeName+TypeNameProps.
            foreach (
                var componentMetadataName in EnumerateCandidateComponentMetadataNames(
                    componentTypeName,
                    resolvedComponentTypeName,
                    usingNamespaces
                )
            )
            {
                if (
                    TryGetTypeSymbol(
                        $"{componentMetadataName}+{candidate}",
                        out var generatedNestedProps
                    )
                )
                    return ToSourceQualifiedTypeName(generatedNestedProps);
            }

            // Also check for a nested Props class (convention: TypeName.Props).
            // In Roslyn metadata names, nested classes use '+': TypeName+Props.
            // If found, return "TypeName.Props" so the emitter emits
            //   V.Func<TypeName.Props>(TypeName.Render, new TypeName.Props { ... })
            // This supports legacy C# static classes that follow the ValuesBarFunc
            // pattern (nested Props class rather than a sibling {TypeName}Props class).
            foreach (
                var componentMetadataName in EnumerateCandidateComponentMetadataNames(
                    componentTypeName,
                    resolvedComponentTypeName,
                    usingNamespaces
                )
            )
            {
                if (TryGetTypeSymbol($"{componentMetadataName}+Props", out var legacyNestedProps))
                    return ToSourceQualifiedTypeName(legacyNestedProps);
            }

            return null;
        }

        /// <summary>
        /// Namespaces of the peer components this file imports BY PATH (import/export grammar): an
        /// <c>import {{ X }} from "./file"</c> names the exact target file, so that peer's namespace
        /// is visible to tag/props resolution on the strength of the file import alone — no separate
        /// <c>import "@Target.Ns"</c> required. Without this, a cross-namespace file-imported
        /// component tag produced false UITKX0109 ("declares no parameters") because
        /// <see cref="TryFindVisiblePeerComponent"/> searched only namespace+usings+baseline.
        /// </summary>
        internal ImmutableArray<string> GetImportedPeerNamespaces(
            Language.Parser.DirectiveSet directives, string importerFilePath)
        {
            if (directives.Imports.IsDefaultOrEmpty
                || _peerComponentsByMetadataName.Count == 0
                || string.IsNullOrEmpty(importerFilePath))
                return ImmutableArray<string>.Empty;

            static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');

            // Peer source path → namespace (peers without a recorded path can't be matched).
            var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var peer in _peerComponentsByMetadataName.Values)
                if (!string.IsNullOrEmpty(peer.SourceFilePath) && !string.IsNullOrEmpty(peer.Namespace))
                    byPath[Norm(peer.SourceFilePath!)] = peer.Namespace;
            if (byPath.Count == 0)
                return ImmutableArray<string>.Empty;

            string importerDir = Norm(System.IO.Path.GetDirectoryName(importerFilePath) ?? string.Empty);
            string rootDir = Language.EffectiveNamespace.UiSourceRootDir(importerFilePath) ?? importerDir;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = ImmutableArray.CreateBuilder<string>();
            foreach (var imp in directives.Imports)
            {
                string? target = Language.ImportResolver.MapSpecifierToPath(
                    importerDir, imp.Specifier, rootDir, out _);
                if (target != null
                    && byPath.TryGetValue(Norm(target), out string? ns)
                    && seen.Add(ns))
                    result.Add(ns);
            }
            return result.ToImmutable();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private string ResolveFuncComponentTypeName(
            string typeName,
            ImmutableArray<string> usingNamespaces,
            out bool typeFound
        )
        {
            if (TryFindVisiblePeerComponent(typeName, usingNamespaces, out var peer))
            {
                typeFound = true;
                return peer.SourceQualifiedTypeName;
            }

            // Try unqualified first (rare but works in the global namespace)
            if (TryGetFuncTypeSymbol(typeName, out var globalSymbol))
            {
                typeFound = true;
                return ToSourceQualifiedTypeName(globalSymbol);
            }

            // Try each @using namespace
            foreach (var ns in usingNamespaces)
            {
                if (TryGetFuncTypeSymbol($"{ns}.{typeName}", out var namespacedSymbol))
                {
                    typeFound = true;
                    return ToSourceQualifiedTypeName(namespacedSymbol);
                }
            }

            typeFound = false;
            return typeName;
        }

        private bool TryFindVisiblePeerComponent(
            string typeName,
            ImmutableArray<string> searchNamespaces,
            out PeerComponentInfo peer
        )
        {
            string normalizedTypeName = NormalizeMetadataTypeName(typeName);

            if (
                normalizedTypeName.Contains(".", StringComparison.Ordinal)
                && _peerComponentsByMetadataName.TryGetValue(normalizedTypeName, out peer!)
            )
                return true;

            foreach (var ns in searchNamespaces)
            {
                if (
                    _peerComponentsByMetadataName.TryGetValue(
                        $"{ns}.{normalizedTypeName}",
                        out peer!
                    )
                )
                    return true;
            }

            // Global namespace fallback for unqualified peer references.
            if (_peerComponentsByMetadataName.TryGetValue(normalizedTypeName, out peer!))
                return true;

            peer = default!;
            return false;
        }

        private IEnumerable<string> EnumerateCandidateComponentMetadataNames(
            string componentTypeName,
            string resolvedComponentTypeName,
            ImmutableArray<string> usingNamespaces
        )
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            static bool Add(HashSet<string> set, string candidate) =>
                !string.IsNullOrWhiteSpace(candidate) && set.Add(candidate);

            string normalizedComponentTypeName = NormalizeMetadataTypeName(componentTypeName);
            if (Add(seen, normalizedComponentTypeName))
                yield return normalizedComponentTypeName;

            string normalizedResolvedTypeName = NormalizeMetadataTypeName(
                resolvedComponentTypeName
            );
            if (Add(seen, normalizedResolvedTypeName))
                yield return normalizedResolvedTypeName;

            foreach (var ns in usingNamespaces)
            {
                string candidate = $"{ns}.{normalizedComponentTypeName}";
                if (Add(seen, candidate))
                    yield return candidate;
            }
        }

        private bool TryGetFuncTypeSymbol(string metadataName, out INamedTypeSymbol? type)
        {
            type = _compilation.GetTypeByMetadataName(NormalizeMetadataTypeName(metadataName));
            if (type == null)
                return false;

            // Check: has a static Render method that returns VirtualNode
            return type.GetMembers("Render")
                .OfType<IMethodSymbol>()
                .Any(m =>
                    m.IsStatic
                    && m.DeclaredAccessibility == Accessibility.Public
                    && m.ReturnType.Name == VirtualNodeName
                );
        }

        private bool TryGetTypeSymbol(string metadataName, out INamedTypeSymbol? type)
        {
            type = _compilation.GetTypeByMetadataName(NormalizeMetadataTypeName(metadataName));
            return type != null;
        }

        private static string ToSourceQualifiedTypeName(INamedTypeSymbol type) =>
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // ── Build built-in map from Roslyn V type ─────────────────────────────

        private static Dictionary<string, TagResolution> BuildBuiltinMapFromCompilation(
            Compilation compilation,
            string? backend = null
        )
        {
            bool isUgui = backend == "ugui";
            var vType = compilation.GetTypeByMetadataName(isUgui ? UTypeName : VTypeName);
            if (vType == null)
            {
                // Compilation doesn't yet contain the shared (or ugui) assembly
                // (first load). Fall back to the hardcoded map for the backend.
                var fallback = isUgui ? s_uguiFallbackMap : s_fallbackMap;
                return new Dictionary<string, TagResolution>(
                    fallback.ToDictionary(kv => kv.Key, kv => kv.Value),
                    StringComparer.OrdinalIgnoreCase
                );
            }

            var map = new Dictionary<string, TagResolution>(StringComparer.OrdinalIgnoreCase);

            foreach (var member in vType.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.DeclaredAccessibility != Accessibility.Public)
                    continue;
                if (!member.IsStatic)
                    continue;
                if (member.ReturnType.Name != VirtualNodeName)
                    continue;
                if (member.Parameters.IsEmpty)
                    continue;

                var methodName = member.Name;
                var parameters = member.Parameters;
                var firstParam = parameters[0];
                var firstType = firstParam.Type;

                // Detect AcceptsChildren: last param is params VirtualNode[]
                bool acceptsChildren = false;
                var lastParam = parameters[parameters.Length - 1];
                if (
                    lastParam.IsParams
                    && lastParam.Type is IArrayTypeSymbol arr
                    && arr.ElementType.Name == VirtualNodeName
                )
                {
                    acceptsChildren = true;
                }

                // Classify
                TagResolutionKind kind;
                string? propsTypeName = null;

                if (string.Equals(methodName, "Fragment", StringComparison.Ordinal))
                {
                    kind = TagResolutionKind.Fragment;
                }
                else if (
                    string.Equals(methodName, "Text", StringComparison.Ordinal)
                    && firstType.SpecialType == SpecialType.System_String
                )
                {
                    kind = TagResolutionKind.BuiltinText;
                }
                else if (firstType.Name.EndsWith("Props", StringComparison.Ordinal))
                {
                    kind = TagResolutionKind.BuiltinTyped;
                    propsTypeName = firstType.Name;
                }
                else if (
                    firstType.Name == "IReadOnlyDictionary"
                    || firstType.Name == "Dictionary"
                    || (
                        firstParam.Type is INamedTypeSymbol namedFirst
                        && namedFirst.ConstructedFrom?.Name == "IReadOnlyDictionary"
                    )
                )
                {
                    kind = TagResolutionKind.BuiltinDictionary;
                }
                else
                {
                    // Func, ForwardRef, Router, etc. — skip
                    // (Portal and Suspense are registered manually below)
                    continue;
                }

                var key = methodName.ToLowerInvariant();

                // Prefer the typed overload when multiple overloads exist for the same tag
                if (
                    !map.TryGetValue(key, out var existing)
                    || kind == TagResolutionKind.BuiltinTyped
                )
                {
                    map[key] = new TagResolution(kind, methodName, propsTypeName, acceptsChildren);
                }
            }

            // Ensure "fragment" is always present
            if (!map.ContainsKey("fragment"))
                map["fragment"] = new TagResolution(
                    TagResolutionKind.Fragment,
                    "Fragment",
                    null,
                    true
                );

            // Suspense is a first-class built-in: V.Suspense() arguments are emitted
            // by a dedicated code path (EmitSuspense) that maps well-known attribute
            // names (isReady, pendingTask, fallback) to the correct overload.
            // V.Suspense's first param is Func<bool> — the Roslyn scanner skips it,
            // so we register it manually here.
            if (!map.ContainsKey("suspense"))
                map["suspense"] = new TagResolution(
                    TagResolutionKind.BuiltinSuspense,
                    "Suspense",
                    null,
                    AcceptsChildren: true
                );

            // Portal: V.Portal(VisualElement, string key, params VirtualNode[]).
            // First param is VisualElement — the scanner skips it, so register manually.
            if (!map.ContainsKey("portal"))
                map["portal"] = new TagResolution(
                    TagResolutionKind.BuiltinPortal,
                    "Portal",
                    null,
                    AcceptsChildren: true
                );

            // VisualElementSafe: V.VisualElementSafe(object, string key, params VirtualNode[]).
            // First param is object — the scanner skips it, so register manually.
            // Emitted via the dictionary code path (the runtime accepts a Dictionary<string,object>
            // as the object argument and extracts style + safe-area insets).
            // UITK-only: the ugui vocabulary has no dictionary-path factory.
            if (!isUgui && !map.ContainsKey("visualelementsafe"))
                map["visualelementsafe"] = new TagResolution(
                    TagResolutionKind.BuiltinDictionary,
                    "VisualElementSafe",
                    null,
                    AcceptsChildren: true
                );

            return map;
        }

        // ── Fallback hardcoded map ────────────────────────────────────────────

        private static IReadOnlyDictionary<string, TagResolution> BuildUguiFallbackMap()
        {
            static TagResolution Typed(string name, string props, bool children = true) =>
                new TagResolution(TagResolutionKind.BuiltinTyped, name, props, children);

            return new Dictionary<string, TagResolution>(StringComparer.OrdinalIgnoreCase)
            {
                ["canvas"] = Typed("Canvas", "UguiCanvasProps"),
                ["panel"] = Typed("Panel", "UguiPanelProps"),
                ["image"] = Typed("Image", "UguiImageProps"),
                ["rawimage"] = Typed("RawImage", "UguiRawImageProps"),
                ["button"] = Typed("Button", "UguiButtonProps"),
                ["horizontallayoutgroup"] = Typed(
                    "HorizontalLayoutGroup",
                    "UguiHorizontalLayoutGroupProps"
                ),
                ["verticallayoutgroup"] = Typed(
                    "VerticalLayoutGroup",
                    "UguiVerticalLayoutGroupProps"
                ),
                ["gridlayoutgroup"] = Typed("GridLayoutGroup", "UguiGridLayoutGroupProps"),
                ["toggle"] = Typed("Toggle", "UguiToggleProps"),
                ["togglegroup"] = Typed("ToggleGroup", "UguiToggleGroupProps"),
                ["slider"] = Typed("Slider", "UguiSliderProps"),
                ["scrollbar"] = Typed("Scrollbar", "UguiScrollbarProps"),
                ["scrollrect"] = Typed("ScrollRect", "UguiScrollRectProps"),
                ["dropdown"] = Typed("Dropdown", "UguiDropdownProps"),
                ["inputfield"] = Typed("InputField", "UguiInputFieldProps"),
                ["prefab"] = Typed("Prefab", "UguiPrefabProps", children: false),
                ["uitkhost"] = Typed("UitkHost", "UguiUitkHostProps", children: false),

                // Matches the scanner's typed-overload preference: U.Text has both
                // a UguiTextProps overload (wins) and a string sugar overload.
                ["text"] = Typed("Text", "UguiTextProps"),
                ["fragment"] = new TagResolution(
                    TagResolutionKind.Fragment,
                    "Fragment",
                    null,
                    true
                ),
                ["suspense"] = new TagResolution(
                    TagResolutionKind.BuiltinSuspense,
                    "Suspense",
                    null,
                    AcceptsChildren: true
                ),
                ["portal"] = new TagResolution(
                    TagResolutionKind.BuiltinPortal,
                    "Portal",
                    null,
                    AcceptsChildren: true
                ),
            };
        }

        private static IReadOnlyDictionary<string, TagResolution> BuildFallbackMap()
        {
            static TagResolution Typed(string name, string props, bool children = false) =>
                new TagResolution(TagResolutionKind.BuiltinTyped, name, props, children);

            static TagResolution Dict(string name) =>
                new TagResolution(TagResolutionKind.BuiltinDictionary, name, null, true);

            return new Dictionary<string, TagResolution>(StringComparer.OrdinalIgnoreCase)
            {
                ["label"] = Typed("Label", "LabelProps"),
                ["button"] = Typed("Button", "ButtonProps"),
                ["textfield"] = Typed("TextField", "TextFieldProps"),
                ["toggle"] = Typed("Toggle", "ToggleProps"),
                ["slider"] = Typed("Slider", "SliderProps"),
                ["sliderint"] = Typed("SliderInt", "SliderIntProps"),
                ["image"] = Typed("Image", "ImageProps"),
                ["box"] = Typed("Box", "BoxProps", children: true),
                ["scrollview"] = Typed("ScrollView", "ScrollViewProps", children: true),
                ["listview"] = Typed("ListView", "ListViewProps"),
                ["treeview"] = Typed("TreeView", "TreeViewProps"),
                ["foldout"] = Typed("Foldout", "FoldoutProps", children: true),
                ["groupbox"] = Typed("GroupBox", "GroupBoxProps", children: true),
                ["helpbox"] = Typed("HelpBox", "HelpBoxProps"),
                ["progressbar"] = Typed("ProgressBar", "ProgressBarProps"),
                ["tab"] = Typed("Tab", "TabProps"),
                ["tabview"] = Typed("TabView", "TabViewProps"),
                ["textelement"] = Typed("TextElement", "TextElementProps"),
                ["radiobutton"] = Typed("RadioButton", "RadioButtonProps"),
                ["radiobuttongroup"] = Typed("RadioButtonGroup", "RadioButtonGroupProps"),
                ["dropdownfield"] = Typed("DropdownField", "DropdownFieldProps"),
                ["enumfield"] = Typed("EnumField", "EnumFieldProps"),
                ["integerfield"] = Typed("IntegerField", "IntegerFieldProps"),
                ["floatfield"] = Typed("FloatField", "FloatFieldProps"),

                ["visualelement"] = Dict("VisualElement"),

                ["text"] = new TagResolution(TagResolutionKind.BuiltinText, "Text", null, false),
                ["fragment"] = new TagResolution(
                    TagResolutionKind.Fragment,
                    "Fragment",
                    null,
                    true
                ),
                ["suspense"] = new TagResolution(
                    TagResolutionKind.BuiltinSuspense,
                    "Suspense",
                    null,
                    AcceptsChildren: true
                ),
                ["portal"] = new TagResolution(
                    TagResolutionKind.BuiltinPortal,
                    "Portal",
                    null,
                    AcceptsChildren: true
                ),
                ["errorboundary"] = Typed("ErrorBoundary", "ErrorBoundaryProps", children: true),
                ["visualelementsafe"] = Dict("VisualElementSafe"),

                // Function-component built-ins that delegate to V.Func<TProps>
                // internally but, from the markup-resolver perspective, look
                // identical to a typed-children element. Listed here so SG
                // tests that don't reference the real V type can resolve them
                // (HMR auto-discovers them via reflection regardless).
                ["animate"] = Typed("Animate", "AnimateProps", children: true),
                ["audio"] = Typed("Audio", "AudioProps", children: true),
                ["video"] = Typed("Video", "VideoProps", children: true),
            };
        }
    }
}
