using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// ES-modules campaign (Plans~/archive/ES_MODULES_EXECUTION_PLAN.md M4): source-text contract pins for the
/// HMR side of the __Exports model (<c>Editor/HMR/</c> cannot be loaded by this test runner —
/// so the pins are source-text asserts against the Editor files).
///
/// <para>The invariant set:</para>
/// <list type="number">
///   <item><description>the hot-side family key is <c>{ns}.__Exports::{name}</c> on BOTH sides
///   (SG ExportsEmitter + HMR HmrHookEmitter.EmitExports) — the runtime matches producer ids to
///   consumer keys by ordinal string equality (U-06/G-09);</description></item>
///   <item><description>hot-side values carry <c>[UitkxHmrSwap]</c> so the static swapper copies
///   them; hot-side hooks emit <c>__{name}_body</c> for the delegate swapper;</description></item>
///   <item><description>the compiler routes member-only files through container
///   <c>"__Exports"</c>, injects the own-exports using into component units, and every
///   companion compiles ITSELF — the legacy companion-parent redirect was removed with the
///   wrapper grammar (0.16.0);</description></item>
///   <item><description>the reverse-dependency import regex covers the full ES surface
///   (named / star / default).</description></item>
/// </list>
/// </summary>
public class HmrExportsParityContractTests
{
    private static string WorkspaceRoot([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile)!; // Tests/
        return Path.GetFullPath(Path.Combine(dir, "../.."));
    }

    private static string HmrHookEmitterSource() =>
        File.ReadAllText(Path.Combine(WorkspaceRoot(), "Editor", "HMR", "HmrHookEmitter.cs"));

    private static string HmrCompilerSource() =>
        File.ReadAllText(Path.Combine(WorkspaceRoot(), "Editor", "HMR", "UitkxHmrCompiler.cs"));

    private static string HmrControllerSource() =>
        File.ReadAllText(Path.Combine(WorkspaceRoot(), "Editor", "HMR", "UitkxHmrController.cs"));

    private static string SgExportsEmitterSource() =>
        File.ReadAllText(Path.Combine(WorkspaceRoot(), "SourceGenerator~", "Emitter", "ExportsEmitter.cs"));

    [Fact]
    public void FamilyKeyShape_IsExportsQualified_OnBothSides()
    {
        // SG side: $"{ns}.__Exports::{m.Name}"; HMR side: ns + ".__Exports::" + hookName.
        Assert.Contains("{ns}.__Exports::{m.Name}", SgExportsEmitterSource());
        Assert.Contains("ns + \".__Exports::\" + hookName", HmrHookEmitterSource());
    }

    [Fact]
    public void HotSideValues_CarryUitkxHmrSwap_AndHooksEmitBodyMethods()
    {
        var src = HmrHookEmitterSource();
        Assert.Contains("public static string EmitExports(object directives, string filePath,", src);
        Assert.Contains("[global::Ruitk.UitkxHmrSwap]", src);
        Assert.Contains("__{name}_body{typeParams}({paramsText})", src);
    }

    [Fact]
    public void Compiler_RoutesNewModeMemberFiles_ThroughExportsContainer()
    {
        var src = HmrCompilerSource();
        Assert.Contains("result.HookContainerClass = \"__Exports\";", src);
        Assert.Contains("HmrHookEmitter.EmitExports(", src);
    }

    [Fact]
    public void Compiler_ThreadsEffectiveNamespace_IntoEveryEmitExportsCall()
    {
        // EmitExports keeps a defaulted `effectiveNs = null` with a raw-parse fallback,
        // so a call site that DROPS the argument still compiles — and every stamp-less
        // (file-keyed) file would emit its hot __Exports into the raw parser-default
        // namespace: Type.FullName stops matching the project assembly and value edits
        // silently stop swapping (the documented field bug the deleted EmitModules
        // parity suite used to fence). Pin the named argument at all three call sites.
        var src = HmrCompilerSource();
        Assert.Contains("effectiveNs: exNs", src);
        Assert.Contains("effectiveNs: newModeNs", src);
        Assert.Contains("effectiveNs: ns", src);
        Assert.Contains("string effectiveNs = null", HmrHookEmitterSource());
    }

    [Fact]
    public void Compiler_InjectsOwnExportsUsing_ForNewModeComponents()
    {
        var src = HmrCompilerSource();
        // The own-exports payload routes through the mode-aware InjectUsings (inside-the-
        // namespace + global:: for new-mode; file-top prepend for legacy — the M7 shadowing fix).
        Assert.Contains("new List<string> { $\"static {ns}.__Exports\" }", src);
        Assert.Contains("internal static string InjectUsings(", src);
        Assert.Contains("internal static string GlobalizeUsingPayload(string payload)", src);
    }

    [Fact]
    public void Controller_CompanionRedirect_IsFullyRemoved()
    {
        // 0.16.0 legacy wave: every file compiles itself; the parent is reached through
        // the import reverse-edge fan-out. A resurrected redirect would misroute saves
        // of dotted-stem member files to a same-stem component that never merges them.
        var src = HmrControllerSource();
        Assert.DoesNotContain("ResolveParentComponentFile", src);
        Assert.DoesNotContain("s_legacyWrapperKeywordRegex", src);
        Assert.Contains("FanOutToImporters(uitkxPath);", src);
    }

    [Fact]
    public void Controller_ImportEdgeRegex_CoversStarAndDefaultForms()
    {
        var src = HmrControllerSource();
        Assert.Contains(@"\{[^}]*\}|\*\s*as\s+[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_][A-Za-z0-9_]*", src);
    }

    [Fact]
    public void InitializerTypeExtraction_MirroredOnBothSides()
    {
        // The `new T {...}` type-inference extraction (G-04 sugar) exists on both sides and
        // keeps the same recognizer shape (leading `new`, generic-depth-aware scan).
        Assert.Contains("internal static string? ExtractInitializerTypeName(string initText)", SgExportsEmitterSource());
        Assert.Contains("internal static string ExtractNewInitializerTypeName(string initText)", HmrHookEmitterSource());
    }

    [Fact]
    public void CompanionPass_GatesInliningOnMemberDeclarations()
    {
        // A companion with no member declarations carries nothing to inline (import-only,
        // export-list-only, and type-only files are legitimate) — the pass must gate on
        // MemberDeclarations, not warn or emit for them.
        Assert.Contains(
            "if (GetItems(GetProp(companionDir, \"MemberDeclarations\")).Count > 0)",
            HmrCompilerSource());
    }
}
