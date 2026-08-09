namespace Ruitk.Core
{
    // The test compilation's half of HostContext.CreateDefaultHostConfig. There is
    // no UI Toolkit backend in this world, so a HostContext must always be given an
    // explicit FiberHostConfig (the tests use MockHostConfig); reaching the default
    // is a test bug, not a fallback.
    public sealed partial class HostContext
    {
        private static partial Fiber.FiberHostConfig CreateDefaultHostConfig(
            Elements.ElementRegistry elementRegistry
        ) =>
            throw new System.InvalidOperationException(
                "The host-agnostic test build has no default host backend; construct HostContext with an explicit FiberHostConfig."
            );
    }
}
