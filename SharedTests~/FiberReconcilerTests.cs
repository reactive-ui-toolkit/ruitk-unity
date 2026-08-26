using System;
using System.Collections.Generic;
using Ruitk;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using Ruitk.Elements;
using Ruitk.Shared.Tests.Fiber;
using Xunit;

namespace Ruitk.Shared.Tests
{
    // The fiber reconciler driven end-to-end through a mock FiberHostConfig over
    // POCO handles - no scheduler, so renders and passive effects are synchronous
    // (the reconciler's documented sync / test mode).
    public class FiberReconcilerTests
    {
        private readonly MockHostConfig _host = new MockHostConfig();
        private readonly MockElement _container = new MockElement { Type = "root" };
        private readonly FiberReconciler _reconciler;
        private readonly FiberRoot _root;

        public FiberReconcilerTests()
        {
            var context = new HostContext(new ElementRegistry(), _host);
            _reconciler = new FiberReconciler(context);
            _root = null;
        }

        private FiberRoot Mount(VirtualNode vnode) => _reconciler.CreateRoot(_container, vnode);

        private void Update(FiberRoot root, VirtualNode vnode) =>
            _reconciler.ScheduleUpdateOnFiber(root.Current, vnode);

        private static VirtualNode El(
            string type,
            string key = null,
            Dictionary<string, object> props = null,
            params VirtualNode[] children
        ) => new VirtualNode(VirtualNodeType.Element, type, null, key, props, children);

        // ── Mount ───────────────────────────────────────────────────────────

        [Fact]
        public void MountBuildsTheHostTreeInDocumentOrder()
        {
            Mount(El("Box", children: new[] { El("Label"), El("Button") }));

            Assert.Equal("root(Box(Label,Button))", MockHostConfig.Dump(_container));
        }

        [Fact]
        public void MountAppliesDictionaryPropsOnPlacement()
        {
            Mount(El("Label", props: new Dictionary<string, object> { ["text"] = "hello" }));

            var label = _container.Children[0];
            Assert.Equal("hello", label.Prop("text"));
            Assert.Equal(1, label.DictApplyCount);
        }

        [Fact]
        public void TextNodeMountsAsALabelHostWithATextProp()
        {
            Mount(V.Text("greetings"));

            var label = _container.Children[0];
            Assert.Equal("Label", label.Type);
            Assert.Equal("greetings", label.Prop("text"));
        }

        [Fact]
        public void FragmentProducesNoHostElementAndFlattensChildren()
        {
            Mount(V.Fragment(null, El("Label"), El("Button")));

            Assert.Equal("root(Label,Button)", MockHostConfig.Dump(_container));
            Assert.Equal(2, _host.CreatedElements.Count);
        }

        [Fact]
        public void FunctionComponentRendersItsOutputWithoutAHostElement()
        {
            var node = V.Func((props, children) => El("Box", children: new[] { El("Label") }));

            Mount(node);

            Assert.Equal("root(Box(Label))", MockHostConfig.Dump(_container));
        }

        // ── Update: reuse in place ──────────────────────────────────────────

        [Fact]
        public void SameTypeUpdateReusesTheHostElementInPlace()
        {
            var root = Mount(
                El("Label", props: new Dictionary<string, object> { ["text"] = "before" })
            );
            var original = _container.Children[0];

            Update(root, El("Label", props: new Dictionary<string, object> { ["text"] = "after" }));

            Assert.Same(original, _container.Children[0]);
            Assert.Equal("after", original.Prop("text"));
            Assert.Single(_host.CreatedElements);
            Assert.Empty(_host.RemovedElements);
        }

        [Fact]
        public void DifferentTypeUpdateRemountsTheHostElement()
        {
            var root = Mount(El("Label"));
            var original = _container.Children[0];

            Update(root, El("Button"));

            Assert.Equal("root(Button)", MockHostConfig.Dump(_container));
            Assert.NotSame(original, _container.Children[0]);
            Assert.True(original.HostRemoved);
        }

        // ── Children: keys, additions, removals ─────────────────────────────

        [Fact]
        public void KeyedReorderMovesElementsWithoutRecreatingThem()
        {
            var root = Mount(
                El("Box", children: new[] { El("Label", "a"), El("Label", "b"), El("Label", "c") })
            );
            var box = _container.Children[0];
            var a = box.Children[0];
            var b = box.Children[1];
            var c = box.Children[2];
            int createdAfterMount = _host.CreatedElements.Count;

            Update(
                root,
                El("Box", children: new[] { El("Label", "c"), El("Label", "a"), El("Label", "b") })
            );

            Assert.Equal(createdAfterMount, _host.CreatedElements.Count);
            Assert.Empty(_host.RemovedElements);
            Assert.Equal(new[] { c, a, b }, box.Children);
        }

        [Fact]
        public void RemovingATailChildRemovesExactlyThatHostElement()
        {
            var root = Mount(El("Box", children: new[] { El("Label", "a"), El("Label", "b") }));
            var box = _container.Children[0];
            var b = box.Children[1];

            Update(root, El("Box", children: new[] { El("Label", "a") }));

            Assert.Equal("root(Box(Label))", MockHostConfig.Dump(_container));
            Assert.True(b.HostRemoved);
            Assert.Equal(new[] { b }, _host.RemovedElements);
        }

        [Fact]
        public void ConditionalChildAppearsAndDisappearsAcrossUpdates()
        {
            var root = Mount(El("Box", children: new[] { El("Label", "always") }));
            var box = _container.Children[0];

            Update(
                root,
                El("Box", children: new[] { El("Label", "always"), El("Button", "maybe") })
            );
            Assert.Equal("root(Box(Label,Button))", MockHostConfig.Dump(_container));

            Update(root, El("Box", children: new[] { El("Label", "always") }));
            Assert.Equal("root(Box(Label))", MockHostConfig.Dump(_container));
            Assert.Same(box, _container.Children[0]);
        }

        // ── Hooks: state, effects, refs ─────────────────────────────────────

        [Fact]
        public void UseStateSetterTriggersASynchronousRerenderWithTheNewValue()
        {
            Hooks.StateSetter<int> setCount = null;
            var node = V.Func(
                (props, children) =>
                {
                    var (count, set) = Hooks.UseState(0);
                    setCount = set;
                    return El(
                        "Label",
                        props: new Dictionary<string, object> { ["text"] = "n=" + count }
                    );
                }
            );

            Mount(node);
            var label = _container.Children[0];
            Assert.Equal("n=0", label.Prop("text"));

            setCount(5);

            Assert.Equal("n=5", label.Prop("text"));
            Assert.Same(label, _container.Children[0]);
            Assert.Single(_host.CreatedElements);
        }

        [Fact]
        public void SettingTheSameStateValueDoesNotRerender()
        {
            int renders = 0;
            Hooks.StateSetter<int> setCount = null;
            var node = V.Func(
                (props, children) =>
                {
                    renders++;
                    var (count, set) = Hooks.UseState(7);
                    setCount = set;
                    return El("Label");
                }
            );

            Mount(node);
            Assert.Equal(1, renders);

            setCount(7);

            Assert.Equal(1, renders);
        }

        [Fact]
        public void UseEffectRunsAfterMountAndItsCleanupRunsOnDeletion()
        {
            var log = new List<string>();
            VirtualNode ComponentTree(bool includeChild)
            {
                var child = V.Func(
                    (props, children) =>
                    {
                        Hooks.UseEffect(
                            () =>
                            {
                                log.Add("setup");
                                return () => log.Add("cleanup");
                            },
                            Array.Empty<object>()
                        );
                        return El("Label");
                    }
                );
                return includeChild
                    ? El("Box", children: new[] { child })
                    : El("Box", children: Array.Empty<VirtualNode>());
            }

            var root = Mount(ComponentTree(true));
            Assert.Equal(new[] { "setup" }, log);

            Update(root, ComponentTree(false));
            Assert.Equal(new[] { "setup", "cleanup" }, log);
        }

        [Fact]
        public void UseEffectWithUnchangedDependenciesDoesNotRerun()
        {
            int runs = 0;
            Hooks.StateSetter<int> setCount = null;
            VirtualNode Node() =>
                V.Func(
                    (props, children) =>
                    {
                        var (count, set) = Hooks.UseState(0);
                        setCount = set;
                        Hooks.UseEffect(
                            () =>
                            {
                                runs++;
                                return null;
                            },
                            "constant-dep"
                        );
                        return El("Label");
                    }
                );

            Mount(Node());
            Assert.Equal(1, runs);

            setCount(1);

            Assert.Equal(1, runs);
        }

        [Fact]
        public void UseRefPersistsItsValueAcrossRerenders()
        {
            Hooks.StateSetter<int> setCount = null;
            var observed = new List<int>();
            var node = V.Func(
                (props, children) =>
                {
                    var (count, set) = Hooks.UseState(0);
                    setCount = set;
                    var box = Hooks.UseRef(100);
                    observed.Add(box.Current);
                    box.Current = box.Current + 1;
                    return El("Label");
                }
            );

            Mount(node);
            setCount(1);
            setCount(2);

            Assert.Equal(new[] { 100, 101, 102 }, observed);
        }

        // ── Unmount ─────────────────────────────────────────────────────────

        [Fact]
        public void UnmountRootRunsEffectCleanupsAndRemovesHostElements()
        {
            var log = new List<string>();
            var node = V.Func(
                (props, children) =>
                {
                    Hooks.UseEffect(
                        () =>
                        {
                            log.Add("setup");
                            return () => log.Add("cleanup");
                        },
                        Array.Empty<object>()
                    );
                    return El("Box", children: new[] { El("Label") });
                }
            );

            Mount(node);
            Assert.Equal(new[] { "setup" }, log);

            _reconciler.UnmountRoot();

            Assert.Equal(new[] { "setup", "cleanup" }, log);
            Assert.Empty(_container.Children);
        }

        // UB-179: unmounting drains the scheduler so effect cleanups run before
        // the host goes away - and that drain used to resume a render slice that
        // had been queued against the tree the unmount had just deleted. The
        // slice walked the dead tree into CompleteWork, which appends to the
        // root's effect list, and the root was null: a NullReferenceException
        // thrown from inside the unmount, on the builder's Save path.
        [Fact]
        public void UnmountDropsRenderWorkAlreadyQueuedAgainstTheDeadTree()
        {
            var scheduler = new QueueScheduler();
            var context = new HostContext(new ElementRegistry(), _host);
            context.Environment["scheduler"] = scheduler;
            var reconciler = new FiberReconciler(context);
            var container = new MockElement { Type = "root" };

            var root = reconciler.CreateRoot(
                container,
                El("Box", children: new[] { El("Label"), El("Label") })
            );

            // An update SCHEDULES a slice rather than running it: the work is in
            // flight and the tree is about to be torn down under it.
            reconciler.ScheduleUpdateOnFiber(
                root.Current,
                El("Box", children: new[] { El("Label") })
            );
            Assert.True(scheduler.Pending > 0, "the update should have queued a slice");

            reconciler.UnmountRoot();

            // The queue still holds the closure - nothing can retract it - so
            // this is the moment the old code threw.
            scheduler.PumpNow();

            // And a second teardown, or any further pump, stays quiet too.
            reconciler.UnmountRoot();
            scheduler.PumpNow();
        }

        [Fact]
        public void AbandonRootDropsRenderWorkAlreadyQueued()
        {
            var scheduler = new QueueScheduler();
            var context = new HostContext(new ElementRegistry(), _host);
            context.Environment["scheduler"] = scheduler;
            var reconciler = new FiberReconciler(context);
            var container = new MockElement { Type = "root" };

            var root = reconciler.CreateRoot(
                container,
                El("Box", children: new[] { El("Label"), El("Label") })
            );
            reconciler.ScheduleUpdateOnFiber(
                root.Current,
                El("Box", children: new[] { El("Label") })
            );
            Assert.True(scheduler.Pending > 0, "the update should have queued a slice");

            reconciler.AbandonRoot();
            scheduler.PumpNow();
        }
    }
}
