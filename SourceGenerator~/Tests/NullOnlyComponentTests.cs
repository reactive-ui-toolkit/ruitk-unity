using System.Collections.Generic;
using Ruitk.Language;
using Ruitk.Language.Formatter;
using Ruitk.Language.Parser;
using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// Null-only components (React case 2): a component whose body has no
/// top-level <c>return (…);</c> but ends in an explicit top-level
/// <c>return null;</c> is valid and always renders nothing. Pins the
/// formatter round-trip (no synthesized markup return) and the ugui-backend
/// variant. Parser acceptance pins live in <see cref="ParserTests"/>; the
/// SG/HMR emission contract lives in <see cref="HmrEmitterParityContractTests"/>.
/// </summary>
public class NullOnlyComponentTests
{
    private const string NewModeSource =
        "export VirtualNode Gone() {\n"
        + "  useEffect(() => {\n"
        + "    Fire();\n"
        + "    return null;\n"
        + "  }, Array.Empty<object>());\n"
        + "  return null;\n"
        + "}\n";

    private const string LegacySource =
        "VirtualNode Gone() {\n"
        + "  useEffect(() => {\n"
        + "    Fire();\n"
        + "    return null;\n"
        + "  }, Array.Empty<object>());\n"
        + "  return null;\n"
        + "}\n";

    [Fact]
    public void Formatter_NewMode_NoSynthesizedMarkupReturn_AndIdempotent()
    {
        var formatter = new AstFormatter(FormatterOptions.Default);
        var once = formatter.Format(NewModeSource, "Gone.uitkx");

        Assert.Contains("return null;", once);
        Assert.DoesNotContain("return (", once);

        var twice = formatter.Format(once, "Gone.uitkx");
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Formatter_Legacy_NoSynthesizedMarkupReturn_AndIdempotent()
    {
        var formatter = new AstFormatter(FormatterOptions.Default);
        var once = formatter.Format(LegacySource, "Gone.uitkx");

        Assert.Contains("return null;", once);
        Assert.DoesNotContain("return (", once);

        var twice = formatter.Format(once, "Gone.uitkx");
        Assert.Equal(once, twice);
    }

    [Fact]
    public void UguiBackend_NullOnlyComponent_Accepted()
    {
        var diags = new List<ParseDiagnostic>();
        var set = DirectiveParser.Parse(
            "@backend ugui\nexport VirtualNode Gone() {\n  return null;\n}\n",
            "C:/p/Assets/UI/Gone.uitkx",
            diags
        );

        Assert.DoesNotContain(diags, d => d.Code == "UITKX2101" || d.Code == "UITKX2102");
        Assert.Equal("ugui", set.Backend);
        Assert.True(set.HasNullReturn);
    }

    [Fact]
    public void Sg_NullOnlyComponent_GeneratedSourceCompiles()
    {
        var result = GeneratorTestHelper.Run(NewModeSource);

        Assert.True(result.SourceWasProduced);
        Assert.False(
            result.HasDiagnostic("UITKX2101"),
            "Null-only component must not raise UITKX2101"
        );
        Assert.False(
            result.HasDiagnostic("UITKX2102"),
            "Null-only component must not raise UITKX2102"
        );
    }
}
