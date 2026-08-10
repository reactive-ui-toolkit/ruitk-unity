using Ruitk.Language.Formatter;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// Plan §10 / A7f — the formatter re-prints the preamble from the model and DROPS unmodeled
    /// lines. These guard that import lines AND export prefixes survive a format pass (else
    /// formatting a codemodded file would silently delete them and break strict mode).
    /// </summary>
    public sealed class FormatterImportTests
    {
        private static string Fmt(string src) =>
            new AstFormatter(FormatterOptions.Default).Format(src, "Test.uitkx").Replace("\r\n", "\n");

        [Fact]
        public void ImportLines_SurviveFormat()
        {
            string outp = Fmt(
                "import { A, B } from \"./X\"\n\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Contains("import { A, B } from \"./X\"", outp);
        }

        [Fact]
        public void MultipleImports_AllSurvive_InOrder()
        {
            string outp = Fmt(
                "import { A } from \"./A\"\nimport { B } from \"~/B\"\n\n" +
                "VirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            int a = outp.IndexOf("import { A } from \"./A\"", System.StringComparison.Ordinal);
            int b = outp.IndexOf("import { B } from \"~/B\"", System.StringComparison.Ordinal);
            Assert.True(a >= 0 && b > a, "both imports must survive, in source order");
        }

        [Fact]
        public void ImportLine_TrailingSemicolon_FormatsToCanonicalForm()
        {
            // The parser tolerates the JS-canonical trailing `;` and drops it from the
            // model, so the formatter re-emits the canonical, semicolon-less line. The
            // second pass pins idempotency of the canonical output.
            string once = Fmt(
                "import { container } from \"./Foo.style\";\n\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Contains("import { container } from \"./Foo.style\"\n", once);
            Assert.DoesNotContain("\"./Foo.style\";", once);
            Assert.Contains("VirtualNode Foo", once); // the file parsed as a component, not a 2105 wreck
            Assert.Equal(once, Fmt(once));
        }

        [Fact]
        public void ExportComponent_PrefixSurvives()
        {
            Assert.Contains("export VirtualNode Foo", Fmt("export VirtualNode Foo() {\n  return ( <Spacer /> );\n}\n"));
        }

        [Fact]
        public void ExportHook_PrefixSurvives()
        {
            Assert.Contains("export void useThing", Fmt("export void useThing() { }\n"));
        }

        [Fact]
        public void ExportModule_PrefixSurvives()
        {
            Assert.Contains("export module Styles", Fmt("export module Styles { }\n"));
        }

        [Fact]
        public void FormatIsIdempotent_WithImportsAndExport()
        {
            string src = "import { A } from \"./X\"\n\nexport VirtualNode Foo() {\n  return ( <Spacer /> );\n}\n";
            string once = Fmt(src);
            string twice = Fmt(once);
            Assert.Equal(once, twice);
        }

        // ── Namespace-import round-trip (unification plan, step 7) ──────────────────

        [Fact]
        public void NamespaceImport_RoundTrips_NotConvertedToAtUsing()
        {
            // The formatter must preserve the authored spelling — NOT silently rewrite
            // import "@X" back to @using X (that would be a data-losing round-trip).
            string outp = Fmt(
                "import \"@Ruitk.Router\"\n\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Contains("import \"@Ruitk.Router\"", outp);
            Assert.DoesNotContain("@using Ruitk.Router", outp);
        }

        [Fact]
        public void AtUsing_RoundTrips_NotConvertedToImport()
        {
            string outp = Fmt(
                "@using Ruitk.Router\n\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            Assert.Contains("@using Ruitk.Router", outp);
            Assert.DoesNotContain("import \"@", outp);
        }

        [Fact]
        public void MixedUsingForms_BothSurvive_InOrder()
        {
            string outp = Fmt(
                "import \"@Ruitk.Router\"\n@using UnityEngine\n\n" +
                "VirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");
            int imp = outp.IndexOf("import \"@Ruitk.Router\"", System.StringComparison.Ordinal);
            int use = outp.IndexOf("@using UnityEngine", System.StringComparison.Ordinal);
            Assert.True(imp >= 0 && use > imp, "both using forms survive, in source order");
        }

        [Fact]
        public void FormatIsIdempotent_WithNamespaceImports()
        {
            string src = "import \"@Ruitk.Router\"\n@using UnityEngine\n\nVirtualNode Foo() {\n  return ( <Spacer /> );\n}\n";
            string once = Fmt(src);
            Assert.Equal(once, Fmt(once));
        }

        // ── Preamble ordering: @namespace first, imports grouped (unification plan, feature 1) ──

        [Fact]
        public void Format_PutsNamespaceFirst_ThenAllImportsGrouped()
        {
            // Author order is deliberately jumbled: file import, @namespace, namespace import.
            string outp = Fmt(
                "import { Chip } from \"./Chip\"\n@namespace My.Ns\nimport \"@Ruitk.Router\"\n\n" +
                "VirtualNode Foo() {\n  return ( <Spacer /> );\n}\n");

            int ns   = outp.IndexOf("@namespace My.Ns", System.StringComparison.Ordinal);
            int file = outp.IndexOf("import { Chip } from \"./Chip\"", System.StringComparison.Ordinal);
            int nsi  = outp.IndexOf("import \"@Ruitk.Router\"", System.StringComparison.Ordinal);

            Assert.True(ns >= 0 && file >= 0 && nsi >= 0);
            Assert.True(ns < file, "@namespace must come before the imports");
            Assert.True(file < nsi, "file imports then namespace imports — all grouped after @namespace");
        }

        [Fact]
        public void Format_NamespaceBeforeImports_IsIdempotent()
        {
            string src =
                "import { Chip } from \"./Chip\"\n@namespace My.Ns\nimport \"@Ruitk.Router\"\n\n" +
                "VirtualNode Foo() {\n  return ( <Spacer /> );\n}\n";
            string once = Fmt(src);
            Assert.Equal(once, Fmt(once));
            // Second pass must keep @namespace on top (not re-jumble).
            Assert.True(once.IndexOf("@namespace", System.StringComparison.Ordinal)
                      < once.IndexOf("import {", System.StringComparison.Ordinal));
        }
    }
}
