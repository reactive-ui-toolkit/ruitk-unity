using System.Collections.Immutable;
using System.IO;
using Ruitk.Language.Parser;
using UitkxLanguageServer;
using Xunit;

namespace UitkxLanguageServer.Tests
{
    /// <summary>
    /// Go-to-definition for the import/export grammar (<see cref="DefinitionHandler.TryResolveImportNavigation"/>):
    /// clicking a specifier jumps to the target file; clicking an imported name jumps to that
    /// declaration's line; a non-import line falls through.
    /// </summary>
    public sealed class ImportDefinitionTests : System.IDisposable
    {
        private readonly string _root;
        private readonly string _importer;
        private readonly string _target;

        public ImportDefinitionTests()
        {
            // <tmp>/uitkx-<n>/Assets/UI/{Screen,StatusChip}.uitkx — needs an "Assets" segment
            // so AssetPathUtil.GetProjectRoot resolves the ~/ root (unused here) + the walk.
            _root = Path.Combine(Path.GetTempPath(), "uitkx-import-def-" + System.Guid.NewGuid().ToString("N"));
            string uiDir = Path.Combine(_root, "Assets", "UI");
            Directory.CreateDirectory(uiDir);
            _importer = Path.Combine(uiDir, "Screen.uitkx");
            _target = Path.Combine(uiDir, "StatusChip.uitkx");
            File.WriteAllText(_importer,
                "import { StatusChip } from \"./StatusChip\"\n\nexport VirtualNode Screen() {\n    return (<StatusChip />);\n}\n");
            File.WriteAllText(_target,
                "\nexport VirtualNode StatusChip() {\n    return (<Label text=\"x\" />);\n}\n");
        }

        private ImmutableArray<ImportDeclaration> Imports() => ImmutableArray.Create(
            // `import { StatusChip } from "./StatusChip"` on line 1; StatusChip name starts at col 9.
            new ImportDeclaration(
                ImmutableArray.Create("StatusChip"),
                "./StatusChip",
                1, 0,
                ImmutableArray.Create(9)));

        [Fact]
        public void CursorOnSpecifier_JumpsToTargetFileTop()
        {
            // Column 30 is inside the "./StatusChip" specifier, not on the name.
            bool handled = DefinitionHandler.TryResolveImportNavigation(
                _importer, Imports(), line1: 1, col0: 30, out var file, out var line);

            Assert.True(handled);
            Assert.Equal(Path.GetFullPath(_target), Path.GetFullPath(file!));
            Assert.Equal(1, line);
        }

        [Fact]
        public void CursorOnImportedName_JumpsToDeclarationLine()
        {
            // Column 9 is on "StatusChip"; its declaration is on line 2 of the target.
            bool handled = DefinitionHandler.TryResolveImportNavigation(
                _importer, Imports(), line1: 1, col0: 9, out var file, out var line);

            Assert.True(handled);
            Assert.Equal(Path.GetFullPath(_target), Path.GetFullPath(file!));
            Assert.Equal(2, line);
        }

        [Fact]
        public void CursorOnNonImportLine_FallsThrough()
        {
            bool handled = DefinitionHandler.TryResolveImportNavigation(
                _importer, Imports(), line1: 3, col0: 5, out var file, out _);

            Assert.False(handled);
            Assert.Null(file);
        }

        [Fact]
        public void UnresolvableSpecifier_HandledButNoTarget()
        {
            var imports = ImmutableArray.Create(new ImportDeclaration(
                ImmutableArray.Create("X"), "./nope", 1, 0, ImmutableArray.Create(9)));

            bool handled = DefinitionHandler.TryResolveImportNavigation(
                _importer, imports, line1: 1, col0: 20, out var file, out _);

            Assert.True(handled);   // on an import line...
            Assert.Null(file);      // ...but the target does not exist → caller returns null
        }

        // ── Wrapped tuple-return heads (StressTest field find, 0.16.0) ──────
        // The formatter wraps over-width hook heads as `export (\n  …\n) useX(…) {`,
        // putting the NAME on a `)`-continuation line that single-line declaration
        // matchers never saw — go-to-definition on the hook dead-ended.

        private const string WrappedHookTarget =
            "export (\n" +                                    // 1
            "  List<(string id, float x)> boxes,\n" +         // 2
            "  float avgFps,\n" +                             // 3
            "  bool finished\n" +                             // 4
            ") useStressTestLoop(int boxCount, float duration) {\n" + // 5 ← name line
            "  var (boxes, setBoxes) = useState(new List<(string id, float x)>());\n" +
            "  return (boxes, 0f, false);\n" +
            "}\n";

        [Fact]
        public void CursorOnImportedName_WrappedTupleHead_JumpsToContinuationLine()
        {
            string hooksPath = Path.Combine(Path.GetDirectoryName(_importer)!, "Loop.hooks.uitkx");
            File.WriteAllText(hooksPath, WrappedHookTarget);
            var imports = ImmutableArray.Create(new ImportDeclaration(
                ImmutableArray.Create("useStressTestLoop"),
                "./Loop.hooks",
                1, 0,
                ImmutableArray.Create(9)));

            bool handled = DefinitionHandler.TryResolveImportNavigation(
                _importer, imports, line1: 1, col0: 9, out var file, out var line);

            Assert.True(handled);
            Assert.Equal(Path.GetFullPath(hooksPath), Path.GetFullPath(file!));
            Assert.Equal(5, line);
        }

        [Fact]
        public void FindDeclarationInUitkx_WrappedTupleHead_FindsNameLine()
        {
            var (line, col) = DefinitionHandler.FindDeclarationInUitkx(
                WrappedHookTarget, "useStressTestLoop");

            Assert.Equal(5, line);
            Assert.Equal(2, col); // after ") "
        }

        [Fact]
        public void FindDeclarationInUitkx_IndentedCallStatement_DoesNotMatch()
        {
            // The `)`-continuation alternative must not turn body statements into
            // declarations: an indented call line never matches (column-0 rule).
            string source =
                "export VirtualNode Panel() {\n" +
                "  useStressTestLoop(1, 2f);\n" +
                "  return (<VisualElement />);\n" +
                "}\n";
            var (line, _) = DefinitionHandler.FindDeclarationInUitkx(source, "useStressTestLoop");

            Assert.Equal(0, line);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
