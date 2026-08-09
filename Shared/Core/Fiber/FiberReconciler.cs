using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ruitk.Core;
using Ruitk.Core.Diagnostics;
using UnityEngine;
using UnityEngine.Profiling;

namespace Ruitk.Core.Fiber
{
    /// <summary>
    /// React Fiber-style reconciler for Ruitk
    /// Handles the work loop, fiber tree building, and commit phase
    /// </summary>
    public class FiberReconciler
    {
        private FiberRoot _root;
        private FiberNode _workInProgressRoot;
        private FiberNode _nextUnitOfWork;
        private HostContext _hostContext;
        private FiberHostConfig _hostConfig;
        private IScheduler _scheduler;
        private bool _isCommitting; // Track if we're in the commit phase
        private bool _isReplayingDeferred; // CommitRoot is draining _deferredUpdates — replayed updates must coalesce into the WIP, not re-defer
        private bool _hasDeletions; // Set during reconciliation when any fiber records deletions
        private List<FiberNode> _pendingPassiveEffects; // Collected during CommitWork, flushed two-pass after tree swap

        // Queue for deferred updates during commit
        // Stores target fiber and the vnode (if any)
        private readonly Queue<(FiberNode Fiber, VirtualNode VNode)> _deferredUpdates =
            new Queue<(FiberNode, VirtualNode)>();

        // Metrics
        private int _workUnitCount;
        private int _sliceCount;
        private int _yieldCount;
        private int _commitCount;
        private int _effectsCommitted;
        private readonly Stopwatch _renderStopwatch = new Stopwatch();

        // Stats
        private static readonly CustomSampler RenderPhaseSampler = CustomSampler.Create(
            "Fiber.RenderPhase"
        );
        private static readonly CustomSampler CommitPhaseSampler = CustomSampler.Create(
            "Fiber.CommitPhase"
        );

        public readonly struct FiberReconcilerMetrics
        {
            public readonly long LastRenderMs;
            public readonly int WorkUnits;
            public readonly int Commits;
            public readonly int Slices;
            public readonly int Yields;
            public readonly int EffectsCommitted;

            public FiberReconcilerMetrics(
                long lastRenderMs,
                int workUnits,
                int commits,
                int slices,
                int yields,
                int effectsCommitted
            )
            {
                LastRenderMs = lastRenderMs;
                WorkUnits = workUnits;
                Commits = commits;
                Slices = slices;
                Yields = yields;
                EffectsCommitted = effectsCommitted;
            }
        }

        public static event Action<FiberReconcilerMetrics> MetricsEmitted;

        public FiberReconciler(HostContext hostContext)
        {
            _hostContext = hostContext;
            // HostContext guarantees HostConfig is non-null (both constructors fall
            // back to the UI Toolkit backend themselves), so the backend default
            // lives there and the reconciler stays host-agnostic.
            _hostConfig = hostContext.HostConfig;

            if (
                hostContext?.Environment != null
                && hostContext.Environment.TryGetValue("scheduler", out var schedObj)
                && schedObj is IScheduler scheduler
            )
            {
                _scheduler = scheduler;
            }
        }

        /// <summary>
        /// Create a fiber root and mount a virtual node tree
        /// </summary>
        public FiberRoot CreateRoot(object container, VirtualNode vnode)
        {
            // Create root fiber
            var rootFiber = new FiberNode
            {
                Tag = FiberTag.HostComponent,
                HostElement = container,
                ElementType = "root",
            };

            var root = new FiberRoot
            {
                ContainerElement = container,
                Current = rootFiber,
                Context = _hostContext,
                Reconciler = this,
                RootVNode = vnode,
            };

            _root = root;

            // Synchronous initial render: run the work loop immediately so all
            // elements are created and placed before CreateRoot returns.  This
            // prevents a visible flash where the container is empty for one
            // frame between Clear() and the first CommitRoot.
            //
            // Passive effects are still deferred via the scheduler (the normal
            // async path) — only the render + commit phase is synchronous.
            // This mirrors React 18's createRoot().render() behaviour: the
            // initial mount is always synchronous; time-slicing is reserved
            // for subsequent state-driven updates.
            ScheduleUpdateOnFiber(rootFiber, vnode, scheduleWork: false);
            WorkLoop();

            return root;
        }

        /// <summary>
        /// Tear down the entire mounted tree synchronously, running every
        /// effect cleanup (UseEffect / UseLayoutEffect) and disposing every
        /// signal subscription on the way down.
        ///
        /// <para>
        /// This is the proper inverse of <see cref="CreateRoot"/>. It must
        /// be called when the host (e.g. an EditorWindow) is going away
        /// and the tree will not be re-rendered. Without it, function
        /// components that own external resources (pooled
        /// <c>VideoPlayer</c>, <c>AudioSource</c>, file handles, native
        /// listeners, etc.) leak their resources because their cleanup
        /// lambdas never run \u2014 e.g. an &lt;Audio&gt; element keeps
        /// playing forever after its owning EditorWindow is closed.
        /// </para>
        /// </summary>
        public void UnmountRoot()
        {
            if (_root?.Current == null)
            {
                _root = null;
                return;
            }

            // Walk every child of the root fiber and treat each as a
            // deletion. CommitDeletion recurses depth-first and runs all
            // effect cleanups + signal subscription disposals before
            // removing host elements.
            var child = _root.Current.Child;
            while (child != null)
            {
                var next = child.Sibling;
                CommitDeletion(child);
                child = next;
            }

            _root.Current.Child = null;
            _root = null;
        }

        /// <summary>
        /// Schedule an update on a fiber (triggered by setState, props change, etc.)
        /// </summary>
        public void ScheduleUpdateOnFiber(
            FiberNode fiber,
            VirtualNode vnode,
            bool scheduleWork = true
        )
        {
            if (_root == null)
            {
                return;
            }

            // Reset metrics for this render
            _workUnitCount = 0;
            _sliceCount = 0;
            _yieldCount = 0;
            _effectsCommitted = 0;
            _renderStopwatch.Restart();

            // If a new root vnode is provided (top-level render), record it;
            // otherwise reuse the last one (for state updates).
            if (vnode != null)
            {
                _root.RootVNode = vnode;
            }
            // NOTE: rootVNode can be null for state-only updates (like setState calls)
            // Don't early return here - we still need to process the update

            // Find the root fiber for this update by walking up the
            // parent chain. Also check for deletion flags along the way.
            FiberNode rootCurrent = fiber;
            bool isDeleted = false;

            // Mark the target fiber as having an update
            if (fiber != null)
            {
                fiber.HasPendingStateUpdate = true;
            }

            while (rootCurrent != null)
            {
                if ((rootCurrent.EffectTag & EffectFlags.Deletion) != 0)
                {
                    isDeleted = true;
                    break;
                }

                // Mark parent as having a subtree update
                if (rootCurrent.Parent != null)
                {
                    rootCurrent.Parent.SubtreeHasUpdates = true;
                }

                if (rootCurrent.Parent == null)
                {
                    break;
                }
                rootCurrent = rootCurrent.Parent;
            }

            if (isDeleted)
            {
                return;
            }

            // If we walked up and found a root, check if it matches the active root.
            if (rootCurrent != null)
            {
                if (rootCurrent == _root.Current)
                {
                    // Found the active root. Good.
                }
                else if (rootCurrent == _root.WorkInProgress)
                {
                    // Found the WorkInProgress root.
                    // This means we are scheduling an update on a tree that is currently being built (cascading update).
                    // We should continue using this root as the WIP.
                }
                else if (_root.Current.Alternate != null && rootCurrent == _root.Current.Alternate)
                {
                    // The scheduling fiber belongs to the SUPERSEDED tree — the
                    // typical case for deferred updates captured mid-pass and
                    // replayed after the pass committed. Building the WIP from
                    // this stale root would reconcile against pre-commit
                    // children: subtrees the last commit already placed get
                    // re-mounted as duplicates while the committed ones are
                    // orphaned with no deletion (frozen ghost elements).
                    // Redirect to the live root and re-mark the pending-update
                    // flags on the live counterpart so the render cannot bail
                    // out past the updated component.
                    var liveFiber = fiber?.Alternate ?? fiber;
                    if (liveFiber != null)
                    {
                        liveFiber.HasPendingStateUpdate = true;
                        var walkUp = liveFiber;
                        while (walkUp != null)
                        {
                            if (walkUp.Parent != null)
                            {
                                walkUp.Parent.SubtreeHasUpdates = true;
                            }
                            walkUp = walkUp.Parent;
                        }
                    }
                    rootCurrent = _root.Current;
                }
                else
                {
                    // This fiber is detached from the current tree (and its alternate)
                    UnityEngine.Debug.LogWarning(
                        $"[FiberReconciler] Attempted update on detached fiber. Ignoring. rootCurrent={rootCurrent.GetHashCode()} _root.Current={_root.Current.GetHashCode()}"
                    );
                    return;
                }
            }

            if (rootCurrent == null)
            {
                rootCurrent = _root.Current;
            }
            if (rootCurrent == null)
            {
                // No valid root to update; safely bail out.
                return;
            }

            // If we're in the commit phase, defer this update until commit completes.
            // Resetting Child=null during commit would corrupt the tree being committed.
            if (_isCommitting)
            {
                // Queue the specific fiber update to replay it after commit
                _deferredUpdates.Enqueue((fiber, vnode));
                return;
            }

            // If a render pass is in flight (parked between time slices, or a
            // reentrant update fired during a synchronous WorkLoop), defer
            // instead of discarding the pass. Restarting on every update
            // starves large trees under sustained per-frame updates (signals
            // ticking every frame): the pass never reaches commit, and hosts
            // already created by the aborted walk leak — for GameObject-backed
            // backends that is a hard resource leak, not just GC pressure.
            // The deferred update replays from CommitRoot's finally block,
            // coalescing everything that arrived during the pass into ONE
            // follow-up render.
            if (_nextUnitOfWork != null && !_isReplayingDeferred)
            {
                _deferredUpdates.Enqueue((fiber, vnode));
                return;
            }

            // Create work-in-progress root
            if (rootCurrent == _root.WorkInProgress)
            {
                // If we are updating the WIP, we don't need to create it.
                // We just need to ensure it has the latest props/vnode if provided.
                _workInProgressRoot = rootCurrent;
                if (vnode != null)
                {
                    _workInProgressRoot.PendingProps = ExtractProps(vnode);
                    _workInProgressRoot.Children = new[] { vnode };
                    _workInProgressRoot.Child = null; // Reset child to force reconciliation
                    _workInProgressRoot.EffectTag = EffectFlags.None;
                    _workInProgressRoot.NextEffect = null;
                    _workInProgressRoot.Deletions = null;
                }
            }
            else
            {
                _workInProgressRoot = CreateWorkInProgress(rootCurrent, vnode);
            }
            _root.WorkInProgress = _workInProgressRoot;

            // When an update lands while a time-sliced render is parked mid-tree, the work
            // loop is restarted from the WIP root (above). The partially-built effect list
            // lives on the persistent _root and is otherwise cleared ONLY in CommitRoot, so
            // without discarding it here the interrupted pass's stale Placement effects get
            // committed again (duplicating freshly-mounted host elements) and its lost
            // deletions can leave the previous route mounted alongside the new one. The
            // restarted walk rebuilds the effect list from scratch and re-derives _hasDeletions.
            _root.FirstEffect = null;
            _root.LastEffect = null;
            _hasDeletions = false;

            _nextUnitOfWork = _workInProgressRoot;

            // Start work loop (scheduler-based when available). time_slicing=false is the
            // explicit scheduler bypass: route through the existing synchronous WorkLoop even
            // when a scheduler is installed (the default, true, keeps today's dispatch).
            if (scheduleWork)
            {
                if (_scheduler != null && FiberConfig.TimeSlicingEnabled)
                {
                    ScheduleRootWork(IScheduler.Priority.Normal);
                }
                else
                {
                    WorkLoop();
                }
            }
        }

        /// <summary>
        /// Main work loop - processes units of work until done
        /// React uses scheduler here for time slicing, we'll keep it simple for now
        /// </summary>
        private void WorkLoop()
        {
            RenderPhaseSampler.Begin();
            try
            {
                while (_nextUnitOfWork != null)
                {
                    _nextUnitOfWork = PerformUnitOfWork(_nextUnitOfWork);
                }
            }
            finally
            {
                RenderPhaseSampler.End();
            }

            // Render phase complete, commit the changes
            if (_workInProgressRoot != null)
            {
                CommitRoot();
            }
        }

        /// <summary>
        /// Schedule a slice of work on the scheduler.
        /// </summary>
        private void ScheduleRootWork(IScheduler.Priority priority)
        {
            if (_scheduler == null)
            {
                WorkLoop();
                return;
            }

            void Slice()
            {
                ProcessWorkUntilDeadline();

                if (_nextUnitOfWork != null)
                {
                    ScheduleRootWork(priority);
                }
            }

            _scheduler.Enqueue(Slice, priority);
        }

        /// <summary>
        /// Process work units until the time slice budget is exhausted.
        /// </summary>
        private void ProcessWorkUntilDeadline()
        {
            if (_nextUnitOfWork == null)
            {
                return;
            }

            _sliceCount++;

            RenderPhaseSampler.Begin();
            try
            {
                float startMs = Time.realtimeSinceStartup * 1000f;
                bool yielded = false;
                int unitsThisSlice = 0;
                while (_nextUnitOfWork != null)
                {
                    _nextUnitOfWork = PerformUnitOfWork(_nextUnitOfWork);
                    unitsThisSlice++;

                    float nowMs = Time.realtimeSinceStartup * 1000f;
                    if (nowMs - startMs >= FiberConfig.TimeSliceMs)
                    {
                        yielded = true;
                        break;
                    }
                }

                if (yielded && _nextUnitOfWork != null)
                {
                    _yieldCount++;
                }
            }
            finally
            {
                RenderPhaseSampler.End();
            }

            // Render phase complete, commit the changes
            if (_nextUnitOfWork == null && _workInProgressRoot != null)
            {
                CommitRoot();
            }
        }

        /// <summary>
        /// Perform a unit of work - process one fiber
        /// Returns next unit of work
        /// </summary>
        private FiberNode PerformUnitOfWork(FiberNode unitOfWork)
        {
            _workUnitCount++;

            // BeginWork: reconcile this fiber's children
            var next = BeginWork(unitOfWork);

            // OPT-26 fix: Track deletions recorded during BeginWork from ANY source.
            // The ReconcileChildren wrapper only covers host/portal/error-boundary paths.
            // Function components (ReconcileSingleChild, null-return deletion) and
            // fragments (direct FiberChildReconciliation call) also record deletions
            // on the fiber but bypass the wrapper. Checking here is the single
            // universal point that covers every BeginWork code path.
            if (unitOfWork.Deletions != null)
                _hasDeletions = true;

            if (next != null)
            {
                // Has children, work on first child next
                return next;
            }

            // CompleteWork: finish this fiber (may set _nextUnitOfWork to sibling)
            CompleteUnitOfWork(unitOfWork);

            // Return whatever CompleteUnitOfWork set (could be sibling or null)
            return _nextUnitOfWork;
        }

        /// <summary>
        /// Begin work on a fiber - reconcile children and return next child
        /// </summary>
        private FiberNode BeginWork(FiberNode fiber)
        {
            try
            {
                switch (fiber.Tag)
                {
                    case FiberTag.HostComponent:
                        return UpdateHostComponent(fiber);

                    case FiberTag.FunctionComponent:
                        return UpdateFunctionComponent(fiber);

                    case FiberTag.Fragment:
                        return UpdateFragment(fiber);

                    case FiberTag.HostPortal:
                        return UpdatePortal(fiber);

                    case FiberTag.ErrorBoundary:
                        return UpdateErrorBoundary(fiber);

                    default:
                        return null;
                }
            }
            catch (FiberSuspenseSuspendException)
            {
                // Suspense control-flow: rendering for this branch is
                // intentionally suspended. A re-render has already been
                // scheduled via Hooks.SuspendUntil / Suspense, so we just
                // stop traversing this subtree for the current render.
                return null;
            }
            catch (Exception ex)
            {
#if UNITY_EDITOR
                // HMR rollback: if the component's Family.Current was just
                // swapped to a new body and that body crashed, ask the
                // Editor side to revert Family.Current to its previous
                // value and retry the render once before falling through to
                // the nearest error boundary.
                //
                // The TryRollbackFamily hook is wired by RefreshRuntime when
                // the Editor registers its root-renderer provider (the
                // Shared assembly cannot reference the Editor asmdef
                // directly). Returns false in player builds (hook is null).
                if (HmrState.IsActive
                    && HmrState.TryRollbackFamily != null
                    && fiber.Tag == FiberTag.FunctionComponent
                    && fiber.Family != null)
                {
                    if (HmrState.TryRollbackFamily(fiber.Family))
                    {
                        // Refresh the fiber's delegate from the rolled-back
                        // Family so the retry uses the previous body.
                        fiber.TypedRender = fiber.Family.Current;

                        // Reset hook state — partially-executed hooks from
                        // the crashing delegate may have corrupted slot
                        // values.
                        ResetComponentStateForHmrRollback(fiber);

                        UnityEngine.Debug.LogWarning(
                            $"[HMR] Render crashed — rolled back component " +
                            $"'{fiber.Family.Id}' to previous version: {ex.Message}"
                        );

                        try
                        {
                            return UpdateFunctionComponent(fiber);
                        }
                        catch
                        {
                            // Old body also failed — fall through to ErrorBoundary.
                        }
                    }
                }
#endif

                var boundary = FindNearestErrorBoundary(fiber);

                if (boundary != null)
                {
                    if (TryActivateErrorBoundary(boundary, ex))
                    {
                        // Re-run work for the boundary using its updated state.
                        return UpdateErrorBoundary(boundary);
                    }
                }

                // No boundary handled the error - rethrow so it surfaces clearly.
                throw;
            }
        }

        /// <summary>
        /// Complete a unit of work - bubbles up to parent
        /// </summary>
        private void CompleteUnitOfWork(FiberNode unitOfWork)
        {
            var completedWork = unitOfWork;

            while (completedWork != null)
            {
                CompleteWork(completedWork);

                var siblingFiber = completedWork.Sibling;
                if (siblingFiber != null)
                {
                    // Work on sibling next
                    _nextUnitOfWork = siblingFiber;
                    return;
                }

                // No more siblings, go back to parent
                completedWork = completedWork.Parent;
            }

            // Reached root, we're done
            _nextUnitOfWork = null;
            // Reached root, we're done
            _nextUnitOfWork = null;
        }

        /// <summary>
        /// Complete work on a fiber - finalize any side effects
        /// </summary>
        private void CompleteWork(FiberNode fiber)
        {
            switch (fiber.Tag)
            {
                case FiberTag.HostComponent:
                    if (fiber.HostElement == null && fiber.ElementType != "root")
                    {
                        // Create the element using HostConfig
                        fiber.HostElement = _hostConfig.CreateElement(fiber.ElementType);
                        fiber.EffectTag |= EffectFlags.Placement;
                    }
                    else if (
                        fiber.PendingHostProps != null
                            ? !fiber.PendingHostProps.HostShallowEquals(fiber.HostProps)
                            : !AreHostPropsEqual(fiber.PendingProps, fiber.Props)
                    )
                    {
                        // Props actually changed (deep comparison), mark for update
                        fiber.EffectTag |= EffectFlags.Update;
                    }
                    else if (
                        fiber.PendingHostProps != null
                        && !ReferenceEquals(fiber.PendingHostProps, fiber.HostProps)
                    )
                    {
                        // Props are equal but the rented object is a different instance.
                        // We do NOT return PendingHostProps to the pool here: this runs
                        // in the render phase, and the source VNode (`vnode._hostProps`)
                        // still holds a live reference to it. If the render is interrupted
                        // and restarted (passive effect / setState during render), the same
                        // VNode reference is re-encountered and the same BaseProps would be
                        // re-scheduled — double-return — and worse, if it has already been
                        // returned and re-rented elsewhere, two fibers end up sharing one
                        // mutable BaseProps instance (the cross-wired "disco" style bug).
                        //
                        // The leak is bounded: when the owning function component eventually
                        // re-renders, the VNode subtree becomes garbage and the unused
                        // BaseProps is collected by the CLR. Pool returns only happen in the
                        // commit phase from CommitUpdate / CommitDeletion, when the OLD
                        // HostProps is provably no longer referenced by the new tree.
                        fiber.PendingHostProps = fiber.HostProps;
                    }
                    break;

                case FiberTag.HostPortal:
                    // Portal target may change between renders when the consumer rebinds
                    // <Portal target={x}>, e.g. via Hooks.UseUiDocumentRoot reacting to a
                    // Unity 6.3 panel rebuild. FiberFactory.CloneFiber already refreshes
                    // PortalTarget/HostElement from the new VNode; here we detect the
                    // identity change so the commit phase can reparent the portal's
                    // existing host descendants from the old target VE to the new one.
                    //
                    // Skipped on first mount (no Alternate) — CommitPlacement on the
                    // children handles initial attachment through fiber.HostElement.
                    if (
                        fiber.Alternate != null
                        && !ReferenceEquals(fiber.PortalTarget, fiber.Alternate.PortalTarget)
                    )
                    {
                        fiber.EffectTag |= EffectFlags.PortalRetarget;
                    }
                    break;
            }

            // Collect effects
            if (fiber.EffectTag != EffectFlags.None)
            {
                AppendToEffectList(fiber);
            }
        }

        /// <summary>
        /// Create work-in-progress fiber from current
        /// </summary>
        private FiberNode CreateWorkInProgress(FiberNode current, VirtualNode vnode)
        {
            var workInProgress = current.Alternate;

            if (workInProgress == null)
            {
                // Create new WIP fiber
                workInProgress = new FiberNode
                {
                    Tag = current.Tag,
                    ElementType = current.ElementType,
                    HostElement = current.HostElement,
                    Alternate = current,
                };
                current.Alternate = workInProgress;
            }
            else
            {
                // When reusing the alternate, ensure Alternate points back to current.
                // This is critical: after a commit, Current becomes the finished tree,
                // and its Alternate (which we're reusing) must point to Current so
                // reconciliation can find the children via wipFiber.Alternate.Child.
                workInProgress.Alternate = current;
            }

            // Propagate update flags to WIP - root is special case, doesn't use factory
            workInProgress.HasPendingStateUpdate = current.HasPendingStateUpdate;
            workInProgress.SubtreeHasUpdates = current.SubtreeHasUpdates;
            workInProgress.ReadsContext = current.ReadsContext;

            // CRITICAL FIX: Copy existing props to WIP so we have a baseline for ArePropsEqual comparison
            // Without this, WIP.Props is null, so comparison against PendingProps always fails
            workInProgress.Props = current.Props;
            workInProgress.HostProps = current.HostProps;

            // Update props for new render
            workInProgress.PendingProps = ExtractProps(vnode);
            workInProgress.PendingHostProps = vnode?._hostProps ?? current.PendingHostProps;
            // The passed vnode IS the child of the root, so we wrap it in a list
            // Fix: If vnode is null (state update), preserve existing children to avoid wiping the tree
            workInProgress.Children = vnode != null ? new[] { vnode } : current.Children;

            // Fix: Preserve Child link so Hooks.ResolveAnimationTarget can find the host element
            // during the render phase (before ReconcileChildren overwrites it).
            workInProgress.Child = current.Child;

            workInProgress.EffectTag = EffectFlags.None;
            workInProgress.NextEffect = null;
            workInProgress.Deletions = null;

            return workInProgress;
        }

        /// <summary>
        /// Append fiber to effect list
        /// </summary>
        private void AppendToEffectList(FiberNode fiber)
        {
            if (_root.LastEffect != null)
            {
                _root.LastEffect.NextEffect = fiber;
                _root.LastEffect = fiber;
            }
            else
            {
                _root.FirstEffect = fiber;
                _root.LastEffect = fiber;
            }
        }

        /// <summary>
        /// Commit phase - apply all effects to the DOM (VisualElement tree)
        /// </summary>
        private void CommitRoot()
        {
            _commitCount++;
            _isCommitting = true; // Prevent ScheduleUpdateOnFiber from corrupting WIP

            CommitPhaseSampler.Begin();
            try
            {
                var finishedWork = _root.WorkInProgress;

                // Process deletions first (from root down).
                // OPT-26: Skip the O(N) tree walk when no deletions were recorded
                // during reconciliation (the common steady-state case).
                if (_hasDeletions)
                {
                    CommitDeletions(_root.WorkInProgress);
                    _hasDeletions = false;
                }

                // Process effect list — passive effect fibers are collected here, not run yet
                _pendingPassiveEffects = new List<FiberNode>();
                var effect = _root.FirstEffect;
                while (effect != null)
                {
                    CommitWork(effect);
                    effect = effect.NextEffect;
                }
                // Swap current and work-in-progress
                _root.Current = finishedWork;

                // Commit props, update ComponentState.Fiber references, and clear flags
                // in a single tree walk (merged from three separate walks).
                // Must happen AFTER swap so ComponentState points to committed fibers,
                // and BEFORE deferred updates so their NEW flags aren't cleared.
                // Safe before passive effects because _isCommitting defers all state updates.
                CommitPropsAndClearFlags(_root.Current);

                // Flush pooled props returns accumulated during commit (every
                // registered backend family; UITK's flusher returns Style then
                // BaseProps in the original order). Must happen after the full
                // tree walk so no fiber still references an object that's
                // being returned to a pool.
                Props.Typed.HostPropsBase.__FlushAllFamilies();

                // Flush passive effects in two passes: all cleanups first, then all setups.
                // This preserves React's invariant that no component's setup runs before all
                // components' cleanups have completed within the same commit.
                //
                // When a scheduler is present (async mode) we enqueue a single batched-effect
                // action so effects fire AFTER the current frame's rendering is fully done —
                // matching React 18's post-paint passive-effect timing.
                // When no scheduler is available (sync / test mode) we run them immediately.
                if (_pendingPassiveEffects != null && _pendingPassiveEffects.Count > 0)
                {
                    var toFlush = _pendingPassiveEffects;
                    _pendingPassiveEffects = null;

                    if (_scheduler != null)
                    {
                        _scheduler.EnqueueBatchedEffect(() =>
                        {
                            for (int i = 0; i < toFlush.Count; i++)
                                FiberFunctionComponent.RunPassiveEffectCleanups(toFlush[i]);
                            for (int i = 0; i < toFlush.Count; i++)
                                FiberFunctionComponent.RunPassiveEffectSetups(toFlush[i]);
                        });
                    }
                    else
                    {
                        // Sync / no-scheduler fallback: run in-place (same behaviour as before).
                        for (int i = 0; i < toFlush.Count; i++)
                            FiberFunctionComponent.RunPassiveEffectCleanups(toFlush[i]);
                        for (int i = 0; i < toFlush.Count; i++)
                            FiberFunctionComponent.RunPassiveEffectSetups(toFlush[i]);
                    }
                }
                _pendingPassiveEffects = null;

                // Only clear WIP if it hasn't been updated by a synchronous effect (e.g. navigate)
                if (_root.WorkInProgress == finishedWork)
                {
                    _root.WorkInProgress = null;
                }

                if (_workInProgressRoot == finishedWork)
                {
                    _workInProgressRoot = null;
                }

                _root.FirstEffect = null;
                _root.LastEffect = null;

                // structural gate (§6): one-line commit summary at commit end.
                if (DiagnosticsConfig.CurrentTraceLevel != DiagnosticsConfig.TraceLevel.None)
                {
                    UnityEngine.Debug.Log(
                        $"[Fiber] Commit #{_commitCount} effects={_effectsCommitted}"
                    );
                }

                EmitMetrics();
            }
            finally
            {
                CommitPhaseSampler.End();
                _isCommitting = false;

                // Process any deferred updates scheduled during commit
                bool pendingUpdates = false;
                _isReplayingDeferred = true;
                while (_deferredUpdates.Count > 0)
                {
                    var (fiber, vnode) = _deferredUpdates.Dequeue();
                    // Update flags/WIP but DON'T schedule work yet
                    ScheduleUpdateOnFiber(fiber, vnode, scheduleWork: false);
                    pendingUpdates = true;
                }
                _isReplayingDeferred = false;

                // Now that all deferred updates are processed and flags are set/merged,
                // schedule the work loop ONCE.
                // Now that all deferred updates are processed and flags are set/merged,
                // schedule the work loop ONCE.
                if (pendingUpdates)
                {
                    if (_scheduler == null || !FiberConfig.TimeSlicingEnabled)
                    {
                        // Sync mode (no scheduler, or the time_slicing bypass): we must restart
                        // the loop manually because the previous WorkLoop exited — with the
                        // bypass active this commit was reached from WorkLoop, so no Slice
                        // callback exists to pick the deferred work up.
                        WorkLoop();
                    }
                    // Async mode: Do NOTHING.
                    // The 'Slice' callback that called us will see _nextUnitOfWork != null and reschedule automatically.
                    // Explicitly calling ScheduleRootWork here causes double-scheduling and race conditions.
                }
            }
        }

        /// <summary>
        /// Recursively commit deletions from a fiber
        /// </summary>
        private void CommitDeletions(FiberNode fiber)
        {
            if (fiber == null)
                return;

            // Process deletions on this fiber
            if (fiber.Deletions != null)
            {
                foreach (var deletion in fiber.Deletions)
                {
                    // structural gate (§6): one line per removed subtree (top-level only,
                    // not per recursive child). Replacements additionally log their own
                    // "[Fiber] Replace old -> new" at the reconcile decision site
                    // (FiberChildReconciliation.TraceReplace) — the family Basic set.
                    if (DiagnosticsConfig.CurrentTraceLevel != DiagnosticsConfig.TraceLevel.None)
                    {
                        UnityEngine.Debug.Log(
                            $"[Fiber] Delete {deletion.ElementType ?? deletion.Tag.ToString()}"
                        );
                    }
                    CommitDeletion(deletion);
                }
                fiber.Deletions = null;
            }

            // Recurse to children
            var child = fiber.Child;
            while (child != null)
            {
                CommitDeletions(child);
                child = child.Sibling;
            }
        }

        /// <summary>
        /// Commit a single effect
        /// </summary>
        private void CommitWork(FiberNode fiber)
        {
            if ((fiber.EffectTag & EffectFlags.Placement) != 0)
            {
                CommitPlacement(fiber);
            }

            if ((fiber.EffectTag & EffectFlags.Update) != 0)
            {
                CommitUpdate(fiber);
            }

            if ((fiber.EffectTag & EffectFlags.Deletion) != 0)
            {
                CommitDeletion(fiber);
            }

            // PortalRetarget runs AFTER Placement/Update/Deletion of this fiber
            // and AFTER all descendant CommitWork passes (the effect list is built
            // in post-order). By this point any newly-placed children have been
            // appended to the new target (because clone refreshed HostElement),
            // and any deleted children have been removed from the old target.
            // We move the remaining stable children from old target to new in
            // fiber order so they end up after the new ones — the chosen
            // ordering for the common case where target changes alone and the
            // child set is stable.
            if ((fiber.EffectTag & EffectFlags.PortalRetarget) != 0)
            {
                CommitPortalRetarget(fiber);
            }

            // Layout effects
            if ((fiber.EffectTag & EffectFlags.LayoutEffect) != 0)
            {
                CommitLayoutEffects(fiber);
            }

            // Passive effects — collected here, flushed two-pass in CommitRoot after tree swap
            if ((fiber.EffectTag & EffectFlags.PassiveEffect) != 0)
            {
                _pendingPassiveEffects?.Add(fiber);
            }

            if ((fiber.EffectTag & (EffectFlags.LayoutEffect | EffectFlags.PassiveEffect)) != 0)
            {
                _effectsCommitted++;
            }
        }

        /// <summary>
        /// Reparents the top-level host descendants of a HostPortal fiber from
        /// the previous <c>PortalTarget</c> VisualElement to the new one. Runs
        /// when <see cref="EffectFlags.PortalRetarget"/> is set in CommitWork.
        ///
        /// The walk is bounded: it descends only through non-host wrapper
        /// fibers (Fragment, FunctionComponent, ErrorBoundary, Suspense) to
        /// reach the first host descendant on each branch. Reparenting a host
        /// VisualElement carries its whole UI Toolkit subtree along, so there
        /// is no need to recurse into host descendants. Nested HostPortal
        /// fibers manage their own targets and are skipped.
        ///
        /// <c>VisualElement.Add</c> transparently removes a child from its
        /// previous parent before appending, which makes this safe even when
        /// the previous target has already been disposed by Unity (the
        /// 6.3 panel-rebuild scenario this fix exists for).
        /// </summary>
        private void CommitPortalRetarget(FiberNode portalFiber)
        {
            var newTarget = portalFiber.PortalTarget;
            if (newTarget == null)
            {
                // Defensive: clearing the target detaches existing host
                // descendants from the old parent so they do not linger as
                // orphan children of a dead panel root.
                DetachTopLevelHostChildren(portalFiber);
                return;
            }

            ReparentTopLevelHostChildren(portalFiber, newTarget);
        }

        /// <summary>
        /// Depth-first walk of <paramref name="parent"/>'s fiber subtree that
        /// reparents the first host fiber encountered on each branch to
        /// <paramref name="newTarget"/>, preserving fiber-tree order. Stops
        /// descent at host fibers (whose VE subtree comes along automatically)
        /// and at nested HostPortal fibers (which own their own target).
        /// </summary>
        private void ReparentTopLevelHostChildren(FiberNode parent, object newTarget)
        {
            var child = parent.Child;
            while (child != null)
            {
                if (child.Tag == FiberTag.HostPortal)
                {
                    // Nested portal owns its own target; do not touch.
                }
                else if (child.HostElement != null)
                {
                    if (!ReferenceEquals(_hostConfig.GetParent(child.HostElement), newTarget))
                    {
                        _hostConfig.AppendChild(newTarget, child.HostElement);
                    }
                }
                else
                {
                    // Wrapper fiber without a VE (Fragment / FunctionComponent /
                    // ErrorBoundary / Suspense). Descend to find the first host
                    // descendant on this branch.
                    ReparentTopLevelHostChildren(child, newTarget);
                }
                child = child.Sibling;
            }
        }

        /// <summary>
        /// Removes the top-level host descendants of a HostPortal from their
        /// current parent. Used when PortalTarget transitions to null between
        /// renders so the children do not remain attached to a stale parent.
        /// </summary>
        private void DetachTopLevelHostChildren(FiberNode parent)
        {
            var child = parent.Child;
            while (child != null)
            {
                if (child.Tag == FiberTag.HostPortal)
                {
                    // Nested portal owns its own target; do not touch.
                }
                else if (child.HostElement != null)
                {
                    var current = _hostConfig.GetParent(child.HostElement);
                    if (current != null)
                    {
                        _hostConfig.RemoveChild(current, child.HostElement);
                    }
                }
                else
                {
                    DetachTopLevelHostChildren(child);
                }
                child = child.Sibling;
            }
        }

        /// <summary>
        /// Commit placement - insert element into DOM at the correct position.
        /// Uses InsertBefore(parent, child, nextHostSibling) when a stable DOM
        /// sibling can be found, falling back to AppendChild when the element
        /// belongs at the end of the parent.
        /// </summary>
        private void CommitPlacement(FiberNode fiber)
        {
            // Portals use an existing VisualElement as their host and
            // should not be inserted into the normal parent hierarchy.
            if (fiber.Tag == FiberTag.HostPortal)
            {
                return;
            }

            if (fiber.HostElement == null)
            {
                return;
            }

            // Find parent host fiber
            var parentFiber = fiber.Parent;
            while (parentFiber != null && parentFiber.HostElement == null)
            {
                parentFiber = parentFiber.Parent;
            }

            if (parentFiber?.HostElement != null)
            {
                if (_hostConfig.GetParent(fiber.HostElement) != null)
                {
                    // Move placement (keyed reorder): the host is already
                    // parented — reposition it before its stable anchor. Props
                    // are NOT re-applied here; a changed-props move also
                    // carries an Update effect, which CommitWork runs right
                    // after this placement.
                    var moveAnchor = GetHostSibling(fiber);
                    _hostConfig.InsertBefore(
                        parentFiber.HostElement,
                        fiber.HostElement,
                        moveAnchor
                    );
                }
                else
                {
                    // Apply initial properties before inserting
                    if (fiber.PendingHostProps != null)
                    {
                        // diff gate (§6): EnableDiffTracing OR Verbose — the exact legacy OR.
                        if (
                            DiagnosticsConfig.EnableDiffTracing
                            || DiagnosticsConfig.CurrentTraceLevel
                                == DiagnosticsConfig.TraceLevel.Verbose
                        )
                        {
                            UnityEngine.Debug.Log(
                                $"[Fiber] Applying typed props to {fiber.ElementType}"
                            );
                        }

                        _hostConfig.ApplyTypedProperties(
                            fiber.HostElement,
                            fiber.ElementType,
                            null, // oldProps
                            fiber.PendingHostProps
                        );
                        fiber.HostProps = fiber.PendingHostProps;
                        fiber.Props = fiber.PendingProps;
                    }
                    else if (fiber.PendingProps != null)
                    {
                        // diff gate (§6)
                        if (
                            DiagnosticsConfig.EnableDiffTracing
                            || DiagnosticsConfig.CurrentTraceLevel
                                == DiagnosticsConfig.TraceLevel.Verbose
                        )
                        {
                            var propsStr = string.Join(", ", fiber.PendingProps.Keys);
                            UnityEngine.Debug.Log(
                                $"[Fiber] Applying props to {fiber.ElementType}: [{propsStr}]"
                            );
                        }

                        _hostConfig.ApplyProperties(
                            fiber.HostElement,
                            fiber.ElementType,
                            null, // oldProps
                            fiber.PendingProps
                        );
                        fiber.Props = fiber.PendingProps;
                    }
                    else
                    {
                        // diff gate (§6)
                        if (
                            DiagnosticsConfig.EnableDiffTracing
                            || DiagnosticsConfig.CurrentTraceLevel
                                == DiagnosticsConfig.TraceLevel.Verbose
                        )
                        {
                            UnityEngine.Debug.LogWarning(
                                $"[Fiber] NO props for {fiber.ElementType}"
                            );
                        }
                    }

                    // Find the nearest already-placed DOM element that logically
                    // follows this fiber in tree order.  If one exists, insert
                    // before it so the element lands at the right visual position.
                    // Otherwise append to the end (correct when there is no later sibling).
                    var before = GetHostSibling(fiber);
                    if (before != null)
                    {
                        // structural gate (§6): Basic restores structural events.
                        if (DiagnosticsConfig.CurrentTraceLevel != DiagnosticsConfig.TraceLevel.None)
                            UnityEngine.Debug.Log(
                                $"[Fiber] InsertBefore {fiber.ElementType} before {_hostConfig.GetDebugName(before)}"
                            );
                        _hostConfig.InsertBefore(
                            parentFiber.HostElement,
                            fiber.HostElement,
                            before
                        );
                    }
                    else
                    {
                        // structural gate (§6)
                        if (DiagnosticsConfig.CurrentTraceLevel != DiagnosticsConfig.TraceLevel.None)
                            UnityEngine.Debug.Log(
                                $"[Fiber] AppendChild {fiber.ElementType} to {parentFiber.ElementType}"
                            );
                        _hostConfig.AppendChild(parentFiber.HostElement, fiber.HostElement);
                    }
                }
            }
            else
            {
                // structural gate (§6): the no-host-parent anomaly is a structural event.
                if (DiagnosticsConfig.CurrentTraceLevel != DiagnosticsConfig.TraceLevel.None)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[Fiber] Could not find host parent for {fiber.ElementType}"
                    );
                }
            }
        }

        /// <summary>
        /// Finds the nearest VisualElement that should appear AFTER
        /// <paramref name="fiber"/> in DOM order and is already present in the
        /// parent element (i.e. not itself being newly placed).
        ///
        /// Mirrors React's <c>getHostSibling</c> algorithm:
        /// walk the fiber-tree siblings (and descend into non-host containers
        /// such as Fragments and FunctionComponents) until a stable host node
        /// is found, or we run out of siblings within the same host-parent.
        /// </summary>
        private static object GetHostSibling(FiberNode fiber)
        {
            FiberNode node = fiber;

            while (true) // outer: advance to the next fiber to examine
            {
                // Walk up until we find a node that has a sibling,
                // stopping if we cross into a host-element's territory.
                while (node.Sibling == null)
                {
                    if (
                        node.Parent == null
                        || node.Parent.Tag == FiberTag.HostComponent
                        || node.Parent.Tag == FiberTag.HostPortal
                    )
                    {
                        return null; // no DOM sibling exists before the host parent boundary
                    }
                    node = node.Parent;
                }
                node = node.Sibling;

                // Descend into `node` looking for the first stable HostComponent.
                // If at any point we hit a Placement node or a childless container,
                // break out so the outer loop can try the next sibling.
                while (node.Tag != FiberTag.HostComponent)
                {
                    if ((node.EffectTag & EffectFlags.Placement) != 0)
                    {
                        break; // this container is also new — try its sibling next
                    }

                    if (node.Tag == FiberTag.HostPortal || node.Child == null)
                    {
                        break; // can't descend further — try next sibling
                    }

                    node = node.Child; // descend into Fragment / FunctionComponent
                }

                // If we landed on a stable HostComponent, that is our anchor.
                if (
                    node.Tag == FiberTag.HostComponent
                    && (node.EffectTag & EffectFlags.Placement) == 0
                    && node.HostElement != null
                )
                {
                    return node.HostElement;
                }

                // Otherwise `node` is wherever we stopped.  The next outer loop
                // iteration will advance to node.Sibling (or walk up if null).
            }
        }

        /// <summary>
        /// Commit update - update element properties
        /// </summary>
        private void CommitUpdate(FiberNode fiber)
        {
            if (fiber.HostElement == null)
                return;

            // diff gate (§6) — the Label filter is kept deliberately: re-gating, not widening.
            if (
                (
                    DiagnosticsConfig.EnableDiffTracing
                    || DiagnosticsConfig.CurrentTraceLevel == DiagnosticsConfig.TraceLevel.Verbose
                )
                && fiber.ElementType == "Label"
            )
            {
                string oldText = null;
                string newText = null;

                // Try typed path first
                if (fiber.HostProps is Props.Typed.IHostTextProps oldTp)
                    oldText = oldTp.HostText;
                else if (
                    fiber.Props != null
                    && fiber.Props.TryGetValue("text", out var ov)
                    && ov is string os
                )
                {
                    oldText = os;
                }

                if (fiber.PendingHostProps is Props.Typed.IHostTextProps newTp)
                    newText = newTp.HostText;
                else if (
                    fiber.PendingProps != null
                    && fiber.PendingProps.TryGetValue("text", out var nv)
                    && nv is string ns
                )
                {
                    newText = ns;
                }

                UnityEngine.Debug.Log(
                    $"[Fiber] CommitUpdate Label oldText='{oldText}' newText='{newText}'"
                );
            }

            // Apply property changes using HostConfig
            if (fiber.PendingHostProps != null)
            {
                // Typed path — all built-in elements
                var oldHostProps = fiber.HostProps;
                _hostConfig.ApplyTypedProperties(
                    fiber.HostElement,
                    fiber.ElementType,
                    fiber.HostProps,
                    fiber.PendingHostProps
                );
                fiber.HostProps = fiber.PendingHostProps;

                // Schedule old props/style for pool return (only if actually replaced)
                if (oldHostProps != null && !ReferenceEquals(oldHostProps, fiber.HostProps))
                {
                    oldHostProps.__ScheduleReturnToFamilyPool();
                }
            }
            else
            {
                // Dict path — only V.Host() and VisualElementSafe reach here
                _hostConfig.ApplyProperties(
                    fiber.HostElement,
                    fiber.ElementType,
                    fiber.Props,
                    fiber.PendingProps
                );
            }
            fiber.Props = fiber.PendingProps;
        }

        /// <summary>
        /// Commit layout effects
        /// </summary>
        private void CommitLayoutEffects(FiberNode fiber)
        {
            if (fiber.Tag == FiberTag.FunctionComponent)
            {
                FiberFunctionComponent.CommitLayoutEffects(fiber);
            }
        }

        /// <summary>
        /// Commit deletion - remove element from DOM
        /// </summary>
        // The non-host half of component deletion: effect cleanups and signal
        // subscription disposal. Shared by CommitDeletion (live-tree deletion)
        // and AbandonFiber (host is dead and must not be touched).
        private static void RunComponentTeardown(FiberNode fiber)
        {
            if (fiber.Tag != FiberTag.FunctionComponent || fiber.ComponentState == null)
            {
                return;
            }
            var state = fiber.ComponentState;

            // Run and clear passive effects (UseEffect)
            if (state.FunctionEffects != null)
            {
                for (int i = 0; i < state.FunctionEffects.Count; i++)
                {
                    var effect = state.FunctionEffects[i];
                    try
                    {
                        effect.cleanup?.Invoke();
                    }
                    catch { }
                }
                state.FunctionEffects.Clear();
            }

            // Run and clear layout effects (UseLayoutEffect)
            if (state.FunctionLayoutEffects != null)
            {
                for (int i = 0; i < state.FunctionLayoutEffects.Count; i++)
                {
                    var effect = state.FunctionLayoutEffects[i];
                    try
                    {
                        effect.cleanup?.Invoke();
                    }
                    catch { }
                }
                state.FunctionLayoutEffects.Clear();
            }

            Hooks.DisposeSignalSubscriptions(state);
        }

        /// <summary>
        /// Tear down the mounted tree WITHOUT touching any host element. The
        /// Unity 6.5 remount path uses this when the host subtree has been
        /// poisoned by <c>ReleaseResources()</c> - a released element throws on
        /// hierarchy access, so the normal <see cref="UnmountRoot"/> is not
        /// legal. Effect cleanups run and signal subscriptions are disposed
        /// (otherwise a later signal Set would re-render the dead tree and
        /// throw); host removal, retention eviction and props pool returns are
        /// deliberately skipped - the caller sweeps retention sites via the
        /// liveness predicate instead.
        /// </summary>
        public void AbandonRoot()
        {
            if (_root?.Current == null)
            {
                _root = null;
                return;
            }
            AbandonFiber(_root.Current);
            _root.Current.Child = null;
            _root = null;
        }

        private static void AbandonFiber(FiberNode fiber)
        {
            while (fiber != null)
            {
                RunComponentTeardown(fiber);
                if (fiber.ComponentState != null)
                {
                    // Sever the re-render path so a stray setter on captured
                    // state cannot schedule work against the abandoned tree.
                    fiber.ComponentState.OnStateUpdated = null;
                }
                AbandonFiber(fiber.Child);
                fiber = fiber.Sibling;
            }
        }

        private void CommitDeletion(FiberNode fiber)
        {
            if (fiber == null)
            {
                return;
            }

            // Depth-first delete: clean up subtree before removing the current node.
            RunComponentTeardown(fiber);

            // Recurse into children so that all host descendants are removed.
            var child = fiber.Child;
            while (child != null)
            {
                CommitDeletion(child);
                child = child.Sibling;
            }

            // If this fiber has a HostElement, remove it from its parent
            if (fiber.HostElement != null)
            {
                _hostConfig.OnHostRemoved(fiber.HostElement);

                var parentFiber = fiber.Parent;
                while (parentFiber != null && parentFiber.HostElement == null)
                {
                    parentFiber = parentFiber.Parent;
                }

                if (parentFiber?.HostElement != null)
                {
                    _hostConfig.RemoveChild(parentFiber.HostElement, fiber.HostElement);
                }

                // Return deleted fiber's props/style to pool
                if (fiber.HostProps != null)
                {
                    fiber.HostProps.__ScheduleReturnToFamilyPool();
                }
            }
        }

        // ===== Host component updates =====

        private FiberNode UpdateHostComponent(FiberNode fiber)
        {
            if (fiber.Children != null)
            {
                ReconcileChildren(fiber, fiber.Children);
            }
            else if (fiber.Alternate?.Child != null)
            {
                // Bailout: Children cleared after commit. Clone existing child fibers.
                FiberFactory.CloneChildrenForBailout(fiber);
            }

            return fiber.Child;
        }

        private FiberNode UpdateFunctionComponent(FiberNode fiber)
        {
            // Render the function component and get child fiber
            return FiberFunctionComponent.RenderFunctionComponent(fiber, _hostContext, this);
        }

        private FiberNode UpdateFragment(FiberNode fiber)
        {
            return FiberFragment.UpdateFragment(fiber);
        }

        private FiberNode UpdatePortal(FiberNode fiber)
        {
            if (fiber.Children != null && fiber.Children.Count > 0)
            {
                ReconcileChildren(fiber, fiber.Children);
            }
            else if (fiber.Alternate?.Child != null)
            {
                // Bailout: Children cleared after commit.
                FiberFactory.CloneChildrenForBailout(fiber);
            }

            return fiber.Child;
        }

        private FiberNode UpdateErrorBoundary(FiberNode fiber)
        {
            bool resetRequested = !string.Equals(
                fiber.ErrorBoundaryResetKey,
                fiber.Alternate?.ErrorBoundaryResetKey,
                StringComparison.Ordinal
            );

            if (resetRequested)
            {
                fiber.ErrorBoundaryActive = false;
                fiber.ErrorBoundaryShowingFallback = false;
                fiber.ErrorBoundaryLastException = null;

                // Mark the reset as consumed by syncing the alternate's key. If a child
                // throws now, the catch path re-invokes UpdateErrorBoundary on this same
                // fiber — without this sync, resetRequested would still be true on the
                // re-entry, clearing ErrorBoundaryActive again and re-rendering the
                // children → throw → catch → reset → infinite loop.
                if (fiber.Alternate != null)
                {
                    fiber.Alternate.ErrorBoundaryResetKey = fiber.ErrorBoundaryResetKey;
                }
            }

            IReadOnlyList<VirtualNode> targetChildren;

            if (fiber.ErrorBoundaryActive && !resetRequested)
            {
                if (fiber.ErrorBoundaryFallback != null)
                {
                    targetChildren = new[] { fiber.ErrorBoundaryFallback };
                }
                else
                {
                    targetChildren = Array.Empty<VirtualNode>();
                }

                fiber.ErrorBoundaryShowingFallback = true;
            }
            else
            {
                targetChildren = fiber.ErrorBoundaryChildren ?? Array.Empty<VirtualNode>();
                fiber.ErrorBoundaryShowingFallback = false;
            }

            fiber.Children = targetChildren;

            if (targetChildren.Count > 0)
            {
                ReconcileChildren(fiber, targetChildren);
            }
            else
            {
                // Ensure any previous children are deleted.
                ReconcileChildren(fiber, Array.Empty<VirtualNode>());
            }

            return fiber.Child;
        }

        private FiberNode FindNearestErrorBoundary(FiberNode fiber)
        {
            var current = fiber;
            while (current != null)
            {
                if (current.Tag == FiberTag.ErrorBoundary)
                {
                    return current;
                }
                current = current.Parent;
            }

            return null;
        }

        private bool TryActivateErrorBoundary(FiberNode boundary, Exception exception)
        {
            if (boundary == null)
            {
                return false;
            }

            // Avoid recursive activation if fallback itself throws.
            if (boundary.ErrorBoundaryActive)
            {
                return false;
            }

            boundary.ErrorBoundaryActive = true;
            boundary.ErrorBoundaryShowingFallback = true;
            boundary.ErrorBoundaryLastException = exception;

            bool handled = false;

            if (boundary.ErrorBoundaryFallback != null)
            {
                handled = true;
            }

            if (boundary.ErrorBoundaryHandler != null)
            {
                try
                {
                    boundary.ErrorBoundaryHandler(exception);
                    handled = true;
                }
                catch (Exception handlerEx)
                {
                    try
                    {
                        UnityEngine.Debug.LogError(
                            $"Ruitk Fiber: Error boundary handler threw: {handlerEx}"
                        );
                    }
                    catch { }
                }
            }

            if (!handled)
            {
                // Reset flags so the boundary doesn't stay in an inconsistent state.
                boundary.ErrorBoundaryActive = false;
                boundary.ErrorBoundaryShowingFallback = false;
                boundary.ErrorBoundaryLastException = null;
                return false;
            }

            try
            {
                if (exception != null)
                {
                    UnityEngine.Debug.LogError(
                        $"Ruitk Fiber: Error boundary captured exception: {exception}"
                    );
                }
            }
            catch { }

            return true;
        }

        // ===== Child reconciliation (The heart of the reconciler) =====

        private void ReconcileChildren(FiberNode wipFiber, IReadOnlyList<VirtualNode> vnodes)
        {
            // Get current children from alternate (if exists)
            var currentFirstChild = wipFiber.Alternate?.Child;

            // Use the full reconciliation algorithm
            FiberChildReconciliation.ReconcileChildren(wipFiber, currentFirstChild, vnodes);

            // OPT-26: Track whether any fiber recorded deletions during reconciliation.
            // This allows CommitRoot to skip the full O(N) CommitDeletions tree walk
            // when no elements were removed (the common steady-state case).
            if (wipFiber.Deletions != null)
                _hasDeletions = true;
        }

        // ===== Helper methods =====

        private IReadOnlyDictionary<string, object> ExtractProps(VirtualNode vnode)
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

        private void EmitMetrics()
        {
            if (!MetricsEmittedHasSubscribers())
            {
                _renderStopwatch.Reset();
                return;
            }

            _renderStopwatch.Stop();
            long elapsedMs = _renderStopwatch.ElapsedMilliseconds;

            var snapshot = new FiberReconcilerMetrics(
                elapsedMs,
                _workUnitCount,
                _commitCount,
                _sliceCount,
                _yieldCount,
                _effectsCommitted
            );

            try
            {
                MetricsEmitted?.Invoke(snapshot);
            }
            catch
            {
                // Swallow listener exceptions to avoid breaking rendering.
            }
            finally
            {
                _renderStopwatch.Reset();
            }
        }

        private static bool MetricsEmittedHasSubscribers() => MetricsEmitted != null;

        /// <summary>
        /// Shallow-compare two prop dictionaries by value.
        /// Used by CompleteWork to avoid marking host components for update
        /// when only the dictionary instance differs but values are identical.
        /// </summary>
        private static bool AreHostPropsEqual(
            IReadOnlyDictionary<string, object> props1,
            IReadOnlyDictionary<string, object> props2
        )
        {
            if (props1 == props2)
                return true;
            if (props1 == null || props2 == null)
                return false;
            if (props1.Count != props2.Count)
                return false;

            foreach (var kvp in props1)
            {
                if (!props2.TryGetValue(kvp.Key, out var value2))
                    return false;
                if (!object.Equals(kvp.Value, value2))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Commit props, update ComponentState references, and clear flags in a single tree walk.
        /// Merges the former UpdateComponentStateReferences and CommitPropsAndClearFlags methods.
        /// </summary>
        private void CommitPropsAndClearFlags(FiberNode fiber)
        {
            if (fiber == null)
                return;

            // Update ComponentState.Fiber so hooks reference the committed fiber
            if (fiber.ComponentState != null)
            {
                fiber.ComponentState.Fiber = fiber;
            }

            // Commit props for next comparison
            fiber.Props = fiber.PendingProps;
            fiber.HostProps = fiber.PendingHostProps;

            // Commit typed props so the next render cycle's IProps equality check sees
            // the last-rendered props as the baseline (not stale pre-bailout props).
            // Without this, TypedProps would only be updated by the bailout path,
            // causing unnecessary re-renders whenever props change (even to same value).
            if (fiber.TypedPendingProps != null)
            {
                fiber.TypedProps = fiber.TypedPendingProps;
            }

            // Clear remaining flags (HasPendingStateUpdate already cleared in bailout check).
            // EffectTag MUST be cleared here: GetHostSibling skips any fiber whose
            // EffectTag has Placement set (treats it as "not yet committed"). If we leave
            // stale Placement flags on committed fibers, subsequent reorder commits cannot
            // find a stable anchor and fall back to AppendChild, producing wrong DOM order.
            fiber.SubtreeHasUpdates = false;
            fiber.EffectTag = EffectFlags.None;

            // Recursively process children
            var child = fiber.Child;
            while (child != null)
            {
                CommitPropsAndClearFlags(child);
                child = child.Sibling;
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Resets hook/effect state after an HMR rollback so the restored
        /// delegate re-initializes cleanly. Mirrors the cleanup in
        /// UitkxHmrDelegateSwapper.FullResetComponentState.
        /// </summary>
        private static void ResetComponentStateForHmrRollback(FiberNode fiber)
        {
            var state = fiber.ComponentState;
            if (state == null)
                return;

            if (state.FunctionEffects != null)
            {
                for (int i = 0; i < state.FunctionEffects.Count; i++)
                {
                    try
                    {
                        state.FunctionEffects[i].cleanup?.Invoke();
                    }
                    catch { }
                }
                state.FunctionEffects.Clear();
            }

            if (state.FunctionLayoutEffects != null)
            {
                for (int i = 0; i < state.FunctionLayoutEffects.Count; i++)
                {
                    try
                    {
                        state.FunctionLayoutEffects[i].cleanup?.Invoke();
                    }
                    catch { }
                }
                state.FunctionLayoutEffects.Clear();
            }

            Hooks.DisposeSignalSubscriptions(state);

            state.HookStates.Clear();
            state.HookOrderSignatures?.Clear();
            state.HookOrderPrimed = false;
            state.HookStateQueues?.Clear();
            state.PendingHookStatePreviews?.Clear();
            state.StateSetterDelegateCache?.Clear();
            state.ContextDependencies?.Clear();
        }
#endif
    }
}
