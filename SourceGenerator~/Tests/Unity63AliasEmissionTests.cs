using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// Pins down the Unity 6.3+ targeted aliases that every emitter must include
/// in the generated component / module / hook preamble so that user-authored
/// <c>.uitkx</c> code can reference <c>StyleMaterialDefinition</c>,
/// <c>MaterialDefinition</c>, <c>Ratio</c>, <c>StyleRatio</c> and
/// <c>FilterFunction</c> by their short names without leaking a wholesale
/// <c>using UnityEngine.UIElements;</c> (which would clash with the
/// string constants imported via <c>using static StyleKeys</c>).
///
/// <para>
/// Aliases are emitted inside <c>#if UNITY_6000_3_OR_NEWER</c> / <c>#endif</c>
/// preprocessor blocks so pre-6.3 builds still compile clean (the wrapped
/// types simply do not exist there).
/// </para>
///
/// <para>
/// Companion text-parity test for HmrCSharpEmitter lives in
/// <see cref="HmrCSharpEmitterAliasParityTests"/> — those tests read the HMR
/// emitter source from disk because the Editor asmdef cannot be loaded into
/// a standalone .NET test runner.
/// </para>
/// </summary>
public class Unity63AliasEmissionTests
{
    [Fact]
    public void Component_emits_guarded_aliases_for_unity_6_3_types()
    {
        var src =
            "@namespace TestNs\n"
            + "VirtualNode MyComp() {\n"
            + "  return (<VisualElement />);\n"
            + "}";

        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        AssertAllAliasesPresent(result.GeneratedSource!);
    }

    [Fact]
    public void Hook_emits_guarded_aliases_for_unity_6_3_types()
    {
        // Hook-only .uitkx (no component) — exercises HookEmitter's preamble
        // exclusively. Filename must match the .uitkx convention used by the
        // SG; signature mirrors README's `hook useCounter(int initial = 0)
        // -> (int, Action)` example shape.
        const string src = """
            @namespace TestNs
            export int useFoo(int initial = 0) {
                return initial;
            }
            """;
        var result = GeneratorTestHelper.Run(src, "UseFoo.uitkx");

        Assert.True(
            result.SourceWasProduced,
            $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.GetMessage()))}"
        );
        AssertAllAliasesPresent(result.GeneratedSource!);
    }

    [Fact]
    public void Module_emits_guarded_aliases_for_unity_6_3_types()
    {
        // Value-only .uitkx (no component) — exercises the __Exports unit's
        // preamble exclusively (0.16.0: the legacy module grammar is gone).
        const string src = """
            @namespace TestNs
            export int Bar = 1;
            """;
        var result = GeneratorTestHelper.Run(src, "Foo.uitkx");

        Assert.True(
            result.SourceWasProduced,
            $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.GetMessage()))}"
        );
        AssertAllAliasesPresent(result.GeneratedSource!);
    }

    /// <summary>
    /// Asserts the five Unity 6.3+ aliases and the surrounding
    /// preprocessor guard are all present in <paramref name="generated"/>.
    /// </summary>
    private static void AssertAllAliasesPresent(string generated)
    {
        Assert.Contains("#if UNITY_6000_3_OR_NEWER", generated);
        Assert.Contains("using FilterFunction = UnityEngine.UIElements.FilterFunction;", generated);
        Assert.Contains("using Ratio = UnityEngine.UIElements.Ratio;", generated);
        Assert.Contains("using StyleRatio = UnityEngine.UIElements.StyleRatio;", generated);
        Assert.Contains(
            "using MaterialDefinition = UnityEngine.UIElements.MaterialDefinition;",
            generated
        );
        Assert.Contains(
            "using StyleMaterialDefinition = UnityEngine.UIElements.StyleMaterialDefinition;",
            generated
        );
        Assert.Contains("#endif", generated);
    }
}

/// <summary>
/// Text-level parity tests for <c>Editor/HMR/HmrCSharpEmitter.cs</c>'s
/// alias block versus <c>SourceGenerator~/Emitter/CSharpEmitter.cs</c>.
///
/// <para>
/// HMR cannot be loaded into a standalone test runner (the Editor asmdef pulls
/// in <c>UnityEditor</c>), so we read both emitter source files from disk and
/// assert each <c>UnityEngine.UIElements</c> alias appears in both. This is a
/// one-way drift tripwire: if SG ever grows a new alias, HMR must follow.
/// </para>
/// </summary>
public class HmrCSharpEmitterAliasParityTests
{
    [Fact]
    public void HmrCSharpEmitter_emits_every_uielements_alias_that_CSharpEmitter_does()
    {
        string sg = File.ReadAllText(SgEmitterPath());
        string hmr = File.ReadAllText(HmrEmitterPath());

        // Baseline aliases — pre-6.3, unconditionally emitted by both emitters.
        string[] baselineAliases =
        {
            "using EasingFunction = UnityEngine.UIElements.EasingFunction;",
            "using EasingMode = UnityEngine.UIElements.EasingMode;",
            "using BackgroundRepeat = UnityEngine.UIElements.BackgroundRepeat;",
            "using BackgroundPosition = UnityEngine.UIElements.BackgroundPosition;",
            "using BackgroundSize = UnityEngine.UIElements.BackgroundSize;",
            "using TransformOrigin = UnityEngine.UIElements.TransformOrigin;",
            "using BackgroundPositionKeyword = UnityEngine.UIElements.BackgroundPositionKeyword;",
            "using BackgroundSizeType = UnityEngine.UIElements.BackgroundSizeType;",
            "using Repeat = UnityEngine.UIElements.Repeat;",
            "using Length = UnityEngine.UIElements.Length;",
            "using StyleKeyword = UnityEngine.UIElements.StyleKeyword;",
            "using TextAutoSizeMode = UnityEngine.UIElements.TextAutoSizeMode;",
        };

        // Unity 6.3+ aliases — wrapped in #if UNITY_6000_3_OR_NEWER by both emitters.
        string[] guardedAliases =
        {
            "using FilterFunction = UnityEngine.UIElements.FilterFunction;",
            "using Ratio = UnityEngine.UIElements.Ratio;",
            "using StyleRatio = UnityEngine.UIElements.StyleRatio;",
            "using MaterialDefinition = UnityEngine.UIElements.MaterialDefinition;",
            "using StyleMaterialDefinition = UnityEngine.UIElements.StyleMaterialDefinition;",
        };

        foreach (string alias in baselineAliases)
        {
            Assert.Contains(alias, sg);
            Assert.Contains(alias, hmr);
        }

        foreach (string alias in guardedAliases)
        {
            Assert.Contains(alias, sg);
            Assert.Contains(alias, hmr);
        }

        // Both emitters must surround the 6.3 aliases with the preprocessor guard
        // (a verbatim string emitted into the generated user source).
        Assert.Contains("#if UNITY_6000_3_OR_NEWER", sg);
        Assert.Contains("#if UNITY_6000_3_OR_NEWER", hmr);
    }

    // ── Path resolution anchored to this source file via [CallerFilePath] ────

    private static string SgEmitterPath()
    {
        // From Tests/Unity63AliasEmissionTests.cs → ../Emitter/CSharpEmitter.cs
        string thisFile = ThisFile();
        string testsDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(testsDir, "..", "Emitter", "CSharpEmitter.cs"));
    }

    private static string HmrEmitterPath()
    {
        // From Tests/Unity63AliasEmissionTests.cs → ../../Editor/HMR/HmrCSharpEmitter.cs
        // (SourceGenerator~/Tests → up to SourceGenerator~ → up to package root → Editor/HMR/)
        string thisFile = ThisFile();
        string testsDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(
            Path.Combine(testsDir, "..", "..", "Editor", "HMR", "HmrCSharpEmitter.cs")
        );
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
