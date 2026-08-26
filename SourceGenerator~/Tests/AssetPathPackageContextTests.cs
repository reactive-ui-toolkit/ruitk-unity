using System;
using System.IO;
using Ruitk.Language;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// GEN-2: package-resident <c>.uitkx</c> files resolve their asset directory to the
    /// Unity asset-path form <c>Packages/&lt;manifest name&gt;/&lt;dir&gt;</c> — keyed by the
    /// package.json <c>name</c>, never the physical folder. The HMR-side mirror
    /// (<c>UitkxHmrController.HmrAssetPathUtil.GetAssetDir</c> + <c>TryGetPackageContext</c>)
    /// must be kept byte-for-byte identical; these pairs pin the canonical rule it mirrors.
    /// </summary>
    public sealed class AssetPathPackageContextTests : IDisposable
    {
        private readonly string _root;

        public AssetPathPackageContextTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "uitkx-pkgctx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            AssetPathUtil.InvalidatePackageContextCache();
        }

        public void Dispose()
        {
            AssetPathUtil.InvalidatePackageContextCache();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string MakePackage(string folderName, string packageName)
        {
            string pkgRoot = Path.Combine(_root, folderName);
            Directory.CreateDirectory(pkgRoot);
            File.WriteAllText(
                Path.Combine(pkgRoot, "package.json"),
                "{\n  \"name\": \"" + packageName + "\",\n  \"version\": \"1.0.0\"\n}");
            return pkgRoot;
        }

        [Fact]
        public void GetAssetDir_PackageResidentFile_UsesManifestName()
        {
            string pkgRoot = MakePackage("SomePhysicalFolder", "com.test.pkg");
            string file = Path.Combine(pkgRoot, "UI", "Panel.uitkx");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "");

            Assert.Equal("Packages/com.test.pkg/UI", AssetPathUtil.GetAssetDir(file));
        }

        [Fact]
        public void GetAssetDir_FileAtPackageRoot_ReturnsPackageRootAssetPath()
        {
            string pkgRoot = MakePackage("Pkg", "com.test.rootfile");
            string file = Path.Combine(pkgRoot, "Panel.uitkx");
            File.WriteAllText(file, "");

            Assert.Equal("Packages/com.test.rootfile", AssetPathUtil.GetAssetDir(file));
        }

        [Fact]
        public void GetAssetDir_FolderNameDiffersFromPackageName_NameWins()
        {
            string pkgRoot = MakePackage("renamed-checkout-folder", "com.owner.realname");
            string file = Path.Combine(pkgRoot, "Nested", "Deep", "X.uitkx");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "");

            Assert.Equal("Packages/com.owner.realname/Nested/Deep", AssetPathUtil.GetAssetDir(file));
        }

        [Fact]
        public void GetAssetDir_AssetsSegmentStillWinsOverPackageWalk()
        {
            string pkgRoot = MakePackage("Proj", "com.should.not.win");
            string file = Path.Combine(pkgRoot, "Assets", "UI", "Panel.uitkx");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "");

            Assert.Equal("Assets/UI", AssetPathUtil.GetAssetDir(file));
        }

        [Fact]
        public void GetAssetDir_NoManifestAnywhere_ReturnsNull()
        {
            string dir = Path.Combine(_root, "loose");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "Orphan.uitkx");
            File.WriteAllText(file, "");

            Assert.Null(AssetPathUtil.GetAssetDir(file));
        }

        [Fact]
        public void GetAssetDir_ManifestWithoutName_ReturnsNull()
        {
            string pkgRoot = Path.Combine(_root, "nameless");
            Directory.CreateDirectory(pkgRoot);
            File.WriteAllText(Path.Combine(pkgRoot, "package.json"), "{ \"version\": \"1.0.0\" }");
            string file = Path.Combine(pkgRoot, "X.uitkx");
            File.WriteAllText(file, "");

            Assert.Null(AssetPathUtil.GetAssetDir(file));
        }

        [Fact]
        public void TryGetPackageContext_ReturnsPhysicalRootAndName()
        {
            string pkgRoot = MakePackage("Phys", "com.ctx.check");
            string file = Path.Combine(pkgRoot, "Sub", "Y.uitkx");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "");

            Assert.True(AssetPathUtil.TryGetPackageContext(file, out string root, out string name));
            Assert.Equal(Path.GetFullPath(pkgRoot), Path.GetFullPath(root));
            Assert.Equal("com.ctx.check", name);
        }

        [Fact]
        public void ResolveAssetPath_WithPackageDir_ProducesPackageAssetPath()
        {
            string resolved = AssetPathUtil.ResolveAssetPath("Packages/com.test.pkg/UI", "./icons/gear.png");
            Assert.Equal("Packages/com.test.pkg/UI/icons/gear.png", resolved);

            string bare = AssetPathUtil.ResolveAssetPath("Packages/com.test.pkg/UI", "styles.uss");
            Assert.Equal("Packages/com.test.pkg/UI/styles.uss", bare);

            string up = AssetPathUtil.ResolveAssetPath("Packages/com.test.pkg/UI", "../shared/base.uss");
            Assert.Equal("Packages/com.test.pkg/shared/base.uss", up);
        }
    }
}
