using System.Collections.Generic;
using System.Linq;
using Ruitk.Language;
using Ruitk.Language.Parser;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// Plan §5 — the parser populates the import/export model (DirectiveSet.Imports,
    /// ComponentDeclarations[].IsExported, HookDeclarations/ModuleDeclarations[].IsExported) and
    /// emits the frozen scan diagnostics 2303/2309. Additive: single-decl behavior is unchanged.
    /// </summary>
    public sealed class ImportExportParsingTests
    {
        private static (DirectiveSet ds, List<ParseDiagnostic> diags) Parse(string src)
        {
            var diags = new List<ParseDiagnostic>();
            var ds = DirectiveParser.Parse(src, "C:/proj/Assets/UI/Foo.uitkx", diags);
            return (ds, diags);
        }

        [Fact]
        public void ImportLine_PopulatesImports()
        {
            var (ds, _) = Parse("import { A, B } from \"./X\"\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Single(ds.Imports);
            Assert.Equal(new[] { "A", "B" }, ds.Imports[0].Names.ToArray());
            Assert.Equal("./X", ds.Imports[0].Specifier);
        }

        [Fact]
        public void ImportLine_CapturesSpecifierColumn()
        {
            // import { A, B } from "./X"
            // 0123456789...        ^ col 21 = opening quote
            var (ds, _) = Parse("import { A, B } from \"./X\"\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            var imp = ds.Imports[0];
            Assert.Equal(21, imp.SpecifierColumn);
            Assert.Equal(new[] { 9, 12 }, imp.NameColumns.ToArray());
            // The specifier span (quotes included) closes exactly at the line's last char.
            Assert.Equal("import { A, B } from \"./X\"".Length, imp.SpecifierColumn + imp.Specifier.Length + 2);
        }

        [Fact]
        public void ImportLine_TrailingSemicolon_Tolerated()
        {
            // The JS-canonical form ends with `;`. The file-import reader must consume the
            // rest of the line (parity with the namespace-import and @using readers) —
            // without it the preamble loop stalls on the `;` and the entire file fails
            // with a misleading UITKX2105 instead of parsing.
            var (ds, diags) = Parse("import { container } from \"./Foo.style\";\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Single(ds.Imports);
            Assert.Equal(new[] { "container" }, ds.Imports[0].Names.ToArray());
            Assert.Equal("./Foo.style", ds.Imports[0].Specifier);
            Assert.Single(ds.ComponentDeclarations);
            Assert.DoesNotContain(diags, d => d.Code == "UITKX2105");
        }

        [Fact]
        public void ImportLine_TrailingSemicolonWithSpaces_Tolerated()
        {
            var (ds, diags) = Parse("import { A } from \"./A\"  ;  \nimport { B } from \"../B\";\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Equal(2, ds.Imports.Length);
            Assert.Equal("./A", ds.Imports[0].Specifier);
            Assert.Equal("../B", ds.Imports[1].Specifier);
            Assert.DoesNotContain(diags, d => d.Code == "UITKX2105");
        }

        [Fact]
        public void NamespaceImport_TrailingSemicolon_StillTolerated()
        {
            // Pin the pre-existing lenience of the namespace-import reader so the three
            // preamble readers can never drift apart on line termination again.
            var (ds, diags) = Parse("import \"@UnityEngine.UIElements\";\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Contains("UnityEngine.UIElements", ds.Usings);
            Assert.Single(ds.ComponentDeclarations);
            Assert.DoesNotContain(diags, d => d.Code == "UITKX2105");
        }

        [Fact]
        public void MultipleImports_MixedSpecifierForms()
        {
            var (ds, _) = Parse(
                "import { A } from \"./A\"\nimport { B } from \"../B\"\nimport { C } from \"~/C\"\n" +
                "VirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Equal(3, ds.Imports.Length);
            Assert.Equal("./A", ds.Imports[0].Specifier);
            Assert.Equal("../B", ds.Imports[1].Specifier);
            Assert.Equal("~/C", ds.Imports[2].Specifier);
        }

        [Fact]
        public void ExportComponent_SetsIsExported()
        {
            var (ds, _) = Parse("export VirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Equal("Foo", ds.ComponentName);
            Assert.Single(ds.ComponentDeclarations);
            Assert.True(ds.ComponentDeclarations[0].IsExported);
            Assert.Equal("Foo", ds.ComponentDeclarations[0].Name);
        }

        [Fact]
        public void PrivateComponent_IsNotExported()
        {
            var (ds, _) = Parse("VirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Single(ds.ComponentDeclarations);
            Assert.False(ds.ComponentDeclarations[0].IsExported);
        }

        [Fact]
        public void ExportHook_SetsIsExported()
        {
            var (ds, _) = Parse("export void useThing() { }\n");
            var m = Assert.Single(ds.MemberDeclarations);
            Assert.Equal(DeclKind.Hook, m.Kind);
            Assert.True(m.IsExported);
        }

        [Fact]
        public void ExportModule_SetsIsExported()
        {
            var (ds, _) = Parse("export module Styles { }\n");
            Assert.Single(ds.ModuleDeclarations);
            Assert.True(ds.ModuleDeclarations[0].IsExported);
        }

        [Fact]
        public void DuplicateImport_Emits2303()
        {
            var (_, diags) = Parse(
                "import { A } from \"./X\"\nimport { A } from \"./Y\"\n" +
                "VirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Contains(diags, d => d.Code == "UITKX2303");
        }

        [Fact]
        public void ImportAfterDeclaration_Emits2309()
        {
            var (_, diags) = Parse(
                "VirtualNode Foo() {\n  return ( <Spacer /> );\n}\nimport { A } from \"./X\"\n");
            Assert.Contains(diags, d => d.Code == "UITKX2309");
        }

        // ── Namespace imports (import "@Ns") + positioned UsingDirectives ────────
        // Namespace-import unification plan: `import "@Ns"` desugars to the same model
        // a `@using` line produces, and both now carry source positions for 2316/2317.

        [Fact]
        public void AtUsing_PopulatesPositionedUsingDirective()
        {
            // @using Ruitk.Router
            // @ col 0, `using` 1-5, space 6, payload `R` col 7
            var (ds, _) = Parse("@using Ruitk.Router\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Contains("Ruitk.Router", ds.Usings);          // back-compat string view intact
            var u = Assert.Single(ds.UsingDirectives);
            Assert.Equal("Ruitk.Router", u.Payload);
            Assert.False(u.FromImportSyntax);
            Assert.Equal(1, u.Line);
            Assert.Equal(0, u.Column);
            Assert.Equal(7, u.PayloadColumn);
        }

        [Fact]
        public void NamespaceImport_DesugarsToUsing()
        {
            // import "@UnityEngine.Audio"
            // `import ` 0-6, `"` col 7, `@` col 8, payload `U` col 9
            var (ds, diags) = Parse("import \"@UnityEngine.Audio\"\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            // Legacy `component` wrapper keyword deprecation warning (ES-modules campaign, G-10)
            // is expected here — this test is about namespace-import desugaring, not that.
            Assert.DoesNotContain(diags, d => d.Code != "UITKX2320");
            Assert.Contains("UnityEngine.Audio", ds.Usings);            // feeds the emitters unchanged
            Assert.Empty(ds.Imports);                                   // NOT a file import
            var u = Assert.Single(ds.UsingDirectives);
            Assert.Equal("UnityEngine.Audio", u.Payload);
            Assert.True(u.FromImportSyntax);
            Assert.Equal(0, u.Column);
            Assert.Equal(9, u.PayloadColumn);
        }

        [Fact]
        public void NamespaceImport_StaticPayload_Preserved()
        {
            var (ds, _) = Parse("import \"@static DoomGame.DoomTypes\"\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Contains("static DoomGame.DoomTypes", ds.Usings);
            Assert.True(Assert.Single(ds.UsingDirectives).FromImportSyntax);
        }

        [Fact]
        public void NamespaceImport_AliasPayload_Preserved()
        {
            var (ds, _) = Parse("import \"@UColor = UnityEngine.Color\"\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Contains("UColor = UnityEngine.Color", ds.Usings);
        }

        [Fact]
        public void NamespaceImport_And_FileImport_And_AtUsing_Coexist()
        {
            var (ds, _) = Parse(
                "import { Chip } from \"./Chip\"\nimport \"@Ruitk.Router\"\n@using UnityEngine\n" +
                "VirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Single(ds.Imports);                                  // file import
            Assert.Equal("./Chip", ds.Imports[0].Specifier);
            Assert.Equal(new[] { "Ruitk.Router", "UnityEngine" }, ds.Usings.ToArray());
            Assert.Equal(2, ds.UsingDirectives.Length);
            Assert.True(ds.UsingDirectives[0].FromImportSyntax);        // the import "@..." one
            Assert.False(ds.UsingDirectives[1].FromImportSyntax);       // the @using one
        }

        [Fact]
        public void NamespaceImport_MissingAtSigil_NotTreatedAsUsing()
        {
            // `import "Ns"` (no @) is reserved/ambiguous — must NOT silently become a using.
            var (ds, _) = Parse("import \"Foo.Bar\"\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.DoesNotContain("Foo.Bar", ds.Usings);
        }

        [Fact]
        public void NamespaceImport_InHookFile_PopulatesUsingDirectives()
        {
            var (ds, _) = Parse("import \"@UnityEngine.Audio\"\nexport void useThing() { }\n");
            Assert.Contains("UnityEngine.Audio", ds.Usings);
            Assert.True(Assert.Single(ds.UsingDirectives).FromImportSyntax);
        }
    }
}
