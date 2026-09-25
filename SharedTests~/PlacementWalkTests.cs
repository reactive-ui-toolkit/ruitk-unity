using System;
using System.Collections.Generic;
using System.Linq;
using Ruitk;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using Ruitk.Elements;
using Ruitk.Shared.Tests.Fiber;
using Xunit;

namespace Ruitk.Shared.Tests
{
    // The commit-phase placement walk: a Placement effect that lands on a fiber with
    // no host element of its own (FunctionComponent, Fragment, ErrorBoundary) must
    // still move that wrapper's host descendants. Mirrors React's commitPlacement +
    // insertOrAppendPlacementNode pair - the anchor is computed once from the placed
    // fiber, and every top-level host descendant is inserted before it.
    public class PlacementWalkTests
    {
        private sealed class RowProps : IProps
        {
            public string Type;

            public override bool Equals(object obj) => obj is RowProps o && o.Type == Type;

            public override int GetHashCode() => Type?.GetHashCode() ?? 0;
        }

        private static readonly Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode> RowRender = (
            props,
            children
        ) =>
            new VirtualNode(
                VirtualNodeType.Element,
                ((RowProps)props).Type,
                null,
                null,
                null,
                null
            );

        private static (
            MockHostConfig host,
            MockElement container,
            FiberReconciler reconciler
        ) Rig()
        {
            var host = new MockHostConfig();
            var container = new MockElement { Type = "root" };
            return (
                host,
                container,
                new FiberReconciler(new HostContext(new ElementRegistry(), host))
            );
        }

        private static VirtualNode El(string type, string key, params VirtualNode[] children) =>
            new VirtualNode(VirtualNodeType.Element, type, null, key, null, children);

        // A list whose children are keyed function components, one host each.
        private static VirtualNode ComponentList(params string[] names) =>
            El(
                "list",
                null,
                names.Select(n => V.Func(RowRender, new RowProps { Type = n }, n)).ToArray()
            );

        // ── Keyed reorder through host-less wrappers ────────────────────────

        [Fact]
        public void KeyedFunctionComponentsReorderTheirHostElements()
        {
            var (_, container, reconciler) = Rig();
            var root = reconciler.CreateRoot(container, ComponentList("a", "b", "c", "d"));
            Assert.Equal("root(list(a,b,c,d))", MockHostConfig.Dump(container));

            var originalA = container.Children[0].Children[0];

            reconciler.ScheduleUpdateOnFiber(root.Current, ComponentList("d", "c", "b", "a"));

            Assert.Equal("root(list(d,c,b,a))", MockHostConfig.Dump(container));
            // The move repositions the existing element; it does not recreate it.
            Assert.Same(originalA, container.Children[0].Children[3]);
        }

        [Fact]
        public void KeyedHostsReorderToo()
        {
            var (_, container, reconciler) = Rig();

            VirtualNode HostList(params string[] names) =>
                El("list", null, names.Select(n => El(n, n)).ToArray());

            var root = reconciler.CreateRoot(container, HostList("a", "b", "c"));
            Assert.Equal("root(list(a,b,c))", MockHostConfig.Dump(container));

            reconciler.ScheduleUpdateOnFiber(root.Current, HostList("c", "a", "b"));

            Assert.Equal("root(list(c,a,b))", MockHostConfig.Dump(container));
        }

        [Fact]
        public void KeyedFragmentsReorderTheirHostElements()
        {
            var (_, container, reconciler) = Rig();

            VirtualNode FragList(params string[] names) =>
                El("list", null, names.Select(n => V.Fragment(n, El(n, null))).ToArray());

            var root = reconciler.CreateRoot(container, FragList("a", "b", "c"));
            Assert.Equal("root(list(a,b,c))", MockHostConfig.Dump(container));

            reconciler.ScheduleUpdateOnFiber(root.Current, FragList("c", "a", "b"));

            Assert.Equal("root(list(c,a,b))", MockHostConfig.Dump(container));
        }

        [Fact]
        public void AComponentWithTwoHostsMovesBothAndKeepsTheirOrder()
        {
            var (_, container, reconciler) = Rig();

            var twoHosts = new Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>(
                (props, children) =>
                {
                    string t = ((RowProps)props).Type;
                    return V.Fragment(null, El(t + "1", null), El(t + "2", null));
                }
            );

            VirtualNode PairList(params string[] names) =>
                El(
                    "list",
                    null,
                    names.Select(n => V.Func(twoHosts, new RowProps { Type = n }, n)).ToArray()
                );

            var root = reconciler.CreateRoot(container, PairList("a", "b"));
            Assert.Equal("root(list(a1,a2,b1,b2))", MockHostConfig.Dump(container));

            reconciler.ScheduleUpdateOnFiber(root.Current, PairList("b", "a"));

            Assert.Equal("root(list(b1,b2,a1,a2))", MockHostConfig.Dump(container));
        }

        [Fact]
        public void NestedWrappersMoveTheHostUnderneathThem()
        {
            var (_, container, reconciler) = Rig();

            var inner = new Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>(
                (props, _) => El(((RowProps)props).Type, null)
            );
            var outer = new Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>(
                (props, _) => V.Fragment(null, V.Func(inner, props, null))
            );

            VirtualNode List(params string[] names) =>
                El(
                    "list",
                    null,
                    names.Select(n => V.Func(outer, new RowProps { Type = n }, n)).ToArray()
                );

            var root = reconciler.CreateRoot(container, List("a", "b", "c"));
            Assert.Equal("root(list(a,b,c))", MockHostConfig.Dump(container));

            reconciler.ScheduleUpdateOnFiber(root.Current, List("c", "b", "a"));

            Assert.Equal("root(list(c,b,a))", MockHostConfig.Dump(container));
        }

        [Fact]
        public void MixedAddRemoveAndMoveLandsInTheNewOrder()
        {
            var (_, container, reconciler) = Rig();
            var root = reconciler.CreateRoot(container, ComponentList("a", "b", "c", "d"));

            reconciler.ScheduleUpdateOnFiber(root.Current, ComponentList("b", "e", "d"));

            Assert.Equal("root(list(b,e,d))", MockHostConfig.Dump(container));
        }

        // ── What the walk must NOT do ───────────────────────────────────────

        // A HostPortal fiber's HostElement IS the portal target. The walk therefore has
        // to test the tag before the host element, or a moved component splices the
        // target itself into its own parent.
        [Fact]
        public void PortalContentIsNotDraggedAlongByAMovedComponent()
        {
            var (_, container, reconciler) = Rig();
            var target = new MockElement { Type = "target" };

            var render = new Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>(
                (props, _) =>
                {
                    string t = ((RowProps)props).Type;
                    return V.Fragment(null, El(t, null), V.Portal(target, null, El("p" + t, null)));
                }
            );

            VirtualNode List(params string[] names) =>
                El(
                    "list",
                    null,
                    names.Select(n => V.Func(render, new RowProps { Type = n }, n)).ToArray()
                );

            var root = reconciler.CreateRoot(container, List("a", "b"));
            Assert.Equal("root(list(a,b))", MockHostConfig.Dump(container));
            Assert.Equal("target(pa,pb)", MockHostConfig.Dump(target));

            reconciler.ScheduleUpdateOnFiber(root.Current, List("b", "a"));

            Assert.Equal("root(list(b,a))", MockHostConfig.Dump(container));
            Assert.Equal("target(pa,pb)", MockHostConfig.Dump(target));
        }

        // The walk stops at the first host on each branch: moving a host carries its
        // whole subtree, so descending past it would reparent grandchildren.
        [Fact]
        public void AMovedHostCarriesItsOwnSubtree()
        {
            var (_, container, reconciler) = Rig();

            var render = new Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>(
                (props, _) =>
                {
                    string t = ((RowProps)props).Type;
                    return El(t, null, El(t + "x", null), El(t + "y", null));
                }
            );

            VirtualNode List(params string[] names) =>
                El(
                    "list",
                    null,
                    names.Select(n => V.Func(render, new RowProps { Type = n }, n)).ToArray()
                );

            var root = reconciler.CreateRoot(container, List("a", "b"));
            Assert.Equal("root(list(a(ax,ay),b(bx,by)))", MockHostConfig.Dump(container));

            reconciler.ScheduleUpdateOnFiber(root.Current, List("b", "a"));

            Assert.Equal("root(list(b(bx,by),a(ax,ay)))", MockHostConfig.Dump(container));
        }

        [Fact]
        public void AComponentRenderingNothingDoesNotBreakTheReorder()
        {
            var (_, container, reconciler) = Rig();

            var render = new Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>(
                (props, _) =>
                {
                    string t = ((RowProps)props).Type;
                    return t == "ghost" ? null : El(t, null);
                }
            );

            VirtualNode List(params string[] names) =>
                El(
                    "list",
                    null,
                    names.Select(n => V.Func(render, new RowProps { Type = n }, n)).ToArray()
                );

            var root = reconciler.CreateRoot(container, List("a", "ghost", "b"));
            Assert.Equal("root(list(a,b))", MockHostConfig.Dump(container));

            reconciler.ScheduleUpdateOnFiber(root.Current, List("b", "ghost", "a"));

            Assert.Equal("root(list(b,a))", MockHostConfig.Dump(container));
        }

        // ── Cost pins ───────────────────────────────────────────────────────

        // The walk must never run for a fiber being mounted for the first time: every
        // host beneath it was created in the same pass and carries its own Placement
        // effect, which the post-order effect list commits first. Without that guard
        // this count doubles, and one host insert is a Remove + Add in UI Toolkit and
        // a SetParent round trip in uGUI.
        [Fact]
        public void MountingComponentWrappedHostsCostsOneInsertEach()
        {
            var (host, container, reconciler) = Rig();
            var names = Enumerable.Range(0, 50).Select(i => "n" + i).ToArray();

            reconciler.CreateRoot(container, ComponentList(names));

            int inserts = host.Operations.Count(o =>
                o.StartsWith("insert:") || o.StartsWith("append:")
            );
            Assert.Equal(51, inserts);
        }

        [Fact]
        public void ReversingAKeyedListCostsTheMinimumNumberOfMoves()
        {
            var (host, container, reconciler) = Rig();
            var forward = Enumerable.Range(0, 50).Select(i => "n" + i).ToArray();
            var root = reconciler.CreateRoot(container, ComponentList(forward));
            host.Operations.Clear();

            reconciler.ScheduleUpdateOnFiber(
                root.Current,
                ComponentList(forward.Reverse().ToArray())
            );

            int moves = host.Operations.Count(o =>
                o.StartsWith("insert:") || o.StartsWith("append:")
            );
            Assert.Equal(49, moves);
        }
    }
}
