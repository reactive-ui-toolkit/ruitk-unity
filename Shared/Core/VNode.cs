using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Ruitk.Props.Typed;

namespace Ruitk.Core
{
    public enum VirtualNodeType
    {
        Element,
        Text,
        FunctionComponent,
        Fragment,
        Portal,
        Suspense,
        ErrorBoundary,
        Host,
    }

    public sealed partial class VirtualNode
    {
        private static readonly IReadOnlyDictionary<string, object> EmptyPropsInstance =
            new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(0));
        private static readonly IReadOnlyList<VirtualNode> EmptyChildrenInstance =
            new ReadOnlyCollection<VirtualNode>(Array.Empty<VirtualNode>());
        private static readonly IReadOnlyList<PropTypeDefinition> EmptyPropTypesInstance =
            new ReadOnlyCollection<PropTypeDefinition>(Array.Empty<PropTypeDefinition>());

        // ═══════════════════════════════════════════════════════════════════
        //  Pool infrastructure
        //  generation = 0 → user-created via public constructor (not pooled)
        //  generation > 0 → rented from pool via __Rent()
        // ═══════════════════════════════════════════════════════════════════
        internal uint _generation;

        private const int PoolCapacity = 4096;
        private static readonly Stack<VirtualNode> s_pool = new Stack<VirtualNode>(256);
        private static readonly List<VirtualNode> s_pendingReturn = new List<VirtualNode>(4096);
        private static uint s_nextGeneration = 1;

        // ═══════════════════════════════════════════════════════════════════
        //  Backing fields — internal so V.cs factories can set them directly
        // ═══════════════════════════════════════════════════════════════════
        internal VirtualNodeType _nodeType;
        internal string _elementTypeName;

        // Opaque host handle: a VisualElement for UI Toolkit portals, a
        // RectTransform/GameObject for uGUI portals. The fiber layer passes it
        // through untouched; only the mount's FiberHostConfig interprets it.
        internal object _portalTarget;
        internal VirtualNode _fallback;
        internal System.Func<bool> _suspenseReady;
        internal System.Threading.Tasks.Task _suspenseReadyTask;
        internal string _textContent;
        internal string _key;
        internal IReadOnlyDictionary<string, object> _properties;
        internal IReadOnlyList<VirtualNode> _children;
        internal VirtualNode _errorFallback;
        internal ErrorEventHandler _errorHandler;
        internal string _errorResetToken;
        internal IReadOnlyList<PropTypeDefinition> _propTypes;
        internal System.Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode> _typedFunctionRender;
        internal IProps _typedProps;
        internal HostPropsBase _hostProps;

        // ── Family handle (UITKX Fast Refresh, editor-only) ────────────────
        // Non-null when the vnode was produced by a V.Func(Family,...) overload
        // emitted by the source generator under #if UNITY_EDITOR. The fiber
        // reconciler compares Family identity in CanReuseFiber, eliminating
        // the cross-DLL Method.DeclaringType inequality that the old
        // trampoline architecture was vulnerable to. Player builds receive
        // direct V.Func(MyComp.Render, ...) calls and never produce vnodes
        // with a Family handle — the field, property, and Refresh.Family
        // type itself do not exist in player compilation.
#if UNITY_EDITOR
        internal Refresh.Family _family;
#endif

        // ═══════════════════════════════════════════════════════════════════
        //  Public read-only properties
        // ═══════════════════════════════════════════════════════════════════
        public VirtualNodeType NodeType => _nodeType;
        public string ElementTypeName => _elementTypeName;

        /// <summary>
        /// Portal target as a UI Toolkit element (null for non-UITK targets).
        /// Kept <c>VisualElement</c>-typed for source compatibility; the fiber
        /// reads the untyped <see cref="PortalTargetHost"/> instead.
        /// </summary>
        public UnityEngine.UIElements.VisualElement PortalTarget =>
            _portalTarget as UnityEngine.UIElements.VisualElement;

        /// <summary>Portal target as the opaque host handle (any backend).</summary>
        public object PortalTargetHost => _portalTarget;
        public VirtualNode Fallback => _fallback;
        public System.Func<bool> SuspenseReady => _suspenseReady;
        public System.Threading.Tasks.Task SuspenseReadyTask => _suspenseReadyTask;
        public string TextContent => _textContent;
        public string Key => _key;
        public IReadOnlyDictionary<string, object> Properties => _properties;
        public IReadOnlyList<VirtualNode> Children => _children;
        public VirtualNode ErrorFallback => _errorFallback;
        public ErrorEventHandler ErrorHandler => _errorHandler;
        public string ErrorResetToken => _errorResetToken;
        public IReadOnlyList<PropTypeDefinition> PropTypes => _propTypes;

        // ── Typed-props path ─────────────────────────────────────────────────
        /// <summary>
        /// Typed render delegate. Set when <c>V.Func&lt;TProps&gt;</c> or <c>V.Func(IProps)</c> is used.
        /// </summary>
        public System.Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode> TypedFunctionRender =>
            _typedFunctionRender;

        /// <summary>
        /// Typed props instance. Non-null when <c>V.Func&lt;TProps&gt;</c> was used.
        /// </summary>
        public IProps TypedProps => _typedProps;

        /// <summary>Typed host props as the backend-neutral base (any family).</summary>
        public HostPropsBase HostPropsRaw => _hostProps;

        /// <summary>
        /// UITKX Fast Refresh family handle. Non-null when the vnode was
        /// produced by a <c>V.Func(Family,...)</c> overload (which the SG
        /// emits in Editor builds). Lets the reconciler match fibers by
        /// Family reference instead of by render-delegate identity, which
        /// breaks across HMR DLL generations. Editor-only — in player
        /// builds neither this property nor <c>Refresh.Family</c> exists.
        /// </summary>
#if UNITY_EDITOR
        public Refresh.Family Family => _family;
#endif

        /// <summary>
        /// Public constructor for backward compatibility and user-created VNodes.
        /// Instances created via this constructor have generation=0 (not pooled).
        /// </summary>
        public VirtualNode(
            VirtualNodeType nodeType,
            string elementTypeName,
            string textContent,
            string key,
            IReadOnlyDictionary<string, object> properties,
            IReadOnlyList<VirtualNode> children,
            UnityEngine.UIElements.VisualElement portalTarget = null,
            VirtualNode fallback = null,
            System.Func<bool> suspenseReady = null,
            System.Threading.Tasks.Task suspenseReadyTask = null,
            VirtualNode errorFallback = null,
            ErrorEventHandler errorHandler = null,
            string errorResetToken = null,
            IReadOnlyList<PropTypeDefinition> propTypes = null,
            System.Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode> typedFunctionRender = null,
            IProps typedProps = null,
            HostPropsBase hostProps = null
        )
        {
            _generation = 0; // user-created, not from pool
            _nodeType = nodeType;
            _elementTypeName = elementTypeName;
            _textContent = textContent;
            _key = key;
            _properties = properties ?? EmptyPropsInstance;
            _children = children ?? EmptyChildrenInstance;
            _portalTarget = portalTarget;
            _fallback = fallback;
            _suspenseReady = suspenseReady;
            _suspenseReadyTask = suspenseReadyTask;
            _errorFallback = errorFallback;
            _errorHandler = errorHandler;
            _errorResetToken = errorResetToken;
            _propTypes = ClonePropTypes(propTypes);
            _typedFunctionRender = typedFunctionRender;
            _typedProps = typedProps;
            _hostProps = hostProps;
        }

        /// <summary>
        /// Private parameterless constructor used by the pool.
        /// </summary>
        private VirtualNode()
        {
            _properties = EmptyPropsInstance;
            _children = EmptyChildrenInstance;
            _propTypes = EmptyPropTypesInstance;
        }

        private VirtualNode(VirtualNode template, IReadOnlyList<PropTypeDefinition> propTypes)
        {
            _generation = 0; // template copies are not pooled
            _nodeType = template._nodeType;
            _elementTypeName = template._elementTypeName;
            _portalTarget = template._portalTarget;
            _fallback = template._fallback;
            _suspenseReady = template._suspenseReady;
            _suspenseReadyTask = template._suspenseReadyTask;
            _textContent = template._textContent;
            _key = template._key;
            _properties = template._properties;
            _children = template._children;
            _errorFallback = template._errorFallback;
            _errorHandler = template._errorHandler;
            _errorResetToken = template._errorResetToken;
            _propTypes = ClonePropTypes(propTypes);
            _typedFunctionRender = template._typedFunctionRender;
            _typedProps = template._typedProps;
            _hostProps = template._hostProps;
        }

        internal static IReadOnlyDictionary<string, object> EmptyProps => EmptyPropsInstance;

        internal static IReadOnlyList<VirtualNode> EmptyChildren => EmptyChildrenInstance;

        internal VirtualNode WithPropTypesImmutable(IReadOnlyList<PropTypeDefinition> propTypes)
        {
            if (propTypes == null || propTypes.Count == 0)
            {
                return this;
            }
            return new VirtualNode(this, propTypes);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Pool API
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Rent a VirtualNode from the pool. The returned instance has all
        /// fields reset to defaults. Callers (V.cs factories) must set the
        /// required fields before returning the VNode to the caller.
        /// </summary>
        internal static VirtualNode __Rent()
        {
            VirtualNode v;
            if (s_pool.Count > 0)
            {
                v = s_pool.Pop();
                v.__Reset();
            }
            else
            {
                v = new VirtualNode();
            }
            uint gen = s_nextGeneration++;
            if (gen == 0)
                gen = s_nextGeneration++; // skip 0 on overflow
            v._generation = gen;
            return v;
        }

        /// <summary>
        /// Clear all fields to defaults for reuse.
        /// </summary>
        private void __Reset()
        {
            _nodeType = default;
            _elementTypeName = null;
            _portalTarget = null;
            _fallback = null;
            _suspenseReady = null;
            _suspenseReadyTask = null;
            _textContent = null;
            _key = null;
            _properties = EmptyPropsInstance;
            _children = EmptyChildrenInstance;
            _errorFallback = null;
            _errorHandler = null;
            _errorResetToken = null;
            _propTypes = EmptyPropTypesInstance;
            _typedFunctionRender = null;
            _typedProps = null;
            _hostProps = null;
#if UNITY_EDITOR
            _family = null;
#endif
        }

        /// <summary>
        /// Schedule a VirtualNode for return to pool on next flush.
        /// Nodes with generation 0 (user-created via public constructor) are ignored.
        /// </summary>
        internal static void __ScheduleReturn(VirtualNode v)
        {
            if (v == null || v._generation == 0)
                return;
            s_pendingReturn.Add(v);
        }

        /// <summary>
        /// Move all pending returns into the pool.
        /// Called once per frame after the full commit tree walk.
        /// </summary>
        internal static void __FlushReturns()
        {
            for (int i = 0; i < s_pendingReturn.Count; i++)
            {
                if (s_pool.Count < PoolCapacity)
                    s_pool.Push(s_pendingReturn[i]);
            }
            s_pendingReturn.Clear();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Legacy helpers
        // ═══════════════════════════════════════════════════════════════════

        private static IReadOnlyDictionary<string, object> CloneProps(
            IReadOnlyDictionary<string, object> props
        )
        {
            if (props == null || props.Count == 0)
            {
                return EmptyPropsInstance;
            }
            if (ReferenceEquals(props, EmptyPropsInstance))
            {
                return EmptyPropsInstance;
            }
            var dict = new Dictionary<string, object>(props.Count);
            foreach (var kvp in props)
            {
                dict[kvp.Key] = kvp.Value;
            }
            return new ReadOnlyDictionary<string, object>(dict);
        }

        private static IReadOnlyList<VirtualNode> CloneChildren(IReadOnlyList<VirtualNode> children)
        {
            if (children == null || children.Count == 0)
            {
                return EmptyChildrenInstance;
            }
            var buffer = new VirtualNode[children.Count];
            for (int i = 0; i < children.Count; i++)
            {
                buffer[i] = children[i];
            }
            return Array.AsReadOnly(buffer);
        }

        private static IReadOnlyList<PropTypeDefinition> ClonePropTypes(
            IReadOnlyList<PropTypeDefinition> propTypes
        )
        {
            if (propTypes == null || propTypes.Count == 0)
            {
                return EmptyPropTypesInstance;
            }
            var buffer = new PropTypeDefinition[propTypes.Count];
            for (int i = 0; i < propTypes.Count; i++)
            {
                buffer[i] = propTypes[i];
            }
            return Array.AsReadOnly(buffer);
        }
    }
}
