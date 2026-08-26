using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Ruitk.Language;
using Ruitk.Language.Diagnostics;
using Ruitk.Language.Lowering;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// Tests for <see cref="DiagnosticsAnalyzer"/> — verifies Tier-2 structural
/// diagnostics (UITKX0101-0111) raised after parsing.
/// The UITKX0107 (unreachable after return) tests are especially important
/// because they validate the fix described in TECH_DEBT TD-08.
/// Pure language-lib tests (no LSP, no Roslyn).
/// </summary>
public sealed class DiagnosticsAnalyzerTests
{
    private static readonly DiagnosticsAnalyzer _analyzer = new();

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ParseResult Parse(string source, string path = "Test.uitkx")
    {
        var diags = new List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(source, path, diags);
        var parsedNodes = UitkxParser.Parse(source, path, directives, diags);
        var nodes = CanonicalLowering.LowerToRenderRoots(directives, parsedNodes, path);
        return new ParseResult(directives, nodes, ImmutableArray.CreateRange(diags));
    }

    private static IReadOnlyList<ParseDiagnostic> Analyze(
        string source,
        string path = "Test.uitkx",
        HashSet<string>? projectElements = null)
    {
        return _analyzer.Analyze(Parse(source, path), path, projectElements, knownAttributes: null, sourceText: source);
    }

    private static bool HasDiag(IReadOnlyList<ParseDiagnostic> diags, string code) =>
        diags.Any(d => d.Code == code);

    // ── UITKX0102: Missing @component ──────────────────────────────────────

    [Fact]
    public void UITKX0102_FunctionStyle_NoWarning()
    {
        var diags = Analyze("VirtualNode Foo() {\n  return (\n    <Label/>\n  );\n}");
        Assert.False(HasDiag(diags, DiagnosticCodes.MissingComponent));
    }

    // ── UITKX0103: filename mismatch is NO LONGER enforced ─────────────────
    // Under the import/export model a file may declare several components in any
    // order, so a filename-match rule is meaningless. Matching the filename is now
    // a documentation convention, not a code-enforced diagnostic.

    [Fact]
    public void UITKX0103_FilenameMismatch_NotFlagged()
    {
        var diags = Analyze("VirtualNode WrongName() {\n  return (\n    <Label/>\n  );\n}", path: "Correct.uitkx");
        Assert.False(HasDiag(diags, DiagnosticCodes.FilenameMismatch));
    }

    [Fact]
    public void UITKX0103_FilenameMatches_NoWarning()
    {
        var diags = Analyze("VirtualNode Test() {\n  return (\n    <Label/>\n  );\n}", path: "Test.uitkx");
        Assert.False(HasDiag(diags, DiagnosticCodes.FilenameMismatch));
    }

    // ── UITKX0104: Duplicate key ───────────────────────────────────────────

    [Fact]
    public void UITKX0104_DuplicateKey()
    {
        var source = "VirtualNode C() {\n  return (\n    <Box>\n      <Label key=\"a\"/>\n      <Label key=\"a\"/>\n    </Box>\n  );\n}";
        var diags = Analyze(source);
        Assert.True(HasDiag(diags, DiagnosticCodes.DuplicateKey));
    }

    [Fact]
    public void UITKX0104_UniqueKeys_NoWarning()
    {
        var source = "VirtualNode C() {\n  return (\n    <Box>\n      <Label key=\"a\"/>\n      <Label key=\"b\"/>\n    </Box>\n  );\n}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.DuplicateKey));
    }

    // ── UITKX0105: Unknown element ─────────────────────────────────────────

    [Fact]
    public void UITKX0105_UnknownElement_WhenIndexProvided()
    {
        var knownElems = new HashSet<string> { "Label", "Box" };
        var source = "VirtualNode C() {\n  return (\n    <UnknownWidget/>\n  );\n}";
        var diags = Analyze(source, projectElements: knownElems);
        Assert.True(HasDiag(diags, DiagnosticCodes.UnknownElement));
    }

    [Fact]
    public void UITKX0105_KnownElement_NoWarning()
    {
        var knownElems = new HashSet<string> { "Label", "Box" };
        var source = "VirtualNode C() {\n  return (\n    <Label/>\n  );\n}";
        var diags = Analyze(source, projectElements: knownElems);
        Assert.False(HasDiag(diags, DiagnosticCodes.UnknownElement));
    }

    [Fact]
    public void UITKX0105_NullIndex_Skipped()
    {
        var source = "VirtualNode C() {\n  return (\n    <Anything/>\n  );\n}";
        var diags = Analyze(source, projectElements: null);
        Assert.False(HasDiag(diags, DiagnosticCodes.UnknownElement));
    }

    // ── UITKX0106: Missing key in @foreach ─────────────────────────────────

    [Fact]
    public void UITKX0106_MissingKeyInForeach()
    {
        var source = "VirtualNode C() {\n  return (\n    <Box>\n      @foreach (var x in items) {\n        <Label/>\n      }\n    </Box>\n  );\n}";
        var diags = Analyze(source);
        Assert.True(HasDiag(diags, DiagnosticCodes.MissingKey));
    }

    [Fact]
    public void UITKX0106_HasKey_NoWarning()
    {
        var source = "VirtualNode C() {\n  return (\n    <Box>\n      @foreach (var x in items) {\n        <Label key={x}/>\n      }\n    </Box>\n  );\n}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.MissingKey));
    }

    // ── UITKX0107: Unreachable after return ────────────────────────────────

    [Fact]
    public void UITKX0107_NoReturn_NoWarning()
    {
        var source = "VirtualNode C() {\n  int x = 5;\n  return (\n    <Label/>\n  );\n}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.UnreachableAfterReturn));
    }

    [Fact]
    public void UITKX0107_FunctionStyle_UnreachableAfterReturn()
    {
        var source = "VirtualNode Foo() {\n  return (\n    <Label/>\n  );\n  int dead = 0;\n}";
        var diags = Analyze(source);
        Assert.True(HasDiag(diags, DiagnosticCodes.UnreachableAfterReturn));
    }

    // ── UITKX0108: Multiple render roots ───────────────────────────────────

    [Fact]
    public void UITKX0108_MultipleRenderRoots()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label/>\n    <Box/>\n  );\n}";
        var diags = Analyze(source);
        Assert.True(HasDiag(diags, DiagnosticCodes.MultipleRenderRoots));
    }

    [Fact]
    public void UITKX0108_SingleRoot_NoWarning()
    {
        var source = "VirtualNode C() {\n  return (\n    <Box>\n      <Label/>\n    </Box>\n  );\n}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.MultipleRenderRoots));
    }

    // ── UITKX0109: Unknown attribute ───────────────────────────────────────

    [Fact]
    public void UITKX0109_UnknownAttribute_WhenMapProvided()
    {
        var knownAttrs = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Label"] = new HashSet<string> { "text", "style" }
        };
        var source = "VirtualNode C() {\n  return (\n    <Label bogus=\"hi\"/>\n  );\n}";
        var diags = _analyzer.Analyze(Parse(source), "Test.uitkx", null, knownAttrs);
        Assert.True(HasDiag(diags, DiagnosticCodes.UnknownAttribute));
    }

    [Fact]
    public void UITKX0109_KnownAttribute_NoWarning()
    {
        var knownAttrs = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Label"] = new HashSet<string> { "text", "style" }
        };
        var source = "VirtualNode C() {\n  return (\n    <Label text=\"hi\"/>\n  );\n}";
        var diags = _analyzer.Analyze(Parse(source), "Test.uitkx", null, knownAttrs);
        Assert.False(HasDiag(diags, DiagnosticCodes.UnknownAttribute));
    }

    // ── UITKX0109 — User-component strict attribute validation ──────────────
    //
    // Pretty-UI bug regression: <UserComp style={x}/> when the user component
    // didn't declare a `style` parameter used to silently allow it (since
    // `style` was lumped under `universalAttributes`), then explode at C#
    // compile time as CS0117 against the generated *Props class.
    //
    // After the schema split, user-component attribute maps contain ONLY
    // declared params + structural-universal (`key`, `ref`). Anything else
    // must produce UITKX0109.

    [Fact]
    public void UITKX0109_UserComponent_StyleNotForwarded_ReportsUnknown()
    {
        // Foo declares only `text` — `style` (intrinsic-element attribute,
        // NOT structural-universal) must NOT be silently allowed.
        var knownAttrs = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Foo"] = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "text", "key", "ref" },
        };
        var source = "VirtualNode C() {\n  return (\n    <Foo text=\"hi\" style=\"x\"/>\n  );\n}";
        var diags = _analyzer.Analyze(Parse(source), "Test.uitkx", null, knownAttrs);
        Assert.Contains(diags, d => d.Code == DiagnosticCodes.UnknownAttribute && d.Message.Contains("style"));
    }

    [Fact]
    public void UITKX0109_UserComponent_KeyAndRefAlwaysAllowed()
    {
        // `key` and `ref` are structural-universal — must NEVER produce UITKX0109.
        var knownAttrs = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Foo"] = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "text", "key", "ref" },
        };
        var source = "VirtualNode C() {\n  return (\n    <Foo text=\"hi\" key=\"k\" ref=\"r\"/>\n  );\n}";
        var diags = _analyzer.Analyze(Parse(source), "Test.uitkx", null, knownAttrs);
        Assert.False(HasDiag(diags, DiagnosticCodes.UnknownAttribute));
    }

    [Fact]
    public void UITKX0109_UserComponent_DeclaredAttribute_NoDiagnostic()
    {
        var knownAttrs = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Foo"] = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "text", "key", "ref" },
        };
        var source = "VirtualNode C() {\n  return (\n    <Foo text=\"hi\"/>\n  );\n}";
        var diags = _analyzer.Analyze(Parse(source), "Test.uitkx", null, knownAttrs);
        Assert.False(HasDiag(diags, DiagnosticCodes.UnknownAttribute));
    }

    [Fact]
    public void UITKX0109_UserComponent_ExtraPropsRejected()
    {
        // `extraProps` is intrinsic-only (escape hatch for built-in elements
        // where the typed pipeline doesn't model the property). It MUST NOT
        // be allowed on user components — they have no underlying VisualElement.
        var knownAttrs = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Foo"] = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "text", "key", "ref" },
        };
        var source = "VirtualNode C() {\n  return (\n    <Foo text=\"hi\" extraProps=\"x\"/>\n  );\n}";
        var diags = _analyzer.Analyze(Parse(source), "Test.uitkx", null, knownAttrs);
        Assert.Contains(diags, d => d.Code == DiagnosticCodes.UnknownAttribute && d.Message.Contains("extraProps"));
    }

    [Fact]
    public void UITKX0109_BuiltIn_StyleAllowed()
    {
        // Built-in element attribute map (as built by DiagnosticsPublisher)
        // contains intrinsic + structural in addition to per-element attrs.
        // Sanity test: <Box style="x"/> must not error.
        var knownAttrs = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Box"] = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "style", "key", "ref", "name", "className", "extraProps",
            },
        };
        var source = "VirtualNode C() {\n  return (\n    <Box style=\"x\" extraProps=\"y\"/>\n  );\n}";
        var diags = _analyzer.Analyze(Parse(source), "Test.uitkx", null, knownAttrs);
        Assert.False(HasDiag(diags, DiagnosticCodes.UnknownAttribute));
    }

    // ── UITKX0111: Unused parameter ────────────────────────────────────────

    [Fact]
    public void UITKX0111_UnusedParam_FunctionStyle()
    {
        var source = "VirtualNode Foo(string name) {\n  return (\n    <Label text=\"static\"/>\n  );\n}";
        var diags = Analyze(source);
        Assert.True(HasDiag(diags, DiagnosticCodes.UnusedParameter));
    }

    [Fact]
    public void UITKX0111_UsedParam_NoWarning()
    {
        var source = "VirtualNode Foo(string name) {\n  return (\n    <Label text={name}/>\n  );\n}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.UnusedParameter));
    }

    // ── ES-modules campaign (M1 audit item, §1.5): plain-declaration components must
    // still receive UITKX0107/0111 — DiagnosticsAnalyzer reads d.FunctionReturnEndLine/
    // FunctionBodyEndLine/FunctionParams off the singular DirectiveSet fields, which the
    // plain-declaration parser mirrors from the first ComponentDeclaration (parity with the
    // legacy `VirtualNode X(...) {...}` path). ──────────────────────────────────────────

    [Fact]
    public void UITKX0111_UnusedParam_PlainDeclarationComponent()
    {
        var source = "export VirtualNode Foo(string name) {\n  return (\n    <Label text=\"static\"/>\n  );\n}";
        var diags = Analyze(source);
        Assert.True(HasDiag(diags, DiagnosticCodes.UnusedParameter));
    }

    [Fact]
    public void UITKX0111_UsedParam_PlainDeclarationComponent_NoWarning()
    {
        var source = "export VirtualNode Foo(string name) {\n  return (\n    <Label text={name}/>\n  );\n}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.UnusedParameter));
    }

    [Fact]
    public void UITKX0111_UnderscorePrefixedParam_IsDeliberatelyUnused_NoWarning()
    {
        // C# discard convention (field find): renaming an unused parameter to
        // `_name` must acknowledge and silence the diagnostic, matching IDE0060.
        var source = "export VirtualNode Foo(string _name, Action<int>? _onChanged = null) {\n  return (\n    <Label text=\"static\"/>\n  );\n}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.UnusedParameter));
    }

    [Fact]
    public void UITKX0114_UnderscoreAndBareParam_CollideOnPropName()
    {
        // `_count` and `count` both expose the prop `count` (the underscore is
        // the deliberately-unused marker, not part of the contract) — ambiguous.
        var source = "export VirtualNode Foo(int _count, int count) {\n  return (\n    <Label text={count.ToString()}/>\n  );\n}";
        var diags = Analyze(source);
        Assert.True(HasDiag(diags, DiagnosticCodes.DuplicatePropName));
    }

    [Fact]
    public void UITKX0114_DistinctPropNames_NoDiagnostic()
    {
        var source = "export VirtualNode Foo(int _count, string title) {\n  return (\n    <Label text={title}/>\n  );\n}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.DuplicatePropName));
    }

    [Fact]
    public void PropSourceName_StripsLeadingUnderscores_KeepsBareUnderscore()
    {
        Assert.Equal("x", Ruitk.Language.Parser.FunctionParam.ToPropSourceName("_x"));
        Assert.Equal("x", Ruitk.Language.Parser.FunctionParam.ToPropSourceName("__x"));
        Assert.Equal("_", Ruitk.Language.Parser.FunctionParam.ToPropSourceName("_"));
        Assert.Equal("x", Ruitk.Language.Parser.FunctionParam.ToPropSourceName("x"));
    }

    [Fact]
    public void UITKX0107_UnreachableAfterReturn_PlainDeclarationComponent()
    {
        var source = "export VirtualNode Foo() {\n  return (\n    <Label/>\n  );\n  var dead = 1;\n}";
        var diags = Analyze(source);
        Assert.True(HasDiag(diags, DiagnosticCodes.UnreachableAfterReturn));
    }

    [Fact]
    public void UITKX0013_HookInConditional_PlainDeclarationComponent()
    {
        var source =
            "export VirtualNode Foo() {\n"
            + "    var (n, setN) = useState(0);\n"
            + "    return (\n"
            + "        <Box>\n"
            + "            @if (n > 0) {\n"
            + "                <Label text={useState(1).ToString()} />\n"
            + "            }\n"
            + "        </Box>\n"
            + "    );\n"
            + "}";
        var diags = Analyze(source);
        Assert.True(HasDiag(diags, DiagnosticCodes.HookInConditional));
    }

    // ── Severity checks ────────────────────────────────────────────────────

    [Fact]
    public void MissingKey_IsSeverityWarning()
    {
        // U-12: must match the SourceGenerator's severity (UitkxDiagnostics.ForeachMissingKey
        // = Warning). This was Error here — the editor blocked on something the build only
        // warned about.
        var source = "VirtualNode C() {\n  return (\n    <Box>\n      @foreach (var x in items) {\n        <Label/>\n      }\n    </Box>\n  );\n}";
        var diags = Analyze(source);
        var mk = diags.FirstOrDefault(d => d.Code == DiagnosticCodes.MissingKey);
        Assert.NotNull(mk);
        Assert.Equal(ParseSeverity.Warning, mk.Severity);
    }

    // (The UITKX0211 const-in-module tests were removed with the legacy module
    // grammar — 0.16.0. Plain-dialect member bodies have no field-position const,
    // so the check has no subject.)

    // ── UITKX0120: Asset not found ─────────────────────────────────────────

    [Fact]
    public void UITKX0120_AssetCall_MissingFile_ReportsError()
    {
        // Resolved path won't exist on disk → should trigger UITKX0120
        var source = "VirtualNode Card() {\n  return (\n    <Label text={Asset<Texture2D>(\"./avatar.png\").name} />\n  );\n}";
        var diags = Analyze(source, "Assets/UI/Card.uitkx");
        Assert.True(HasDiag(diags, DiagnosticCodes.AssetNotFound),
            $"Expected UITKX0120. Got: [{string.Join(", ", diags.Select(d => d.Code))}]");
    }

    [Fact]
    public void UITKX0120_UssDirective_MissingFile_ReportsError()
    {
        var source = "@uss \"./Card.uss\"\nVirtualNode Card() {\n  return (\n    <Label/>\n  );\n}";
        var diags = Analyze(source, "Assets/UI/Card.uitkx");
        Assert.True(HasDiag(diags, DiagnosticCodes.AssetNotFound));
    }

    [Fact]
    public void UITKX0120_AstCall_MissingFile_ReportsError()
    {
        var source = "VirtualNode Card() {\n  return (\n    <Label text={Ast<Sprite>(\"./icon.png\").name} />\n  );\n}";
        var diags = Analyze(source, "Assets/UI/Card.uitkx");
        Assert.True(HasDiag(diags, DiagnosticCodes.AssetNotFound));
    }

    [Fact]
    public void UITKX0120_DiagnosticIsSeverityError()
    {
        var source = "VirtualNode Card() {\n  return (\n    <Label text={Asset<Texture2D>(\"./missing.png\").name} />\n  );\n}";
        var diags = Analyze(source, "Assets/UI/Card.uitkx");
        var assetDiag = diags.FirstOrDefault(d => d.Code == DiagnosticCodes.AssetNotFound);
        Assert.NotNull(assetDiag);
        Assert.Equal(ParseSeverity.Error, assetDiag.Severity);
    }

    [Fact]
    public void UITKX0120_NoAssetCalls_NoDiagnostic()
    {
        var source = "VirtualNode Card() {\n  return (\n    <Label text=\"hello\" />\n  );\n}";
        var diags = Analyze(source, "Assets/UI/Card.uitkx");
        Assert.False(HasDiag(diags, DiagnosticCodes.AssetNotFound));
    }

    // ── U-10: hook-rules scanning must not false-positive on comments/strings/
    //          identifier-boundary matches ───────────────────────────────────

    [Fact]
    public void UITKX0013_CommentMentioningHook_NoFalsePositive()
    {
        var source =
            "VirtualNode Foo() {\n"
            + "    var (n, setN) = useState(0);\n"
            + "    return (\n"
            + "        <Box>\n"
            + "            @if (n > 0) {\n"
            + "                // TODO: maybe useState(1) here later\n"
            + "                return (<Label text=\"pos\" />);\n"
            + "            }\n"
            + "        </Box>\n"
            + "    );\n"
            + "}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.HookInConditional));
    }

    [Fact]
    public void UITKX0013_UnderscorePrefixedIdentifier_NoFalsePositive()
    {
        var source =
            "VirtualNode Foo() {\n"
            + "    var (n, setN) = useState(0);\n"
            + "    return (\n"
            + "        <Box>\n"
            + "            @if (n > 0) {\n"
            + "                var z = _useState(1);\n"
            + "                return (<Label text=\"pos\" />);\n"
            + "            }\n"
            + "        </Box>\n"
            + "    );\n"
            + "}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.HookInConditional));
    }

    [Fact]
    public void UITKX0013_HookMentionedInsideStringLiteral_NoFalsePositive()
    {
        var source =
            "VirtualNode Foo() {\n"
            + "    var (n, setN) = useState(0);\n"
            + "    return (\n"
            + "        <Box>\n"
            + "            @if (n > 0) {\n"
            + "                var label = \"useState(x) is a hook\";\n"
            + "                return (<Label text={label} />);\n"
            + "            }\n"
            + "        </Box>\n"
            + "    );\n"
            + "}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.HookInConditional));
    }

    [Fact]
    public void UITKX0013_RealHookCallInsideIf_StillErrors()
    {
        var source =
            "VirtualNode Foo() {\n"
            + "    var flag = true;\n"
            + "    return (\n"
            + "        <Box>\n"
            + "            @if (flag) {\n"
            + "                var (n, setN) = useState(0);\n"
            + "                return (<Label text=\"pos\" />);\n"
            + "            }\n"
            + "        </Box>\n"
            + "    );\n"
            + "}";
        var diags = Analyze(source);
        Assert.True(HasDiag(diags, DiagnosticCodes.HookInConditional));
    }

    [Fact]
    public void UITKX0016_MemberAccessNamedUseState_NoFalsePositive()
    {
        var source =
            "VirtualNode Foo() {\n"
            + "    return (\n"
            + "        <Button onClick={() => obj.useState(1)} />\n"
            + "    );\n"
            + "}";
        var diags = Analyze(source);
        Assert.False(HasDiag(diags, DiagnosticCodes.HookInEventHandler));
    }
}
