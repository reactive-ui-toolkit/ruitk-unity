using System;
using System.Collections;
using System.Reflection;
using Ruitk;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using UnityEngine;

namespace Ruitk.EditorSupport.HMR
{
    /// <summary>
    /// Hook-file HMR delegate swapper.
    ///
    /// <para>
    /// Re-binds <c>__hmr_*</c> static delegate (and <c>MethodInfo</c>) fields
    /// emitted by <see cref="HookEmitter"/> on the live project type to point
    /// at the freshly-compiled body methods on the HMR-loaded type, then
    /// triggers a global re-render so any consumer of the changed hook picks
    /// up the new body on its next render pass.
    /// </para>
    ///
    /// <para>
    /// Component swaps no longer live here. As of 0.6.0 component identity
    /// is carried by <c>Ruitk.Refresh.Family</c> handles published
    /// from each component's <c>[ModuleInitializer]</c>; the HMR controller
    /// calls <c>RefreshRuntime.PerformRefresh()</c> after a successful
    /// compile to drive the reconciler. See
    /// <c>Plans~/HMR_FAST_REFRESH_PLAN.md</c>.
    /// </para>
    /// </summary>
    internal static class UitkxHmrDelegateSwapper
    {
        /// <summary>
        /// Swaps hook delegate fields in the project assembly to point at the
        /// new body methods from the HMR-compiled assembly, then triggers a
        /// global re-render on all active fiber trees.
        /// </summary>
        /// <returns>Number of hooks swapped.</returns>
        public static int SwapHooks(Assembly hmrAssembly, string containerClassName, string ns)
        {
            // ── 1. Find the container class in the project assemblies ────────
            string fullName = string.IsNullOrEmpty(ns)
                ? containerClassName
                : $"{ns}.{containerClassName}";

            Type projectContainer = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic)
                    continue;
                projectContainer = asm.GetType(fullName);
                if (projectContainer != null)
                    break;
            }

            if (projectContainer == null)
            {
                Debug.LogWarning(
                    $"[HMR] Could not find hook container '{fullName}' in loaded assemblies."
                );
                return 0;
            }

            // ── 2. Find the HMR container class with new body methods ────────
            Type hmrContainer = hmrAssembly.GetType(fullName);
            if (hmrContainer == null)
            {
                Debug.LogWarning(
                    $"[HMR] Could not find hook container '{fullName}' in HMR assembly."
                );
                return 0;
            }

            // ── 3. Swap each __hmr_* field ───────────────────────────────────
            int swapped = 0;
            var bindingFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

            foreach (var field in projectContainer.GetFields(bindingFlags))
            {
                if (!field.Name.StartsWith("__hmr_"))
                    continue;
                if (field.Name.EndsWith("_cache"))
                    continue;

                string hookName = field.Name.Substring("__hmr_".Length);
                string bodyMethodName = $"__{hookName}_body";

                if (field.FieldType == typeof(MethodInfo))
                {
                    // ── Generic hook: swap MethodInfo + clear cache ──────
                    var newMethod = hmrContainer.GetMethod(bodyMethodName, bindingFlags);
                    if (newMethod != null)
                    {
                        field.SetValue(null, newMethod);
                        swapped++;

                        var cacheField = projectContainer.GetField(
                            field.Name + "_cache",
                            bindingFlags
                        );
                        if (cacheField?.GetValue(null) is IDictionary dict)
                            dict.Clear();
                    }
                }
                else if (typeof(Delegate).IsAssignableFrom(field.FieldType))
                {
                    // ── Non-generic hook: swap Func/Action delegate ──────
                    var newMethod = hmrContainer.GetMethod(bodyMethodName, bindingFlags);
                    if (newMethod != null)
                    {
                        try
                        {
                            var newDel = Delegate.CreateDelegate(field.FieldType, newMethod);
                            field.SetValue(null, newDel);
                            swapped++;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning(
                                $"[HMR] Failed to swap hook '{hookName}': {ex.Message}"
                            );
                        }
                    }
                }
            }

            // ── 4. Trigger global re-render ──────────────────────────────────
            if (swapped > 0)
                TriggerGlobalReRender();

            return swapped;
        }

        /// <summary>
        /// Triggers a re-render on all active fiber trees so components pick up
        /// new hook implementations. Used by hook HMR since any component might
        /// call the changed hook. Also invoked directly by the controller after
        /// a module-method swap on a module-only file (which has no hook
        /// container and therefore never routes through <see cref="SwapHooks"/>).
        /// </summary>
        internal static void TriggerGlobalReRender()
        {
            foreach (var rootFiber in Ruitk.Core.Fiber.MountRegistry.EnumerateRootFibers())
            {
                ScheduleFullTreeUpdate(rootFiber);
            }
        }

        private static void ScheduleFullTreeUpdate(FiberNode fiber)
        {
            if (fiber == null)
                return;
            if (fiber.Tag == FiberTag.FunctionComponent)
            {
                try
                {
                    fiber.ComponentState?.OnStateUpdated?.Invoke();
                }
                catch
                { /* best effort */
                }
            }
            ScheduleFullTreeUpdate(fiber.Child);
            ScheduleFullTreeUpdate(fiber.Sibling);
        }
    }
}
