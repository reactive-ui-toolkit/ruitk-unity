using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ruitk.Signals
{
    public abstract class SignalBase
    {
        internal SignalBase(string key)
        {
            Key = key ?? string.Empty;
        }

        /// <summary>
        /// The registry key this signal is addressed by, or empty for one made with
        /// <c>SignalFactory.Create</c>, which is owned by its caller and never registered.
        /// </summary>
        public string Key { get; }
        internal abstract Type ValueType { get; }
        internal abstract object UntypedValue { get; }
        internal abstract IDisposable SubscribeRaw(Action<object> listener);
    }

    public sealed class Signal<T> : SignalBase
    {
        private readonly object gate = new();
        private readonly List<Action<T>> listeners = new();
        private readonly IEqualityComparer<T> comparer;
        private Action<T>[] cachedSnapshot;
        private T value;

        internal Signal(string key, T initialValue, IEqualityComparer<T> comparer = null)
            : base(key)
        {
            value = initialValue;
            this.comparer = comparer ?? EqualityComparer<T>.Default;
        }

        public T Value
        {
            get
            {
                lock (gate)
                {
                    return value;
                }
            }
        }

        internal override Type ValueType => typeof(T);
        internal override object UntypedValue => Value;

        public IDisposable Subscribe(Action<T> listener)
        {
            if (listener == null)
            {
                return SignalSubscription.Empty;
            }
            lock (gate)
            {
                listeners.Add(listener);
                cachedSnapshot = null;
            }
            return new SignalSubscription(() => Unsubscribe(listener));
        }

        internal void Unsubscribe(Action<T> listener)
        {
            lock (gate)
            {
                listeners.Remove(listener);
                cachedSnapshot = null;
            }
        }

        internal override IDisposable SubscribeRaw(Action<object> listener)
        {
            if (listener == null)
            {
                return SignalSubscription.Empty;
            }
            Action<T> wrapper = v => listener(v);
            return Subscribe(wrapper);
        }

        public void Set(T newValue)
        {
            Action<T>[] snapshot = null;
            lock (gate)
            {
                if (comparer.Equals(value, newValue))
                {
                    return;
                }
                value = newValue;
                if (listeners.Count > 0)
                {
                    cachedSnapshot ??= listeners.ToArray();
                    snapshot = cachedSnapshot;
                }
            }
            if (snapshot == null)
            {
                return;
            }
            foreach (Action<T> listener in snapshot)
            {
                try
                {
                    listener?.Invoke(newValue);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        public void Dispatch(Func<T, T> updater)
        {
            if (updater == null)
            {
                return;
            }
            T next;
            lock (gate)
            {
                next = updater(value);
            }
            Set(next);
        }

        public void Dispatch(SignalUpdate<T> update)
        {
            Set(update.Apply(Value));
        }
    }

    public readonly struct SignalUpdate<T>
    {
        private readonly bool usesUpdater;
        private readonly T value;
        private readonly Func<T, T> updater;

        private SignalUpdate(T value, Func<T, T> updater, bool usesUpdater)
        {
            this.value = value;
            this.updater = updater;
            this.usesUpdater = usesUpdater;
        }

        internal T Apply(T previous)
        {
            if (!usesUpdater)
            {
                return value;
            }
            return updater != null ? updater(previous) : previous;
        }

        public static implicit operator SignalUpdate<T>(T directValue) =>
            new SignalUpdate<T>(directValue, null, false);

        public static implicit operator SignalUpdate<T>(Func<T, T> updater) =>
            new SignalUpdate<T>(default, updater, true);
    }

    internal sealed class SignalSubscription : IDisposable
    {
        private readonly Action unsubscribe;
        private bool disposed;
        public static readonly SignalSubscription Empty = new(() => { });

        public SignalSubscription(Action unsubscribeAction)
        {
            unsubscribe = unsubscribeAction ?? (() => { });
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            unsubscribe();
        }
    }
}
