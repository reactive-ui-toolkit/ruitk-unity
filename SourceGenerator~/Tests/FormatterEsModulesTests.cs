using Ruitk.Language.Formatter;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// ES-modules campaign (Plans~/archive/ES_MODULES_EXECUTION_PLAN.md M6, U-10): the formatter's
    /// plain-declaration path — canonical import spellings (named/as/star/default), member
    /// re-emission (the A7f data-loss guard extended to the new record fields), export
    /// default/list placement, round-trip idempotency, and the safe-untouched guarantee for
    /// shapes the formatter cannot yet re-emit losslessly.
    /// </summary>
    public sealed class FormatterEsModulesTests
    {
        private static readonly AstFormatter _fmt = new AstFormatter(FormatterOptions.Default);

        private static string N(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");
        private static string Format(string source) => N(_fmt.Format(source));

        // ── Canonical fixture round-trip (general plan §3, trimmed self-contained) ──

        private const string Fixture =
            "import { FormatTime } from \"./TimeUtils\"\n" +
            "\n" +
            "export Style container = new Style { Padding = 10f };\n" +
            "export int MaxItems = 5;\n" +
            "export theme = new Style { BackgroundColor = ColorGray };\n" +
            "\n" +
            "export string FormatScore(int score) {\n" +
            "  return $\"Score: {score}\";\n" +
            "}\n" +
            "\n" +
            "export (int value, Action reset) useCountdown(int start) {\n" +
            "  var (value, setValue) = useState(start);\n" +
            "  Action reset = () => setValue(start);\n" +
            "  return (value, reset);\n" +
            "}\n" +
            "\n" +
            "Style rowStyle = new Style { MarginTop = 2f };\n" +
            "\n" +
            "export VirtualNode ScoreRow(string label) {\n" +
            "  return (\n" +
            "    <Label text={label} style={rowStyle} />\n" +
            "  );\n" +
            "}\n" +
            "\n" +
            "export default ScoreRow;\n";

        [Fact]
        public void Fixture_FormatPreservesEveryDeclaration()
        {
            string formatted = Format(Fixture);

            Assert.Contains("import { FormatTime } from \"./TimeUtils\"", formatted);
            Assert.Contains("export Style container = new Style { Padding = 10f };", formatted);
            Assert.Contains("export int MaxItems = 5;", formatted);
            Assert.Contains("export theme = new Style { BackgroundColor = ColorGray };", formatted);
            Assert.Contains("export string FormatScore(int score) {", formatted);
            Assert.Contains("export (int value, Action reset) useCountdown(int start) {", formatted);
            Assert.Contains("Style rowStyle = new Style { MarginTop = 2f };", formatted);
            Assert.Contains("export VirtualNode ScoreRow(string label) {", formatted);
            Assert.Contains("export default ScoreRow;", formatted);
        }

        [Fact]
        public void Fixture_FormatIsIdempotent()
        {
            string once = Format(Fixture);
            string twice = Format(once);
            Assert.Equal(once, twice);
        }

        [Fact]
        public void Fixture_DeclarationsKeepSourceOrder()
        {
            string formatted = Format(Fixture);
            int container = formatted.IndexOf("export Style container", System.StringComparison.Ordinal);
            int format = formatted.IndexOf("export string FormatScore", System.StringComparison.Ordinal);
            int hook = formatted.IndexOf("useCountdown", System.StringComparison.Ordinal);
            int rowStyle = formatted.IndexOf("Style rowStyle", System.StringComparison.Ordinal);
            int comp = formatted.IndexOf("export VirtualNode ScoreRow", System.StringComparison.Ordinal);
            int deflt = formatted.IndexOf("export default", System.StringComparison.Ordinal);
            Assert.True(container < format && format < hook && hook < rowStyle && rowStyle < comp && comp < deflt);
        }

        // ── Import spellings (U-10 canonical forms) ─────────────────────────

        [Fact]
        public void ImportForms_RoundTripCanonically()
        {
            string src =
                "import { a, b as c } from \"./x\"\n" +
                "import * as X from \"./y\"\n" +
                "import D from \"./z\"\n" +
                "export int V = 1;\n";
            string formatted = Format(src);

            Assert.Contains("import { a, b as c } from \"./x\"", formatted);
            Assert.Contains("import * as X from \"./y\"", formatted);
            Assert.Contains("import D from \"./z\"", formatted);
            Assert.Equal(formatted, Format(formatted));
        }

        [Fact]
        public void ImportTrailingSemicolon_FormatsToCanonicalSemicolonlessForm()
        {
            string formatted = Format("import { a } from \"./x\";\nexport int V = 1;\n");
            Assert.Contains("import { a } from \"./x\"\n", formatted);
            Assert.DoesNotContain("\"./x\";", formatted);
        }

        // ── Export list placement ────────────────────────────────────────────

        [Fact]
        public void ExportList_PrintedAtEndOfFile()
        {
            string src =
                "int MaxItems = 5;\n" +
                "export { MaxItems };\n";
            string formatted = Format(src);

            Assert.Contains("export { MaxItems };", formatted);
            Assert.True(
                formatted.IndexOf("int MaxItems = 5;", System.StringComparison.Ordinal)
                < formatted.IndexOf("export { MaxItems };", System.StringComparison.Ordinal));
            Assert.Equal(formatted, Format(formatted));
        }

        [Fact]
        public void ExpressionBodiedUtil_RoundTrips()
        {
            string src = "export int Double(int x) => x * 2;\n";
            string formatted = Format(src);
            Assert.Contains("export int Double(int x) => x * 2;", formatted);
            Assert.Equal(formatted, Format(formatted));
        }

        // ── Safety guarantees (never lose data) ──────────────────────────────

        [Fact]
        public void MultiComponentNewModeFile_LeftUntouched()
        {
            // Losslessly re-formatting the 2nd+ component needs per-component markup re-parsing
            // the formatter does not do yet — the safe minimal guarantee (same precedent as
            // ContainsInlineJsxControlFlow) is: leave the WHOLE file untouched.
            string src =
                "export VirtualNode A() {\n  return (\n    <Box />\n  );\n}\n" +
                "export VirtualNode B() {\n  return (\n    <Box />\n  );\n}\n";
            Assert.Equal(N(src), Format(src));
        }

        [Fact]
        public void MemberOnlyFile_FormatsAndKeepsPrivateMembers()
        {
            string src =
                "export int Gap = 8;\n" +
                "int hidden = 1;\n";
            string formatted = Format(src);
            Assert.Contains("export int Gap = 8;", formatted);
            Assert.Contains("int hidden = 1;", formatted);
            Assert.Equal(formatted, Format(formatted));
        }

        // ── Trivia preservation (audit finding: comments must not hoist) ────

        [Fact]
        public void CommentBetweenDecls_StaysBetweenDecls()
        {
            string src = "export int A = 1;\n\n// belongs to B\nexport int B = 2;\n";
            string formatted = Format(src);
            int a = formatted.IndexOf("export int A", System.StringComparison.Ordinal);
            int c = formatted.IndexOf("// belongs to B", System.StringComparison.Ordinal);
            int b = formatted.IndexOf("export int B", System.StringComparison.Ordinal);
            Assert.True(a >= 0 && c >= 0 && b >= 0);
            Assert.True(a < c && c < b, "the comment must stay attached to B, not hoist to the top");
            Assert.Equal(formatted, Format(formatted));
        }

        [Fact]
        public void LeadingLicenseHeader_StaysOnTop()
        {
            string src = "// (c) 2026 Owner\nexport int A = 1;\n";
            string formatted = Format(src);
            Assert.StartsWith("// (c) 2026 Owner", formatted);
            Assert.Equal(formatted, Format(formatted));
        }

        [Fact]
        public void CommentInsideValueInitializer_Survives()
        {
            string src = "export Style s = new Style {\n  Padding = 1f, // px\n};\n";
            Assert.Contains("// px", Format(src));
        }

        [Fact]
        public void StringWithBraceAndSemicolonInValue_Parses()
        {
            string src = "export string s = \"a;b}c\";\nexport int t = 2;\n";
            string formatted = Format(src);
            Assert.Contains("\"a;b}c\"", formatted);
            Assert.Contains("export int t = 2;", formatted);
        }

        // ── F9: generic declaration heads round-trip ────────────────────────

        [Fact]
        public void GenericHead_FormatIsIdempotent_AndKeepsTypeParams()
        {
            string src =
                "export (T value, System.Action<T> set) useSlot<T>(T seed) {\n" +
                "  return (seed, _ => {});\n" +
                "}\n" +
                "\n" +
                "export T identity<T>(T v) => v;\n";
            string once = Format(src);
            Assert.Contains("useSlot<T>(T seed)", once);
            Assert.Contains("identity<T>(T v)", once);
            Assert.Equal(once, Format(once));
        }

        // ── Legacy formatting is byte-untouched by this campaign ────────────

        [Fact]
        public void LegacySingleComponent_FormatUnchangedShape()
        {
            string src = "component Foo {\n  return (\n    <Box />\n  );\n}\n";
            string formatted = Format(src);
            Assert.Contains("component Foo {", formatted);
            Assert.Equal(formatted, Format(formatted));
        }
    }
}
