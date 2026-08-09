using System.Collections.Generic;
using System.IO;
using Ruitk.Language;
using Ruitk.Language.Parser;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// ES-modules campaign (Plans~/archive/ES_MODULES_EXECUTION_PLAN.md M2, U-01): the mode-aware
    /// <see cref="EffectiveNamespace.Resolve(bool, string, string, bool)"/> overload — new-syntax
    /// files are FILE-keyed (folder segments + sanitized file stem, unique per file), legacy files
    /// keep folder-keyed derivation verbatim, and the explicit <c>@namespace</c> stamp wins in
    /// both modes. Exercised over real temp files since the resolver walks the filesystem for the
    /// owning asmdef.
    /// </summary>
    public sealed class FileKeyedNamespaceTests : System.IDisposable
    {
        private readonly string _dir;

        public FileKeyedNamespaceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uitkx_filekeyed_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "Widgets"));
            File.WriteAllText(Path.Combine(_dir, "Asm.asmdef"), "{ \"name\": \"Game.UI\" }");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string File_(string relative, string content)
        {
            string p = Path.Combine(_dir, relative);
            File.WriteAllText(p, content);
            return p;
        }

        private static DirectiveSet Parse(string path)
        {
            var diags = new List<ParseDiagnostic>();
            return DirectiveParser.Parse(File.ReadAllText(path), path, diags);
        }

        private static string? ResolveFor(string path)
        {
            var ds = Parse(path);
            return EffectiveNamespace.Resolve(
                ds.HasExplicitNamespace, ds.Namespace, path, fileKeyed: !ds.UsesLegacySyntax);
        }

        [Fact]
        public void TwoSameFolderNewModeFiles_GetDifferentNamespaces()
        {
            string a = File_("Widgets/Board.uitkx", "export VirtualNode Board() { return (<Box/>); }");
            string b = File_("Widgets/Panel.uitkx", "export VirtualNode Panel() { return (<Box/>); }");

            string? nsA = ResolveFor(a);
            string? nsB = ResolveFor(b);
            Assert.Equal("Ruitk.Uitkx.Widgets.Board", nsA);
            Assert.Equal("Ruitk.Uitkx.Widgets.Panel", nsB);
            Assert.NotEqual(nsA, nsB);
        }

        [Fact]
        public void TwoSameFolderLegacyFiles_StillShareTheFolderNamespace()
        {
            string a = File_("Widgets/Board.uitkx", "component Board { return (<Box/>); }");
            string b = File_("Widgets/Panel.uitkx", "component Panel { return (<Box/>); }");

            string? nsA = ResolveFor(a);
            string? nsB = ResolveFor(b);
            Assert.Equal("Ruitk.Uitkx.Widgets", nsA);
            Assert.Equal(nsA, nsB);
        }

        [Fact]
        public void ExplicitNamespaceStamp_WinsInBothModes()
        {
            string legacy = File_("Widgets/L.uitkx", "@namespace My.Stamp\ncomponent L { return (<Box/>); }");
            string plain = File_("Widgets/P.uitkx", "@namespace My.Stamp\nexport VirtualNode P() { return (<Box/>); }");

            Assert.Equal("My.Stamp", ResolveFor(legacy));
            Assert.Equal("My.Stamp", ResolveFor(plain));
        }

        [Fact]
        public void ThreeArgOverload_StaysFolderKeyed()
        {
            // The legacy 3-arg Resolve must keep its exact pre-campaign behavior — HMR binds the
            // 4-arg overload by parameter types, but source callers of the 3-arg form must not
            // silently change meaning.
            string a = File_("Widgets/Board.uitkx", "export VirtualNode Board() { return (<Box/>); }");
            var ds = Parse(a);
            Assert.Equal("Ruitk.Uitkx.Widgets",
                EffectiveNamespace.Resolve(ds.HasExplicitNamespace, ds.Namespace, a));
        }

        [Fact]
        public void CompanionStyleFilename_NewMode_GetsItsOwnSanitizedNamespace()
        {
            string style = File_("Widgets/Board.style.uitkx", "export Style bg = new Style { };");
            Assert.Equal("Ruitk.Uitkx.Widgets.Board_style", ResolveFor(style));
        }
    }
}
