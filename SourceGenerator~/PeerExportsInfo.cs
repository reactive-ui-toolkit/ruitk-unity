using System.Collections.Immutable;
using Ruitk.Language.Parser;

namespace Ruitk.SourceGenerator
{
    /// <summary>
    /// Per-file descriptor of a NEW-MODE (plain-declaration, ES-modules campaign) peer's export
    /// surface, discovered during the pre-scan (U-03). One entry per new-syntax file. Feeds:
    /// <list type="bullet">
    ///   <item><description>member-import lowering in <c>UitkxPipeline.ResolveInjectedUsings</c>
    ///   (<c>static {ns}.__Exports</c> / star / default payloads);</description></item>
    ///   <item><description>typed BRIDGE emission for rename-on-import / default member imports
    ///   (<c>ExportsEmitter</c>);</description></item>
    ///   <item><description>dotted-tag (<c>&lt;X.Comp/&gt;</c>) namespace resolution
    ///   (star-alias map, U-05).</description></item>
    /// </list>
    /// One entry per member-bearing file (0.16.0: the legacy wrapper grammar and its
    /// peer-module table are gone; components additionally appear in PeerComponentInfo).
    /// </summary>
    public sealed record PeerExportsInfo(
        /// <summary>Absolute path of the owning <c>.uitkx</c> file.</summary>
        string SourceFilePath,
        /// <summary>The file's EFFECTIVE (file-keyed) namespace.</summary>
        string Namespace,
        /// <summary>Every top-level member declaration (values/utils/hooks), exported or not.</summary>
        ImmutableArray<MemberDeclaration> Members,
        /// <summary>Names of the file's component declarations (exported only).</summary>
        ImmutableArray<string> ExportedComponentNames,
        /// <summary>The <c>export default</c> name, or <c>null</c>.</summary>
        string? DefaultExportName
    )
    {
        /// <summary>The peer's <c>@backend</c> value ("ugui" or null for UI Toolkit).</summary>
        public string? Backend { get; init; } = null;
    };
}
