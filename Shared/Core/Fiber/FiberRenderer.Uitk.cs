using Ruitk.Elements;

namespace Ruitk.Core.Fiber
{
    public partial class FiberRenderer
    {
        private static partial HostContext CreateDefaultContext() =>
            new HostContext(ElementRegistryProvider.GetDefaultRegistry());
    }
}
