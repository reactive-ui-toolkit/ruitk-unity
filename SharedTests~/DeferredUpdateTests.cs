using System;
using System.Collections.Generic;
using System.Linq;
using Ruitk;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using Ruitk.Elements;
using Ruitk.Shared.Tests.Fiber;
using Ruitk.Signals;
using Xunit;

namespace Ruitk.Shared.Tests
{
    // Updates raised while a render pass is in flight are deferred and replayed
    // together once that pass commits. By replay time the tree has been swapped, so
    // the fiber the caller captured is on the superseded face of the double buffer
    // while the next pass clones from the live one. ScheduleUpdateOnFiber therefore
    // has to mark BOTH faces - marking only the one it was handed loses every update
    // after the first in a coalesced batch.
    public class DeferredUpdateTests
    {
        private sealed class LeafProps : IProps
        {
            public Signal<int> Signal;
            public string Name;

            public override bool Equals(object obj) =>
                obj is LeafProps o && o.Name == Name && ReferenceEquals(o.Signal, Signal);

            public override int GetHashCode() => Name?.GetHashCode() ?? 0;
        }

        private static (MockElement container, FiberReconciler reconciler) Rig()
        {
            var host = new MockHostConfig();
            var container = new MockElement { Type = "root" };
            return (container, new FiberReconciler(new HostContext(new ElementRegistry(), host)));
        }

        private static VirtualNode El(string type, string key, params VirtualNode[] children) =>
            new VirtualNode(VirtualNodeType.Element, type, null, key, null, children);

        private static readonly Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode> LeafRender = (
            raw,
            _
        ) =>
        {
            var props = (LeafProps)raw;
            int value = Hooks.UseSignal(props.Signal);
            return new VirtualNode(
                VirtualNodeType.Element,
                props.Name,
                null,
                null,
                new Dictionary<string, object> { ["text"] = props.Name + value },
                null
            );
        };

        // Builds a tree whose parent reads no signal, so the trigger update bails it out
        // and clones every child at once. The trigger renders first and, mid-pass, sets
        // the leaves' signals: their clones already exist but have not rendered yet, so
        // every leaf update is deferred until commit and replayed back to back.
        private static VirtualNode Tree(
            Signal<int> trigger,
            IReadOnlyList<Signal<int>> leaves,
            Func<bool> shouldFire,
            Action fired
        )
        {
            var triggerRender = new Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>(
                (raw, _) =>
                {
                    int value = Hooks.UseSignal(trigger);
                    if (shouldFire())
                    {
                        fired();
                        foreach (var leaf in leaves)
                        {
                            leaf.Set(1);
                        }
                    }
                    return El("trigger" + value, null);
                }
            );

            var children = new List<VirtualNode>
            {
                V.Func(triggerRender, EmptyProps.Instance, "trigger"),
            };
            for (int i = 0; i < leaves.Count; i++)
            {
                children.Add(
                    V.Func(
                        LeafRender,
                        new LeafProps { Signal = leaves[i], Name = "L" + i },
                        "L" + i
                    )
                );
            }

            var parent = new Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>(
                (raw, _) => El("parent", null, children.ToArray())
            );
            return V.Func(parent, EmptyProps.Instance);
        }

        [Fact]
        public void BothOfTwoCoalescedDeferredUpdatesRender()
        {
            var (container, reconciler) = Rig();
            var trigger = SignalFactory.Create(0);
            var leaves = new[] { SignalFactory.Create(0), SignalFactory.Create(0) };
            bool fire = false;

            reconciler.CreateRoot(container, Tree(trigger, leaves, () => fire, () => fire = false));

            var parentEl = container.Children[0];
            Assert.Equal("L00", parentEl.Children[1].Prop("text"));
            Assert.Equal("L10", parentEl.Children[2].Prop("text"));

            fire = true;
            trigger.Set(1);

            Assert.Equal("L01", parentEl.Children[1].Prop("text"));
            Assert.Equal("L11", parentEl.Children[2].Prop("text"));
        }

        // The loss is "everything after the first", not "the second": every replay past
        // the first takes the cascading-update-on-WIP branch, because by then the
        // recreated WIP root and the superseded root are the same object.
        [Fact]
        public void EveryOneOfManyCoalescedDeferredUpdatesRenders()
        {
            var (container, reconciler) = Rig();
            var trigger = SignalFactory.Create(0);
            var leaves = Enumerable.Range(0, 6).Select(_ => SignalFactory.Create(0)).ToArray();
            bool fire = false;

            reconciler.CreateRoot(container, Tree(trigger, leaves, () => fire, () => fire = false));

            var parentEl = container.Children[0];

            fire = true;
            trigger.Set(1);

            for (int i = 0; i < leaves.Length; i++)
            {
                Assert.Equal("L" + i + "1", parentEl.Children[i + 1].Prop("text"));
            }
        }

        // A component that updates itself from its own render body during the very
        // first pass: the update is deferred (the pass owns the work loop) and replayed
        // on commit, so the mount must settle on the post-update value rather than the
        // one that was rendered. Marking both faces must not disturb this single-update
        // path - it is the case the whole deferral machinery was built for.
        [Fact]
        public void AnUpdateRaisedMidRenderOnTheTreeBeingBuiltStillRenders()
        {
            var (container, reconciler) = Rig();
            var own = SignalFactory.Create(0);
            int renders = 0;

            var component = new Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode>(
                (raw, _) =>
                {
                    renders++;
                    int value = Hooks.UseSignal(own);
                    if (value == 0 && renders == 1)
                    {
                        own.Set(1);
                    }
                    return new VirtualNode(
                        VirtualNodeType.Element,
                        "Label",
                        null,
                        null,
                        new Dictionary<string, object> { ["text"] = "v" + value },
                        null
                    );
                }
            );

            reconciler.CreateRoot(container, V.Func(component, EmptyProps.Instance));

            Assert.Equal("v1", container.Children[0].Prop("text"));
        }
    }
}
