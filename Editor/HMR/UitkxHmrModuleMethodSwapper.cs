// ════════════════════════════════════════════════════════════════════════════
//  UitkxHmrModuleMethodSwapper — re-bind `static` module method delegate slots
//  after a hot-reload compile.
// ════════════════════════════════════════════════════════════════════════════
//
//  Problem this solves (v0.4.20 follow-up to Issue 13)
//  ───────────────────────────────────────────────────
//
//  The source generator (SourceGenerator~/Emitter/ModuleBodyRewriter.cs)
//  rewrites every top-level `public static` method declared inside a
//  `module {…}` body into an HMR trampoline triplet:
//
//      private delegate <ret> __<Name>_h<sig>_Delegate(<params>);
//      internal static __<Name>_h<sig>_Delegate __hmr_<Name>_h<sig> = __<Name>_body_h<sig>;
//
//      public static <ret> <Name>(<params>) {
//          if (HmrState.IsActive) return __hmr_<Name>_h<sig>(<args>);
//          return __<Name>_body_h<sig>(<args>);
//      }
//
//      private static <ret> __<Name>_body_h<sig>(<params>) { /* user body */ }
//
//  The `__hmr_*` field is initialised at type-load time to the body method.
//  When HMR rebuilds the module, we want every project-side trampoline to
//  start dispatching to the NEW user code that lives in the freshly-loaded
//  HMR assembly. That is exactly what this swapper does:
//
//    1. Walk every type in the HMR assembly.
//    2. Locate the equivalent project-loaded type (by Type.FullName).
//    3. Walk every project-side `__hmr_<name>_h<hash>` static field.
//    4. Find the matching `public static` method on the HMR-loaded type by
//       method name + parameter-count + parameter-Type.Name signature.
//    5. Bind the field via Delegate.CreateDelegate (or, for generic methods
//       represented by a MethodInfo field, store the open MethodInfo and
//       clear the per-closed-type companion cache).
//
//  Why this design (no HMR-side rewriter)
//  ──────────────────────────────────────
//
//  HMR's own emitter (Editor/HMR/HmrHookEmitter.EmitExports) writes util
//  method bodies VERBATIM — exactly the user's source, unchanged. That gives
//  us a fresh `public static <Name>` MethodInfo on the HMR-loaded type with
//  the user's new implementation, and the swapper can `CreateDelegate` to it
//  directly. No `__<name>_body_h<hash>` companion has to exist on the HMR
//  side, which keeps HMR's emit pipeline free of any Roslyn dependency.
//
//
//  Safety guardrails
//  ─────────────────
//
//    • Each field is wrapped in its own try/catch — a single mismatch never
//      breaks the HMR cycle.
//
//    • Generic-method MethodInfo fields whose companion `_cache` field is
//      present are reset together (the cache caches per-closed-type
//      delegates derived from the OLD MethodInfo — stale after a swap).
//
//    • Compiler-generated types are skipped (matches
//      UitkxHmrModuleStaticSwapper).
//
//    • Overload disambiguation uses (parameter count, parameter-Type.Name
//      sequence). For the small number of overloads we see in real samples
//      this is sufficient. If multiple HMR-side candidates match, we log a
//      warning and skip — a full domain reload will take care of it.

using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace Ruitk.EditorSupport.HMR
{
    /// <summary>
    /// Re-binds the <c>__hmr_*</c> delegate / MethodInfo fields on
    /// project-loaded module types to the freshly compiled methods produced
    /// by the HMR pipeline. See file header for the full design.
    /// </summary>
    internal static class UitkxHmrModuleMethodSwapper
    {
        // BindingFlags re-used everywhere — pre-cached.
        private const BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // Field-name prefix that identifies HMR trampoline slots produced by
        // SourceGenerator~/Emitter/ModuleBodyRewriter.cs. Must stay in lockstep
        // with the SG-side naming convention.
        private const string HmrFieldPrefix = "__hmr_";
        private const string HmrCacheSuffix = "_cache";

        /// <summary>
        /// For every type in <paramref name="hmrAssembly"/>, locate the
        /// matching project type and re-bind every <c>__hmr_*</c> static
        /// field to point at the HMR-loaded method. Returns the count of
        /// successful swaps. Safe to call when no module methods exist.
        /// </summary>
        public static int SwapModuleMethods(Assembly hmrAssembly)
        {
            if (hmrAssembly == null) return 0;

            int swapped = 0;

            Type[] hmrTypes;
            try
            {
                hmrTypes = hmrAssembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                Debug.LogWarning(
                    "[HMR] UitkxHmrModuleMethodSwapper: could not enumerate types " +
                    "from HMR assembly — module methods will not be re-bound. " +
                    $"({ex.Message})"
                );
                return 0;
            }

            foreach (var hmrType in hmrTypes)
            {
                if (hmrType == null) continue;
                if (ShouldSkipType(hmrType)) continue;

                var projectType = FindProjectType(hmrType.FullName);
                if (projectType == null) continue;

                swapped += SwapTypeMethods(hmrType, projectType);
            }

            return swapped;
        }

        // ── Per-type swap loop ────────────────────────────────────────────────

        /// <summary>
        /// Walks <paramref name="projectType"/>'s static fields, finds those
        /// produced by the SG-side trampoline rewriter, and re-binds each to
        /// the matching method on <paramref name="hmrType"/>.
        /// </summary>
        private static int SwapTypeMethods(Type hmrType, Type projectType)
        {
            int swapped = 0;

            FieldInfo[] projectFields;
            try { projectFields = projectType.GetFields(AllStatic); }
            catch { return 0; }

            foreach (var fld in projectFields)
            {
                string fldName = fld.Name;
                if (fldName == null) continue;
                if (!fldName.StartsWith(HmrFieldPrefix, StringComparison.Ordinal)) continue;
                // Skip the per-closed-type delegate cache companions — they
                // are reset alongside their owning generic-method MethodInfo
                // field, never as standalone targets.
                if (fldName.EndsWith(HmrCacheSuffix, StringComparison.Ordinal)) continue;

                string methodName = ExtractMethodName(fldName);
                if (string.IsNullOrEmpty(methodName)) continue;

                try
                {
                    if (typeof(Delegate).IsAssignableFrom(fld.FieldType))
                    {
                        if (TrySwapDelegateField(hmrType, projectType, fld, methodName))
                            swapped++;
                    }
                    else if (fld.FieldType == typeof(MethodInfo))
                    {
                        if (TrySwapGenericMethodInfoField(hmrType, projectType, fld, methodName))
                            swapped++;
                    }
                    // Other field shapes (int, struct, …) shouldn't appear with
                    // the __hmr_ prefix — silently ignore to avoid spam.
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[HMR] UitkxHmrModuleMethodSwapper: failed to swap " +
                        $"'{projectType.FullName}.{fldName}' — {ex.GetType().Name}: {ex.Message}"
                    );
                }
            }

            return swapped;
        }

        // ── Non-generic delegate field swap ───────────────────────────────────

        /// <summary>
        /// Binds a project-side delegate field to a public-static method on
        /// the HMR type. Disambiguates overloads by (parameter count +
        /// parameter-Type.Name sequence) — derived from the delegate's
        /// <c>Invoke</c> signature.
        /// </summary>
        private static bool TrySwapDelegateField(
            Type hmrType, Type projectType, FieldInfo fld, string methodName)
        {
            var invoke = fld.FieldType.GetMethod("Invoke");
            if (invoke == null) return false;

            var invokeParams = invoke.GetParameters();
            int arity = 0; // delegate-typed fields are always non-generic
            var hmrMethod = FindMatchingMethod(hmrType, methodName, arity, invokeParams);
            if (hmrMethod == null) return false;

            Delegate del;
            try
            {
                del = Delegate.CreateDelegate(fld.FieldType, hmrMethod);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[HMR] UitkxHmrModuleMethodSwapper: CreateDelegate failed for " +
                    $"'{projectType.FullName}.{fld.Name}' → '{hmrType.FullName}.{methodName}' — " +
                    $"{ex.GetType().Name}: {ex.Message}"
                );
                return false;
            }

            fld.SetValue(null, del);
            return true;
        }

        // ── Generic-method MethodInfo field swap ──────────────────────────────

        /// <summary>
        /// Binds a project-side <see cref="MethodInfo"/>-typed field
        /// (the open generic-method slot) to the corresponding generic
        /// method on the HMR type, and clears the per-closed-type delegate
        /// cache (otherwise stale closed delegates would survive the swap).
        /// </summary>
        private static bool TrySwapGenericMethodInfoField(
            Type hmrType, Type projectType, FieldInfo fld, string methodName)
        {
            // For generic methods, all overloads with the same name and arity
            // are candidates; we cannot disambiguate further from a MethodInfo
            // field alone (no Invoke signature). If multiple match the
            // FindMatchingMethod call returns null and we skip — full domain
            // reload will take care of those rare cases.
            MethodInfo hmrMethod = null;
            int matchCount = 0;

            foreach (var m in hmrType.GetMethods(AllStatic))
            {
                if (!m.IsGenericMethodDefinition) continue;
                if (!string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;
                hmrMethod = m;
                matchCount++;
            }

            if (matchCount != 1)
            {
                if (matchCount > 1)
                {
                    Debug.LogWarning(
                        $"[HMR] UitkxHmrModuleMethodSwapper: '{hmrType.FullName}.{methodName}' " +
                        $"has {matchCount} generic overloads — cannot disambiguate from " +
                        $"MethodInfo field '{fld.Name}'. Trigger a domain reload to refresh."
                    );
                }
                return false;
            }

            fld.SetValue(null, hmrMethod);

            // Reset the companion `_cache` field so subsequent calls rebuild
            // closed-type delegates against the new open MethodInfo.
            var cacheField = projectType.GetField(fld.Name + HmrCacheSuffix, AllStatic);
            if (cacheField != null)
            {
                var cache = cacheField.GetValue(null);
                if (cache is IDictionary dict)
                {
                    try { dict.Clear(); } catch { /* concurrent dict — best effort */ }
                }
            }

            return true;
        }

        // ── Method matching ───────────────────────────────────────────────────

        /// <summary>
        /// Finds the unique <c>public static</c> method on
        /// <paramref name="hmrType"/> with the given <paramref name="name"/>,
        /// matching <paramref name="arity"/> generic parameters and the
        /// supplied <paramref name="invokeParams"/> signature
        /// (parameter-count + each parameter's <see cref="Type.Name"/>).
        /// Returns <c>null</c> if zero or more than one method matches —
        /// the caller logs the ambiguity at one level up.
        /// </summary>
        private static MethodInfo FindMatchingMethod(
            Type hmrType, string name, int arity, ParameterInfo[] invokeParams)
        {
            MethodInfo unique = null;
            int hits = 0;

            foreach (var m in hmrType.GetMethods(AllStatic))
            {
                if (!string.Equals(m.Name, name, StringComparison.Ordinal)) continue;
                int methodArity = m.IsGenericMethodDefinition ? m.GetGenericArguments().Length : 0;
                if (methodArity != arity) continue;
                if (!ParametersMatch(m.GetParameters(), invokeParams)) continue;

                unique = m;
                hits++;
            }

            return hits == 1 ? unique : null;
        }

        /// <summary>
        /// Compares two parameter lists for delegate-binding compatibility:
        /// same length, and pair-wise <see cref="Type.Name"/> equality.
        /// Using <c>Name</c> (not <c>FullName</c>) lets a parameter typed
        /// as a generic-method type-parameter <c>T</c> on the HMR side match
        /// the corresponding <c>T</c> on the delegate's <c>Invoke</c> — both
        /// have <c>Name == "T"</c>. By-ref / out / in modifiers are
        /// preserved by <see cref="Type.IsByRef"/> being part of <c>Name</c>
        /// (e.g. <c>"Int32&amp;"</c>), so a ref parameter on one side cannot
        /// silently bind to a non-ref parameter on the other.
        /// </summary>
        private static bool ParametersMatch(ParameterInfo[] candidate, ParameterInfo[] expected)
        {
            if (candidate.Length != expected.Length) return false;
            for (int i = 0; i < candidate.Length; i++)
            {
                var ct = candidate[i].ParameterType;
                var et = expected[i].ParameterType;
                if (!string.Equals(ct.Name, et.Name, StringComparison.Ordinal))
                    return false;
                // Belt-and-braces: ref/out/in show up via IsByRef. Equal Name
                // already implies same IsByRef (the trailing '&' is part of
                // Name) but the explicit guard is cheap and self-documenting.
                if (ct.IsByRef != et.IsByRef) return false;
            }
            return true;
        }

        // ── Field-name parsing ────────────────────────────────────────────────

        /// <summary>
        /// Extracts the user-visible method name from a SG-emitted HMR field
        /// name of the form <c>__hmr_&lt;methodName&gt;_h&lt;8hex&gt;</c>.
        /// Returns <c>null</c> if the suffix doesn't match the expected shape
        /// (e.g. an unrelated user field happened to share the prefix).
        /// </summary>
        private static string ExtractMethodName(string fieldName)
        {
            // Required shape: __hmr_<name>_h<8hex>
            //                   ^^^^^                       prefix (already checked)
            //                          ^^^^^                arbitrary user identifier
            //                                _h             literal separator
            //                                  ^^^^^^^^     8 hex chars
            const int PrefixLen = 6;        // "__hmr_"
            const int HashSuffixLen = 10;   // "_h" + 8 hex chars

            if (fieldName.Length < PrefixLen + HashSuffixLen + 1) return null;

            int hashStart = fieldName.Length - HashSuffixLen;
            if (fieldName[hashStart] != '_' || fieldName[hashStart + 1] != 'h') return null;

            for (int i = hashStart + 2; i < fieldName.Length; i++)
            {
                char c = fieldName[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHex) return null;
            }

            return fieldName.Substring(PrefixLen, hashStart - PrefixLen);
        }

        // ── Type filter (mirror of UitkxHmrModuleStaticSwapper) ───────────────

        private static bool ShouldSkipType(Type t)
        {
            if (string.IsNullOrEmpty(t.FullName)) return true;
            if (t.FullName.StartsWith("<", StringComparison.Ordinal)) return true;
            if (t.FullName.IndexOf("<>", StringComparison.Ordinal) >= 0) return true;
            if (t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
                return true;
            return false;
        }

        private static Type FindProjectType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;

                Type t;
                try { t = asm.GetType(fullName, throwOnError: false); }
                catch { continue; }

                if (t != null) return t;
            }
            return null;
        }
    }
}
