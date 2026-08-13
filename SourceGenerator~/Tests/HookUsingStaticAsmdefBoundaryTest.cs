using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

// Stage 3d asmdef-boundary coverage. With the cross-namespace fix in place,
// the only remaining filter that prevents `using static` injection across
// asmdef boundaries lives in UitkxGenerator's pre-scan via
// IsOwnedByCompilation. This test pins that contract: a hook file owned by
// Assembly-CSharp-Editor (under an Editor/ folder) must NOT contribute a
// using-static to a runtime-asmdef component.
public class HookUsingStaticAsmdefBoundaryTest
{
    [Fact]
    public void HookInForeignAsmdef_DoesNotContribute_UsingStatic()
    {
        var hooksContent = @"
@namespace MyApp.EditorOnly

int useEditorOnlyValue() {
  var (v, _) = useState(42);
  return v;
}
";
        var componentContent = @"
@namespace MyApp.UI

VirtualNode RuntimeComp() {
  return (
    <VisualElement />
  );
}
";

        // Hook lives under an Editor/ segment, so IsOwnedByCompilation classifies
        // it as Assembly-CSharp-Editor. Component lives in a runtime folder, so
        // it belongs to Assembly-CSharp.
        string editorDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uitkx_tests_asmdef", "Editor");
        string runtimeDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uitkx_tests_asmdef", "Runtime");

        var stubTree = CSharpSyntaxTree.ParseText(
            "namespace Ruitk.Core { public abstract class VirtualNode { } }",
            path: System.IO.Path.Combine(runtimeDir, "_Stubs.g.cs"));

        var compilation = CSharpCompilation.Create(
            assemblyName: "Assembly-CSharp", // runtime asmdef
            syntaxTrees: new[] { stubTree },
            references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new UitkxGenerator();
        var addlTexts = ImmutableArray.Create<AdditionalText>(
            new InMemoryAdditionalText(System.IO.Path.Combine(editorDir, "UseEditorOnlyValue.hooks.uitkx"), hooksContent),
            new InMemoryAdditionalText(System.IO.Path.Combine(runtimeDir, "RuntimeComp.uitkx"), componentContent)
        );

        var driver = CSharpGeneratorDriver.Create(generator).AddAdditionalTexts(addlTexts);
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        var runResult = driver.GetRunResult();

        string? componentSource = null;
        foreach (var r in runResult.Results)
            foreach (var src in r.GeneratedSources)
                if (src.HintName.Contains("RuntimeComp.uitkx"))
                    componentSource = src.SourceText.ToString();

        Assert.NotNull(componentSource);
        Assert.DoesNotContain("UseEditorOnlyValueHooks", componentSource);
    }
}
