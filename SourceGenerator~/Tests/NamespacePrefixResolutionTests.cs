using System.IO;
using Ruitk.Language;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// EffectiveNamespace.ResolveNamespacePrefix precedence (namespace-import unification plan,
    /// feature 3), most-specific WINS: uitkx.config.json "namespacePrefix" → owning .asmdef
    /// "rootNamespace" → Ruitk.Uitkx default. The asmdef "name" is deliberately NOT used.
    /// Exercised over real temp files since the resolver walks the filesystem.
    /// </summary>
    public sealed class NamespacePrefixResolutionTests : System.IDisposable
    {
        private readonly string _dir;

        public NamespacePrefixResolutionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uitkx_nsprefix_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string File_(string name, string content)
        {
            string p = Path.Combine(_dir, name);
            File.WriteAllText(p, content);
            return p;
        }

        [Fact]
        public void NoConfig_NoRootNamespace_UsesDefault()
        {
            // An asmdef with a NAME but no rootNamespace must NOT re-root (name is ignored).
            File_("Asm.asmdef", "{ \"name\": \"MyGame.UI\" }");
            string uitkx = File_("Screen.uitkx", "VirtualNode Screen() { return (<Box/>); }");

            Assert.Equal(NamespaceDerivation.Root, EffectiveNamespace.ResolveNamespacePrefix(uitkx));
        }

        [Fact]
        public void AsmdefRootNamespace_Wins_OverDefault()
        {
            File_("Asm.asmdef", "{ \"name\": \"MyGame.UI\", \"rootNamespace\": \"MyGame\" }");
            string uitkx = File_("Screen.uitkx", "VirtualNode Screen() { return (<Box/>); }");

            Assert.Equal("MyGame", EffectiveNamespace.ResolveNamespacePrefix(uitkx));
        }

        [Fact]
        public void ConfigNamespacePrefix_Wins_OverAsmdefRootNamespace()
        {
            File_("Asm.asmdef", "{ \"name\": \"MyGame.UI\", \"rootNamespace\": \"MyGame\" }");
            File_("uitkx.config.json", "{ \"namespacePrefix\": \"UI.App\" }");
            string uitkx = File_("Screen.uitkx", "VirtualNode Screen() { return (<Box/>); }");

            Assert.Equal("UI.App", EffectiveNamespace.ResolveNamespacePrefix(uitkx));
        }

        [Fact]
        public void EmptyRootNamespace_FallsThroughToDefault()
        {
            File_("Asm.asmdef", "{ \"name\": \"MyGame.UI\", \"rootNamespace\": \"\" }");
            string uitkx = File_("Screen.uitkx", "VirtualNode Screen() { return (<Box/>); }");

            Assert.Equal(NamespaceDerivation.Root, EffectiveNamespace.ResolveNamespacePrefix(uitkx));
        }
    }
}
