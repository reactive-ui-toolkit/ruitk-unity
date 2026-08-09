using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruitk.Core;
using Ruitk.Props.Typed;
using UnityEngine.UIElements;

namespace Ruitk
{
    // The host-agnostic half of V: text, function-component and structural-node
    // factories whose dependencies stop at the virtual-node core (VirtualNode,
    // IProps, ErrorBoundaryProps). The UI Toolkit element factories - typed
    // props over BaseProps - stay in V.cs. The CI test suite links this file
    // together with the fiber core; V.cs only compiles where the real
    // UnityEngine assemblies exist.
    public static partial class V
    {
        // ═══════════════════════════════════════════════════════════════════
        //  Text
        // ═══════════════════════════════════════════════════════════════════

        public static VirtualNode Text(string textContent, string key = null)
        {
            var v = VirtualNode.__Rent();
            v._nodeType = VirtualNodeType.Text;
            v._textContent = textContent ?? string.Empty;
            v._key = key;
            return v;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Function components
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a typed-props function component VirtualNode.
        ///
        /// The <paramref name="renderFunction"/> receives an <see cref="Core.IProps"/>
        /// which it should cast to <typeparamref name="TProps"/> at the top of the method:
        /// <code>
        ///   var props = rawProps as MyProps;
        /// </code>
        /// The cast succeeds because <c>V.Func&lt;TProps&gt;</c> always stores the concrete
        /// instance passed here, wrapped only in the <see cref="Core.IProps"/> interface.
        ///
        /// Equality for bailout is determined by <typeparamref name="TProps"/>'s
        /// <see cref="object.Equals(object)"/> implementation — generated props classes
        /// get structural equality automatically.
        /// </summary>
        public static VirtualNode Func<TProps>(
            System.Func<Core.IProps, IReadOnlyList<VirtualNode>, VirtualNode> renderFunction,
            TProps typedProps,
            string key = null,
            params VirtualNode[] children
        )
            where TProps : class, Core.IProps
        {
            var v = VirtualNode.__Rent();
            v._nodeType = VirtualNodeType.FunctionComponent;
            v._key = key;
            v._children = children ?? EmptyChildren();
            v._typedFunctionRender = renderFunction;
            v._typedProps = (Core.IProps)typedProps ?? Core.EmptyProps.Instance;
#if UNITY_EDITOR
            v._family = ResolveFamilyForDelegate(renderFunction);
#endif
            return v;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Associates a plain-delegate mount with its Fast Refresh Family (editor only). The SG
        /// rewrites CHILD component tags to family-based <c>V.Func(__fam_X, …)</c>, but hand-written
        /// ROOT mounts (<c>rootRenderer.Render(V.Func(App.Render))</c>) pass a raw method group —
        /// leaving the root fiber family-less, so editing the root component of any mount never
        /// hot-reloaded (found by RUNTIME-V: "compiled OK but 0 fibers refreshed"). A component
        /// family's id IS the component type's FQN — exactly the method group's
        /// <c>Method.DeclaringType.FullName</c> — so the lookup is exact by construction. Lambda
        /// mounts resolve to a closure type (name contains <c>+</c>) → registry miss → null, i.e.
        /// the old behavior. Editor-only cost: one dictionary lookup per plain V.Func call.
        /// </summary>
        private static Refresh.Family ResolveFamilyForDelegate(System.Delegate render)
        {
            if (render == null)
                return null;
            try
            {
                var declaring = render.Method?.DeclaringType;
                if (declaring == null)
                    return null;
                return Refresh.RefreshRuntime.FindRegisteredFamily(declaring.FullName);
            }
            catch
            {
                return null; // never let family association break a mount
            }
        }
#endif // UNITY_EDITOR

        // ── Family-based overloads (UITKX Fast Refresh, editor-only) ────────
        //
        // The SG emits these in Editor builds. The Family carries a stable
        // reference identity that survives HMR DLL recompiles; its Current
        // delegate is mutated in place by RefreshRuntime.Register. The vnode
        // captures BOTH the Family (for reconciler identity matching) AND a
        // snapshot of family.Current at call time (so a Family.Current swap
        // mid-render-pass doesn't produce inconsistent results within the
        // pass -- the next pass picks up the new body).
        //
        // These overloads do not exist in player compilation. The SG emits
        // direct V.Func(delegate, ...) calls under #if !UNITY_EDITOR so
        // player builds have zero Family-related code or types.

        /// <summary>
        /// Family-based typed function component overload. SG-emitted in
        /// Editor builds. See Plans~/HMR_FAST_REFRESH_PLAN.md for the
        /// architecture.
        /// </summary>
#if UNITY_EDITOR
        public static VirtualNode Func<TProps>(
            Refresh.Family family,
            TProps typedProps,
            string key = null,
            params VirtualNode[] children
        )
            where TProps : class, Core.IProps
        {
            var v = VirtualNode.__Rent();
            v._nodeType = VirtualNodeType.FunctionComponent;
            v._key = key;
            v._children = children ?? EmptyChildren();
            v._typedFunctionRender = family?.Current;
            v._typedProps = (Core.IProps)typedProps ?? Core.EmptyProps.Instance;
            v._family = family;
            return v;
        }
#endif // UNITY_EDITOR

        /// <summary>
        /// Creates an untyped IProps function component VirtualNode.
        /// Use this when no strongly-typed props class is needed (no-props components or
        /// when you only need reference equality for bailout via <see cref="Core.EmptyProps.Instance"/>).
        /// </summary>
        public static VirtualNode Func(
            System.Func<Core.IProps, IReadOnlyList<VirtualNode>, VirtualNode> render,
            Core.IProps props = null,
            string key = null,
            params VirtualNode[] children
        )
        {
            var v = VirtualNode.__Rent();
            v._nodeType = VirtualNodeType.FunctionComponent;
            v._key = key;
            v._children = children ?? EmptyChildren();
            v._typedFunctionRender = render;
            v._typedProps = props ?? Core.EmptyProps.Instance;
#if UNITY_EDITOR
            v._family = ResolveFamilyForDelegate(render);
#endif
            return v;
        }

        /// <summary>
        /// Family-based untyped (no-props) function component overload.
        /// SG-emitted in Editor builds for components with no typed props.
        /// </summary>
#if UNITY_EDITOR
        public static VirtualNode Func(
            Refresh.Family family,
            Core.IProps props = null,
            string key = null,
            params VirtualNode[] children
        )
        {
            var v = VirtualNode.__Rent();
            v._nodeType = VirtualNodeType.FunctionComponent;
            v._key = key;
            v._children = children ?? EmptyChildren();
            v._typedFunctionRender = family?.Current;
            v._typedProps = props ?? Core.EmptyProps.Instance;
            v._family = family;
            return v;
        }
#endif // UNITY_EDITOR

        // ═══════════════════════════════════════════════════════════════════
        //  Structural nodes
        // ═══════════════════════════════════════════════════════════════════

        public static VirtualNode Fragment(string key = null, params VirtualNode[] children)
        {
            var v = VirtualNode.__Rent();
            v._nodeType = VirtualNodeType.Fragment;
            v._key = key;
            v._children = children ?? EmptyChildren();
            return v;
        }

        public static VirtualNode Portal(
            VisualElement portalTargetElement,
            string key = null,
            params VirtualNode[] children
        )
        {
            var v = VirtualNode.__Rent();
            v._nodeType = VirtualNodeType.Portal;
            v._key = key;
            v._children = children ?? EmptyChildren();
            v._portalTarget = portalTargetElement;
            return v;
        }

        public static VirtualNode Suspense(
            System.Func<bool> isReady,
            VirtualNode fallbackNode,
            string key = null,
            params VirtualNode[] children
        ) => Suspense(isReady, null, fallbackNode, key, children);

        public static VirtualNode Suspense(
            System.Func<bool> isReady,
            Task readyTask,
            VirtualNode fallbackNode,
            string key = null,
            params VirtualNode[] children
        )
        {
            var v = VirtualNode.__Rent();
            v._nodeType = VirtualNodeType.Suspense;
            v._key = key;
            v._children = children ?? EmptyChildren();
            v._suspenseReady = isReady;
            v._suspenseReadyTask = readyTask;
            v._fallback = fallbackNode;
            return v;
        }

        public static VirtualNode Suspense(
            Task readyTask,
            VirtualNode fallbackNode,
            string key = null,
            params VirtualNode[] children
        ) => Suspense(null, readyTask, fallbackNode, key, children);

        public static VirtualNode ErrorBoundary(
            ErrorBoundaryProps props,
            string key = null,
            params VirtualNode[] children
        )
        {
            var v = VirtualNode.__Rent();
            v._nodeType = VirtualNodeType.ErrorBoundary;
            v._key = key;
            v._children = children ?? EmptyChildren();
            v._errorFallback = props?.Fallback;
            v._errorHandler = props?.OnError;
            v._errorResetToken = props?.ResetKey;
            return v;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Memo (delegates to Func)
        // ═══════════════════════════════════════════════════════════════════

        public static VirtualNode Memo(
            System.Func<Core.IProps, IReadOnlyList<VirtualNode>, VirtualNode> renderFunction,
            Core.IProps functionProps = null,
            string key = null,
            params VirtualNode[] children
        )
        {
            return Func(renderFunction, functionProps, key, children);
        }

        private static IReadOnlyList<VirtualNode> EmptyChildren() => VirtualNode.EmptyChildren;
    }
}
