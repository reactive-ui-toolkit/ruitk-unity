using System.Linq;
using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// Build-side value-import cycle (UITKX2306) edge-construction regressions
    /// (<c>UitkxGenerator.BuildValueCycleMap</c> / <c>ExtractImportsForCycle</c>).
    /// 2306 is ERROR severity, so a spurious edge breaks an otherwise-valid Unity
    /// compile — these pin the two false-positive sources the emit review found.
    /// </summary>
    public sealed class ValueCycleBuildTests
    {
        [Fact]
        public void SelfImport_DoesNotProduce2306()
        {
            // A file that imports its OWN exported hook forms a self-edge A→A, which
            // FindCycle would report as [A,A]. A self-import is harmless (the reference
            // stays inside the file's own generated container), so it must NOT be a cycle.
            var result = GeneratorTestHelper.RunMultiple(
                new[]
                {
                    ("SelfHook.uitkx",
                        "import { useSelf } from \"./SelfHook\"\n"
                        + "export void useSelf() {\n  return 1;\n}"),
                },
                primaryFileName: "SelfHook.uitkx");

            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX2306");
        }

        [Fact]
        public void CommentedImport_DoesNotClosePhantomCycle()
        {
            // Real value graph: A→B only (A imports B's hook). B's import of A's hook is
            // COMMENTED OUT, so the true graph is acyclic. A comment-blind scan would add
            // a phantom B→A edge and report A→B→A → spurious 2306 on both files.
            var a =
                "import { useB } from \"./BHooks\"\n"
                + "export void useA() {\n  return useB();\n}";
            var b =
                "/*\nimport { useA } from \"./AHooks\"\n*/\n"
                + "export void useB() {\n  return 1;\n}";

            var result = GeneratorTestHelper.RunMultiple(
                new[] { ("AHooks.uitkx", a), ("BHooks.uitkx", b) },
                primaryFileName: "AHooks.uitkx");

            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX2306");
        }

        [Fact]
        public void RealMutualHookImport_StillProduces2306()
        {
            // Positive control: a genuine A↔B mutual value import must still be flagged
            // (family-wide TDZ-parity policy) so the fixes above didn't disable 2306.
            var a =
                "import { useB } from \"./BHooks\"\n"
                + "export void useA() {\n  return useB();\n}";
            var b =
                "import { useA } from \"./AHooks\"\n"
                + "export void useB() {\n  return useA();\n}";

            var result = GeneratorTestHelper.RunMultiple(
                new[] { ("AHooks.uitkx", a), ("BHooks.uitkx", b) },
                primaryFileName: "AHooks.uitkx");

            Assert.Contains(result.Diagnostics, d => d.Id == "UITKX2306");
        }
    }
}
