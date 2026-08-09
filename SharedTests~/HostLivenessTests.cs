using Ruitk.Core.Fiber;
using Ruitk.Shared.Tests.Fiber;
using Xunit;

namespace Ruitk.Shared.Tests
{
    // The IsAlive seam on FiberHostConfig - the liveness primitive the Unity 6.5
    // retention cleanup and the PanelRenderer host's reuse/retarget/remount
    // branch key on. The UITK and uGUI overrides read engine state and are
    // exercised in-editor; these tests pin the seam's contract.
    public class HostLivenessTests
    {
        private sealed class DefaultConfig : FiberHostConfig
        {
            public override object CreateElement(string elementType) => new object();

            public override void ApplyProperties(
                object element,
                string elementType,
                System.Collections.Generic.IReadOnlyDictionary<string, object> oldProps,
                System.Collections.Generic.IReadOnlyDictionary<string, object> newProps
            ) { }

            public override void ApplyTypedProperties(
                object element,
                string elementType,
                Ruitk.Props.Typed.HostPropsBase oldProps,
                Ruitk.Props.Typed.HostPropsBase newProps
            ) { }

            public override void AppendChild(object parent, object child) { }

            public override void InsertBefore(object parent, object child, object beforeChild) { }

            public override void RemoveChild(object parent, object child) { }

            public override object GetParent(object element) => null;

            public override void ClearChildren(object element) { }

            public override int GetChildCount(object element) => 0;

            public override object GetChildAt(object element, int index) => null;
        }

        [Fact]
        public void IsAliveDefaultsToTrueForAnyNonNullHandle()
        {
            var config = new DefaultConfig();

            Assert.True(config.IsAlive(new object()));
            Assert.False(config.IsAlive(null));
        }

        [Fact]
        public void ABackendOverrideCanDeclareAnElementDead()
        {
            var host = new MockHostConfig();
            var element = (MockElement)host.CreateElement("Label");

            Assert.True(host.IsAlive(element));

            host.DeadElements.Add(element);

            Assert.False(host.IsAlive(element));
        }
    }
}
