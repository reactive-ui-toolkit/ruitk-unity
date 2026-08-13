using System.Collections.Immutable;

namespace Ruitk.SourceGenerator
{
    /// <summary>
    /// Lightweight descriptor for a hook container class discovered during
    /// the pre-scan phase. Used to inject <c>using static</c> directives
    /// into component files so that hook methods (e.g. <c>useCounter()</c>)
    /// are directly callable without qualifying the container class name.
    /// </summary>
    public sealed record PeerHookContainerInfo(
        /// <summary>Namespace declared in the hook file (<c>@namespace</c>).</summary>
        string Namespace,
        /// <summary>
        /// Generated container class name — always <c>__Exports</c> since the 0.16.0
        /// legacy wave (the filename-derived <c>{Stem}Hooks</c> naming died with the
        /// wrapper grammar).
        /// </summary>
        string ClassName
    )
    {
        // ── Import/export grammar scaffolding (leg 3, M6b) ──────────────────────
        // Additive fields consumed by the strict resolver + scoped injection (M8).
        // Inert today: nothing reads them yet, so populating them is behavior-neutral.

        /// <summary>Absolute path of the owning <c>.uitkx</c> file (import resolution + HMR invalidation).</summary>
        public string? SourceFilePath { get; init; }

        /// <summary>Path-derived default namespace for this container (M5b), or <c>null</c> until wired.</summary>
        public string? DerivedNamespace { get; init; }

        /// <summary>
        /// Names of the hooks declared with <c>export</c> in this container. Empty until
        /// strictness needs it; the whole container is still exposed to C# via
        /// <c>using static</c> (C# has no per-method static import) — per-NAME strictness
        /// is a uitkx diagnostic, not a C# fence (§6).
        /// </summary>
        public ImmutableArray<string> ExportedHookNames { get; init; } = ImmutableArray<string>.Empty;
    };
}
