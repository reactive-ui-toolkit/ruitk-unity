using System.Collections.Generic;
using Ruitk.Props.Typed;

namespace Ruitk.Core.Fiber
{
    /// <summary>
    /// Host configuration — the backend seam between the fiber reconciler and a
    /// concrete UI system. Host instances are opaque handles (<c>object</c>);
    /// each backend casts exactly once per operation at this boundary. A fiber
    /// tree is owned by a single backend for its entire lifetime, so a handle
    /// created by one config is never passed to another.
    /// </summary>
    public abstract class FiberHostConfig
    {
        /// <summary>
        /// Create a host element of the given logical type. Never returns null;
        /// unknown types produce the backend's generic container element.
        /// </summary>
        public abstract object CreateElement(string elementType);

        /// <summary>
        /// Apply dictionary props to a host element (legacy untyped path).
        /// </summary>
        public abstract void ApplyProperties(
            object element,
            string elementType,
            IReadOnlyDictionary<string, object> oldProps,
            IReadOnlyDictionary<string, object> newProps
        );

        /// <summary>
        /// Apply typed props to a host element (primary path). Props are the
        /// backend family's own types; the config casts once at this boundary.
        /// </summary>
        public abstract void ApplyTypedProperties(
            object element,
            string elementType,
            HostPropsBase oldProps,
            HostPropsBase newProps
        );

        public abstract void AppendChild(object parent, object child);

        public abstract void InsertBefore(object parent, object child, object beforeChild);

        public abstract void RemoveChild(object parent, object child);

        public abstract object GetParent(object element);

        public abstract void ClearChildren(object element);

        public abstract int GetChildCount(object element);

        public abstract object GetChildAt(object element, int index);

        /// <summary>
        /// Called by the reconciler when a host element leaves the tree for
        /// good (commit-phase deletion). Backends release per-element tracking
        /// or return the element to a pool here.
        /// </summary>
        public virtual void OnHostRemoved(object element) { }

        /// <summary>
        /// Whether the host element is still safe to read and mutate. Backends
        /// whose elements can die out from under the tree override this — the
        /// UI Toolkit backend reports Unity 6.5's <c>resourcesReleased</c>
        /// poisoning, the uGUI backend its destroyed-<c>GameObject</c>
        /// fake-null. Mount hosts consult it before reusing, retargeting or
        /// touching a retained element; a dead element must be dropped, never
        /// mutated or pooled.
        /// </summary>
        public virtual bool IsAlive(object element) => element != null;

        /// <summary>Human-readable identity for fiber debug logging.</summary>
        public virtual string GetDebugName(object element) => element?.GetType().Name;
    }
}
