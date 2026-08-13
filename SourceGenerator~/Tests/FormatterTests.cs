using Ruitk.Language.Formatter;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

public class FormatterTests
{
    [Fact]
    public void Format_DoesNotAppendSemicolon_ToControlFlowHeader()
    {
        var formatter = new AstFormatter(FormatterOptions.Default);
        var source =
            """
            VirtualNode MyComp() {
                if (mode == "normal")
                {
                    setMode("active")
                }
                return (
                    <box/>
                );
            }
            """;

        var formatted = formatter.Format(source, "Test.uitkx");

        Assert.Contains("if (mode == \"normal\")", formatted);
        Assert.DoesNotContain("if (mode == \"normal\");", formatted);
        Assert.Contains("setMode(\"active\")", formatted);
    }

    [Fact]
    public void Format_FunctionStyle_PreservesFunctionDeclarationForm()
    {
        var formatter = new AstFormatter(FormatterOptions.Default);
        var source =
            """
            VirtualNode CounterPanel() {
                var (count, setCount) = useState(0)
                return (
                    <Box>
                        <Button text={count.ToString()} onClick={() => setCount(count + 1)} />
                    </Box>
                );
            }
            """;

        var formatted = formatter.Format(source, "Test.uitkx").Replace("\r\n", "\n");

        Assert.StartsWith("VirtualNode CounterPanel() {\n", formatted);
        Assert.Contains("var (count, setCount) = useState(0)", formatted);
        Assert.Contains("\n  return (\n", formatted);
        Assert.Contains("\n  );\n", formatted);
        Assert.EndsWith("}\n", formatted);
        Assert.DoesNotContain("@component", formatted);
        Assert.DoesNotContain("@namespace", formatted);
    }

    [Fact]
    public void Format_FunctionStyle_WithNoSetup_FormatsReturnMarkup()
    {
        var formatter = new AstFormatter(FormatterOptions.Default);
        var source = "VirtualNode HelloWorld() { return (<Label text=\"Hi\"/>); }";

        var formatted = formatter.Format(source, "Test.uitkx").Replace("\r\n", "\n");

        Assert.Contains("VirtualNode HelloWorld() {", formatted);
        Assert.Contains("return (", formatted);
        Assert.Contains("<Label text=\"Hi\" />", formatted);
        Assert.Contains(");", formatted);
    }
}