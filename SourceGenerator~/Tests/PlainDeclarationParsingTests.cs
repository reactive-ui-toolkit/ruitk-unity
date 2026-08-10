using System.Collections.Generic;
using System.Linq;
using Ruitk.Language;
using Ruitk.Language.Parser;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// ES-modules campaign (Plans~/archive/ES_MODULES_EXECUTION_PLAN.md M1, U-04): plain
    /// (wrapper-keyword-free) top-level declarations classify from the signature alone
    /// (G-03), full export surface (default/list), and the mixed-style guard (U-08/2108).
    /// </summary>
    public sealed class PlainDeclarationParsingTests
    {
        private static (DirectiveSet ds, List<ParseDiagnostic> diags) Parse(string src)
        {
            var diags = new List<ParseDiagnostic>();
            var ds = DirectiveParser.Parse(src, "C:/proj/Assets/UI/Foo.uitkx", diags);
            return (ds, diags);
        }

        // ── Classification table ────────────────────────────────────────────

        [Fact]
        public void PlainDecl_VirtualNodeReturn_ClassifiesAsComponent()
        {
            var (ds, diags) = Parse("export VirtualNode ScoreRow(string label) {\n  return ( <Label text={label} /> );\n}\n");
            Assert.Empty(diags);
            Assert.Single(ds.ComponentDeclarations);
            Assert.Equal("ScoreRow", ds.ComponentDeclarations[0].Name);
            Assert.True(ds.ComponentDeclarations[0].IsExported);
            Assert.Empty(ds.MemberDeclarations);
            Assert.False(ds.UsesLegacySyntax);
        }

        [Fact]
        public void PlainDecl_PrivateComponent_NotExported()
        {
            var (ds, diags) = Parse("VirtualNode ScorePanel(string title) {\n  return ( <Label text={title} /> );\n}\n");
            Assert.Empty(diags);
            Assert.Single(ds.ComponentDeclarations);
            Assert.False(ds.ComponentDeclarations[0].IsExported);
        }

        [Fact]
        public void PlainDecl_UsePrefixedName_ClassifiesAsHook()
        {
            var (ds, diags) = Parse("export (int value, Action reset) useCountdown(int start) {\n  var (value, setValue) = useState(start);\n  return (value, null);\n}\n");
            Assert.Empty(diags);
            Assert.Single(ds.MemberDeclarations);
            var m = ds.MemberDeclarations[0];
            Assert.Equal("useCountdown", m.Name);
            Assert.Equal(DeclKind.Hook, m.Kind);
            Assert.Equal("(int value, Action reset)", m.ReturnTypeText);
            Assert.True(m.IsExported);
        }

        [Fact]
        public void PlainDecl_NonUsePrefixedFunction_ClassifiesAsUtil()
        {
            var (ds, diags) = Parse("export string FormatScore(int score) { return $\"Score: {score}\"; }\n");
            Assert.Empty(diags);
            Assert.Single(ds.MemberDeclarations);
            Assert.Equal(DeclKind.Util, ds.MemberDeclarations[0].Kind);
            Assert.Equal("string", ds.MemberDeclarations[0].ReturnTypeText);
        }

        [Fact]
        public void PlainDecl_TypedValue_ClassifiesAsValue()
        {
            var (ds, diags) = Parse("export Style container = new Style { Padding = 10f };\n");
            Assert.Empty(diags);
            Assert.Single(ds.MemberDeclarations);
            var m = ds.MemberDeclarations[0];
            Assert.Equal(DeclKind.Value, m.Kind);
            Assert.Equal("Style", m.ReturnTypeText);
            Assert.Equal("new Style { Padding = 10f }", m.BodyText);
        }

        [Fact]
        public void PlainDecl_InferredValue_WithNewInitializer_NoDiagnostic()
        {
            var (ds, diags) = Parse("export theme = new Style { BackgroundColor = ColorGray };\n");
            Assert.DoesNotContain(diags, d => d.Code == "UITKX2322");
            Assert.Single(ds.MemberDeclarations);
            Assert.Null(ds.MemberDeclarations[0].ReturnTypeText);
        }

        [Fact]
        public void PlainDecl_InferredValue_WithoutNewInitializer_Emits2322()
        {
            var (ds, diags) = Parse("export theme = SomeFactory();\n");
            Assert.Contains(diags, d => d.Code == "UITKX2322");
        }

        [Fact]
        public void PlainDecl_PrivateValue_NoExportKeyword()
        {
            var (ds, diags) = Parse("Style rowStyle = new Style { MarginTop = 2f };\n");
            Assert.Empty(diags);
            Assert.Single(ds.MemberDeclarations);
            Assert.False(ds.MemberDeclarations[0].IsExported);
        }

        [Fact]
        public void PlainDecl_ExpressionBodiedUtil_ParsesBody()
        {
            var (ds, diags) = Parse("export int Double(int x) => x * 2;\n");
            Assert.Empty(diags);
            Assert.Single(ds.MemberDeclarations);
            Assert.True(ds.MemberDeclarations[0].IsExpressionBodied);
            Assert.Equal("x * 2", ds.MemberDeclarations[0].BodyText);
        }

        // ── Cross-guards ─────────────────────────────────────────────────────

        [Fact]
        public void PlainDecl_UsePrefixedVirtualNodeReturn_Emits2321()
        {
            var (ds, diags) = Parse("export VirtualNode useBroken(int x) {\n  return ( <Label text=\"x\" /> );\n}\n");
            Assert.Contains(diags, d => d.Code == "UITKX2321");
        }

        [Fact]
        public void PlainDecl_NonPascalCaseComponent_Emits2100()
        {
            var (ds, diags) = Parse("export VirtualNode scoreRow(string label) {\n  return ( <Label text={label} /> );\n}\n");
            Assert.Contains(diags, d => d.Code == "UITKX2100");
        }

        // ── export default / export { … } ───────────────────────────────────

        [Fact]
        public void ExportDefault_ValidTarget_SetsDefaultExportName()
        {
            var (ds, diags) = Parse(
                "VirtualNode ScorePanel(string title) {\n  return ( <Label text={title} /> );\n}\n" +
                "export default ScorePanel;\n");
            Assert.Empty(diags);
            Assert.Equal("ScorePanel", ds.DefaultExportName);
        }

        [Fact]
        public void ExportDefault_DuplicateDefault_Emits2327()
        {
            var (ds, diags) = Parse(
                "VirtualNode A() { return ( <Spacer /> ); }\n" +
                "VirtualNode B() { return ( <Spacer /> ); }\n" +
                "export default A;\n" +
                "export default B;\n");
            Assert.Contains(diags, d => d.Code == "UITKX2327");
        }

        [Fact]
        public void ExportDefault_UnknownName_Emits2323()
        {
            var (ds, diags) = Parse(
                "VirtualNode A() { return ( <Spacer /> ); }\n" +
                "export default NotDeclared;\n");
            Assert.Contains(diags, d => d.Code == "UITKX2323");
        }

        [Fact]
        public void ExportList_MarksMatchingDeclarationsExported()
        {
            var (ds, diags) = Parse(
                "int MaxItems = 5;\n" +
                "string Label = \"hi\";\n" +
                "export { MaxItems, Label };\n");
            Assert.Empty(diags);
            Assert.True(ds.MemberDeclarations.Single(m => m.Name == "MaxItems").IsExported);
            Assert.True(ds.MemberDeclarations.Single(m => m.Name == "Label").IsExported);
        }

        [Fact]
        public void ExportList_UnknownName_Emits2323()
        {
            var (ds, diags) = Parse(
                "int MaxItems = 5;\n" +
                "export { MaxItems, NotDeclared };\n");
            Assert.Contains(diags, d => d.Code == "UITKX2323");
        }

        [Fact]
        public void ExportList_DuplicateName_Emits2324()
        {
            var (ds, diags) = Parse(
                "int MaxItems = 5;\n" +
                "export { MaxItems, MaxItems };\n");
            Assert.Contains(diags, d => d.Code == "UITKX2324");
        }

        // ── Mixed-style guard (U-08 / Unity-local 2108) ─────────────────────

        [Fact]
        public void MixedFile_PlainFirst_ThenWrapperKeyword_IsAnError()
        {
            // 0.16.0: a wrapper keyword anywhere is an error (2108 mixed-mode detection
            // still anchors it inside a plain file; retires to the 2320 removal error
            // with the phase-4 parser deletions).
            var (ds, diags) = Parse(
                "int MaxItems = 5;\n" +
                "compo" + "nent Foo {\n  return ( <Spacer /> );\n}\n");
            Assert.Contains(diags, d =>
                d.Severity == ParseSeverity.Error
                && (d.Code == "UITKX2108" || d.Code == "UITKX2320"));
            Assert.False(ds.UsesLegacySyntax);
        }

        // ── The general plan §3 fixture (normative — must parse clean end to end) ──

        [Fact]
        public void GeneralPlanFixture_ParsesAllDeclarationsWithNoDiagnostics()
        {
            const string src =
                "import { FormatTime } from \"../Shared/TimeUtils\"\n" +
                "\n" +
                "export Style container = new Style { Padding = 10f };\n" +
                "export int MaxItems = 5;\n" +
                "export theme = new Style { BackgroundColor = ColorGray };\n" +
                "\n" +
                "export string FormatScore(int score) { return $\"Score: {score}\"; }\n" +
                "\n" +
                "export (int value, Action reset) useCountdown(int start) {\n" +
                "  var (value, setValue) = useState(start);\n" +
                "  Action reset = () => setValue(start);\n" +
                "  return (value, reset);\n" +
                "}\n" +
                "\n" +
                "Style rowStyle = new Style { MarginTop = 2f };\n" +
                "\n" +
                "export VirtualNode ScoreRow(string label) {\n" +
                "  return ( <Label text={label} style={rowStyle} /> );\n" +
                "}\n" +
                "\n" +
                "VirtualNode ScorePanel(string title) {\n" +
                "  var (count, reset) = useCountdown(MaxItems);\n" +
                "  return (\n" +
                "    <VisualElement style={container}>\n" +
                "      <Label text={FormatScore(count)} />\n" +
                "      <ScoreRow label={FormatTime()} />\n" +
                "    </VisualElement>\n" +
                "  );\n" +
                "}\n" +
                "\n" +
                "export default ScorePanel;\n";

            var (ds, diags) = Parse(src);
            Assert.Empty(diags);
            Assert.False(ds.UsesLegacySyntax);
            Assert.Equal("ScorePanel", ds.DefaultExportName);
            Assert.Single(ds.Imports);

            Assert.Equal(new[] { "container", "MaxItems", "theme", "FormatScore", "useCountdown", "rowStyle" },
                ds.MemberDeclarations.Select(m => m.Name).ToArray());
            Assert.Equal(new[] { DeclKind.Value, DeclKind.Value, DeclKind.Value, DeclKind.Util, DeclKind.Hook, DeclKind.Value },
                ds.MemberDeclarations.Select(m => m.Kind).ToArray());

            Assert.Equal(new[] { "ScoreRow", "ScorePanel" }, ds.ComponentDeclarations.Select(c => c.Name).ToArray());
            Assert.True(ds.ComponentDeclarations.Single(c => c.Name == "ScoreRow").IsExported);
            // `export default ScorePanel;` marks ScorePanel exported (a default export IS an export).
            Assert.True(ds.ComponentDeclarations.Single(c => c.Name == "ScorePanel").IsExported);
        }

        // ── Empty file (U-08: no declarations at all ⇒ new mode, no error) ──

        [Fact]
        public void EmptyFile_KeepsBaselineDiagnostics_NotAnEmptyNewModeModule()
        {
            // Audit F3: committing an empty/imports-only file as a zero-declaration new-mode
            // set let the formatter fabricate `VirtualNode Component() { … }` out of nothing.
            // An empty file falls back to the legacy dispatch and its baseline 2105.
            var (ds, diags) = Parse("");
            Assert.Contains(diags, d => d.Code == "UITKX2105");
            Assert.Empty(ds.MemberDeclarations.IsDefault
                ? System.Collections.Immutable.ImmutableArray<Ruitk.Language.Parser.MemberDeclaration>.Empty
                : ds.MemberDeclarations);
        }

        [Fact]
        public void ImportsOnlyFile_KeepsBaselineDiagnostics()
        {
            var (ds, diags) = Parse("import { a } from \"./x\"\n");
            Assert.Contains(diags, d => d.Code == "UITKX2105");
        }

        // ── Full ES import surface (G-05) ───────────────────────────────────

        [Fact]
        public void Import_RenameOnImport_PopulatesAlias()
        {
            var (ds, diags) = Parse("import { a as b } from \"./x\"\nexport int Y = 1;\n");
            Assert.Empty(diags);
            Assert.Single(ds.Imports);
            Assert.Equal(new[] { "a" }, ds.Imports[0].Names.ToArray());
            Assert.Equal(new string?[] { "b" }, ds.Imports[0].Aliases.ToArray());
        }

        [Fact]
        public void Import_StarAs_PopulatesStarAlias()
        {
            var (ds, diags) = Parse("import * as Shapes from \"./Shapes\"\nexport int Y = 1;\n");
            Assert.Empty(diags);
            Assert.Single(ds.Imports);
            Assert.True(ds.Imports[0].IsStar);
            Assert.Equal("Shapes", ds.Imports[0].StarAlias);
        }

        [Fact]
        public void Import_Default_PopulatesDefaultAlias()
        {
            var (ds, diags) = Parse("import ScorePanel from \"./ScorePanel\"\nexport int Y = 1;\n");
            Assert.Empty(diags);
            Assert.Single(ds.Imports);
            Assert.True(ds.Imports[0].IsDefault);
            Assert.Equal("ScorePanel", ds.Imports[0].DefaultAlias);
        }

        [Fact]
        public void Import_DuplicateBoundAlias_Emits2325()
        {
            // Family §3.1 (audit PC-5): an alias colliding with another import binding is
            // UITKX2325's "another import" arm; plain name-vs-name duplicates stay 2303.
            var (ds, diags) = Parse(
                "import { a as b } from \"./x\"\n" +
                "import { c as b } from \"./y\"\n" +
                "export int Y = 1;\n");
            Assert.Contains(diags, d => d.Code == "UITKX2325"
                && d.Message.Contains("collides with another import"));
        }

        [Fact]
        public void Import_DuplicatePlainName_StillEmits2303()
        {
            var (ds, diags) = Parse(
                "import { a } from \"./x\"\n" +
                "import { a } from \"./y\"\n" +
                "export int Y = 1;\n");
            Assert.Contains(diags, d => d.Code == "UITKX2303");
        }
    }
}
