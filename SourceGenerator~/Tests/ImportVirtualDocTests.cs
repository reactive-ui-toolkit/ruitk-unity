using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Ruitk.Language;
using Ruitk.Language.Lowering;
using Ruitk.Language.Parser;
using Ruitk.Language.Roslyn;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// Virtual-document import lowering (import/export grammar §10): a file's <c>import</c>s become
    /// hidden using-static (hook containers) / alias (modules) scaffold lines so C# IntelliSense in
    /// setup code resolves imported symbols.
    /// </summary>
    public sealed class ImportVirtualDocTests : IDisposable
    {
        private static readonly VirtualDocumentGenerator _gen = new();
        private readonly string _dir;

        public ImportVirtualDocTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uitkx-vdoc-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }
        public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

        private string Write(string name, string body)
        {
            string full = Path.Combine(_dir, name);
            File.WriteAllText(full, body);
            return full;
        }

        private string Generate(string importerName, string source)
        {
            string path = Path.Combine(_dir, importerName);
            File.WriteAllText(path, source);
            var diags = new List<ParseDiagnostic>();
            var directives = DirectiveParser.Parse(source, path, diags);
            var parsed = UitkxParser.Parse(source, path, directives, diags);
            var nodes = CanonicalLowering.LowerToRenderRoots(directives, parsed, path);
            var pr = new ParseResult(directives, nodes, ImmutableArray.CreateRange(diags));
            return _gen.Generate(pr, source, path).Text;
        }

        [Fact]
        public void ImportedHook_LowersToUsingStaticContainer()
        {
            Write("Counter.hooks.uitkx", "@namespace My.Ns\nexport void useCounter() {\n}\n");
            string vdoc = Generate("Screen.uitkx",
                "import { useCounter } from \"./Counter.hooks\"\n@namespace My.Other\nVirtualNode Screen() {\n    var c = useCounter();\n    return (<Box />);\n}\n");

            // 0.16.0: member imports lower to the target's per-file __Exports container
            // (inside-namespace, global::-qualified).
            Assert.Contains("using static global::My.Ns.__Exports;", vdoc);
        }

        [Fact]
        public void StarImportOfMemberFile_LowersToExportsAlias()
        {
            // 0.16.0: the legacy module-name alias became the star-import alias onto the
            // target's per-file __Exports container (call sites Palette.Gap unchanged).
            Write("Palette.uitkx", "@namespace My.Shared\nexport int Gap = 4;\n");
            string vdoc = Generate("Screen.uitkx",
                "import * as Palette from \"./Palette\"\n@namespace My.Ns\nVirtualNode Screen() {\n    var g = Palette.Gap;\n    return (<Box />);\n}\n");

            Assert.Contains("using Palette = global::My.Shared.__Exports;", vdoc);
        }

        [Fact]
        public void ImportedModule_SameNamespace_NoAlias()
        {
            // Same namespace → NO alias (SG parity via ImportScopeFacts: the bare name already
            // resolves through the shared namespace; the SG skips it to avoid CS0576).
            Write("Palette.uitkx", "@namespace My.Ns\nexport module Palette {\n    public const int Gap = 4;\n}\n");
            string vdoc = Generate("Screen.uitkx",
                "import { Palette } from \"./Palette\"\n@namespace My.Ns\nVirtualNode Screen() {\n    var g = Palette.Gap;\n    return (<Box />);\n}\n");

            Assert.DoesNotContain("using Palette =", vdoc);
        }

        [Fact]
        public void NonExportedTarget_NoUsing()
        {
            Write("Priv.uitkx", "@namespace My.Ns\nmodule Priv {\n    public const int X = 1;\n}\n");
            string vdoc = Generate("Screen.uitkx",
                "import { Priv } from \"./Priv\"\n@namespace My.Ns\nVirtualNode Screen() {\n    return (<Box />);\n}\n");

            Assert.DoesNotContain("using Priv =", vdoc);
        }
    }
}
