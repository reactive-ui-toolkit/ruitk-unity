using System;
using System.IO;
using System.Linq;
using UitkxLanguageServer;
using Xunit;

namespace UitkxLanguageServer.Tests
{
    /// <summary>
    /// Completion inside <c>import {{ … }} from "…"</c>
    /// (<see cref="CompletionHandler.GetImportBraceCompletions"/>): suggests the resolved target
    /// file's exported names, excludes non-exported and already-listed names, and only fires when the
    /// cursor is inside the brace list with a resolvable specifier.
    /// </summary>
    public sealed class ImportCompletionTests : IDisposable
    {
        private readonly string _root;
        private readonly string _importer;

        public ImportCompletionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "uitkx-import-cmp-" + Guid.NewGuid().ToString("N"));
            string uiDir = Path.Combine(_root, "Assets", "UI");
            Directory.CreateDirectory(uiDir);
            _importer = Path.Combine(uiDir, "Screen.uitkx");
            File.WriteAllText(_importer, "import {  } from \"./Lib\"\n");
            File.WriteAllText(Path.Combine(uiDir, "Lib.uitkx"),
                "export VirtualNode Widget() {\n    return (<Box />);\n}\n" +
                "export void useThing() { return 0; }\n" +
                "module Private { public const int X = 1; }\n");   // NOT exported
        }

        [Fact]
        public void InsideBraces_SuggestsExportedNames_ExcludesNonExported()
        {
            string line = "import {  } from \"./Lib\"";
            // col 8 is inside the "{  }" list.
            var names = CompletionHandler.GetImportBraceCompletions(_importer, line, 8);

            Assert.Contains("Widget", names);
            Assert.Contains("useThing", names);
            Assert.DoesNotContain("Private", names); // module is not exported
        }

        [Fact]
        public void AlreadyListedName_Excluded()
        {
            string line = "import { Widget,  } from \"./Lib\"";
            var names = CompletionHandler.GetImportBraceCompletions(_importer, line, 17);

            Assert.DoesNotContain("Widget", names); // already in the list
            Assert.Contains("useThing", names);
        }

        [Fact]
        public void OutsideBraces_Empty()
        {
            string line = "import {  } from \"./Lib\"";
            // col 2 is on the `import` keyword, before the brace.
            var names = CompletionHandler.GetImportBraceCompletions(_importer, line, 2);
            Assert.Empty(names);
        }

        [Fact]
        public void NoSpecifier_Empty()
        {
            string line = "import {  }";
            var names = CompletionHandler.GetImportBraceCompletions(_importer, line, 8);
            Assert.Empty(names);
        }

        [Fact]
        public void NonImportLine_Empty()
        {
            string line = "var x = new Style { };";
            var names = CompletionHandler.GetImportBraceCompletions(_importer, line, 19);
            Assert.Empty(names);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
