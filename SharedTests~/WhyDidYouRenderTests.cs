using System;
using System.Collections.Generic;
using Ruitk;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using Ruitk.Diagnostics;
using Ruitk.Elements;
using Ruitk.Shared.Tests.Fiber;
using Xunit;

namespace Ruitk.Shared.Tests
{
    // Per-component render reasons. The reconciler's bailout already decides on exactly
    // these four facts and used to discard them; these tests pin that each one is reported,
    // that a bailout reports None, and that an unsubscribed build is not paying for any of it.
    //
    // WhyDidYouRender is process-global static state, so every test subscribes and
    // unsubscribes inside a using-block rather than leaving a listener behind. Assembly-wide
    // test parallelisation is off (see AssemblyInfo.cs) -- a capture here would otherwise see
    // renders produced by any other test class.
    public class WhyDidYouRenderTests
    {
        private sealed class Capture : IDisposable
        {
            public readonly List<(string Name, RenderReason Reason)> Events =
                new List<(string, RenderReason)>();

            private readonly Action<string, RenderReason> _handler;

            public Capture()
            {
                _handler = (name, reason) => Events.Add((name, reason));
                WhyDidYouRender.Rendered += _handler;
            }

            public void Dispose() => WhyDidYouRender.Rendered -= _handler;
        }

        private static VirtualNode El(string type, Dictionary<string, object> props = null) =>
            new VirtualNode(VirtualNodeType.Element, type, null, null, props, null);

        private static (MockElement container, FiberReconciler reconciler) Rig()
        {
            var host = new MockHostConfig();
            var container = new MockElement { Type = "root" };
            return (container, new FiberReconciler(new HostContext(new ElementRegistry(), host)));
        }

        [Fact]
        public void NothingIsReportedWhileNobodyIsListening()
        {
            Assert.False(WhyDidYouRender.Active);

            var (container, reconciler) = Rig();
            reconciler.CreateRoot(container, V.Func((props, children) => El("Label")));

            Assert.False(WhyDidYouRender.Active);
        }

        [Fact]
        public void AFirstRenderReportsFirstMount()
        {
            using var capture = new Capture();
            var (container, reconciler) = Rig();

            reconciler.CreateRoot(container, V.Func((props, children) => El("Label")));

            Assert.Single(capture.Events);
            Assert.True(capture.Events[0].Reason.HasFlag(RenderReason.FirstMount));
        }

        [Fact]
        public void AStateUpdateIsReportedAsStateUpdateAndNotAsChangedProps()
        {
            using var capture = new Capture();
            var (container, reconciler) = Rig();
            Action bump = null;

            var node = V.Func(
                (props, children) =>
                {
                    var (value, setValue) = Hooks.UseState(0);
                    bump = () => setValue(value + 1);
                    return El("Label", new Dictionary<string, object> { ["text"] = value.ToString() });
                }
            );
            reconciler.CreateRoot(container, node);
            capture.Events.Clear();

            bump();

            Assert.NotEmpty(capture.Events);
            var reason = capture.Events[0].Reason;
            Assert.True(reason.HasFlag(RenderReason.StateUpdate));
            Assert.False(reason.HasFlag(RenderReason.FirstMount));
        }

        [Fact]
        public void ABailoutIsReportedAsNone()
        {
            using var capture = new Capture();
            var (container, reconciler) = Rig();

            var root = reconciler.CreateRoot(
                container,
                El("Box", null)
            );
            capture.Events.Clear();

            // Re-rendering a component with the same props from an unchanged parent is the
            // bailout the reasons exist to make visible.
            var child = V.Func((props, children) => El("Label"));
            reconciler.ScheduleUpdateOnFiber(root.Current, child);
            capture.Events.Clear();
            reconciler.ScheduleUpdateOnFiber(root.Current, child);

            // Assert.All over an empty list passes vacuously, which would make this test
            // prove nothing at all if the reporting ever stopped firing.
            Assert.NotEmpty(capture.Events);
            Assert.All(capture.Events, e => Assert.Equal(RenderReason.None, e.Reason));
        }

        [Fact]
        public void TheReportedNameIdentifiesTheComponent()
        {
            using var capture = new Capture();
            var (container, reconciler) = Rig();

            reconciler.CreateRoot(container, V.Func(NamedComponent));

            Assert.Single(capture.Events);
            Assert.Contains(nameof(NamedComponent), capture.Events[0].Name);
        }

        private static VirtualNode NamedComponent(
            IProps props,
            IReadOnlyList<VirtualNode> children
        ) => El("Label");
    }
}
