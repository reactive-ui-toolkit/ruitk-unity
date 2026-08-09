using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruitk.Core.Diagnostics;
using Ruitk.Core.Fiber;
using Ruitk.Core.Util;
using Ruitk.Signals;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Core
{
    internal static class HookContext
    {
        [ThreadStatic]
        public static FunctionComponentState Current;
    }

    public static partial class Hooks
    {
        public static bool EnableHookValidation { get; set; } = true;
        public static bool EnableStrictDiagnostics { get; set; } = false;

        public static bool EnableHookAutoRealign { get; set; } = true;

        /// <summary>Use <see cref="Ref{T}"/> (top-level class) instead.</summary>
        [Obsolete(
            "Hooks.MutableRef<T> is deprecated. Use Ref<T> instead (obtained via Hooks.UseRef<T>())."
        )]
        public sealed class MutableRef<T>
        {
            public T Value;

            public T Current
            {
                get => Value;
                set => Value = value;
            }
        }

        public delegate T StateSetter<T>(StateUpdate<T> update);

        public readonly struct StateUpdate<T>
        {
            internal readonly T Value;
            internal readonly Func<T, T> Updater;
            internal readonly bool UsesUpdater;

            private StateUpdate(T value, Func<T, T> updater, bool usesUpdater)
            {
                Value = value;
                Updater = updater;
                UsesUpdater = usesUpdater;
            }

            internal T Apply(T previous)
            {
                if (!UsesUpdater)
                {
                    return Value;
                }
                if (Updater == null)
                {
                    return previous;
                }
                return Updater(previous);
            }

            public static implicit operator StateUpdate<T>(T value) =>
                new StateUpdate<T>(value, null, false);

            public static implicit operator StateUpdate<T>(Func<T, T> updater) =>
                new StateUpdate<T>(default, updater, true);
        }

        internal readonly struct PendingStateUpdate
        {
            private readonly bool usesUpdater;
            private readonly object directValue;
            private readonly Func<object, object> updater;

            private PendingStateUpdate(
                bool usesUpdater,
                object directValue,
                Func<object, object> updater
            )
            {
                this.usesUpdater = usesUpdater;
                this.directValue = directValue;
                this.updater = updater;
            }

            public static PendingStateUpdate From<T>(StateUpdate<T> update)
            {
                if (update.UsesUpdater)
                {
                    Func<object, object> bridge = previous =>
                    {
                        T typedPrev = previous is T cast ? cast : default;
                        return update.Apply(typedPrev);
                    };
                    return new PendingStateUpdate(true, null, bridge);
                }
                return new PendingStateUpdate(false, update.Value, null);
            }

            public object Apply(object previous)
            {
                if (usesUpdater)
                {
                    return updater != null ? updater(previous) : previous;
                }
                return directValue;
            }
        }

        internal readonly struct StateSetterHandle<T>
        {
            private readonly FunctionComponentState state;
            private readonly NodeMetadata metadata;
            private readonly int index;
            private const byte ValueDelegateKind = 0;
            private const byte UpdaterDelegateKind = 1;
            private const byte CombinedDelegateKind = 2;
            private static readonly Action<T> NoopValueDelegate = _ => { };
            private static readonly Action<Func<T, T>> NoopUpdaterDelegate = _ => { };
            private static readonly StateSetter<T> NoopCombinedDelegate = _ => default;

            internal StateSetterHandle(FunctionComponentState state, int index)
            {
                this.state = state;
                metadata = state?.Owner;
                this.index = index;
            }

            public void Invoke(T value) => GetValueDelegate()(value);

            public void Invoke(Func<T, T> updater) => GetUpdaterDelegate()(updater);

            public T Invoke(StateUpdate<T> update) => GetCombinedDelegate()(update);

            public T Set(StateUpdate<T> update) => ApplyAndQueue(update);

            public T Set(Func<T, T> updater) => ApplyAndQueue(updater);

            public T Set(T value) => ApplyAndQueue(value);

            private T ApplyAndQueue(StateUpdate<T> update)
            {
                if (state == null)
                {
                    return update.Apply(default);
                }
                if (state.IsRendering)
                {
                    WarnStrict(
                        state,
                        metadata,
                        "state-update-during-render",
                        $"[Hooks][Strict] State update scheduled during render of '{DescribeComponent(metadata)}'. Move this set call to an effect or event handler."
                    );
                }
                var previous = GetProjectedState();
                var computed = update.Apply(previous);

                // Avoid scheduling if the state value did not actually change.
                if (EqualityComparer<T>.Default.Equals(previous, computed))
                {
                    return previous;
                }

                EnqueuePendingUpdate(update, computed);
                metadata?.SyncComponentState(state);
                return computed;
            }

            private T GetProjectedState()
            {
                if (state == null)
                {
                    return default;
                }

                if (
                    state.PendingHookStatePreviews != null
                    && state.PendingHookStatePreviews.TryGetValue(index, out var pending)
                    && pending is T projected
                )
                {
                    return projected;
                }

                if (state.HookStates == null || index >= state.HookStates.Count)
                {
                    return default;
                }

                return state.HookStates[index] is T current ? current : default;
            }

            private void EnqueuePendingUpdate(StateUpdate<T> update, T computed)
            {
                // Fiber path: if the component state has an update callback,
                // update the hook state immediately and trigger Fiber to
                // schedule a re-render. This avoids relying on the legacy
                // queued-update mechanism, which is primarily for the
                // old reconciler.
                if (state.OnStateUpdated != null)
                {
                    state.HookStates ??= new List<object>();
                    while (state.HookStates.Count <= index)
                    {
                        state.HookStates.Add(null);
                    }
                    state.HookStates[index] = computed;
                    state.PendingHookStatePreviews?.Remove(index);
                    metadata?.SyncComponentState(state);

                    if (InternalLogOptions.EnableInternalLogs)
                    {
                        try
                        {
                            UnityEngine.Debug.Log(
                                $"[Hooks] EnqueuePendingUpdate (Fiber immediate) state={state?.GetHashCode() ?? 0} slot={index} computed={computed}"
                            );
                        }
                        catch { }
                    }

                    state.OnStateUpdated.Invoke();
                    return;
                }

                // Legacy reconciler path: enqueue into a pending queue that
                // will be flushed before the next render.
                state.HookStateQueues ??= new Dictionary<int, HookStateUpdateQueue>();
                if (!state.HookStateQueues.TryGetValue(index, out var queue))
                {
                    queue = new HookStateUpdateQueue();
                    state.HookStateQueues[index] = queue;
                }
                queue.Enqueue(PendingStateUpdate.From(update));
                state.PendingHookStatePreviews ??= new Dictionary<int, object>();
                state.PendingHookStatePreviews[index] = computed;
                metadata?.SyncComponentState(state);

                if (InternalLogOptions.EnableInternalLogs)
                {
                    try
                    {
                        UnityEngine.Debug.Log(
                            $"[Hooks] EnqueuePendingUpdate state={state?.GetHashCode() ?? 0} slot={index} computed={computed} hasUpdateCb={(state?.OnStateUpdated != null ? "true" : "false")}"
                        );
                    }
                    catch { }
                }

                // Schedule a rerender via either Fiber (preferred) or legacy metadata.
                Hooks.RequestComponentRerender(metadata);
            }

            private Action<T> GetValueDelegate()
            {
                if (state == null)
                {
                    return NoopValueDelegate;
                }
                state.StateSetterDelegateCache ??= new Dictionary<(int, byte), Delegate>();
                var key = (index, ValueDelegateKind);
                if (
                    state.StateSetterDelegateCache.TryGetValue(key, out var existing)
                    && existing is Action<T> typed
                )
                {
                    return typed;
                }
                var setter = this;
                Action<T> action = value => setter.Set(value);
                state.StateSetterDelegateCache[key] = action;
                return action;
            }

            private Action<Func<T, T>> GetUpdaterDelegate()
            {
                if (state == null)
                {
                    return NoopUpdaterDelegate;
                }
                state.StateSetterDelegateCache ??= new Dictionary<(int, byte), Delegate>();
                var key = (index, UpdaterDelegateKind);
                if (
                    state.StateSetterDelegateCache.TryGetValue(key, out var existing)
                    && existing is Action<Func<T, T>> typed
                )
                {
                    return typed;
                }
                var setter = this;
                Action<Func<T, T>> action = update =>
                {
                    if (update == null)
                    {
                        return;
                    }
                    setter.Set(update);
                };
                state.StateSetterDelegateCache[key] = action;
                return action;
            }

            internal StateSetter<T> GetCombinedDelegate()
            {
                if (state == null)
                {
                    return NoopCombinedDelegate;
                }
                state.StateSetterDelegateCache ??= new Dictionary<(int, byte), Delegate>();
                var key = (index, CombinedDelegateKind);
                if (
                    state.StateSetterDelegateCache.TryGetValue(key, out var existing)
                    && existing is StateSetter<T> typed
                )
                {
                    return typed;
                }
                var setter = this;
                StateSetter<T> combined = update =>
                {
                    if (!update.UsesUpdater)
                    {
                        return setter.Set(update.Value);
                    }
                    if (update.Updater == null)
                    {
                        return setter.GetProjectedState();
                    }
                    return setter.Set(update.Updater);
                };
                state.StateSetterDelegateCache[key] = combined;
                return combined;
            }
        }

        private const string HookIdUseSafeArea = "UseSafeArea";
        private const string HookIdStableFunc = "UseStableFunc";
        private const string HookIdStableAction = "UseStableAction";
        private const string HookIdStableCallback = "UseStableCallback";
        private const string HookIdLayoutEffect = "UseLayoutEffect";
        private const string HookIdState = "UseState";
        private const string HookIdReducer = "UseReducer";
        private const string HookIdMemo = "UseMemo";
        private const string HookIdDeferred = "UseDeferredValue";
        private const string HookIdCallback = "UseCallback";
        private const string HookIdImperative = "UseImperativeHandle";
        private const string HookIdElementRef = "UseRefElement";
        private const string HookIdEffect = "UseEffect";
        private const string HookIdAnimate = "UseAnimate";
        private const string HookIdTween = "UseTween";
        private const string HookIdContext = "UseContext";
        private const string HookIdMutableRef = "UseMutableRef";
        private const string HookIdSignal = "UseSignal";
        private const string HookIdSfx = "UseSfx";
        private const string HookIdTransition = "UseTransition";

        private static readonly Action<Action> s_noOpStartTransition = a =>
        {
            if (a != null)
                a();
        };

        private static FunctionComponentState EnsureState(NodeMetadata metadata)
        {
            // Fiber path: rely on HookContext.Current when no metadata is available.
            if (HookContext.Current != null)
            {
                return HookContext.Current;
            }
            var state = metadata.ComponentState;
            if (state == null)
            {
                state = metadata.EnsureComponentState();
            }
            metadata?.SyncComponentState(state);
            return state;
        }

        private static void SyncState(NodeMetadata metadata, FunctionComponentState state)
        {
            metadata?.SyncComponentState(state);
        }

        private static UnityEngine.UIElements.VisualElement ResolveAnimationTarget(
            NodeMetadata metadata,
            FunctionComponentState state
        )
        {
            // Prefer the legacy container when available.
            if (metadata?.Container != null)
            {
                return metadata.Container;
            }

            // Fiber path: first try to find a host element in the current
            // function component's subtree (for headless wrappers like
            // Animate), then fall back to walking ancestor fibers.
            var fiber = state?.Fiber;
            if (fiber != null)
            {
                var descendantHost = FindFirstHostElement(fiber.Child);
                if (descendantHost == null && fiber.Child == null && fiber.Alternate != null)
                {
                    // Fallback to alternate if current child is not yet populated
                    descendantHost = FindFirstHostElement(fiber.Alternate.Child);
                }

                if (descendantHost != null)
                {
                    return descendantHost;
                }
            }

            while (fiber != null)
            {
                if (fiber.HostElement != null)
                {
                    return fiber.HostElement as UnityEngine.UIElements.VisualElement;
                }
                fiber = fiber.Parent;
            }
            return null;
        }

        private static UnityEngine.UIElements.VisualElement FindFirstHostElement(
            Fiber.FiberNode node
        )
        {
            var current = node;
            while (current != null)
            {
                if (current.HostElement != null)
                {
                    return current.HostElement as UnityEngine.UIElements.VisualElement;
                }
                var childResult = FindFirstHostElement(current.Child);
                if (childResult != null)
                {
                    return childResult;
                }
                current = current.Sibling;
            }
            return null;
        }

        private static void RecordHook(
            NodeMetadata metadata,
            FunctionComponentState state,
            string hookId
        )
        {
            if (!EnableHookValidation || metadata == null || state == null)
            {
                return;
            }
            state.HookOrderSignatures ??= new List<string>();
            int index = state.HookIndex;
            if (!state.HookOrderPrimed)
            {
                if (state.HookOrderSignatures.Count > index)
                {
                    state.HookOrderSignatures[index] = hookId;
                }
                else
                {
                    state.HookOrderSignatures.Add(hookId);
                }
                return;
            }
            if (index >= state.HookOrderSignatures.Count)
            {
                Debug.LogError(
                    $"[Hooks] Hook count changed for component {DescribeComponent(metadata)}"
                );
                return;
            }
            if (!string.Equals(state.HookOrderSignatures[index], hookId, StringComparison.Ordinal))
            {
                Debug.LogError(
                    $"[Hooks] Hook order mismatch: expected {state.HookOrderSignatures[index]} but saw {hookId} for component {DescribeComponent(metadata)}"
                );
                if (EnableHookAutoRealign)
                {
                    try
                    {
                        state.HookOrderSignatures.Clear();
                    }
                    catch { }
                    state.HookOrderSignatures.Add(hookId);
                    state.HookOrderPrimed = false;
                }
            }
        }

        private static string DescribeComponent(NodeMetadata metadata)
        {
            if (metadata == null)
            {
                return "<null>";
            }
            if (!string.IsNullOrEmpty(metadata.Key))
            {
                return metadata.Key;
            }
            try
            {
                return metadata.Container != null ? metadata.Container.name : "<container-null>";
            }
            catch
            {
                return "<unknown>";
            }
        }

        private static void WarnStrict(
            FunctionComponentState state,
            NodeMetadata metadata,
            string key,
            string message
        )
        {
            if (!EnableStrictDiagnostics || string.IsNullOrEmpty(message))
            {
                return;
            }
            state ??= metadata?.ComponentState ?? HookContext.Current;
            if (state == null)
            {
                return;
            }
            state.StrictDiagnosticsKeys ??= new HashSet<string>();
            if (!state.StrictDiagnosticsKeys.Add(key))
            {
                return;
            }
            try
            {
                Debug.LogWarning(message);
            }
            catch { }
        }

        private static void WarnMissingDependencies(
            NodeMetadata metadata,
            FunctionComponentState state,
            string hookId,
            int logicalIndex,
            object[] dependencies,
            bool treatEmptyAsMissing = false
        )
        {
            bool missing =
                dependencies == null || (treatEmptyAsMissing && (dependencies?.Length ?? 0) == 0);
            if (!missing)
            {
                return;
            }
            string component = DescribeComponent(metadata);
            string hookName = hookId ?? "Hook";
            string key = $"missing-deps:{hookName}:{logicalIndex}";
            WarnStrict(
                state,
                metadata,
                key,
                $"[Hooks][Strict] {hookName} in component '{component}' was invoked without a dependency array; it will re-run every render. Provide explicit dependencies or refactor the logic."
            );
        }

        private static IScheduler ResolveScheduler(NodeMetadata metadata)
        {
            if (metadata?.HostContext?.Environment == null)
            {
                return null;
            }
            if (
                metadata.HostContext.Environment.TryGetValue("scheduler", out var schedulerObj)
                && schedulerObj is IScheduler scheduler
            )
            {
                return scheduler;
            }
            return null;
        }

        private static void RequestComponentRerender(NodeMetadata metadata)
        {
            // Resolve state from either the current hook context (Fiber) or metadata (legacy).
            FunctionComponentState state =
                HookContext.Current ?? metadata?.ComponentState ?? metadata?.EnsureComponentState();
            if (state?.IsRendering == true)
            {
                state.PendingUpdate = true;
                if (metadata != null)
                {
                    WarnStrict(
                        state,
                        metadata,
                        "state-update-during-render",
                        $"[Hooks][Strict] State update scheduled during render of '{DescribeComponent(metadata)}'. Move this set call to an effect or event handler."
                    );
                    metadata?.SyncComponentState(state);
                }
                return;
            }
            if (metadata != null)
            {
                metadata?.SyncComponentState(state);
            }

            // Fiber integration: If we have a callback, use it
            if (state.OnStateUpdated != null)
            {
                state.OnStateUpdated.Invoke();
                return;
            }
        }

        internal static void FlushQueuedStateUpdates(FunctionComponentState state)
        {
            if (state == null)
            {
                return;
            }
            if (state.HookStateQueues == null || state.HookStateQueues.Count == 0)
            {
                state.PendingHookStatePreviews?.Clear();
                return;
            }

            if (InternalLogOptions.EnableInternalLogs)
            {
                try
                {
                    Debug.Log(
                        $"[Hooks] FlushQueuedStateUpdates state={state.GetHashCode()} queues={state.HookStateQueues.Count}"
                    );
                }
                catch { }
            }

            state.HookStates ??= new List<object>();
            foreach (var kvp in state.HookStateQueues)
            {
                var queue = kvp.Value;
                if (queue == null || !queue.HasPending)
                {
                    continue;
                }
                int slot = kvp.Key;
                object current = slot < state.HookStates.Count ? state.HookStates[slot] : null;
                var node = queue.ConsumeAll();
                while (node != null)
                {
                    var before = current;
                    current = node.Update.Apply(current);
                    node = node.Next;
                    if (InternalLogOptions.EnableInternalLogs)
                    {
                        try
                        {
                            Debug.Log($"[Hooks] Flush slot={slot} before={before} after={current}");
                        }
                        catch { }
                    }
                }
                while (state.HookStates.Count <= slot)
                {
                    state.HookStates.Add(null);
                }
                state.HookStates[slot] = current;
            }
            state.PendingHookStatePreviews?.Clear();
        }

        internal static void FlushQueuedStateUpdates(NodeMetadata metadata)
        {
            if (metadata == null)
            {
                return;
            }
            var state = metadata.EnsureComponentState();
            if (state.HookStateQueues == null || state.HookStateQueues.Count == 0)
            {
                state.PendingHookStatePreviews?.Clear();
                metadata?.SyncComponentState(state);
                return;
            }
            state.HookStates ??= new List<object>();
            foreach (var kvp in state.HookStateQueues)
            {
                var queue = kvp.Value;
                if (queue == null || !queue.HasPending)
                {
                    continue;
                }
                int slot = kvp.Key;
                object current = slot < state.HookStates.Count ? state.HookStates[slot] : null;
                var node = queue.ConsumeAll();
                while (node != null)
                {
                    current = node.Update.Apply(current);
                    node = node.Next;
                }
                while (state.HookStates.Count <= slot)
                {
                    state.HookStates.Add(null);
                }
                state.HookStates[slot] = current;
            }
            state.PendingHookStatePreviews?.Clear();
            metadata?.SyncComponentState(state);
        }

        public static SafeAreaInsets UseSafeArea(float tolerance = 0.5f)
        {
            var current = SafeAreaUtility.GetInsets();
            FunctionComponentState state = HookContext.Current;
            NodeMetadata metadata = state?.Owner;
            if (metadata == null || state == null)
            {
                return current;
            }
            RecordHook(metadata, state, HookIdUseSafeArea);
            state.HookStates ??= new System.Collections.Generic.List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add(current);
            }

            state.HookStates[state.HookIndex] = current;
            state.HookIndex++;
            metadata?.SyncComponentState(state);
            return current;
        }

        public static Func<T> UseStableFunc<T>(Func<T> function)
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (metadata == null || state == null)
            {
                return function;
            }
            RecordHook(metadata, state, HookIdStableFunc);
            state.HookStates ??= new List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add(function);
            }
            Func<T> stored = (Func<T>)state.HookStates[state.HookIndex];
            state.HookIndex++;
            state.HookStates[state.HookIndex - 1] = function;
            metadata?.SyncComponentState(state);
            return stored;
        }

        public static Action<T> UseStableAction<T>(Action<T> action)
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (metadata == null || state == null)
            {
                return action;
            }
            RecordHook(metadata, state, HookIdStableAction);
            state.HookStates ??= new List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add(action);
            }
            Action<T> stored = (Action<T>)state.HookStates[state.HookIndex];
            state.HookIndex++;
            state.HookStates[state.HookIndex - 1] = action;
            metadata?.SyncComponentState(state);
            return stored;
        }

        public static Action UseStableCallback(Action callback)
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (metadata == null || state == null)
            {
                return callback;
            }
            RecordHook(metadata, state, HookIdStableCallback);
            state.HookStates ??= new List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add(callback);
            }
            Action stored = (Action)state.HookStates[state.HookIndex];
            state.HookIndex++;
            state.HookStates[state.HookIndex - 1] = callback;
            metadata?.SyncComponentState(state);
            return stored;
        }

        public static void UseLayoutEffect(Func<Action> effectFactory, params object[] dependencies)
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return;
            }
            RecordHook(metadata, state, HookIdLayoutEffect);
            if (metadata != null)
            {
                WarnMissingDependencies(
                    metadata,
                    state,
                    HookIdLayoutEffect,
                    state.LayoutEffectIndex,
                    dependencies
                );
            }
            state.FunctionLayoutEffects ??= new List<(Func<Action>, object[], object[], Action)>();
            int index = state.LayoutEffectIndex;
            if (index >= state.FunctionLayoutEffects.Count)
            {
                state.FunctionLayoutEffects.Add((effectFactory, dependencies, null, null));
            }
            else
            {
                var entry = state.FunctionLayoutEffects[index];
                entry.factory = effectFactory;
                entry.deps = dependencies;
                state.FunctionLayoutEffects[index] = entry;
            }
            state.LayoutEffectIndex++;
            SyncState(metadata, state);
        }

        public static (T value, StateSetter<T> set) UseState<T>(T initial = default)
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            FunctionComponentState state = HookContext.Current ?? metadata?.EnsureComponentState();
            if (state == null)
            {
                StateSetter<T> noop = update =>
                {
                    if (!update.UsesUpdater)
                    {
                        return update.Value;
                    }
                    if (update.Updater == null)
                    {
                        return initial;
                    }
                    try
                    {
                        return update.Updater(initial);
                    }
                    catch
                    {
                        return initial;
                    }
                };
                return (initial, noop);
            }
            RecordHook(metadata, state, HookIdState);
            state.HookStates ??= new List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add(initial);
            }
            T currentValue = (T)state.HookStates[state.HookIndex];
            int capturedIndex = state.HookIndex;
            state.HookIndex++;
            if (metadata != null)
            {
                metadata?.SyncComponentState(state);
            }
            var setterHandle = new StateSetterHandle<T>(state, capturedIndex);
            return (currentValue, setterHandle.GetCombinedDelegate());
        }

        public static (TState state, Action<TAction> dispatch) UseReducer<TState, TAction>(
            Func<TState, TAction, TState> reducer,
            TState initialState
        )
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            FunctionComponentState state = HookContext.Current ?? metadata?.EnsureComponentState();
            if (state == null)
            {
                return (initialState, _ => { });
            }
            RecordHook(metadata, state, HookIdReducer);
            state.HookStates ??= new List<object>();

            ReducerHookState<TState, TAction> hookState;
            if (
                state.HookIndex < state.HookStates.Count
                && state.HookStates[state.HookIndex] is ReducerHookState<TState, TAction> existing
            )
            {
                hookState = existing;
                hookState.Reducer = reducer; // Always use the latest reducer
            }
            else
            {
                hookState = new ReducerHookState<TState, TAction>
                {
                    Value = initialState,
                    Reducer = reducer,
                };
                if (state.HookIndex < state.HookStates.Count)
                    state.HookStates[state.HookIndex] = hookState;
                else
                    state.HookStates.Add(hookState);
            }

            int index = state.HookIndex;
            TState currentState = hookState.Value;

            // Cache dispatch delegate — only allocate once per hook slot
            if (hookState.Dispatch == null)
            {
                hookState.Dispatch = action =>
                {
                    var hs = (ReducerHookState<TState, TAction>)state.HookStates[index];
                    TState previous = hs.Value;
                    TState next;
                    try
                    {
                        next = hs.Reducer(previous, action);
                    }
                    catch
                    {
                        next = previous;
                    }
                    if (!EqualityComparer<TState>.Default.Equals(previous, next))
                    {
                        hs.Value = next;
                        RequestComponentRerender(metadata);
                    }
                };
            }

            state.HookIndex++;
            if (metadata != null)
            {
                metadata?.SyncComponentState(state);
            }
            return (currentState, hookState.Dispatch);
        }

        private sealed class ReducerHookState<TState, TAction>
        {
            public TState Value;
            public Func<TState, TAction, TState> Reducer;
            public Action<TAction> Dispatch;
        }

        public static T UseMemo<T>(Func<T> factory, params object[] dependencies)
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return factory();
            }
            RecordHook(metadata, state, HookIdMemo);
            if (metadata != null)
            {
                WarnMissingDependencies(
                    metadata,
                    state,
                    HookIdMemo,
                    state.HookIndex,
                    dependencies,
                    treatEmptyAsMissing: true
                );
            }
            state.HookStates ??= new List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add((factory(), dependencies));
            }

            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add((factory(), dependencies));
            }
            var tuple = ((T value, object[] d))state.HookStates[state.HookIndex];
            bool changed = DepsChanged(tuple.d, dependencies);
            if (changed)
            {
                tuple = (factory(), dependencies);
                state.HookStates[state.HookIndex] = tuple;
            }
            state.HookIndex++;
            SyncState(metadata, state);
            return tuple.value;
        }

        public static T UseDeferredValue<T>(T value, params object[] dependencies)
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return value;
            }
            RecordHook(metadata, state, HookIdDeferred);
            if (metadata != null)
            {
                WarnMissingDependencies(
                    metadata,
                    state,
                    HookIdDeferred,
                    state.HookIndex,
                    dependencies
                );
            }
            state.HookStates ??= new List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add((value, dependencies));
            }
            var tuple = ((T val, object[] d))state.HookStates[state.HookIndex];
            bool changed = DepsChanged(tuple.d, dependencies);
            if (changed && !Equals(tuple.val, value))
            {
                T newVal = value;
                int index = state.HookIndex;
                var scheduler = ResolveScheduler(metadata);
                if (scheduler != null)
                {
                    scheduler.EnqueueBatchedEffect(() =>
                    {
                        state.HookStates[index] = (newVal, dependencies);
                        SyncState(metadata, state);
                    });
                }
                else
                {
                    state.HookStates[index] = (newVal, dependencies);
                }
            }
            state.HookIndex++;
            SyncState(metadata, state);
            var latest = ((T val, object[] d))state.HookStates[state.HookIndex - 1];
            return latest.val;
        }

        public static (bool isPending, Action<Action> startTransition) UseTransition()
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return (false, s_noOpStartTransition);
            }
            RecordHook(metadata, state, HookIdTransition);
            // Materialize the slot before bumping the cursor. HookIndex and
            // HookStates.Count must stay in lockstep: a bare cursor bump leaves the
            // list one short, so the NEXT Add-if-fresh hook (UseState, UseMemo,
            // UseImperativeHandle, ...) appends one element and then indexes one past
            // the end — ArgumentOutOfRangeException on the first render of any
            // component that calls another slot hook after UseTransition.
            state.HookStates ??= new List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add(null);
            }
            state.HookIndex++;
            SyncState(metadata, state);
            return (false, s_noOpStartTransition);
        }

        public static Func<T> UseCallback<T>(Func<T> callback, params object[] dependencies)
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return callback;
            }
            RecordHook(metadata, state, HookIdCallback);
            if (metadata != null)
            {
                WarnMissingDependencies(
                    metadata,
                    state,
                    HookIdCallback,
                    state.HookIndex,
                    dependencies,
                    treatEmptyAsMissing: true
                );
            }
            state.HookStates ??= new List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add((callback, dependencies));
            }
            var tuple = ((Func<T> cb, object[] d))state.HookStates[state.HookIndex];
            bool changed = DepsChanged(tuple.d, dependencies);
            if (changed)
            {
                tuple = (callback, dependencies);
                state.HookStates[state.HookIndex] = tuple;
            }
            state.HookIndex++;
            SyncState(metadata, state);
            return tuple.cb;
        }

        public static THandle UseImperativeHandle<THandle>(
            Func<THandle> factory,
            params object[] dependencies
        )
            where THandle : class
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return factory();
            }
            RecordHook(metadata, state, HookIdImperative);
            if (metadata != null)
            {
                WarnMissingDependencies(
                    metadata,
                    state,
                    HookIdImperative,
                    state.HookIndex,
                    dependencies,
                    treatEmptyAsMissing: true
                );
            }
            state.HookStates ??= new List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add((factory(), dependencies));
            }
            var tuple = ((THandle handle, object[] d))state.HookStates[state.HookIndex];
            if (DepsChanged(tuple.d, dependencies))
            {
                tuple = (factory(), dependencies);
                state.HookStates[state.HookIndex] = tuple;
            }
            state.HookIndex++;
            SyncState(metadata, state);
            return tuple.handle;
        }

        public static Ref<T> UseRef<T>(T initial = default)
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return new Ref<T> { Current = initial };
            }
            RecordHook(metadata, state, HookIdMutableRef);
            state.HookStates ??= new List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add(new Ref<T> { Current = initial });
            }
            var stored = (Ref<T>)state.HookStates[state.HookIndex];
            state.HookIndex++;
            SyncState(metadata, state);
            return stored;
        }

        public static VisualElement UseRef()
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return null;
            }

            // Fiber path: prefer the current fiber's host element or
            // its first host-descendant as the ref target.
            VisualElement target = null;
            var fiber = state.Fiber;
            if (fiber != null)
            {
                target = ResolveAnimationTarget(metadata, state);
            }

            // Legacy path: fall back to metadata.Container when available.
            target ??= metadata?.Container;

            if (target == null)
            {
                return null;
            }

            RecordHook(metadata, state, HookIdElementRef);
            state.HookStates ??= new List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add(target);
            }
            else
            {
                state.HookStates[state.HookIndex] = target;
            }
            state.HookIndex++;
            SyncState(metadata, state);
            return target;
        }

        public static void UseEffect(Func<Action> effectFactory, params object[] dependencies)
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return;
            }
            RecordHook(metadata, state, HookIdEffect);
            if (metadata != null)
            {
                WarnMissingDependencies(
                    metadata,
                    state,
                    HookIdEffect,
                    state.EffectIndex,
                    dependencies
                );
            }
            state.FunctionEffects ??= new List<(Func<Action>, object[], object[], Action)>();
            int index = state.EffectIndex;
            if (index >= state.FunctionEffects.Count)
            {
                state.FunctionEffects.Add((effectFactory, dependencies, null, null));
            }
            else
            {
                var entry = state.FunctionEffects[index];
                entry.factory = effectFactory;
                entry.deps = dependencies;
                state.FunctionEffects[index] = entry;
            }
            // detail gate (§6): routed through InternalLogOptions (the file's majority
            // style) — EnableInternalLogs is set from == Verbose at the mount seams, so the
            // meaning is unchanged. Under strict_mode this logs per invoke (twice) at
            // Verbose — truthful: two captures happened.
            if (InternalLogOptions.EnableInternalLogs)
            {
                try
                {
                    Debug.Log(
                        "[Hooks] UseEffect captured index="
                            + index
                            + ", depsLen="
                            + (dependencies?.Length ?? 0)
                    );
                }
                catch { }
            }
            state.EffectIndex++;
            SyncState(metadata, state);
        }

        public static bool HasContextChanged(Fiber.FiberNode fiber)
        {
            if (
                fiber == null
                || fiber.ComponentState == null
                || fiber.ComponentState.ContextDependencies == null
            )
            {
                return false;
            }

            var deps = fiber.ComponentState.ContextDependencies;

            // DEBUG LOG: CHECK COUNT
            var compName =
                fiber.ElementType ?? fiber.TypedRender?.Method.DeclaringType?.Name ?? "Unknown";
            for (int i = 0; i < deps.Count; i++)
            {
                var dep = deps[i];
                object currentValue = ResolveContextValue(fiber, fiber.ComponentState, dep.Key);

                if (!Equals(currentValue, dep.Value))
                {
                    return true;
                }
            }

            return false;
        }

        private static object ResolveContextValue(
            Fiber.FiberNode fiber,
            FunctionComponentState state,
            string key
        )
        {
            object resolved = null;

            // Fiber path: walk the Fiber tree's provided context
            var current = fiber;
            while (current != null)
            {
                if (
                    current.ProvidedContext != null
                    && current.ProvidedContext.TryGetValue(key, out var providedValue)
                )
                {
                    resolved = providedValue;
                    break;
                }
                current = current.Parent;
            }

            // Fallback to HostContext / Environment if not found in Fiber tree
            if (resolved == null && state != null && state.HostContext != null)
            {
                if (state.HostContext.Environment != null)
                {
                    if (state.HostContext.Environment.TryGetValue(key, out var envValue))
                    {
                        resolved = envValue;
                    }
                }
            }

            return resolved;
        }

        public static T UseContext<T>(string key)
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null || string.IsNullOrEmpty(key))
            {
                return default;
            }

            RecordHook(metadata, state, HookIdContext);

            // CRITICAL: Mark fiber as reading context
            if (state.Fiber != null)
            {
                state.Fiber.ReadsContext = true;
            }

            // Resolve context value
            object resolved = ResolveContextValue(state.Fiber, state, key);

            // Store dependency in specific list (React-style), NOT in hook slots
            // This avoids breaking memory layout and allows UseContext to be truly stateless regarding hook order
            state.ContextDependencies ??= new List<ContextDependency>();
            state.ContextDependencies.Add(new ContextDependency(key, resolved));

            // DEBUG UNCONDITIONAL
            var compName =
                state.Fiber?.ElementType
                ?? state.Fiber?.TypedRender?.Method.DeclaringType?.Name
                ?? "Unknown";
            return resolved is T result ? result : default;
        }

        public static T UseSignal<T>(Signal<T> signal)
        {
            // Fast-path: identity selector + default comparer → skip boxing wrappers
            return (T)UseSignalInternal(signal, null, null);
        }

        public static TSlice UseSignal<T, TSlice>(
            Signal<T> signal,
            Func<T, TSlice> selector,
            IEqualityComparer<TSlice> comparer = null
        )
        {
            if (signal == null)
            {
                return default;
            }
            if (selector == null)
            {
                throw new ArgumentNullException(nameof(selector));
            }
            var boxedSelector = new Func<object, object>(raw => selector((T)raw));
            var boxedComparer = new BoxedEqualityComparer<TSlice>(
                comparer ?? EqualityComparer<TSlice>.Default
            );
            return (TSlice)UseSignalInternal(signal, boxedSelector, boxedComparer);
        }

        public static T UseSignal<T>(string key, T initialValue = default)
        {
            return UseSignal(Ruitk.Signals.SignalFactory.Get<T>(key, initialValue));
        }

        public static TSlice UseSignal<T, TSlice>(
            string key,
            Func<T, TSlice> selector,
            IEqualityComparer<TSlice> comparer = null,
            T initialValue = default
        )
        {
            return UseSignal(
                Ruitk.Signals.SignalFactory.Get<T>(key, initialValue),
                selector,
                comparer
            );
        }

        public static void ProvideContext<T>(string key, T value) =>
            ProvideContext(key, (object)value);

        public static void ProvideContext(string key, object value)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            // Fiber path: attach provided context to the current function component state / fiber
            var state = HookContext.Current;
            var fiber = state?.Fiber;
            if (fiber == null)
            {
                return;
            }

            fiber.ProvidedContext ??= new Dictionary<string, object>();

            // Detect whether the context value actually changed vs the committed tree.
            // If it did, walk the current (alternate) subtree and mark every consumer fiber
            // with HasPendingStateUpdate=true and every intermediate ancestor with
            // SubtreeHasUpdates=true.  FiberFactory.CloneForReuse copies both flags into the
            // WIP tree, so bailed-out intermediaries are forced to traverse into their
            // subtrees and consumers are forced to re-render — exactly React 18's
            // propagateContextChange behaviour.
            var alternateFiber = fiber.Alternate;
            bool valueChanged = true;
            if (
                alternateFiber?.ProvidedContext != null
                && alternateFiber.ProvidedContext.TryGetValue(key, out var oldValue)
                && Equals(oldValue, value)
            )
            {
                valueChanged = false;
            }

            fiber.ProvidedContext[key] = value;

            if (valueChanged && alternateFiber?.Child != null)
            {
                PropagateContextChange(key, alternateFiber.Child);
            }
        }

        /// <summary>
        /// Depth-first walk of the CURRENT (committed) subtree rooted at <paramref name="fiber"/>.
        /// For every function component that has a UseContext dependency on <paramref name="key"/>
        /// we set HasPendingStateUpdate=true.  For every ancestor that has at least one such
        /// descendant we set SubtreeHasUpdates=true.  Both flags are propagated into the WIP
        /// tree by FiberFactory.CloneForReuse, ensuring bailed-out parents still visit their
        /// children and consumers are not incorrectly skipped.
        /// Recursion stops when a nested provider for the same key is found because that
        /// provider's subtree reads its own (inner) value, not ours.
        /// </summary>
        private static bool PropagateContextChange(string key, Fiber.FiberNode fiber)
        {
            bool anyMarked = false;
            while (fiber != null)
            {
                // A nested provider for this key shadows the outer one — stop here.
                if (fiber.ProvidedContext != null && fiber.ProvidedContext.ContainsKey(key))
                {
                    fiber = fiber.Sibling;
                    continue;
                }

                bool selfMarked = false;
                bool childrenMarked = false;

                // Check if this fiber directly consumes the key.
                if (fiber.ReadsContext && fiber.ComponentState?.ContextDependencies != null)
                {
                    foreach (var dep in fiber.ComponentState.ContextDependencies)
                    {
                        if (dep.Key == key)
                        {
                            fiber.HasPendingStateUpdate = true;
                            selfMarked = true;
                            break;
                        }
                    }
                }

                // Recurse into children.
                if (fiber.Child != null)
                {
                    childrenMarked = PropagateContextChange(key, fiber.Child);
                }

                // If descendants (but not self) were marked, record that this fiber's
                // subtree has pending work so the bailout path traverses into it.
                if (childrenMarked && !selfMarked)
                {
                    fiber.SubtreeHasUpdates = true;
                }

                if (selfMarked || childrenMarked)
                {
                    anyMarked = true;
                }

                fiber = fiber.Sibling;
            }
            return anyMarked;
        }

        /// <summary>
        /// Suspend the current Fiber function component until the given task
        /// completes, using Suspense. If the task is already completed, this
        /// is a no-op. Otherwise it schedules a re-render and throws a
        /// FiberSuspenseSuspendException to abort the current render pass.
        /// </summary>
        public static void SuspendUntil(Task task)
        {
            if (task == null || task.IsCompleted)
            {
                return;
            }

            var state = HookContext.Current;
            if (state != null)
            {
                state.SuspensePendingTask = task;
                state.OnStateUpdated?.Invoke();
            }

            throw new FiberSuspenseSuspendException(task);
        }

        public static void FlushSync(Action action)
        {
            if (action == null)
            {
                return;
            }

            // Prefer the current hook context's scheduler when available.
            var state = HookContext.Current;
            IScheduler scheduler = ResolveScheduler(state?.Owner);

            if (scheduler != null)
            {
                scheduler.BeginBatch();
                try
                {
                    action();
                    scheduler.PumpNow();
                }
                finally
                {
                    scheduler.EndBatch();
                }
            }
            else
            {
                action();
            }
        }

        public static void FlushSync()
        {
            var state = HookContext.Current;
            IScheduler scheduler = ResolveScheduler(state?.Owner);
            scheduler?.PumpNow();
        }

        private static readonly Func<object, object> IdentitySelector = value => value;

        private static object UseSignalInternal(
            SignalBase signal,
            Func<object, object> selector,
            IEqualityComparer<object> comparer
        )
        {
            selector ??= IdentitySelector;
            comparer ??= EqualityComparer<object>.Default;

            NodeMetadata metadata = HookContext.Current?.Owner;
            FunctionComponentState state = HookContext.Current ?? metadata?.EnsureComponentState();
            if (state == null)
            {
                return selector(signal?.UntypedValue);
            }
            RecordHook(metadata, state, HookIdSignal);
            state.HookStates ??= new List<object>();
            SignalSubscriptionState slot;
            if (
                state.HookIndex < state.HookStates.Count
                && state.HookStates[state.HookIndex] is SignalSubscriptionState existing
            )
            {
                slot = existing;
            }
            else
            {
                slot = new SignalSubscriptionState(metadata, state);
                if (state.HookIndex < state.HookStates.Count)
                {
                    state.HookStates[state.HookIndex] = slot;
                }
                else
                {
                    state.HookStates.Add(slot);
                }
            }
            object latest = slot.Bind(signal, selector, comparer);
            state.HookIndex++;
            SyncState(metadata, state);
            return latest;
        }

        private static bool DepsChanged(object[] previousDependencies, object[] nextDependencies)
        {
            if (previousDependencies == null || nextDependencies == null)
            {
                return true;
            }
            if (previousDependencies.Length != nextDependencies.Length)
            {
                return true;
            }
            for (int i = 0; i < previousDependencies.Length; i++)
            {
                if (!Equals(previousDependencies[i], nextDependencies[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private sealed class SignalSubscriptionState : IDisposable
        {
            private readonly NodeMetadata owner;
            private readonly FunctionComponentState state;
            private SignalBase signal;
            private IDisposable subscription;
            private Func<object, object> selector = IdentitySelector;
            private IEqualityComparer<object> comparer = EqualityComparer<object>.Default;
            private object lastValue;
            private bool initialized;

            internal SignalSubscriptionState(NodeMetadata owner, FunctionComponentState state)
            {
                this.owner = owner;
                this.state = state;
            }

            public object Bind(
                SignalBase nextSignal,
                Func<object, object> nextSelector,
                IEqualityComparer<object> nextComparer
            )
            {
                bool signalChanged = !ReferenceEquals(signal, nextSignal);
                Func<object, object> resolvedSelector = nextSelector ?? IdentitySelector;
                IEqualityComparer<object> resolvedComparer =
                    nextComparer ?? EqualityComparer<object>.Default;
                bool selectorChanged =
                    !ReferenceEquals(selector, resolvedSelector)
                    || !ReferenceEquals(comparer, resolvedComparer);

                selector = resolvedSelector;
                comparer = resolvedComparer;

                if (signalChanged)
                {
                    DisposeSubscription();
                    signal = nextSignal;
                    if (signal != null)
                    {
                        subscription = signal.SubscribeRaw(OnSignalChanged);
                    }
                    initialized = false;
                }

                if (!initialized || selectorChanged)
                {
                    lastValue = selector(signal?.UntypedValue);
                    initialized = true;
                }

                return lastValue;
            }

            private void OnSignalChanged(object rawValue)
            {
                var next = selector(rawValue);
                if (comparer.Equals(next, lastValue))
                {
                    return;
                }
                lastValue = next;

                // Fiber path: prefer FunctionComponentState.OnStateUpdated when available.
                if (state?.OnStateUpdated != null)
                {
                    try
                    {
                        state.OnStateUpdated.Invoke();
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Debug.LogException(ex);
                        }
                        catch { }
                    }
                    return;
                }

                // Legacy path: fall back to metadata-based rerender.
                RequestComponentRerender(owner);
            }

            private void DisposeSubscription()
            {
                subscription?.Dispose();
                subscription = null;
            }

            public void Dispose()
            {
                DisposeSubscription();
                signal = null;
                initialized = false;
            }
        }

        private sealed class BoxedEqualityComparer<T> : IEqualityComparer<object>
        {
            private readonly IEqualityComparer<T> comparer;

            public BoxedEqualityComparer(IEqualityComparer<T> comparer)
            {
                this.comparer = comparer ?? EqualityComparer<T>.Default;
            }

            public new bool Equals(object x, object y)
            {
                if (x is T tx && y is T ty)
                {
                    return comparer.Equals(tx, ty);
                }
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                if (obj is T t)
                {
                    return comparer.GetHashCode(t);
                }
                return obj?.GetHashCode() ?? 0;
            }
        }

        internal static void DisposeSignalSubscriptions(NodeMetadata metadata)
        {
            if (metadata == null)
            {
                return;
            }
            DisposeSignalSubscriptions(metadata.ComponentState);
        }

        internal static void DisposeSignalSubscriptions(FunctionComponentState state)
        {
            if (state?.HookStates == null)
            {
                return;
            }
            foreach (var hookState in state.HookStates)
            {
                if (hookState is SignalSubscriptionState subscription)
                {
                    subscription.Dispose();
                }
            }
        }
    }
}
