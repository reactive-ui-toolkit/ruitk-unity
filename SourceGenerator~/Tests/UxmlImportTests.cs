using System.Collections.Generic;
using Ruitk.Language;
using Ruitk.Language.Import;
using Ruitk.Language.Parser;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// VE-17 golden tests: hand-authored UXML fixtures convert to exact uitkx
    /// text, every conversion parses clean through the real parser, and every
    /// dropped construct surfaces as a warning (never silently).
    /// </summary>
    public sealed class UxmlImportTests
    {
        private static void AssertParsesClean(string uitkx)
        {
            var diags = new List<ParseDiagnostic>();
            var directives = DirectiveParser.Parse(uitkx, "Converted.uitkx", diags);
            UitkxParser.Parse(uitkx, "Converted.uitkx", directives, diags);
            Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        }

        [Fact]
        public void SimpleTree_ConvertsToGolden()
        {
            var result = UxmlToUitkx.Convert(
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n"
                + "  <ui:VisualElement name=\"root\" class=\"panel dark\">\n"
                + "    <ui:Label text=\"Hello\" />\n"
                + "    <ui:Button text=\"Go\" />\n"
                + "  </ui:VisualElement>\n"
                + "</ui:UXML>",
                "ImportedPanel");

            string expected =
                "export VirtualNode ImportedPanel() {\n"
                + "  return (\n"
                + "    <VisualElement name=\"root\" className=\"panel dark\">\n"
                + "      <Label text=\"Hello\" />\n"
                + "      <Button text=\"Go\" />\n"
                + "    </VisualElement>\n"
                + "  );\n"
                + "}\n";
            Assert.Equal(expected, result.UitkxText);
            Assert.Empty(result.Warnings);
            AssertParsesClean(result.UitkxText);
        }

        [Fact]
        public void InlineStyles_MapToTypedStyle()
        {
            var result = UxmlToUitkx.Convert(
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n"
                + "  <ui:VisualElement style=\"width: 150px; height: 50%; flex-direction: row; "
                + "background-color: #232329; flex-grow: 1; align-items: center;\" />\n"
                + "</ui:UXML>",
                "Styled");

            Assert.Contains("Width = Px(150)", result.UitkxText);
            Assert.Contains("Height = Pct(50)", result.UitkxText);
            Assert.Contains("FlexDirection = FlexRow", result.UitkxText);
            Assert.Contains("BackgroundColor = Hex(\"#232329\")", result.UitkxText);
            Assert.Contains("FlexGrow = 1f", result.UitkxText);
            Assert.Contains("AlignItems = AlignCenter", result.UitkxText);
            Assert.Empty(result.Warnings);
            AssertParsesClean(result.UitkxText);
        }

        [Fact]
        public void MultiRoot_WrapsInContainer()
        {
            var result = UxmlToUitkx.Convert(
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n"
                + "  <ui:Label text=\"a\" />\n"
                + "  <ui:Label text=\"b\" />\n"
                + "</ui:UXML>",
                "TwoRoots");

            Assert.Contains("    <VisualElement>\n", result.UitkxText);
            Assert.Contains("<Label text=\"a\" />", result.UitkxText);
            AssertParsesClean(result.UitkxText);
        }

        [Fact]
        public void DroppedConstructs_AlwaysWarn()
        {
            var result = UxmlToUitkx.Convert(
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n"
                + "  <ui:Style src=\"theme.uss\" />\n"
                + "  <ui:VisualElement style=\"transition-duration: 2s;\" />\n"
                + "</ui:UXML>",
                "Warned");

            Assert.Contains(result.Warnings, w => w.Contains("theme.uss"));
            Assert.Contains(result.Warnings, w => w.Contains("transition-duration"));
            AssertParsesClean(result.UitkxText);
        }

        [Fact]
        public void NumericAndBoolAttributes_EmitAsExpressions()
        {
            var result = UxmlToUitkx.Convert(
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n"
                + "  <ui:Slider low-value=\"0\" high-value=\"10\" />\n"
                + "  <ui:Toggle value=\"true\" />\n"
                + "</ui:UXML>",
                "Fields");

            Assert.Contains("lowValue={0}", result.UitkxText);
            Assert.Contains("highValue={10}", result.UitkxText);
            Assert.Contains("value={true}", result.UitkxText);
            AssertParsesClean(result.UitkxText);
        }
    }
}
