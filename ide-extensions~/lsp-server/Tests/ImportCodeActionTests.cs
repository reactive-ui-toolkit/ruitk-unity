using UitkxLanguageServer;
using Xunit;

namespace UitkxLanguageServer.Tests
{
    /// <summary>
    /// The "add import" quick-fix core (<see cref="ImportCodeActionHandler.TryBuildAddImportEdit"/>):
    /// parses the <c>import {{ … }} from "…"</c> line out of a 2305 message and inserts it at the top
    /// of the preamble; no-op when the import already exists or the message carries no import line.
    /// </summary>
    public sealed class ImportCodeActionTests
    {
        private const string Msg2305 =
            "`StatusChip` is defined in StatusChip.uitkx but not imported — add: import { StatusChip } from \"./StatusChip\"";

        [Fact]
        public void From2305Message_BuildsImportInsert_AtTop()
        {
            string text = "VirtualNode Screen() {\n    return (<StatusChip />);\n}\n";
            bool ok = ImportCodeActionHandler.TryBuildAddImportEdit(text, Msg2305, out int line, out string insert);

            Assert.True(ok);
            Assert.Equal(0, line);
            Assert.StartsWith("import { StatusChip } from \"./StatusChip\"", insert);
        }

        [Fact]
        public void InsertsAfterLeadingComments()
        {
            string text = "// license header\n// line 2\nVirtualNode Screen() {\n    return (<StatusChip />);\n}\n";
            bool ok = ImportCodeActionHandler.TryBuildAddImportEdit(text, Msg2305, out int line, out _);

            Assert.True(ok);
            Assert.Equal(2, line); // after the two comment lines
        }

        [Fact]
        public void AlreadyImported_NoFix()
        {
            string text = "import { StatusChip } from \"./StatusChip\"\nVirtualNode Screen() {\n    return (<StatusChip />);\n}\n";
            bool ok = ImportCodeActionHandler.TryBuildAddImportEdit(text, Msg2305, out _, out _);
            Assert.False(ok);
        }

        [Fact]
        public void MessageWithoutImportLine_NoFix()
        {
            bool ok = ImportCodeActionHandler.TryBuildAddImportEdit(
                "VirtualNode Screen() { }", "`useX` is used like a hook but no file exports it", out _, out _);
            Assert.False(ok);
        }

        [Fact]
        public void FirstNonTriviaLine_SkipsBlockComment()
        {
            string text = "/*\n header\n*/\n\nVirtualNode Screen() { }";
            Assert.Equal(4, ImportCodeActionHandler.FirstNonTriviaLine(text));
        }

        // ── convert-@using refactor core (namespace-import unification plan) ────────

        [Fact]
        public void ConvertUsing_AtUsingForm()
        {
            bool ok = ImportCodeActionHandler.TryBuildConvertUsingToImport(
                "@using Ruitk.Router", out string repl);
            Assert.True(ok);
            Assert.Equal("import \"@Ruitk.Router\"", repl);
        }

        [Fact]
        public void ConvertUsing_CSharpFormWithSemicolon()
        {
            bool ok = ImportCodeActionHandler.TryBuildConvertUsingToImport(
                "using System.Text;", out string repl);
            Assert.True(ok);
            Assert.Equal("import \"@System.Text\"", repl);
        }

        [Fact]
        public void ConvertUsing_PreservesIndentAndStaticPayload()
        {
            bool ok = ImportCodeActionHandler.TryBuildConvertUsingToImport(
                "  @using static DoomGame.DoomTypes", out string repl);
            Assert.True(ok);
            Assert.Equal("  import \"@static DoomGame.DoomTypes\"", repl);
        }

        [Fact]
        public void ConvertUsing_NotAUsingLine_False()
        {
            Assert.False(ImportCodeActionHandler.TryBuildConvertUsingToImport(
                "VirtualNode Foo() {", out _));
            Assert.False(ImportCodeActionHandler.TryBuildConvertUsingToImport(
                "import { X } from \"./X\"", out _));
        }

        [Fact]
        public void GetLine_ReturnsRequestedLine()
        {
            string text = "a\nb\nc";
            Assert.Equal("b", ImportCodeActionHandler.GetLine(text, 1));
            Assert.Equal("", ImportCodeActionHandler.GetLine(text, 9));
        }
    }
}
