namespace Ruitk.Core.Fiber
{
    // The test compilation's half of FiberRenderer.CreateDefaultContext. There is
    // no UI Toolkit backend in this world, so a FiberRenderer must always be given
    // an explicit HostContext; reaching the default is a test bug, not a fallback.
    public partial class FiberRenderer
    {
        private static partial HostContext CreateDefaultContext() =>
            throw new System.InvalidOperationException(
                "The host-agnostic test build has no default host backend; construct FiberRenderer with an explicit HostContext."
            );
    }
}
