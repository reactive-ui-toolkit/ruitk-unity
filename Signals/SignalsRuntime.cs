using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ruitk.Signals
{
    public sealed class SignalRegistry
    {
        private readonly Dictionary<string, SignalBase> signals = new(StringComparer.Ordinal);
        private readonly object gate = new();

        public Signal<T> GetOrCreate<T>(string key, T initialValue = default)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Signal key must be non-empty", nameof(key));
            }
            lock (gate)
            {
                if (signals.TryGetValue(key, out SignalBase existing))
                {
                    if (existing is Signal<T> typed)
                    {
                        return typed;
                    }
                    throw new InvalidOperationException(
                        $"Signal '{key}' already exists with type {existing.ValueType.Name}."
                    );
                }
                var created = new Signal<T>(key, initialValue);
                signals[key] = created;
                return created;
            }
        }

        internal bool TryGet<T>(string key, out Signal<T> signal)
        {
            lock (gate)
            {
                if (
                    signals.TryGetValue(key, out SignalBase existing) && existing is Signal<T> typed
                )
                {
                    signal = typed;
                    return true;
                }
            }
            signal = null;
            return false;
        }
    }

    public static class SignalFactory
    {
        /// <summary>
        /// The keyed, process-wide signal for <paramref name="key"/>, created on first use.
        /// The registry owns it for the lifetime of the process: there is no Remove, so a
        /// signal obtained this way is never collected. That is the right trade for shared
        /// application state and the wrong one for state owned by something short-lived —
        /// use <see cref="Create{T}"/> for that.
        /// </summary>
        public static Signal<T> Get<T>(string key, T initialValue = default)
        {
            SignalsRuntime.EnsureInitialized();
            return SignalsRuntime.Registry.GetOrCreate(key, initialValue);
        }

        /// <summary>
        /// A signal owned by its caller. It is never placed in the registry, so it is not
        /// discoverable by <see cref="TryGet{T}"/>, cannot collide with a key, and is
        /// collected with whatever holds it — the point being that a short-lived owner
        /// (a presenter, a window, one screen) no longer has to invent a unique key and
        /// leak a registry entry plus its last value for the lifetime of the process.
        ///
        /// Its <see cref="SignalBase.Key"/> is empty, which is how an unkeyed signal is
        /// spelled; nothing in the library reads Key, and an unkeyed signal is not in the
        /// dictionary that keys address.
        ///
        /// Unlike <see cref="Get{T}"/> this does not initialise the signals runtime, so it
        /// creates no host GameObject. Making a self-contained object should not have a
        /// process-wide side effect.
        /// </summary>
        /// <param name="comparer">
        /// Decides whether <see cref="Signal{T}.Set"/> is a change worth publishing.
        /// Defaults to <see cref="EqualityComparer{T}.Default"/>.
        /// </param>
        public static Signal<T> Create<T>(
            T initialValue = default,
            IEqualityComparer<T> comparer = null
        )
        {
            return new Signal<T>(string.Empty, initialValue, comparer);
        }

        public static bool TryGet<T>(string key, out Signal<T> signal)
        {
            SignalsRuntime.EnsureInitialized();
            return SignalsRuntime.Registry.TryGet(key, out signal);
        }
    }

    public static class SignalsRuntime
    {
        private static SignalRegistry registry;
        private static bool hostCreated;

        public static SignalRegistry Registry => registry ??= InitializeRegistry();

        public static void EnsureInitialized()
        {
            _ = Registry;
        }

        private static SignalRegistry InitializeRegistry()
        {
            var reg = new SignalRegistry();
            CreateRuntimeHost();
            return reg;
        }

        private static void CreateRuntimeHost()
        {
            if (hostCreated)
            {
                return;
            }
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                hostCreated = true;
                return;
            }
#endif
            var go = new GameObject("__ReactiveSignalsRuntime");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<SignalsRuntimeHost>();
            hostCreated = true;
        }
    }

    internal sealed class SignalsRuntimeHost : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }
    }
}
