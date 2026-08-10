using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// Regression tests for the implicit <c>using UnityEngine;</c> injected by
/// every component / hook / module emitter (and their HMR mirrors).
///
/// <para>Background: the IDE's <c>VirtualDocumentGenerator</c> has always
/// auto-injected <c>UnityEngine</c> into the virtual C# document used for
/// Roslyn analysis, but the runtime emitters did not. The result was a
/// silent IDE/runtime divergence — editor showed clean, build failed with
/// CS0246 on bare Unity types like <c>Texture2D</c>, <c>Vector2</c>,
/// <c>Color</c>, etc.</para>
///
/// <para>These tests pin down that the wildcard import is now present in the
/// generated component source and that the explicit
/// <c>using Color = UnityEngine.Color;</c> alias still wins over the
/// wildcard (so <c>Color</c> at the type position keeps resolving to
/// <c>UnityEngine.Color</c> rather than becoming ambiguous with the
/// <c>StyleKeys.Color</c> static-imported string constant).</para>
/// </summary>
public class UnityEngineImportTests
{
    [Fact]
    public void Generated_ImportsUnityEngineWildcard()
    {
        var src =
            "@namespace TestNs\n"
            + "VirtualNode MyComp() {\n"
            + "  return (<VisualElement />);\n"
            + "}";

        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("using UnityEngine;"),
            "Generated component must implicitly import the UnityEngine namespace."
        );
    }

    [Fact]
    public void Texture2D_NoExplicitUsing_StillCompiles()
    {
        // Reproduces the original bug: a setup block referencing a bare Unity
        // type without an `@using UnityEngine` directive. Pre-fix this would
        // emit CS0246 at runtime; post-fix the wildcard import resolves it.
        var src =
            "@namespace TestNs\n"
            + "VirtualNode MyComp() {\n"
            + "  Texture2D? icon = null;\n"
            + "  return (<VisualElement />);\n"
            + "}";

        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        // The setup line emits verbatim into the component method body.
        Assert.True(result.SourceContains("Texture2D? icon = null;"));
        // And the wildcard import that makes it resolve must be present.
        Assert.True(result.SourceContains("using UnityEngine;"));
    }

    [Fact]
    public void ColorAlias_StillEmittedAfterWildcard()
    {
        // The explicit `using Color = UnityEngine.Color;` alias must remain
        // even after the wildcard import is added — the alias takes precedence
        // over both the wildcard namespace import and the `using static
        // StyleKeys` (which exposes a `const string Color`). Removing the
        // alias would surface CS0104 ambiguity on bare `Color` references.
        var src =
            "@namespace TestNs\n"
            + "VirtualNode MyComp() {\n"
            + "  return (<VisualElement />);\n"
            + "}";

        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("using UnityEngine;"));
        Assert.True(
            result.SourceContains("using Color = UnityEngine.Color;"),
            "Color alias must remain after wildcard import to disambiguate "
                + "against StyleKeys.Color and UnityEngine.UIElements types."
        );
    }
}
