using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Ruitk.Language;
using Ruitk.Language.Parser;
using UitkxLanguageServer;
using Xunit;

namespace UitkxLanguageServer.Tests
{
    /// <summary>
    /// ES-modules LSP audit fixes (Plans~/archive/ES_MODULES_AUDIT_FINDINGS.md): declaration-head
    /// matching requires a type token (B1/B2/B6), import-list rename applies a single
    /// origin-decided interpretation of the renamed identifier (B3, PC-10), the workspace
    /// index ignores comment-shaped declarations (B5), the import-brace completion parses
    /// aliased entries (B7), and UITKX2107 surfaces live (PC-7).
    /// </summary>
    public sealed class EsModulesAuditFixTests : IDisposable
    {
        private readonly string _root;
        private readonly string _uiDir;

        public EsModulesAuditFixTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "uitkx-esm-audit-" + Guid.NewGuid().ToString("N"));
            _uiDir = Path.Combine(_root, "Assets", "UI");
            Directory.CreateDirectory(_uiDir);
            File.WriteAllText(Path.Combine(_uiDir, "Asm.asmdef"), "{ \"name\": \"Game.UI\" }");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string F(string name, string content)
        {
            string p = Path.Combine(_uiDir, name);
            File.WriteAllText(p, content);
            return p;
        }

        private static DirectiveSet Parse(string text, string path)
            => DirectiveParser.Parse(text, path, new List<ParseDiagnostic>());

        // ── B1: import-name navigation must land on the DECLARATION, not a call ──

        private const string HooksContent =
            "export VirtualNode Ticker() {\n" +
            "  useCounter(0);\n" +
            "  if (true) { }\n" +
            "  var c = 1;\n" +
            "  return (<VisualElement />);\n" +
            "}\n" +
            "export (int count, System.Action inc) useCounter(int start) {\n" +
            "  return (start, null);\n" +
            "}\n" +
            "export int c = 5;\n";

        [Fact]
        public void ImportedHookName_ResolvesToDeclaration_NotCallSiteAboveIt()
        {
            F("Hooks.uitkx", HooksContent);
            string importer = Path.Combine(_uiDir, "Screen.uitkx");
            string importLine = "import { useCounter, c } from \"./Hooks\"";
            File.WriteAllText(importer, importLine + "\n");

            var imports = ImmutableArray.Create(new ImportDeclaration(
                ImmutableArray.Create("useCounter", "c"),
                "./Hooks",
                1, 0,
                ImmutableArray.Create(
                    importLine.IndexOf("useCounter", StringComparison.Ordinal),
                    importLine.IndexOf(" c ", StringComparison.Ordinal) + 1)));

            bool handled = DefinitionHandler.TryResolveImportNavigation(
                importer, imports, line1: 1,
                col0: importLine.IndexOf("useCounter", StringComparison.Ordinal),
                out var file, out var line);

            Assert.True(handled);
            Assert.EndsWith("Hooks.uitkx", file!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(7, line);
        }

        [Fact]
        public void ImportedValueName_ResolvesToDeclaration_NotVarStatement()
        {
            F("Hooks.uitkx", HooksContent);
            string importer = Path.Combine(_uiDir, "Screen.uitkx");
            string importLine = "import { useCounter, c } from \"./Hooks\"";
            File.WriteAllText(importer, importLine + "\n");

            int cCol = importLine.IndexOf(" c ", StringComparison.Ordinal) + 1;
            var imports = ImmutableArray.Create(new ImportDeclaration(
                ImmutableArray.Create("useCounter", "c"),
                "./Hooks",
                1, 0,
                ImmutableArray.Create(
                    importLine.IndexOf("useCounter", StringComparison.Ordinal), cCol)));

            bool handled = DefinitionHandler.TryResolveImportNavigation(
                importer, imports, line1: 1, col0: cCol, out var file, out var line);

            Assert.True(handled);
            Assert.EndsWith("Hooks.uitkx", file!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(10, line);
        }

        [Fact]
        public void FindDeclarationInUitkx_SkipsCallAboveDeclaration()
        {
            var (line, col) = DefinitionHandler.FindDeclarationInUitkx(HooksContent, "useCounter");

            Assert.Equal(7, line);
            Assert.Equal(38, col);
        }

        // ── B2: hook rename must not touch comment/string occurrences ───────────

        [Fact]
        public void HookRename_SkipsBlockCommentAndVerbatimString()
        {
            string text =
                "/*\n" +
                "export (int value) useCounter(int start) {\n" +
                "*/\n" +
                "var s = @\"\n" +
                "export (int value) useCounter(int start) {\n" +
                "  useCounter(9);\n" +
                "\";\n" +
                "export (int count, System.Action inc) useCounter(int start) {\n" +
                "  return (start, null);\n" +
                "}\n" +
                "useCounter(3);\n";

            var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(_uiDir, "Hooks.uitkx"));
            RenameHandler.CollectHookRenameEditsInText(text, uri, "useCounter", "useTicker", changes);

            var edits = ((List<TextEdit>)changes[uri]).OrderBy(e => e.Range.Start.Line).ToList();
            Assert.Equal(2, edits.Count);
            Assert.Equal(7, (int)edits[0].Range.Start.Line);
            Assert.Equal(10, (int)edits[1].Range.Start.Line);
        }

        [Fact]
        public void HookRename_IndentedStatementLine_DoesNotMatchAsDeclaration()
        {
            string text =
                "export VirtualNode Panel() {\n" +
                "  useCounter(0);\n" +
                "  return (<VisualElement />);\n" +
                "}\n";

            var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(_uiDir, "Panel.uitkx"));
            RenameHandler.CollectHookRenameEditsInText(text, uri, "useCounter", "useTicker", changes);

            // Exactly the CALL-site edit — no phantom declaration edit for the same token.
            var edits = (List<TextEdit>)changes[uri];
            Assert.Single(edits);
            Assert.Equal(1, (int)edits[0].Range.Start.Line);
        }

        // ── B3 / PC-10: import-list rename — origin decides the interpretation ──

        private const string AliasImportLine = "import { a as b, c as a } from \"./Lib\"";

        // The directive parser only records imports for files carrying a declaration,
        // so every importer fixture ends with a minimal component.
        private const string MinimalComponent =
            "export VirtualNode App() {\n  return (<VisualElement />);\n}\n";

        private static DirectiveSet AliasImporterDirectives(string path)
            => Parse(AliasImportLine + "\n" + MinimalComponent, path);

        [Fact]
        public void ClassifyOrigin_AliasStarAndDefaultBindings_AreLocal()
        {
            string p = Path.Combine(_uiDir, "A.uitkx");
            Assert.Equal(RenameHandler.ImportRenameOrigin.LocalBinding,
                RenameHandler.ClassifyImportRenameOrigin(AliasImporterDirectives(p), "b"));
            Assert.Equal(RenameHandler.ImportRenameOrigin.ExportName,
                RenameHandler.ClassifyImportRenameOrigin(AliasImporterDirectives(p), "c"));

            var star = Parse("import * as X from \"./Lib\"\n" + MinimalComponent, p);
            Assert.Equal(RenameHandler.ImportRenameOrigin.LocalBinding,
                RenameHandler.ClassifyImportRenameOrigin(star, "X"));

            var def = Parse("import fmt from \"./Fmt\"\n" + MinimalComponent, p);
            Assert.Equal(RenameHandler.ImportRenameOrigin.LocalBinding,
                RenameHandler.ClassifyImportRenameOrigin(def, "fmt"));
        }

        [Fact]
        public void ClassifyOrigin_AliasShadowsSameNamedImport()
        {
            // `import { a as b, c as a }`: in THIS file `a` is the local binding of export
            // `c` — a rename triggered here targets that binding, not the export `a`.
            string p = Path.Combine(_uiDir, "A.uitkx");
            Assert.Equal(RenameHandler.ImportRenameOrigin.LocalBinding,
                RenameHandler.ClassifyImportRenameOrigin(AliasImporterDirectives(p), "a"));
        }

        [Fact]
        public void ExportRename_TouchesImportedNameToken_NeverAliasToken()
        {
            string p = Path.Combine(_uiDir, "A.uitkx");
            var edits = RenameHandler.ComputeImportListEdits(
                AliasImporterDirectives(p), (AliasImportLine + "\n").Split('\n'),
                "a", "a2", RenameHandler.ImportRenameOrigin.ExportName);

            Assert.Single(edits);
            Assert.Equal(AliasImportLine.IndexOf("a as b", StringComparison.Ordinal),
                (int)edits[0].Range.Start.Character);
        }

        [Fact]
        public void LocalBindingRename_TouchesAliasToken_NeverImportedNameToken()
        {
            string p = Path.Combine(_uiDir, "A.uitkx");
            var edits = RenameHandler.ComputeImportListEdits(
                AliasImporterDirectives(p), (AliasImportLine + "\n").Split('\n'),
                "a", "a2", RenameHandler.ImportRenameOrigin.LocalBinding);

            Assert.Single(edits);
            Assert.Equal(AliasImportLine.IndexOf("c as a", StringComparison.Ordinal) + 5,
                (int)edits[0].Range.Start.Character);
        }

        [Fact]
        public void StarAliasRename_EditsBindingToken_NotSpecifier()
        {
            string line = "import * as Tok from \"./Tok\"";
            string p = Path.Combine(_uiDir, "B.uitkx");
            var edits = RenameHandler.ComputeImportListEdits(
                Parse(line + "\n" + MinimalComponent, p), (line + "\n").Split('\n'),
                "Tok", "Tokens", RenameHandler.ImportRenameOrigin.LocalBinding);

            Assert.Single(edits);
            Assert.Equal(line.IndexOf("Tok", StringComparison.Ordinal),
                (int)edits[0].Range.Start.Character);
        }

        [Fact]
        public void DefaultAliasRename_EditsBindingToken_NotSpecifier()
        {
            string line = "import fmt from \"./fmt\"";
            string p = Path.Combine(_uiDir, "C.uitkx");
            var edits = RenameHandler.ComputeImportListEdits(
                Parse(line + "\n" + MinimalComponent, p), (line + "\n").Split('\n'),
                "fmt", "formatter", RenameHandler.ImportRenameOrigin.LocalBinding);

            Assert.Single(edits);
            Assert.Equal(line.IndexOf("fmt", StringComparison.Ordinal),
                (int)edits[0].Range.Start.Character);
        }

        [Fact]
        public void LocalBindingRename_TouchesOnlyTheInvokingFile()
        {
            // Two files alias-bind the same local name X; the rename was triggered in
            // Star1.uitkx, so Star2.uitkx must stay untouched even though it already
            // participates in the rename (simulated by its pre-existing changes entry).
            F("Lib.uitkx", "export int Gap = 8;\n");
            string invoking = F("Star1.uitkx", "import * as X from \"./Lib\"\n" + MinimalComponent);
            string other = F("Star2.uitkx", "import * as X from \"./Lib\"\n" + MinimalComponent);

            var otherUri = DocumentUri.FromFileSystemPath(other);
            var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
            {
                [otherUri] = new List<TextEdit>(),
            };

            var handler = new RenameHandler(new DocumentStore(), new WorkspaceIndex(), null!, null!);
            handler.CollectImportListRenameEdits("X", "Tokens", changes, invoking);

            Assert.Empty((List<TextEdit>)changes[otherUri]);
            var invokingUri = changes.Keys.Single(u =>
                u.GetFileSystemPath().EndsWith("Star1.uitkx", StringComparison.OrdinalIgnoreCase));
            var edits = Assert.IsType<List<TextEdit>>(changes[invokingUri]);
            Assert.Single(edits);
        }

        [Fact]
        public void ExportRename_MergesIntoExistingListInPlace()
        {
            // B4: the merge must keep the "changes values are List<TextEdit>" invariant.
            F("Lib.uitkx", "export int Gap = 8;\n");
            string importer = F("User.uitkx", "import { Gap } from \"./Lib\"\nexport int pad = Gap;\n");

            var importerUri = DocumentUri.FromFileSystemPath(importer);
            var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
            {
                [importerUri] = new List<TextEdit>
                {
                    new TextEdit
                    {
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(1, 17), new Position(1, 20)),
                        NewText = "Gap2",
                    },
                },
            };

            var handler = new RenameHandler(new DocumentStore(), new WorkspaceIndex(), null!, null!);
            handler.CollectImportListRenameEdits("Gap", "Gap2", changes, importer);

            var edits = Assert.IsType<List<TextEdit>>(changes[importerUri]);
            Assert.Equal(2, edits.Count);
            Assert.Contains(edits, e => e.Range.Start.Line == 0);
        }

        // ── B6: component declaration edit requires the declaration shape ───────

        [Fact]
        public void ComponentDeclRename_IgnoresIndentedLocalAndParenlessLine()
        {
            string text =
                "VirtualNode Other() {\n" +
                "  VirtualNode Foo = MakeIt();\n" +
                "  return (<VisualElement />);\n" +
                "}\n" +
                "VirtualNode Foo = MakeIt();\n";

            var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();
            RenameHandler.RenameComponentDeclarationInFile(
                Path.Combine(_uiDir, "Other.uitkx"), text, "Foo", "Bar", changes);

            Assert.Empty(changes);
        }

        [Fact]
        public void ComponentDeclRename_MatchesPlainAndLegacyHeads()
        {
            var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();
            RenameHandler.RenameComponentDeclarationInFile(
                Path.Combine(_uiDir, "Foo.uitkx"),
                "export VirtualNode Foo(string title) {\n  return (<VisualElement />);\n}\n",
                "Foo", "Bar", changes);
            Assert.Single(changes);

            changes.Clear();
            RenameHandler.RenameComponentDeclarationInFile(
                Path.Combine(_uiDir, "Legacy.uitkx"),
                "VirtualNode Foo() {\n  return (<VisualElement />);\n}\n",
                "Foo", "Bar", changes);
            Assert.Single(changes);
        }

        // ── B5: workspace index ignores declaration shapes inside block comments ─

        [Fact]
        public void Index_BlockCommentedDeclaration_DoesNotCreatePhantomElement()
        {
            string p = F("Real.uitkx",
                "/*\n" +
                "VirtualNode Phantom() {\n" +
                "export VirtualNode Ghost() {\n" +
                "*/\n" +
                "export VirtualNode Real() {\n" +
                "  return (<VisualElement />);\n" +
                "}\n");

            var index = new WorkspaceIndex();
            index.Refresh(p);

            Assert.Contains("Real", index.KnownElements);
            Assert.DoesNotContain("Phantom", index.KnownElements);
            Assert.DoesNotContain("Ghost", index.KnownElements);
        }

        // ── B7: import-brace completion parses `a as b` entries ─────────────────

        [Fact]
        public void ImportBraceCompletion_AliasedEntry_ExcludesNameAndAlias()
        {
            F("Tokens.uitkx",
                "export int Gap = 8;\n" +
                "export int Pad = 4;\n" +
                "export int g = 1;\n");
            string importer = Path.Combine(_uiDir, "Screen.uitkx");
            string line = "import { Gap as g } from \"./Tokens\"";

            var names = CompletionHandler.GetImportBraceCompletions(
                importer, line, line.IndexOf('}'));

            Assert.DoesNotContain("Gap", names);
            Assert.DoesNotContain("g", names);
            Assert.Contains("Pad", names);
        }

        [Fact]
        public void ImportBraceCompletion_CombinedDefaultForm_SuggestsRemainingExports()
        {
            F("SomeOtherName.utils.uitkx",
                "export int something = 42;\n" +
                "\n" +
                "export int getSomething() {\n" +
                "  return something;\n" +
                "}\n" +
                "\n" +
                "bool isSomethingEven() {\n" +
                "  return something % 2 == 0;\n" +
                "}\n" +
                "\n" +
                "export default isSomethingEven;\n");
            string importer = Path.Combine(_uiDir, "Screen.uitkx");
            string line = "import isSomethingEven, {} from \"./SomeOtherName.utils\"";

            var names = CompletionHandler.GetImportBraceCompletions(
                importer, line, line.IndexOf('}'));

            Assert.Contains("getSomething", names);
            Assert.Contains("something", names);
            Assert.DoesNotContain("isSomethingEven", names);
        }

        // (The PC-7 UITKX2107 companion-merge facts were removed with companion
        // partial-class merging — 0.16.0 legacy removal.)
    }
}
