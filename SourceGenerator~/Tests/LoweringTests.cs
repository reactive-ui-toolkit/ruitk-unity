using Ruitk.Language;
using Ruitk.Language.Lowering;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

public class LoweringTests
{
    [Fact]
    public void CanonicalLowering_PassThrough_ReturnsParsedRootsUnchanged()
    {
        const string src =
            """
            VirtualNode CounterPanel() {
                var (count, setCount) = useState(0);
                return (
                    <Box><Label text={$"{count}"} /></Box>
                );
            }
            """;

        var diags = new System.Collections.Generic.List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(src, "CounterPanel.uitkx", diags);
        var parsed = UitkxParser.Parse(src, "CounterPanel.uitkx", directives, diags);

        var lowered = CanonicalLowering.LowerToRenderRoots(directives, parsed, "CounterPanel.uitkx");

        // CanonicalLowering is now a pass-through — roots are unchanged
        Assert.Equal(parsed.Length, lowered.Length);
        for (int i = 0; i < parsed.Length; i++)
            Assert.Same(parsed[i], lowered[i]);
    }
}
