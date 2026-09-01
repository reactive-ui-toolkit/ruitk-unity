using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ruitk.Language.Parser;

namespace Ruitk.Language
{
    /// <summary>
    /// The single source of truth for "what <c>using</c> lines does a file's <c>import</c> set
    /// imply" (import/export grammar §6.2/§6.3): for every <c>import {{ X }} from "./file"</c>
    /// this resolves the specifier to its target, parses it, computes the target's EFFECTIVE
    /// (path-derived, config-aware) namespace, and yields the injection payloads:
    /// <list type="bullet">
    ///   <item><description>hook import → <c>static {Ns}.{Container}</c> (whole container — C# has
    ///   no per-method static import);</description></item>
    ///   <item><description>module import → <c>{Name} = {Ns}.{Name}</c> alias (skipped same-namespace
    ///   → CS0576, and for <see cref="ReservedTypeAliases"/> → CS1537);</description></item>
    ///   <item><description>component import → <c>{Name} = {Ns}.{Name}</c> alias, same guards — a
    ///   C# BODY reference to an imported component's type (<c>Preset.PresetProps</c>,
    ///   <c>TableView.Column</c>) needs it; tags alone don't (they emit FQN calls).</description></item>
    /// </list>
    /// <para>Consumed by the LSP virtual-document generator (typed) and by the Unity HMR compiler
    /// (via reflection, like <see cref="EffectiveNamespace"/>) so editor IntelliSense, hot reload,
    /// and the real build agree on what an import brings into scope. The source generator computes
    /// the equivalent from its pre-scan peer tables (no file IO in the incremental pipeline) —
    /// behavioral parity with it is pinned by tests.</para>
    /// <para>Filesystem-reads each target; degrades silently (no line) when a target is
    /// unresolvable/unreadable, matching the historical VDG behavior.</para>
    /// </summary>
    public static class ImportScopeFacts
    {
        /// <summary>
        /// Type-alias identifiers the emitters emit unconditionally (<c>using Color =
        /// UnityEngine.Color;</c>, …). An imported module/component whose name matches one cannot be
        /// injected as an alias (CS1537 duplicate alias). Mirrored by
        /// <c>UitkxPipeline.ReservedTypeAliases</c> (SG side) — keep in lockstep.
        /// </summary>
        public static readonly HashSet<string> ReservedTypeAliases = new HashSet<string>(StringComparer.Ordinal)
        {
            "Color", "UColor", "EasingFunction", "EasingMode", "BackgroundRepeat",
            "BackgroundPosition", "BackgroundSize", "TransformOrigin", "BackgroundPositionKeyword",
            "BackgroundSizeType", "Repeat", "Length", "StyleKeyword", "TextAutoSizeMode",
            "FilterFunction", "Ratio", "StyleRatio", "MaterialDefinition", "StyleMaterialDefinition",
        };

        /// <summary>
        /// Computes the injected-using payloads for <paramref name="directives"/>' imports (the text
        /// between <c>using </c> and <c>;</c> — e.g. <c>static My.Ns.FooHooks</c> or
        /// <c>Widget = My.Ns.Widget</c>), deduplicated, in import order. Empty when the file has no
        /// imports or none resolve.
        /// </summary>
        /// <summary>Reads a resolved import target. An editor holding a module as
        /// an UNSAVED buffer supplies this so the module can be found at all; a
        /// null overlay, or a null return from it, falls through to disk.
        ///
        /// Without it, every one of the four lookups below asked the filesystem
        /// directly, so a module that had never been written could not be an import
        /// target: no using alias was emitted for it, and every reference to it
        /// failed to compile with CS0103 on the alias name. The RUITK Builder holds
        /// entire trees in memory by design, which makes that the normal case
        /// rather than an edge one.</summary>
        public static Func<string, string?>? SourceOverlay;

        private static DirectiveSet? ReadTargetDirectives(string target)
        {
            string? text = null;
            bool overlaid = SourceOverlay != null;
            try
            {
                text = SourceOverlay?.Invoke(target);
            }
            catch { text = null; }

            if (text == null)
            {
                // An installed overlay is AUTHORITATIVE. It speaks for a caller
                // that holds its modules in memory (the RUITK Builder), and it
                // already falls through to disk itself for anything it does not
                // own - so a null here means the module genuinely is not there.
                // Reading the file anyway resurrects what that caller has
                // deleted or moved away, which is the one module question this
                // file could still answer from the wrong world.
                //
                // No overlay means HMR, whose truth IS the disk: unchanged.
                if (overlaid)
                    return null;
                if (!File.Exists(target))
                    return null;
                try
                {
                    text = File.ReadAllText(target);
                }
                catch { return null; }
            }

            try
            {
                return DirectiveParser.Parse(text, target, new List<ParseDiagnostic>());
            }
            catch { return null; }
        }

        public static IReadOnlyList<string> ComputeInjectedUsingPayloads(
            DirectiveSet directives, string uitkxFilePath)
        {
            var result = new List<string>();
            if (directives.Imports.IsDefaultOrEmpty || string.IsNullOrEmpty(uitkxFilePath))
                return result;

            string importerDir = (Path.GetDirectoryName(uitkxFilePath) ?? string.Empty).Replace('\\', '/');
            string rootDir = EffectiveNamespace.UiSourceRootDir(uitkxFilePath) ?? importerDir;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var imp in directives.Imports)
            {
                string? target = ImportResolver.MapSpecifierToPath(
                    importerDir, imp.Specifier, rootDir, out _);
                if (target == null)
                    continue;
                DirectiveSet? resolved = ReadTargetDirectives(target);
                if (resolved == null)
                    continue;
                DirectiveSet tds = resolved;

                // EFFECTIVE namespace of the target — the namespace its generated types actually
                // land in (explicit @namespace wins, else path-derived + config prefix).
                string? tns = EffectiveNamespace.Resolve(
                    tds.HasExplicitNamespace, tds.Namespace, target, fileKeyed: true);
                if (string.IsNullOrEmpty(tns))
                    continue;

                // ── U-03 lowering table ──
                // Members (values/utils/hooks) live on the target's per-file `__Exports`
                // container. Aliased member imports get typed BRIDGES (emitter-side, not
                // usings); aliased component imports are plain alias-renames.
                {
                    var aliasesN = imp.Aliases.IsDefaultOrEmpty
                        ? System.Collections.Immutable.ImmutableArray<string?>.Empty : imp.Aliases;
                    bool AliasedAt(int k) => k < aliasesN.Length && aliasesN[k] != null;

                    // NOTE: no exclusive branching — a COMBINED import (`import Def, { a } from`
                    // / `import Def, * as X from`) carries default + named/star parts in one
                    // declaration and every part must yield its payload.
                    if (imp.IsStar && imp.StarAlias != null)
                    {
                        // `import * as X` → alias-to-type of the whole exports container.
                        // Component-only targets emit no __Exports unit — no alias (the build
                        // side skips it identically; dotted tags resolve via the tag maps).
                        bool targetHasExportedMembers = !tds.MemberDeclarations.IsDefaultOrEmpty
                            && System.Linq.Enumerable.Any(tds.MemberDeclarations, m => m.IsExported);
                        if (targetHasExportedMembers && !ReservedTypeAliases.Contains(imp.StarAlias))
                        {
                            string line = $"{imp.StarAlias} = {tns}.__Exports";
                            if (seen.Add(line))
                                result.Add(line);
                        }
                    }

                    if (imp.IsDefault && imp.DefaultAlias != null)
                    {
                        // Default import: component default → alias to the component type;
                        // RENAMED member default → bridge (emitter-side, no using here);
                        // SAME-NAME member default → the container using, like a named import
                        // (a bridge would CS0121-collide with the container's public member
                        // whenever a named part or second import injects the container too —
                        // field find).
                        string? defName = tds.DefaultExportName;
                        bool defIsComponent = defName != null
                            && !tds.ComponentDeclarations.IsDefaultOrEmpty
                            && System.Linq.Enumerable.Any(tds.ComponentDeclarations, c => c.Name == defName);
                        if (defIsComponent && !ReservedTypeAliases.Contains(imp.DefaultAlias))
                        {
                            string line = $"{imp.DefaultAlias} = {tns}.{defName}";
                            if (seen.Add(line))
                                result.Add(line);
                        }
                        else if (defName != null && !defIsComponent && imp.DefaultAlias == defName
                            && !tds.MemberDeclarations.IsDefaultOrEmpty
                            && System.Linq.Enumerable.Any(tds.MemberDeclarations, m => m.Name == defName))
                        {
                            string line = $"static {tns}.__Exports";
                            if (seen.Add(line))
                                result.Add(line);
                        }
                    }

                    bool exportsContainerAdded = false;
                    for (int k = 0; k < imp.Names.Length; k++)
                    {
                        string name = imp.Names[k];

                        bool isComponent = !tds.ComponentDeclarations.IsDefaultOrEmpty
                            && System.Linq.Enumerable.Any(tds.ComponentDeclarations, c => c.IsExported && c.Name == name);
                        if (isComponent)
                        {
                            string bound = AliasedAt(k) ? aliasesN[k]! : name;
                            if (!ReservedTypeAliases.Contains(bound))
                            {
                                string line = $"{bound} = {tns}.{name}";
                                if (seen.Add(line))
                                    result.Add(line);
                            }
                            continue;
                        }

                        bool isMember = !tds.MemberDeclarations.IsDefaultOrEmpty
                            && System.Linq.Enumerable.Any(tds.MemberDeclarations, m => m.IsExported && m.Name == name);
                        if (isMember && !AliasedAt(k) && !exportsContainerAdded)
                        {
                            // Un-aliased member import → the whole container once per target
                            // file (C# has no per-member static import; per-name strictness
                            // stays a uitkx diagnostic, same rationale as the legacy hook path).
                            string line = $"static {tns}.__Exports";
                            if (seen.Add(line))
                                result.Add(line);
                            exportsContainerAdded = true;
                        }
                        // Aliased members → bridge emission (M3, consumer's __Exports) — no using.
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Imported-member bridge surface (ES-modules campaign, U-03): for every `as`-renamed or
        /// default MEMBER import (values/utils/hooks — components alias via usings instead), the
        /// bound name + signature the consumer's <c>__Exports</c> re-states. Consumed by the LSP
        /// virtual doc (scaffold stubs so the bound name resolves in setup code); the SG computes
        /// the equivalent from its pre-scan PeerExportsInfo table (no file IO in the incremental
        /// pipeline) — shape parity is pinned by tests. Filesystem-parses each target like
        /// <see cref="ComputeInjectedUsingPayloads"/>; degrades silently on unresolvable targets.
        /// </summary>
        public static IReadOnlyList<(string Alias, string? ReturnType, string? ParamsText, bool IsValue, string? TypeParams)>
            ComputeImportedMemberBridges(DirectiveSet directives, string uitkxFilePath)
        {
            var result = new List<(string, string?, string?, bool, string?)>();
            if (directives.Imports.IsDefaultOrEmpty || string.IsNullOrEmpty(uitkxFilePath))
                return result;

            string importerDir = (Path.GetDirectoryName(uitkxFilePath) ?? string.Empty).Replace('\\', '/');
            string rootDir = EffectiveNamespace.UiSourceRootDir(uitkxFilePath) ?? importerDir;

            foreach (var imp in directives.Imports)
            {
                bool anyAlias = !imp.Aliases.IsDefaultOrEmpty && System.Linq.Enumerable.Any(imp.Aliases, a => a != null);
                if (!anyAlias && !imp.IsDefault)
                    continue;

                string? target = ImportResolver.MapSpecifierToPath(importerDir, imp.Specifier, rootDir, out _);
                if (target == null)
                    continue;
                DirectiveSet? resolved = ReadTargetDirectives(target);
                if (resolved == null)
                    continue;
                DirectiveSet tds = resolved;
                if (tds.MemberDeclarations.IsDefaultOrEmpty)
                    continue;

                MemberDeclaration? Find(string name)
                {
                    foreach (var m in tds.MemberDeclarations)
                        if (m.IsExported && m.Name == name)
                            return m;
                    return null;
                }

                // No exclusive branching — combined imports need default AND named bridges.
                // Same-name member defaults lower to the container using, not a bridge
                // (CS0121 field find) — only RENAMED defaults bridge.
                if (imp.IsDefault && imp.DefaultAlias != null && tds.DefaultExportName != null
                    && imp.DefaultAlias != tds.DefaultExportName)
                {
                    bool defaultIsComponent = !tds.ComponentDeclarations.IsDefaultOrEmpty
                        && System.Linq.Enumerable.Any(tds.ComponentDeclarations, c => c.Name == tds.DefaultExportName);
                    if (!defaultIsComponent)
                    {
                        var dm = Find(tds.DefaultExportName);
                        if (dm != null)
                            result.Add((imp.DefaultAlias,
                                dm.ReturnTypeText ?? ExtractNewInitializerTypeName(dm.BodyText),
                                dm.ParamsText, dm.Kind == DeclKind.Value, dm.TypeParamsText));
                    }
                }

                if (!anyAlias)
                    continue;
                for (int k = 0; k < imp.Names.Length && k < imp.Aliases.Length; k++)
                {
                    string? alias = imp.Aliases[k];
                    if (alias == null) continue;
                    var m = Find(imp.Names[k]);
                    if (m != null)
                        result.Add((alias,
                            m.ReturnTypeText ?? ExtractNewInitializerTypeName(m.BodyText),
                            m.ParamsText, m.Kind == DeclKind.Value, m.TypeParamsText));
                }
            }
            return result;
        }

        /// <summary>
        /// Tag maps for the U-05/U-03 markup surface (audit H3) — the disk-parse twin of the
        /// SG's <c>BuildImportAliasTagMaps</c>, shared with the HMR emitter via reflection:
        /// star-alias → target file-keyed namespace (dotted tags <c>&lt;X.Comp/&gt;</c>), and
        /// bound name → component FQN (renamed / default component tags <c>&lt;Tile/&gt;</c>).
        /// New-mode targets only; unresolvable entries degrade silently like every other
        /// helper here.
        /// </summary>
        public static Dictionary<string, string> ComputeStarImportNamespaces(
            DirectiveSet directives, string uitkxFilePath)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            ForEachNewModeImportTarget(directives, uitkxFilePath, (imp, tds, tns) =>
            {
                if (imp.IsStar && imp.StarAlias != null)
                    map[imp.StarAlias] = tns;
            });
            return map;
        }

        /// <summary>Bound tag name → component FQN for `as`-renamed and default COMPONENT
        /// imports (see <see cref="ComputeStarImportNamespaces"/>).</summary>
        public static Dictionary<string, string> ComputeImportAliasTypeMap(
            DirectiveSet directives, string uitkxFilePath)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            ForEachNewModeImportTarget(directives, uitkxFilePath, (imp, tds, tns) =>
            {
                bool IsExportedComponent(string name) =>
                    !tds.ComponentDeclarations.IsDefaultOrEmpty
                    && System.Linq.Enumerable.Any(tds.ComponentDeclarations, c => c.IsExported && c.Name == name);

                // No exclusive branching — combined imports contribute default AND named tags.
                if (imp.IsDefault && imp.DefaultAlias != null)
                {
                    if (tds.DefaultExportName != null && IsExportedComponent(tds.DefaultExportName))
                        map[imp.DefaultAlias] = $"{tns}.{tds.DefaultExportName}";
                }
                if (imp.Aliases.IsDefaultOrEmpty)
                    return;
                for (int k = 0; k < imp.Names.Length && k < imp.Aliases.Length; k++)
                {
                    string? alias = imp.Aliases[k];
                    if (alias != null && IsExportedComponent(imp.Names[k]))
                        map[alias] = $"{tns}.{imp.Names[k]}";
                }
            });
            return map;
        }

        private static void ForEachNewModeImportTarget(
            DirectiveSet directives, string uitkxFilePath,
            Action<ImportDeclaration, DirectiveSet, string> visit)
        {
            if (directives.Imports.IsDefaultOrEmpty || string.IsNullOrEmpty(uitkxFilePath))
                return;
            string importerDir = (Path.GetDirectoryName(uitkxFilePath) ?? string.Empty).Replace('\\', '/');
            string rootDir = EffectiveNamespace.UiSourceRootDir(uitkxFilePath) ?? importerDir;
            foreach (var imp in directives.Imports)
            {
                bool interesting = imp.IsStar || imp.IsDefault
                    || (!imp.Aliases.IsDefaultOrEmpty && System.Linq.Enumerable.Any(imp.Aliases, a => a != null));
                if (!interesting)
                    continue;
                string? target = ImportResolver.MapSpecifierToPath(importerDir, imp.Specifier, rootDir, out _);
                if (target == null)
                    continue;
                DirectiveSet? resolved = ReadTargetDirectives(target);
                if (resolved == null)
                    continue;
                DirectiveSet tds = resolved;
                string? tns = EffectiveNamespace.Resolve(
                    tds.HasExplicitNamespace, tds.Namespace, target, fileKeyed: true);
                if (!string.IsNullOrEmpty(tns))
                    visit(imp, tds, tns!);
            }
        }

        /// <summary>
        /// Rendered C# bridge lines for every `as`-renamed / default MEMBER import (audit H1/H4):
        /// the exact <c>internal static …</c> forwarding lines the SG's <c>ExportsEmitter</c>
        /// emits into the consumer's <c>__Exports</c> — byte-shape parity is what lets the HMR
        /// hot unit compile the same surface (reached by reflection; contract-test pinned).
        /// Forwards to <c>global::{targetNs}.__Exports.{original}</c>, which is PUBLIC in the
        /// project assembly (bridges only target exported members), so the lines are valid in
        /// both the SG unit and a cross-assembly hot unit.
        /// </summary>
        public static IReadOnlyList<string> ComputeImportedMemberBridgeLines(
            DirectiveSet directives, string uitkxFilePath)
        {
            var lines = new List<string>();
            if (directives.Imports.IsDefaultOrEmpty || string.IsNullOrEmpty(uitkxFilePath))
                return lines;

            string importerDir = (Path.GetDirectoryName(uitkxFilePath) ?? string.Empty).Replace('\\', '/');
            string rootDir = EffectiveNamespace.UiSourceRootDir(uitkxFilePath) ?? importerDir;

            foreach (var imp in directives.Imports)
            {
                bool anyAlias = !imp.Aliases.IsDefaultOrEmpty && System.Linq.Enumerable.Any(imp.Aliases, a => a != null);
                if (!anyAlias && !imp.IsDefault)
                    continue;

                string? target = ImportResolver.MapSpecifierToPath(importerDir, imp.Specifier, rootDir, out _);
                if (target == null)
                    continue;
                DirectiveSet? resolved = ReadTargetDirectives(target);
                if (resolved == null)
                    continue;
                DirectiveSet tds = resolved;
                if (tds.MemberDeclarations.IsDefaultOrEmpty)
                    continue;
                string? tns = EffectiveNamespace.Resolve(
                    tds.HasExplicitNamespace, tds.Namespace, target, fileKeyed: true);
                if (string.IsNullOrEmpty(tns))
                    continue;

                MemberDeclaration? Find(string name)
                {
                    foreach (var m in tds.MemberDeclarations)
                        if (m.IsExported && m.Name == name)
                            return m;
                    return null;
                }

                void Append(string alias, MemberDeclaration m)
                {
                    if (m.Kind == DeclKind.Value)
                    {
                        string type = m.ReturnTypeText ?? ExtractNewInitializerTypeName(m.BodyText) ?? "object";
                        lines.Add($"        internal static {type} {alias} => global::{tns}.__Exports.{m.Name};");
                        return;
                    }
                    string ret = m.ReturnTypeText ?? "void";
                    // F9: a generic member forwards through a generic bridge — the bridge's
                    // own type parameters close the target's (`alias<T>(…) => target.Name<T>(…)`).
                    string typeParams = m.TypeParamsText ?? string.Empty;
                    var ps = m.Params;
                    var pl = new StringBuilder();
                    var an = new StringBuilder();
                    if (!ps.IsDefaultOrEmpty)
                        for (int i = 0; i < ps.Length; i++)
                        {
                            if (i > 0) { pl.Append(", "); an.Append(", "); }
                            pl.Append(ps[i].Type).Append(' ').Append(ps[i].Name);
                            if (ps[i].DefaultValue != null)
                                pl.Append(" = ").Append(ps[i].DefaultValue);
                            if (ps[i].Type.StartsWith("ref ", StringComparison.Ordinal)) an.Append("ref ");
                            else if (ps[i].Type.StartsWith("out ", StringComparison.Ordinal)) an.Append("out ");
                            an.Append(ps[i].Name);
                        }
                    lines.Add($"        internal static {ret} {alias}{typeParams}({pl}) => global::{tns}.__Exports.{m.Name}{typeParams}({an});");
                }

                // No exclusive branching — combined imports need default AND named bridges.
                // Same-name member defaults lower to the container using, not a bridge
                // (CS0121 field find) — only RENAMED defaults bridge.
                if (imp.IsDefault && imp.DefaultAlias != null && tds.DefaultExportName != null
                    && imp.DefaultAlias != tds.DefaultExportName)
                {
                    bool defaultIsComponent = !tds.ComponentDeclarations.IsDefaultOrEmpty
                        && System.Linq.Enumerable.Any(tds.ComponentDeclarations, c => c.Name == tds.DefaultExportName);
                    if (!defaultIsComponent)
                    {
                        var dm = Find(tds.DefaultExportName);
                        if (dm != null)
                            Append(imp.DefaultAlias, dm);
                    }
                }

                if (!anyAlias)
                    continue;
                for (int k = 0; k < imp.Names.Length && k < imp.Aliases.Length; k++)
                {
                    string? alias = imp.Aliases[k];
                    if (alias == null) continue;
                    var m = Find(imp.Names[k]);
                    if (m != null)
                        Append(alias, m);
                }
            }
            return lines;
        }

        /// <summary>
        /// Rewrites a using payload for emission INSIDE a namespace block (ES-modules campaign,
        /// M7): inside-namespace usings resolve RELATIVE to the enclosing namespaces, so
        /// <c>using Samples.X;</c> inside <c>Ruitk.Samples…</c> re-resolves `Samples`
        /// against <c>Ruitk.Samples</c> and breaks (found live). <c>global::</c> makes the
        /// payload absolute: plain → <c>global::Ns</c>; <c>static T</c> → <c>static global::T</c>;
        /// <c>A = T</c> → <c>A = global::T</c>. Idempotent on already-global payloads.
        /// </summary>
        public static string GlobalizeUsingPayload(string payload)
        {
            string p = payload.Trim();
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

        /// <summary>Extracts <c>T</c> from a <c>new T {...}</c>/<c>new T(...)</c> initializer
        /// (G-04 inference sugar) — the language-lib single source; the SG's ExportsEmitter keeps
        /// a thin call-through, HMR keeps a reflective-world mirror pinned by contract test.</summary>
        public static string? ExtractNewInitializerTypeName(string initText)
        {
            if (string.IsNullOrEmpty(initText))
                return null;
            string t = initText.TrimStart();
            if (!t.StartsWith("new", StringComparison.Ordinal))
                return null;
            int p = 3;
            while (p < t.Length && (t[p] == ' ' || t[p] == '\t')) p++;
            int start = p;
            int depth = 0;
            while (p < t.Length)
            {
                char c = t[p];
                if (c == '<') { depth++; p++; continue; }
                if (c == '>') { depth--; p++; continue; }
                if (depth == 0 && (c == '{' || c == '(' || c == ' ' || c == '\t' || c == '\r' || c == '\n'))
                    break;
                if (depth == 0 && c == '[')
                {
                    while (p < t.Length && t[p] != ']') p++;
                    if (p < t.Length) p++;
                    continue;
                }
                p++;
            }
            string name = t.Substring(start, p - start).Trim();
            return name.Length > 0 ? name : null;
        }

        // (DeriveHookContainerClassName was removed in the 0.16.0 legacy wave — every
        // member-bearing file's container is the per-file __Exports.)
    }
}
