using System;
using System.Collections.Generic;
using Ruitk.Core;
using Ruitk.Core.Diagnostics;

namespace Ruitk.Core.Fiber
{
    /// <summary>
    /// Function component support for Fiber reconciler
    /// Handles rendering, hooks, and effects
    /// </summary>
    public static class FiberFunctionComponent
    {
        // Guards against infinite render loops (e.g. setState called unconditionally during render).
        // ThreadStatic ensures each thread keeps its own independent depth counter.
        [ThreadStatic]
        private static int s_renderDepth;
        private const int MaxRenderDepth = 25;

        /// <summary>
        /// Render a function component and return the child fiber
        /// </summary>
        public static FiberNode RenderFunctionComponent(
            FiberNode wipFiber,
            HostContext hostContext,
            FiberReconciler reconciler
        )
        {
            if (wipFiber.TypedRender == null)
            {
                return null;
            }

            // Get or create component state
            var componentState = wipFiber.ComponentState ?? new FunctionComponentState(null);
            componentState.HostContext = hostContext;
            componentState.Fiber = wipFiber;
            wipFiber.ComponentState = componentState;

            // Apply any queued state updates before rendering
            Hooks.FlushQueuedStateUpdates(componentState);

            // (Hook cursors are reset per render pass inside RunRenderPass below — under
            // strict_mode BOTH invokes re-run that preparation, so the second pass
            // overwrites the first's index-keyed hook slots in place.)

            // Wire up state updates to Fiber reconciler
            // CRITICAL FIX: Use componentState.Fiber (kept current by UpdateComponentStateReferences)
            // instead of capturing wipFiber, which becomes stale after tree swap in CommitRoot.
            // PERF: Only create the delegate once — componentState.Fiber is updated by
            // reference via UpdateComponentStateReferences, so the captured closure remains valid.
            componentState.OnStateUpdated ??= () =>
                reconciler.ScheduleUpdateOnFiber(componentState.Fiber, null);

            // Props equality check (typed path — all function components use IProps now).
            // IMPORTANT: TypedProps == null means this fiber has never been rendered (first mount).
            // A null baseline must never be considered equal to any pending props — doing so would
            // cause the bailout to fire on the very first render, producing empty windows.
            var tp = wipFiber.TypedPendingProps ?? EmptyProps.Instance;
            var cp = wipFiber.TypedProps; // null == "never rendered"; never equal to any pending props
            bool propsEqual = cp != null && (ReferenceEquals(tp, cp) || tp.Equals(cp));
            bool contextUnchanged = !wipFiber.ReadsContext || !Hooks.HasContextChanged(wipFiber);

            // Children equality check: if the parent passed different children (e.g. {__children}
            // slot pattern), we must re-render even if props are equal. Compare by reference —
            // every render produces a new __C(...) array, so a changed count or different reference
            // means the parent re-evaluated its children and we must too.
            var prevChildren = wipFiber.Alternate?.Children;
            var nextChildren = wipFiber.Children;
            bool childrenChanged =
                !ReferenceEquals(prevChildren, nextChildren)
                && !ChildrenListEqual(prevChildren, nextChildren);

            // Every input the "why did this render?" question needs has just been computed and
            // was previously discarded. Active is one static bool; nothing below it runs, and no
            // component name is resolved, unless something is listening.
            if (Ruitk.Diagnostics.WhyDidYouRender.Active)
            {
                var reason = Ruitk.Diagnostics.RenderReason.None;
                if (cp == null)
                {
                    reason |= Ruitk.Diagnostics.RenderReason.FirstMount;
                }
                else if (!propsEqual)
                {
                    reason |= Ruitk.Diagnostics.RenderReason.PropsChanged;
                }
                if (wipFiber.HasPendingStateUpdate)
                {
                    reason |= Ruitk.Diagnostics.RenderReason.StateUpdate;
                }
                if (!contextUnchanged)
                {
                    reason |= Ruitk.Diagnostics.RenderReason.ContextChanged;
                }
                if (childrenChanged)
                {
                    reason |= Ruitk.Diagnostics.RenderReason.ChildrenChanged;
                }
                Ruitk.Diagnostics.WhyDidYouRender.Report(DescribeComponent(wipFiber), reason);
            }

            // Bailout check: if no state update and props match AND context unchanged AND children unchanged, skip rendering
            if (
                !wipFiber.HasPendingStateUpdate
                && contextUnchanged
                && propsEqual
                && !childrenChanged
            )
            {
                // If the subtree has updates, we still need to clone the children but skip *this* component's render logic
                if (wipFiber.SubtreeHasUpdates)
                {
                    FiberNode newChild = FiberChildReconciliation.CloneChildFibers(wipFiber);
                    return newChild;
                }
                // Commit props so the next render cycle sees matching props for ArePropsEqual
                wipFiber.Props = wipFiber.PendingProps;
                wipFiber.HostProps = wipFiber.PendingHostProps;
                // Also commit typed props so the next cycle's equality check works correctly
                wipFiber.TypedProps = wipFiber.TypedPendingProps;

                // CRITICAL FIX: We must carry over the existing child pointer to the WIP tree
                // even if we don't visit it. Otherwise, this branch is severed in the new tree.
                if (wipFiber.Alternate != null)
                {
                    wipFiber.Child = wipFiber.Alternate.Child;

                    // CRITICAL FIX: We must update the child's Parent pointer to point to this new WIP fiber!
                    // Otherwise, the child remains attached to the OLD parent (Alternate), breaking the update chain.
                    if (wipFiber.Child != null)
                    {
                        var child = wipFiber.Child;
                        while (child != null)
                        {
                            child.Parent = wipFiber;
                            child = child.Sibling;
                        }
                    }
                }

                componentState.IsRendering = false;
                HookContext.Current = null;
                return null;
            }
            else
            {
                // Clear HasPendingStateUpdate now that we've read it
                // (SubtreeHasUpdates stays for reconciliation, cleared after commit)
                if (wipFiber.HasPendingStateUpdate)
                {
                    wipFiber.HasPendingStateUpdate = false;
                }
            }

            // Set hook context
            HookContext.Current = componentState;
            componentState.IsRendering = true;

            VirtualNode childVNode = null;

            // One render pass: reset the per-pass state — hook cursors restart from zero,
            // and context deps are cleared right before render (UseContext rebuilds them) —
            // then call the render function (typed IProps path — all function components).
            // Children may be null on the very first render before parent sets them; fall
            // back to empty list for safety.
            VirtualNode RunRenderPass()
            {
                componentState.HookIndex = 0;
                componentState.EffectIndex = 0;
                componentState.LayoutEffectIndex = 0;
                if (componentState.ContextDependencies != null)
                {
                    componentState.ContextDependencies.Clear();
                }
                return wipFiber.TypedRender(
                    wipFiber.TypedPendingProps ?? EmptyProps.Instance,
                    wipFiber.Children ?? VirtualNode.EmptyChildren
                );
            }

            s_renderDepth++;
            try
            {
                // Guard against infinite render loops caused by unconditional setState during render
                if (s_renderDepth > MaxRenderDepth)
                {
                    var componentName =
                        wipFiber.ElementType
                        ?? wipFiber.TypedRender?.Method.DeclaringType?.Name
                        ?? "Unknown";
                    UnityEngine.Debug.LogError(
                        $"[Fiber] Maximum render depth ({MaxRenderDepth}) exceeded in '{componentName}'. "
                            + "A component may be calling setState unconditionally during render."
                    );
                    return null;
                }

                childVNode = RunRenderPass();

                // strict_mode (U-07): double-invoke the render function so impure render
                // bodies surface in development. The FIRST result is discarded, the SECOND
                // is the one reconciled (the family's shared shape — see the sibling legs'
                // RunOnce(); if strict, Result = RunOnce()). Hook slots are index-keyed and
                // overwritten in place by the second pass (never appended); effect and
                // layout-effect registrations re-capture into the same slot and run once,
                // at commit; the depth guard counts ONE logical render (both invokes run
                // inside a single increment). The discarded first tree is strict-mode-only
                // per-render garbage BY DESIGN: memoized subtrees (UseMemo-cached vnodes
                // with deps unchanged between the invokes) are SHARED between the two
                // results, so an explicit pool release of the first tree would corrupt the
                // second — no safe release exists.
                if (FiberConfig.StrictModeEnabled)
                {
                    childVNode = RunRenderPass();
                }
            }
            finally
            {
                s_renderDepth--;
                componentState.IsRendering = false;
                HookContext.Current = null;
            }

            // After first render: lock in recorded hook order for validation on subsequent renders
            if (!componentState.HookOrderPrimed && componentState.HookOrderSignatures?.Count > 0)
                componentState.HookOrderPrimed = true;

            // Mark effect flags so the commit phase can run effects.
            if (
                componentState.FunctionLayoutEffects != null
                && componentState.FunctionLayoutEffects.Count > 0
            )
            {
                wipFiber.EffectTag |= EffectFlags.LayoutEffect;
            }

            if (componentState.FunctionEffects != null && componentState.FunctionEffects.Count > 0)
            {
                wipFiber.EffectTag |= EffectFlags.PassiveEffect;
            }

            // Reconcile single child
            if (childVNode != null)
            {
                var currentChild = wipFiber.Alternate?.Child;
                var newChild = ReconcileSingleChild(wipFiber, currentChild, childVNode);
                wipFiber.Child = newChild;
                return newChild;
            }

            // If the function component now returns null, ensure any
            // previously rendered child fibers are marked for deletion.
            var existingChild = wipFiber.Alternate?.Child;
            if (existingChild != null)
            {
                var child = existingChild;
                while (child != null)
                {
                    DeleteChild(wipFiber, child);
                    child = child.Sibling;
                }
            }

            wipFiber.Child = null;
            return null;
        }

        /// <summary>
        /// Reconcile a single child (function components return single root)
        /// </summary>
        private static FiberNode ReconcileSingleChild(
            FiberNode parent,
            FiberNode currentChild,
            VirtualNode newVNode
        )
        {
            // Try to reuse existing fiber
            if (currentChild != null && CanReuseFiber(currentChild, newVNode))
            {
                // Use centralized factory for consistent flag propagation
                var clone = FiberFactory.CloneForReuse(currentChild, newVNode);
                clone.Parent = parent;
                clone.Index = 0;

                // Delete remaining siblings
                var sibling = currentChild.Sibling;
                while (sibling != null)
                {
                    DeleteChild(parent, sibling);
                    sibling = sibling.Sibling;
                }

                return clone;
            }

            // Can't reuse, create new fiber
            var newFiber = CreateFiber(newVNode, parent, 0);

            // Delete all old children
            if (currentChild != null)
            {
                // structural gate (§6): the component's root changed type — the old
                // subtree is torn down and a fresh one takes its slot.
                FiberChildReconciliation.TraceReplace(currentChild, newVNode);
                var child = currentChild;
                while (child != null)
                {
                    DeleteChild(parent, child);
                    child = child.Sibling;
                }
            }

            return newFiber;
        }

        /// <summary>
        /// Check if fiber can be reused for vnode
        /// </summary>
        private static bool CanReuseFiber(FiberNode fiber, VirtualNode vnode)
        {
            if (fiber == null || vnode == null)
                return false;

            switch (vnode.NodeType)
            {
                case VirtualNodeType.Element:
                    return fiber.Tag == FiberTag.HostComponent
                        && fiber.ElementType == vnode.ElementTypeName;

                case VirtualNodeType.Text:
                    // Text nodes are modeled as host "Label" elements.
                    // Reuse when the host element type matches.
                    return fiber.Tag == FiberTag.HostComponent && fiber.ElementType == "Label";

                case VirtualNodeType.FunctionComponent:
                    if (fiber.Tag != FiberTag.FunctionComponent)
                        return false;

                    // Family-based identity (UITKX Fast Refresh, editor-only):
                    // see FiberChildReconciliation.CanReuseFiber for the
                    // design rationale -- these two copies must stay in
                    // lockstep. Player builds compile this branch out.
#if UNITY_EDITOR
                    if (fiber.Family != null && vnode._family != null)
                        return ReferenceEquals(fiber.Family, vnode._family);
#endif

                    // Legacy delegate path.
                    if (fiber.TypedRender == null || vnode.TypedFunctionRender == null)
                        return false;
                    if (ReferenceEquals(fiber.TypedRender, vnode.TypedFunctionRender))
                        return true;
                    if (
                        fiber.TypedRender.Method == vnode.TypedFunctionRender.Method
                        && fiber.TypedRender.Target == vnode.TypedFunctionRender.Target
                    )
                        return true;
                    return false;

                case VirtualNodeType.Suspense:
                    return fiber.Tag == FiberTag.FunctionComponent
                        && fiber.TypedRender == FiberIntrinsicComponents.SuspenseRender;

                case VirtualNodeType.Portal:
                    return fiber.Tag == FiberTag.HostPortal;

                case VirtualNodeType.ErrorBoundary:
                    return fiber.Tag == FiberTag.ErrorBoundary;

                case VirtualNodeType.Fragment:
                    return fiber.Tag == FiberTag.Fragment;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Create new fiber from vnode
        /// </summary>
        private static FiberNode CreateFiber(VirtualNode vnode, FiberNode parent, int index)
        {
            // Use centralized factory for consistent flag management
            return FiberFactory.CreateNew(vnode, parent, index);
        }

        /// <summary>
        /// Mark fiber for deletion
        /// </summary>
        private static void DeleteChild(FiberNode parentFiber, FiberNode childFiber)
        {
            if (parentFiber.Deletions == null)
            {
                parentFiber.Deletions = new List<FiberNode>();
            }
            childFiber.EffectTag |= EffectFlags.Deletion;
            parentFiber.Deletions.Add(childFiber);
        }

        /// <summary>
        /// Extract props from vnode
        /// </summary>
        private static IReadOnlyDictionary<string, object> ExtractProps(VirtualNode vnode)
        {
            if (vnode == null)
            {
                return VirtualNode.EmptyProps;
            }

            switch (vnode.NodeType)
            {
                case VirtualNodeType.Suspense:
                    return VirtualNode.EmptyProps;

                case VirtualNodeType.Text:
                    return new Dictionary<string, object>
                    {
                        { "text", vnode.TextContent ?? string.Empty },
                    };

                default:
                    return vnode.Properties ?? VirtualNode.EmptyProps;
            }
        }

        /// <summary>
        /// Commit layout effects for a function component
        /// </summary>
        public static void CommitLayoutEffects(FiberNode fiber)
        {
            var componentState = fiber.ComponentState;
            if (componentState?.FunctionLayoutEffects == null)
                return;

            for (int i = 0; i < componentState.FunctionLayoutEffects.Count; i++)
            {
                var effect = componentState.FunctionLayoutEffects[i];
                bool shouldRun =
                    effect.lastDeps == null || DepsChanged(effect.lastDeps, effect.deps);

                if (shouldRun)
                {
                    // Cleanup previous
                    try
                    {
                        effect.cleanup?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError($"Layout effect cleanup error: {ex}");
                    }

                    // Run new effect
                    Action newCleanup = null;
                    try
                    {
                        newCleanup = effect.factory?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError($"Layout effect error: {ex}");
                    }

                    // Update effect entry
                    if (i < componentState.FunctionLayoutEffects.Count)
                    {
                        componentState.FunctionLayoutEffects[i] = (
                            effect.factory,
                            effect.deps,
                            (object[])effect.deps?.Clone(),
                            newCleanup
                        );
                    }
                }
            }
        }

        /// <summary>
        /// Pass 1 of 2 for passive-effect flushing: run only the cleanup of each dirty effect.
        /// Must be called for ALL committed fibers before RunPassiveEffectSetups is called for any.
        /// </summary>
        public static void RunPassiveEffectCleanups(FiberNode fiber)
        {
            var componentState = fiber.ComponentState;
            if (componentState?.FunctionEffects == null)
                return;

            for (int i = 0; i < componentState.FunctionEffects.Count; i++)
            {
                var effect = componentState.FunctionEffects[i];
                bool shouldRun =
                    effect.lastDeps == null || DepsChanged(effect.lastDeps, effect.deps);
                if (!shouldRun || effect.cleanup == null)
                    continue;

                try
                {
                    effect.cleanup.Invoke();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"Effect cleanup error: {ex}");
                }

                // Clear the cleanup reference so it cannot fire twice, but preserve lastDeps
                // so RunPassiveEffectSetups can re-evaluate shouldRun identically.
                componentState.FunctionEffects[i] = (
                    effect.factory,
                    effect.deps,
                    effect.lastDeps,
                    null
                );
            }
        }

        /// <summary>
        /// Pass 2 of 2 for passive-effect flushing: run the setup of each dirty effect and store the new cleanup.
        /// Must be called after RunPassiveEffectCleanups has been called for ALL committed fibers.
        /// </summary>
        public static void RunPassiveEffectSetups(FiberNode fiber)
        {
            var componentState = fiber.ComponentState;
            if (componentState?.FunctionEffects == null)
                return;

            for (int i = 0; i < componentState.FunctionEffects.Count; i++)
            {
                var effect = componentState.FunctionEffects[i];
                bool shouldRun =
                    effect.lastDeps == null || DepsChanged(effect.lastDeps, effect.deps);
                if (!shouldRun)
                    continue;

                Action newCleanup = null;
                try
                {
                    newCleanup = effect.factory?.Invoke();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"Effect error: {ex}");
                }

                componentState.FunctionEffects[i] = (
                    effect.factory,
                    effect.deps,
                    (object[])effect.deps?.Clone(),
                    newCleanup
                );
            }
        }

        /// <summary>
        /// Check if dependencies changed
        /// </summary>
        /// <summary>
        /// Returns true if two children lists are considered equal (same count and same
        /// VirtualNode references in each position). VirtualNodes are created fresh on
        /// every parent render, so reference equality is the correct comparison here.
        /// </summary>
        private static bool ChildrenListEqual(
            IReadOnlyList<VirtualNode> prev,
            IReadOnlyList<VirtualNode> next
        )
        {
            if (prev == null && next == null)
                return true;
            if (prev == null || next == null)
                return false;
            if (prev.Count != next.Count)
                return false;
            for (int i = 0; i < prev.Count; i++)
                if (!ReferenceEquals(prev[i], next[i]))
                    return false;
            return true;
        }

        private static bool DepsChanged(object[] oldDeps, object[] newDeps)
        {
            if (oldDeps == null || newDeps == null)
                return true;

            if (oldDeps.Length != newDeps.Length)
                return true;

            for (int i = 0; i < oldDeps.Length; i++)
            {
                if (!object.Equals(oldDeps[i], newDeps[i]))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// A human name for a fiber, for WhyDidYouRender. Reached only when something is
        /// listening, and cached by the render delegate's method rather than by fiber: one
        /// entry per component function, however many times it renders. Reflection on
        /// DeclaringType is the expensive part and runs once per component, ever.
        /// </summary>
        private static string DescribeComponent(FiberNode fiber)
        {
            if (!string.IsNullOrEmpty(fiber.ElementType))
            {
                return fiber.ElementType;
            }

            var method = fiber.TypedRender?.Method;
            if (method == null)
            {
                return "<anonymous>";
            }

            lock (s_componentNameGate)
            {
                if (s_componentNames.TryGetValue(method, out string cached))
                {
                    return cached;
                }
                string declaring = method.DeclaringType?.Name;
                string name = string.IsNullOrEmpty(declaring) ? method.Name : declaring + "." + method.Name;
                s_componentNames[method] = name;
                return name;
            }
        }

        private static readonly object s_componentNameGate = new object();

        private static readonly Dictionary<System.Reflection.MethodInfo, string> s_componentNames =
            new Dictionary<System.Reflection.MethodInfo, string>();
    }
}
