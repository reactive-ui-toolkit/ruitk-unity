using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ruitk.Language;
using Ruitk.Language.Parser;
using Ruitk.Language.Roslyn;
using UitkxLanguageServer;
using Xunit;

namespace UitkxLanguageServer.Tests
{
    /// <summary>
    /// ES-modules campaign (Plans~/archive/ES_MODULES_EXECUTION_PLAN.md M5): the LSP surface for
    /// plain-declaration files — virtual-document __Exports scaffolding (the shape ALL editor
    /// C# diagnostics ride on), import-brace completion over member exports, and the
    /// bridge/alias handling.
    /// </summary>
    public sealed class EsModulesLspTests : IDisposable
    {
        private readonly string _root;
        private readonly string _uiDir;

        public EsModulesLspTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "uitkx-esm-lsp-" + Guid.NewGuid().ToString("N"));
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

        private static (ParseResult Result, string Source) Parse(string path)
        {
            string src = File.ReadAllText(path);
            var diags = new List<ParseDiagnostic>();
            var ds = DirectiveParser.Parse(src, path, diags);
            var nodes = UitkxParser.Parse(src, path, ds, diags);
            var roots = Ruitk.Language.Lowering.CanonicalLowering.LowerToRenderRoots(ds, nodes, path);
            return (new ParseResult(ds, roots, System.Collections.Immutable.ImmutableArray.CreateRange(diags)), src);
        }

        private static VirtualDocument GenerateVdoc(string path)
        {
            var (pr, src) = Parse(path);
            return new VirtualDocumentGenerator().Generate(pr, src, path);
        }

        // ── Virtual document: __Exports scaffolding ──────────────────────────

        [Fact]
        public void Vdoc_MemberOnlyFile_ScaffoldsExportsContainer()
        {
            string p = F("Tokens.uitkx",
                "export int Gap = 8;\n" +
                "export string FormatScore(int s) { return $\"{s}\"; }\n" +
                "int hidden = 1;\n");
            var vdoc = GenerateVdoc(p);

            Assert.Contains("static partial class __Exports", vdoc.Text);
            Assert.Contains("public static int Gap = ", vdoc.Text);
            Assert.Contains("public static string FormatScore(int s)", vdoc.Text);
            Assert.Contains("internal static int hidden = ", vdoc.Text);
        }

        [Fact]
        public void Vdoc_MixedFile_HasExportsAndComponentClass_AndOwnUsing()
        {
            string p = F("Panel.uitkx",
                "export int Gap = 8;\n" +
                "export VirtualNode Panel(string title) {\n  return (<VisualElement />);\n}\n");
            var vdoc = GenerateVdoc(p);

            Assert.Contains("static partial class __Exports", vdoc.Text);
            Assert.Contains("partial class Panel", vdoc.Text);
            // The component body must see file members bare-name.
            Assert.Contains(".__Exports;", vdoc.Text);
        }

        [Fact]
        public void Vdoc_MemberBodies_AreSourceMapped()
        {
            string p = F("Scoring.uitkx",
                "export string FormatScore(int s) { return $\"Score {s}\"; }\n");
            var vdoc = GenerateVdoc(p);

            // The body text is a MAPPED region (IntelliSense/diagnostics anchor there).
            Assert.Contains(vdoc.Map.Entries, e =>
                e.Kind == SourceRegionKind.ModuleBody);
            Assert.Contains("return $\"Score {s}\";", vdoc.Text);
        }

        [Fact]
        public void Vdoc_MemberDeclNames_AreSourceMapped()
        {
            // The declared NAME is user-authored source, not scaffold: cursor-on-name
            // must resolve through the source map so semantic tokens / definition /
            // references / rename go through Roslyn instead of the text/TextMate
            // heuristics (field repro: first-export navigation and case-dependent
            // coloring of names in .style.uitkx companions).
            string src =
                "export Style containerStyle = new Style { };\n" +
                "\n" +
                "export Style ButtonStyles = new Style { };\n" +
                "\n" +
                "export int clamp(int v) { return v; }\n" +
                "\n" +
                "export (int value, System.Action reset) useCountdown(int start) {\n" +
                "  return (start, null);\n" +
                "}\n";
            string p = F("Board.style.uitkx", src);
            var vdoc = GenerateVdoc(p);

            foreach (var name in new[] { "containerStyle", "ButtonStyles", "clamp", "useCountdown" })
            {
                int nameOffset = src.IndexOf(name, StringComparison.Ordinal);
                var mapped = vdoc.Map.ToVirtualOffset(nameOffset);
                Assert.True(mapped.HasValue, $"{name}: decl name is not source-mapped");
                Assert.Equal(name, vdoc.Text.Substring(mapped!.Value.VirtualOffset, name.Length));
            }
        }

        [Fact]
        public void Vdoc_HookMember_MapsAsHookBody()
        {
            string p = F("Countdown.uitkx",
                "export (int value, System.Action reset) useCountdown(int start) {\n  return (start, null);\n}\n");
            var vdoc = GenerateVdoc(p);

            Assert.Contains(vdoc.Map.Entries, e => e.Kind == SourceRegionKind.HookBody);
            Assert.Contains("public static (int value, System.Action reset) useCountdown(int start)", vdoc.Text);
        }

        [Fact]
        public void Vdoc_AliasedMemberImport_ScaffoldsBridgeStub()
        {
            F("Scoring.uitkx", "export string FormatScore(int s) { return \"\"; }\n");
            string p = F("Home.uitkx",
                "import { FormatScore as fmt } from \"./Scoring\"\n" +
                "export VirtualNode Home() {\n  return (<VisualElement />);\n}\n");
            var vdoc = GenerateVdoc(p);

            Assert.Contains("internal static string fmt(int s) => default!;", vdoc.Text);
        }

        [Fact]
        public void Vdoc_LegacyFile_Unchanged_NoExportsContainer()
        {
            string p = F("Legacy.uitkx",
                "VirtualNode Legacy() {\n  return (<VisualElement />);\n}\n");
            var vdoc = GenerateVdoc(p);

            Assert.DoesNotContain("__Exports", vdoc.Text);
            Assert.Contains("partial class Legacy", vdoc.Text);
        }

        // ── Import-brace completion over member exports ──────────────────────

        [Fact]
        public void ImportBraceCompletion_IncludesMemberExports_ExcludesPrivate()
        {
            F("Tokens.uitkx",
                "export int Gap = 8;\n" +
                "export (int v, System.Action r) useThing(int s) { return (s, null); }\n" +
                "int hidden = 1;\n");
            string importer = F("Screen.uitkx", "import {  } from \"./Tokens\"\n");

            var names = CompletionHandler.GetImportBraceCompletions(
                importer, "import {  } from \"./Tokens\"", 8);

            Assert.Contains("Gap", names);
            Assert.Contains("useThing", names);
            Assert.DoesNotContain("hidden", names);
        }

        // ── ImportScopeFacts bridge surface (feeds the VDG stubs) ────────────

        [Fact]
        public void MemberBridges_ValueAndMethod_CarrySignatures()
        {
            F("Lib.uitkx",
                "export int Gap = 8;\n" +
                "export string FormatScore(int s) { return \"\"; }\n");
            string importer = F("Home2.uitkx",
                "import { Gap as Spacing, FormatScore as fmt } from \"./Lib\"\n" +
                "export VirtualNode Home2() {\n  return (<VisualElement />);\n}\n");

            var ds = DirectiveParser.Parse(File.ReadAllText(importer), importer, new List<ParseDiagnostic>());
            var bridges = ImportScopeFacts.ComputeImportedMemberBridges(ds, importer);

            Assert.Contains(bridges, b => b.Alias == "Spacing" && b.IsValue && b.ReturnType == "int");
            Assert.Contains(bridges, b => b.Alias == "fmt" && !b.IsValue && b.ReturnType == "string" && b.ParamsText == "int s");
        }

        [Fact]
        public void MemberBridges_DefaultMemberImport_Bridged()
        {
            F("Fmt.uitkx",
                "string FormatIt(int s) { return \"\"; }\n" +
                "export default FormatIt;\n");
            string importer = F("Home3.uitkx",
                "import fmt from \"./Fmt\"\n" +
                "export VirtualNode Home3() {\n  return (<VisualElement />);\n}\n");

            var ds = DirectiveParser.Parse(File.ReadAllText(importer), importer, new List<ParseDiagnostic>());
            var bridges = ImportScopeFacts.ComputeImportedMemberBridges(ds, importer);

            Assert.Contains(bridges, b => b.Alias == "fmt" && !b.IsValue);
        }
    }
}
