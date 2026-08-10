using System;
using System.IO;
using System.Linq;
using Ruitk.Language;
using UitkxLanguageServer;
using Xunit;

namespace UitkxLanguageServer.Tests
{
    /// <summary>
    /// The workspace export table (<see cref="WorkspaceIndex.GetPeerExports"/>) that feeds live
    /// strict diagnostics (import/export grammar, leg 3): it tracks exported component/hook/module
    /// names per file (from the parser, so the <c>export</c> flag is honored), excludes non-exported
    /// declarations and the current file, and drives <see cref="StrictImportDetector.Detect"/>.
    /// </summary>
    public sealed class WorkspaceIndexExportsTests : IDisposable
    {
        private readonly string _root;

        public WorkspaceIndexExportsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "uitkx-exports-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private string Write(string rel, string body)
        {
            string full = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
            return full;
        }

        [Fact]
        public void GetPeerExports_TracksExportedNames_ExcludesNonExportedAndSelf()
        {
            var idx = new WorkspaceIndex();
            string lib = Write("Lib.uitkx",
                "export VirtualNode Widget() {\n  return (<Box />);\n}\n" +
                "export void useThing() { return 0; }\n" +
                "module Priv { public const int X = 1; }\n");
            string screen = Write("Screen.uitkx", "export VirtualNode Screen() {\n  return (<Box />);\n}\n");
            idx.Refresh(lib);
            idx.Refresh(screen);

            var exports = idx.GetPeerExports(screen, owningAsmdefDir: null);

            Assert.Contains(exports, e => e.Name == "Widget" && e.Kind == StrictImportDetector.ExportKind.Component);
            Assert.Contains(exports, e => e.Name == "useThing" && e.Kind == StrictImportDetector.ExportKind.Hook);
            Assert.DoesNotContain(exports, e => e.Name == "Priv");   // module not exported
            Assert.DoesNotContain(exports, e => e.Name == "Screen"); // current file excluded
        }

        [Fact]
        public void ExportTable_FeedsDetector_UnimportedPeerTag_Emits2305()
        {
            var idx = new WorkspaceIndex();
            string chip = Write("StatusChip.uitkx", "export VirtualNode StatusChip() {\n  return (<Label text=\"x\" />);\n}\n");
            string screen = Write("Screen.uitkx", "export VirtualNode Screen() {\n  return (<StatusChip />);\n}\n");
            idx.Refresh(chip);
            idx.Refresh(screen);

            var peers = idx.GetPeerExports(screen, null);
            var diags = new System.Collections.Generic.List<Ruitk.Language.ParseDiagnostic>();
            var directives = Ruitk.Language.Parser.DirectiveParser.Parse(File.ReadAllText(screen), screen, diags);

            var findings = StrictImportDetector.Detect(
                directives, screen, StrictImportDetector.ScrubNonCode(File.ReadAllText(screen)),
                peers, _ => false);

            var f = Assert.Single(findings, x => x.Code == "UITKX2305");
            Assert.Contains("StatusChip", f.Message);
            Assert.Contains("import { StatusChip } from \"./StatusChip\"", f.Message);
        }

        [Fact]
        public void Import_SatisfiesReference_NoDiagnostic()
        {
            var idx = new WorkspaceIndex();
            string chip = Write("StatusChip.uitkx", "export VirtualNode StatusChip() {\n  return (<Label text=\"x\" />);\n}\n");
            string screen = Write("Screen.uitkx",
                "import { StatusChip } from \"./StatusChip\"\n\nexport VirtualNode Screen() {\n  return (<StatusChip />);\n}\n");
            idx.Refresh(chip);
            idx.Refresh(screen);

            var peers = idx.GetPeerExports(screen, null);
            var diags = new System.Collections.Generic.List<Ruitk.Language.ParseDiagnostic>();
            var directives = Ruitk.Language.Parser.DirectiveParser.Parse(File.ReadAllText(screen), screen, diags);

            var findings = StrictImportDetector.Detect(
                directives, screen, StrictImportDetector.ScrubNonCode(File.ReadAllText(screen)),
                peers, _ => false);

            Assert.Empty(findings);
        }
    }
}
