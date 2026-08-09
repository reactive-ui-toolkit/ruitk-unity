using System;
using System.Collections.Generic;
using Ruitk.Elements;
using UnityEngine.UIElements;

namespace Ruitk.Core
{
    internal readonly struct ContextKey : IEquatable<ContextKey>
    {
        public ContextKey(string name, int providerId)
        {
            Name = name ?? string.Empty;
            ProviderId = providerId;
        }

        public string Name { get; }
        public int ProviderId { get; }

        public bool Equals(ContextKey other) =>
            ProviderId == other.ProviderId
            && string.Equals(Name, other.Name, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is ContextKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return ((ProviderId * 397) ^ (Name?.GetHashCode() ?? 0));
            }
        }

        public override string ToString() => $"{Name}@{ProviderId}";
    }

    public sealed partial class HostContext
    {
        internal sealed class ContextFrame
        {
            public ContextFrame Parent;
            public IReadOnlyDictionary<string, object> Values;
            public int ProviderId;
        }

        internal readonly struct ContextFrameHandle : IEquatable<ContextFrameHandle>
        {
            internal ContextFrameHandle(ContextFrame frame)
            {
                Frame = frame;
            }

            internal ContextFrame Frame { get; }
            public bool IsValid => Frame != null;

            public bool Equals(ContextFrameHandle other) => ReferenceEquals(Frame, other.Frame);

            public override bool Equals(object obj) =>
                obj is ContextFrameHandle handle && Equals(handle);

            public override int GetHashCode() => Frame != null ? Frame.GetHashCode() : 0;
        }

        internal ContextFrameHandle CaptureFrame() => new ContextFrameHandle(currentFrame);

        internal void RestoreFrame(ContextFrameHandle handle)
        {
            currentFrame = handle.Frame;
        }

        public ElementRegistry ElementRegistry { get; }
        public Dictionary<string, object> Environment { get; } = new();

        /// <summary>
        /// The host backend this context renders through. Defaults to the
        /// UI Toolkit backend built over <see cref="ElementRegistry"/>; a
        /// different backend (e.g. uGUI) is supplied via the two-argument
        /// constructor. One mount owns exactly one backend for its lifetime.
        /// </summary>
        public Fiber.FiberHostConfig HostConfig { get; }

        private ContextFrame currentFrame;
        private int nextProviderId = 1;

        public HostContext(ElementRegistry elementRegistry)
        {
            ElementRegistry = elementRegistry;
            HostConfig = CreateDefaultHostConfig(elementRegistry);
            currentFrame = null;
        }

        public HostContext(ElementRegistry elementRegistry, Fiber.FiberHostConfig hostConfig)
        {
            ElementRegistry = elementRegistry;
            HostConfig = hostConfig ?? CreateDefaultHostConfig(elementRegistry);
            currentFrame = null;
        }

        // The default backend is supplied per compilation flavour: the Unity build
        // returns UitkHostConfig (HostContext.Uitk.cs); the host-agnostic test
        // build supplies its own part. Every implementation must return non-null -
        // FiberReconciler relies on HostConfig never being null.
        private static partial Fiber.FiberHostConfig CreateDefaultHostConfig(
            ElementRegistry elementRegistry
        );

        public void SetContextValue(string key, object value)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            Environment[key] = value;
        }

        internal ContextFrameHandle PushProvider(
            IReadOnlyDictionary<string, object> values,
            ref int providerId
        )
        {
            if (values == null || values.Count == 0)
            {
                return default;
            }
            if (providerId <= 0)
            {
                providerId = nextProviderId++;
            }
            var frame = new ContextFrame
            {
                Parent = currentFrame,
                Values = values,
                ProviderId = providerId,
            };
            currentFrame = frame;
            return new ContextFrameHandle(frame);
        }

        internal void PopProvider(ContextFrameHandle handle)
        {
            if (!handle.IsValid)
            {
                return;
            }
            if (currentFrame == handle.Frame)
            {
                currentFrame = handle.Frame.Parent;
                return;
            }

            var cursor = currentFrame;
            while (cursor != null && cursor != handle.Frame)
            {
                cursor = cursor.Parent;
            }
            if (cursor == handle.Frame)
            {
                currentFrame = handle.Frame.Parent;
            }
        }

        public object ResolveContext(string key)
        {
            var frame = currentFrame;
            while (frame != null)
            {
                if (frame.Values != null && frame.Values.TryGetValue(key, out object provided))
                {
                    return provided;
                }
                frame = frame.Parent;
            }
            Environment.TryGetValue(key, out object globalValue);
            return globalValue;
        }
    }
}
