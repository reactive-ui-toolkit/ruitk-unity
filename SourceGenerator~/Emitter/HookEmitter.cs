using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Ruitk.Language.Parser;

namespace Ruitk.SourceGenerator.Emitter
{
    /// <summary>
    /// Emits C# source for <c>hook</c> declarations parsed from .uitkx files.
    ///
    /// Each hook generates:
    /// <list type="bullet">
    ///   <item><description>Non-generic: <c>Func&lt;...&gt;</c> delegate field for HMR + trampoline + body method</description></item>
    ///   <item><description>Generic: <c>MethodInfo</c> field + <c>ConcurrentDictionary</c> cache for HMR + trampoline + body method</description></item>
    /// </list>
    ///
    /// All HMR indirection is wrapped in <c>#if UNITY_EDITOR</c> guards — zero overhead in builds.
    /// </summary>
    public static class HookEmitter
    {
        // (The legacy {Stem}Hooks container Emit path and its refresh companion were
        // removed in the 0.16.0 legacy wave — hooks emit through ExportsEmitter's
        // per-file __Exports container, which calls EmitSingleHook below.)

        /// <summary>
        /// Maps each bare custom-hook name to its PATH-QUALIFIED family key via
        /// <paramref name="map"/> (built by the pipeline from the peer-container table +
        /// this file's imports + its own container). A name with no mapping (e.g. an
        /// unresolved reference) falls back to the bare name. Keeping the same map on the
        /// producer and every consumer is what makes the runtime string-match work.
        /// </summary>
        internal static string[] QualifyKeys(
            string[] bareKeys, IReadOnlyDictionary<string, string>? map)
        {
            if (bareKeys == null || bareKeys.Length == 0 || map == null || map.Count == 0)
                return bareKeys ?? Array.Empty<string>();
            var result = new string[bareKeys.Length];
            for (int i = 0; i < bareKeys.Length; i++)
                result[i] = map.TryGetValue(bareKeys[i], out var qualified) ? qualified : bareKeys[i];
            return result;
        }

        private static string EscapeStringLiteral(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // `access` (ES-modules campaign, U-02): the legacy hook-container path always emits
        // `public` (every legacy container member was public); the __Exports path passes
        // `internal` for non-exported members. Optional with the legacy default so the
        // existing call site — and the HMR mirror's shape — are unchanged.
        internal static void EmitSingleHook(StringBuilder sb, HookDeclaration hook, string linePath, IList<Diagnostic> diagnostics, string access = "public")
        {
            bool isGeneric = !string.IsNullOrEmpty(hook.GenericParams);
            bool isVoid = string.IsNullOrEmpty(hook.ReturnType);
            string paramList = BuildParamList(hook.Params);
            string paramNames = BuildParamNames(hook.Params);
            string genericSuffix = hook.GenericParams ?? string.Empty;
            string bodyMethodName = $"__{hook.Name}_body";
            string hmrFieldName = $"__hmr_{hook.Name}";
            string returnType = isVoid ? "void" : hook.ReturnType!;

            // Apply hook aliases and resolve relative asset paths to absolute
            // Unity registry keys (parity with component setup code so that
            // Asset<T>("./x.png") works inside hook bodies just like inside
            // component setup code).
            string transformedBody = EmitContext.ApplyHookAliases(hook.Body);
            transformedBody = EmitContext.ResolveAssetPaths(transformedBody, linePath, diagnostics);

            // Extract hook signature for [HookSignature] attribute
            string hookSig = EmitContext.ExtractHookSignature(hook.Body);

            sb.AppendLine();

            if (isGeneric)
            {
                // ── Generic hook: MethodInfo + ConcurrentDictionary cache ─────
                sb.AppendLine("#if UNITY_EDITOR");
                sb.AppendLine($"        [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                sb.AppendLine($"        internal static global::System.Reflection.MethodInfo {hmrFieldName} = null;");
                sb.AppendLine($"        [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                sb.AppendLine($"        internal static readonly global::System.Collections.Concurrent.ConcurrentDictionary<global::System.Type, global::System.Delegate> {hmrFieldName}_cache = new();");
                sb.AppendLine("#endif");
                sb.AppendLine();

                // Trampoline
                if (!string.IsNullOrEmpty(hookSig))
                    sb.AppendLine($"        [global::Ruitk.HookSignature(\"{hookSig}\")]");
                sb.AppendLine($"        {access} static {returnType} {hook.Name}{genericSuffix}({paramList})");
                sb.AppendLine("        {");
                sb.AppendLine("#if UNITY_EDITOR");
                sb.AppendLine($"            if (global::Ruitk.Core.HmrState.IsActive && {hmrFieldName} != null)");
                sb.AppendLine("            {");

                // Build Func/Action type for the delegate
                string delegateType = BuildDelegateType(hook.Params, hook.ReturnType);
                sb.AppendLine($"                var __del = ({delegateType}){hmrFieldName}_cache");
                sb.AppendLine($"                    .GetOrAdd(typeof({BuildTypeofArgs(hook.GenericParams)}), __t =>");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        var __closed = {hmrFieldName}.MakeGenericMethod(__t);");
                sb.AppendLine($"                        return global::System.Delegate.CreateDelegate(typeof({delegateType}), __closed);");
                sb.AppendLine("                    });");
                if (isVoid)
                    sb.AppendLine($"                __del({paramNames}); return;");
                else
                    sb.AppendLine($"                return __del({paramNames});");
                sb.AppendLine("            }");
                sb.AppendLine("#endif");
                if (isVoid)
                    sb.AppendLine($"            {bodyMethodName}{genericSuffix}({paramNames});");
                else
                    sb.AppendLine($"            return {bodyMethodName}{genericSuffix}({paramNames});");
                sb.AppendLine("        }");
            }
            else
            {
                // ── Non-generic hook: Func/Action delegate field ──────────────
                string delegateType = BuildDelegateType(hook.Params, hook.ReturnType);

                sb.AppendLine("#if UNITY_EDITOR");
                sb.AppendLine($"        [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                sb.AppendLine($"        internal static {delegateType} {hmrFieldName} = {bodyMethodName};");
                sb.AppendLine("#endif");
                sb.AppendLine();

                // Trampoline
                if (!string.IsNullOrEmpty(hookSig))
                    sb.AppendLine($"        [global::Ruitk.HookSignature(\"{hookSig}\")]");
                sb.AppendLine($"        {access} static {returnType} {hook.Name}({paramList})");
                sb.AppendLine("        {");
                sb.AppendLine("#if UNITY_EDITOR");
                sb.AppendLine($"            if (global::Ruitk.Core.HmrState.IsActive)");
                if (isVoid)
                {
                    sb.AppendLine($"            {{ {hmrFieldName}({paramNames}); return; }}");
                }
                else
                {
                    sb.AppendLine($"                return {hmrFieldName}({paramNames});");
                }
                sb.AppendLine("#endif");
                if (isVoid)
                    sb.AppendLine($"            {bodyMethodName}({paramNames});");
                else
                    sb.AppendLine($"            return {bodyMethodName}({paramNames});");
                sb.AppendLine("        }");
            }

            // ── Body method ──────────────────────────────────────────────────
            sb.AppendLine();
            sb.AppendLine($"        private static {returnType} {bodyMethodName}{genericSuffix}({paramList})");
            sb.AppendLine("        {");
            sb.AppendLine($"#line {hook.BodyStartLine} \"{linePath}\"");
            sb.AppendLine($"            {transformedBody}");
            sb.AppendLine("#line hidden");
            sb.AppendLine("        }");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a comma-separated parameter list for method signatures:
        /// <c>int initial = 0, string label = ""</c>
        /// </summary>
        private static string BuildParamList(ImmutableArray<FunctionParam> hookParams)
        {
            if (hookParams.IsDefaultOrEmpty)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < hookParams.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                var p = hookParams[i];
                sb.Append(p.Type).Append(' ').Append(p.Name);
                if (p.DefaultValue != null)
                    sb.Append(" = ").Append(p.DefaultValue);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Builds a comma-separated list of parameter names for call forwarding:
        /// <c>initial, label</c>
        /// </summary>
        private static string BuildParamNames(ImmutableArray<FunctionParam> hookParams)
        {
            if (hookParams.IsDefaultOrEmpty)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < hookParams.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(hookParams[i].Name);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Builds the delegate type for a hook:
        /// <c>global::System.Func&lt;int, (int, Action)&gt;</c> for non-void,
        /// <c>global::System.Action&lt;int&gt;</c> for void.
        /// </summary>
        private static string BuildDelegateType(ImmutableArray<FunctionParam> hookParams, string? returnType)
        {
            bool isVoid = string.IsNullOrEmpty(returnType);
            var sb = new StringBuilder();

            if (isVoid)
            {
                if (hookParams.IsDefaultOrEmpty)
                {
                    sb.Append("global::System.Action");
                }
                else
                {
                    sb.Append("global::System.Action<");
                    for (int i = 0; i < hookParams.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(hookParams[i].Type);
                    }
                    sb.Append('>');
                }
            }
            else
            {
                sb.Append("global::System.Func<");
                if (!hookParams.IsDefaultOrEmpty)
                {
                    for (int i = 0; i < hookParams.Length; i++)
                    {
                        sb.Append(hookParams[i].Type).Append(", ");
                    }
                }
                sb.Append(returnType);
                sb.Append('>');
            }

            return sb.ToString();
        }

        /// <summary>
        /// Extracts type argument names from a generic params string for typeof():
        /// <c>&lt;T&gt;</c> → <c>T</c>, <c>&lt;TKey, TValue&gt;</c> → <c>TKey, TValue</c>.
        /// For single type param returns the name; for multiple wraps in tuple-style typeof.
        /// </summary>
        private static string BuildTypeofArgs(string? genericParams)
        {
            if (string.IsNullOrEmpty(genericParams))
                return "object";

            // Strip < and >
            string inner = genericParams!.TrimStart('<').TrimEnd('>').Trim();
            return inner;
        }

        private static string NormalizeLinePath(string filePath)
        {
            return (filePath ?? string.Empty).Replace('\\', '/');
        }
    }
}
