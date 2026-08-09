namespace Ruitk.Props.Typed
{
    /// <summary>
    /// Implemented by host props that carry a primary text value.
    /// </summary>
    /// <remarks>
    /// The reconciler's diff tracing wants to report what a text-bearing element's
    /// content changed to. It previously did that by testing for a concrete UI Toolkit
    /// props type, which coupled the family-neutral fiber core to one backend's props
    /// tree - and through it to the whole typed <c>Style</c> surface. Asking for a
    /// capability instead keeps the core neutral and lets any backend family opt in.
    /// </remarks>
    public interface IHostTextProps
    {
        /// <summary>
        /// The element's primary text, or <c>null</c> when it carries none.
        /// </summary>
        string HostText { get; }
    }
}
