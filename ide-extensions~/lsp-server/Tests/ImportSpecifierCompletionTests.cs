using System;
using System.IO;
using System.Linq;
using UitkxLanguageServer;
using Xunit;

namespace UitkxLanguageServer.Tests
{
    /// <summary>
    /// Path completion inside an import <c>from "…"</c> specifier
    /// (<see cref="CompletionHandler.GetSpecifierCompletions"/>): <c>./</c>-relative extensionless
    /// specifiers to peer <c>.uitkx</c> files under the importer's directory, excluding self.
    /// </summary>
    public sealed class ImportSpecifierCompletionTests : IDisposable
    {
        private readonly string _root;
        private readonly string _importer;

        public ImportSpecifierCompletionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "uitkx-spec-cmp-" + Guid.NewGuid().ToString("N"));
            string uiDir = Path.Combine(_root, "Assets", "UI");
            Directory.CreateDirectory(Path.Combine(uiDir, "sub"));
            _importer = Path.Combine(uiDir, "Screen.uitkx");
            File.WriteAllText(_importer, "import { W } from \"\"\n");
            File.WriteAllText(Path.Combine(uiDir, "StatusChip.uitkx"), "export VirtualNode StatusChip() { return (<Box />); }\n");
            File.WriteAllText(Path.Combine(uiDir, "sub", "Deep.uitkx"), "export VirtualNode Deep() { return (<Box />); }\n");
        }

        [Fact]
        public void InsideSpecifier_ListsPeerFiles_ExtensionlessRelative_ExcludesSelf()
        {
            string line = "import { W } from \"\"";
            // col 19 is between the two quotes.
            var specs = CompletionHandler.GetSpecifierCompletions(_importer, line, 19);

            Assert.Contains("./StatusChip", specs);
            Assert.Contains("./sub/Deep", specs);
            Assert.DoesNotContain(specs, s => s.EndsWith("Screen")); // self excluded
            Assert.DoesNotContain(specs, s => s.EndsWith(".uitkx"));  // extensionless
        }

        [Fact]
        public void OutsideSpecifier_Empty()
        {
            string line = "import { W } from \"\"";
            // col 8 is inside the braces, not the specifier.
            Assert.Empty(CompletionHandler.GetSpecifierCompletions(_importer, line, 8));
        }

        [Fact]
        public void NonImportLine_Empty()
        {
            Assert.Empty(CompletionHandler.GetSpecifierCompletions(_importer, "var s = \"\";", 9));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
