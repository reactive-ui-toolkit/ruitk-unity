using System.Linq;
using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// UITKX2316 — unknown-namespace validation for <c>@using</c> / <c>import "@Ns"</c>
    /// (namespace-import unification plan, step 4). The sound namespace analogue of UITKX2300:
    /// a misspelled using now produces a located UITKX2316 on the .uitkx line instead of a raw
    /// CS0246 buried in generated code. The test compilation references only the core assembly,
    /// so System.* and the stub's Ruitk.Core namespace are "known"; anything else is not.
    /// </summary>
    public sealed class UnknownNamespaceTests
    {
        [Fact]
        public void MisspelledAtUsing_Emits2316()
        {
            const string src =
                "@namespace TestNs\n@using Zzz.Totally.Bogus\nVirtualNode MyComp() {\n  return (<VisualElement />);\n}";

            var result = GeneratorTestHelper.Run(src);

            var diag = Assert.Single(result.Diagnostics, d => d.Id == "UITKX2316");
            Assert.Contains("Zzz.Totally.Bogus", diag.GetMessage());
        }

        [Fact]
        public void MisspelledNamespaceImport_Emits2316()
        {
            const string src =
                "@namespace TestNs\nimport \"@Zzz.Totally.Bogus\"\nVirtualNode MyComp() {\n  return (<VisualElement />);\n}";

            var result = GeneratorTestHelper.Run(src);

            Assert.Contains(result.Diagnostics, d => d.Id == "UITKX2316");
        }

        [Fact]
        public void ValidCoreNamespace_No2316()
        {
            const string src =
                "@namespace TestNs\n@using System.Collections.Generic\nVirtualNode MyComp() {\n  return (<VisualElement />);\n}";

            var result = GeneratorTestHelper.Run(src);

            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX2316");
        }

        [Fact]
        public void StaticPayload_NotFlagged_OutOfScopeForNamespaceCheck()
        {
            // `static X` targets a TYPE, not a namespace — v1 SG check skips it (no false 2316
            // even though this test compilation lacks a Doom.Nonexistent type).
            const string src =
                "@namespace TestNs\nimport \"@static Doom.Nonexistent.Thing\"\nVirtualNode MyComp() {\n  return (<VisualElement />);\n}";

            var result = GeneratorTestHelper.Run(src);

            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX2316");
        }

        [Fact]
        public void PeerGeneratedNamespace_NotFlagged()
        {
            // File B `@using`s the namespace that file A's component generates into. That namespace
            // exists only in generated output (invisible to the pre-generation compilation), so the
            // peer-namespace union must keep 2316 from firing.
            var files = new[]
            {
                ("Widget.uitkx",
                    "@namespace MyApp.Widgets\nexport VirtualNode Widget() {\n  return (<VisualElement />);\n}"),
                ("Screen.uitkx",
                    "@namespace MyApp.Screens\nimport \"@MyApp.Widgets\"\nVirtualNode Screen() {\n  return (<VisualElement />);\n}"),
            };

            var result = GeneratorTestHelper.RunMultiple(files, "Screen.uitkx");

            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX2316");
        }

        [Fact]
        public void Emits2316_AsBuildWarning_NotError()
        {
            // At build time 2316 is a WARNING — it must never break an otherwise-valid build
            // (the emitted using's CS0246 stays the real gate; the editor pass is the error tier).
            // Because it doesn't halt, the component source is still produced.
            const string src =
                "@namespace TestNs\n@using Zzz.Bogus\nVirtualNode MyComp() {\n  return (<VisualElement />);\n}";

            var result = GeneratorTestHelper.Run(src);

            var diag = Assert.Single(result.Diagnostics, d => d.Id == "UITKX2316");
            Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, diag.Severity);
            Assert.True(result.SourceWasProduced); // warning does not halt emission
        }
    }
}
