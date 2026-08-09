using System.Collections.Generic;
using Ruitk;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using Ruitk.Elements;
using Ruitk.Shared.Tests.Fiber;
using Xunit;

namespace Ruitk.Shared.Tests
{
    // FiberRenderer - the mount-level driver - through the mock backend. The
    // retarget tests pin the contract the Unity 6.5 PanelRenderer host's
    // reuse/retarget/remount branch depends on: retargeting moves the mounted
    // host children to the new container and preserves fiber and hook state.
    public class FiberRendererTests
    {
        private readonly MockHostConfig _host = new MockHostConfig();
        private readonly FiberRenderer _renderer;
        private readonly MockElement _container = new MockElement { Type = "root" };

        public FiberRendererTests()
        {
            var context = new HostContext(new ElementRegistry(), _host);
            _renderer = new FiberRenderer(_container, context);
        }

        private static VirtualNode El(
            string type,
            string key = null,
            Dictionary<string, object> props = null,
            params VirtualNode[] children
        ) => new VirtualNode(VirtualNodeType.Element, type, null, key, props, children);

        [Fact]
        public void RenderMountsOnFirstCallAndUpdatesInPlaceOnSubsequentCalls()
        {
            _renderer.Render(
                El("Label", props: new Dictionary<string, object> { ["text"] = "one" })
            );
            var label = _container.Children[0];

            _renderer.Render(
                El("Label", props: new Dictionary<string, object> { ["text"] = "two" })
            );

            Assert.Same(label, _container.Children[0]);
            Assert.Equal("two", label.Prop("text"));
            Assert.Single(_host.CreatedElements);
        }

        [Fact]
        public void ClearUnmountsTheTreeAndEmptiesTheContainer()
        {
            _renderer.Render(El("Box", children: new[] { El("Label") }));
            Assert.NotEmpty(_container.Children);

            _renderer.Clear();

            Assert.Empty(_container.Children);
        }

        [Fact]
        public void RetargetMovesMountedChildrenToTheNewContainerInOrder()
        {
            _renderer.Render(El("Box", children: new[] { El("Label"), El("Button") }));
            var box = _container.Children[0];
            var next = new MockElement { Type = "next" };

            _renderer.RetargetContainer(next);

            Assert.Empty(_container.Children);
            Assert.Equal("next(Box(Label,Button))", MockHostConfig.Dump(next));
            Assert.Same(box, next.Children[0]);
        }

        [Fact]
        public void UpdatesAfterRetargetWriteToTheNewContainer()
        {
            _renderer.Render(El("Box", children: new[] { El("Label", "a") }));
            var next = new MockElement { Type = "next" };
            _renderer.RetargetContainer(next);

            _renderer.Render(El("Box", children: new[] { El("Label", "a"), El("Label", "b") }));

            Assert.Equal("next(Box(Label,Label))", MockHostConfig.Dump(next));
            Assert.Empty(_container.Children);
        }

        [Fact]
        public void RetargetPreservesHookStateAcrossTheMove()
        {
            Hooks.StateSetter<int> setCount = null;
            VirtualNode Node() =>
                V.Func(
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

            _renderer.Render(Node());
            setCount(3);
            var label = _container.Children[0];
            Assert.Equal("n=3", label.Prop("text"));

            var next = new MockElement { Type = "next" };
            _renderer.RetargetContainer(next);
            setCount(4);

            Assert.Same(label, next.Children[0]);
            Assert.Equal("n=4", label.Prop("text"));
        }

        [Fact]
        public void RetargetToNullOrTheSameContainerIsANoOp()
        {
            _renderer.Render(El("Label"));
            var opsBefore = _host.Operations.Count;

            _renderer.RetargetContainer(null);
            _renderer.RetargetContainer(_container);

            Assert.Equal(opsBefore, _host.Operations.Count);
            Assert.Single(_container.Children);
        }
    }
}
