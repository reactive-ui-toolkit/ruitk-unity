using Ruitk.Elements;

namespace Ruitk.Core
{
    public sealed partial class HostContext
    {
        private static partial Fiber.FiberHostConfig CreateDefaultHostConfig(
            ElementRegistry elementRegistry
        ) => new Fiber.UitkHostConfig(elementRegistry);
    }
}
