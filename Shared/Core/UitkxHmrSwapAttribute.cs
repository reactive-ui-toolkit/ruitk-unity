using System;

namespace Ruitk
{
    /// <summary>
    /// Marks a static field as one whose value the UITKX hot-module-reload
    /// (HMR) pipeline is permitted to overwrite at runtime.
    ///
    /// The UITKX source generator emits this attribute on every module-scope
    /// static field it produces (hoisted styles, user-declared <c>static
    /// readonly</c> module fields, <c>__uitkx_ussKeys</c>, etc.) AFTER
    /// stripping the <c>readonly</c> keyword.
    ///
    /// Why the <c>readonly</c> is stripped: when a field is emitted with the
    /// <c>initonly</c> IL flag, Mono's JIT is permitted to inline the field's
    /// reference into machine code after the type initializer has run.
    /// Subsequent reflection-based slot updates (used by HMR to refresh the
    /// field across edit-save cycles) are then invisible to already-JIT'd
    /// methods, leaving stale references in flight. Removing <c>initonly</c>
    /// forces every read to go through the slot, making the swap visible
    /// immediately.
    ///
    /// External code MUST NOT write to fields marked with this attribute.
    /// The Roslyn analyzer rule <c>UITKX0210</c> flags any non-cctor write
    /// at compile time. Writing from non-cctor code defeats hot-reload and
    /// produces undefined visual behavior across HMR cycles.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class UitkxHmrSwapAttribute : Attribute
    {
    }
}
