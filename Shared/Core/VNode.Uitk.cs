using Ruitk.Props.Typed;

namespace Ruitk.Core
{
    // The UI Toolkit-flavoured face of VirtualNode.
    //
    // VirtualNode itself is family-neutral: it stores host props as HostPropsBase and the
    // fiber reads HostPropsRaw. This accessor exists purely for source compatibility with
    // consumer code written against the UITK props tree, and lives here rather than in
    // VNode.cs so the neutral core does not depend on BaseProps - which transitively pulls
    // in Style.cs and the whole typed style surface, and is what previously made the
    // reconciler untestable outside Unity.
    public sealed partial class VirtualNode
    {
        /// <summary>
        /// Typed host props for built-in UI Toolkit host elements, or <c>null</c> for other
        /// backend families. Prefer <see cref="HostPropsRaw"/> in family-neutral code.
        /// </summary>
        public BaseProps HostProps => _hostProps as BaseProps;
    }
}
