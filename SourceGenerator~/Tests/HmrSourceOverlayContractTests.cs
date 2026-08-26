using System.IO;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// VE-05 contract pins: the HMR compiler's .uitkx reads route through the
    /// SourceOverlay seam (the RUITK Builder compiles unsaved buffers through it).
    /// The compiler is Unity-Editor-compiled and cannot execute here, so these pin
    /// the source text the same way HmrAuditWaveContractTests do — a refactor that
    /// reverts a read site to raw disk IO fails loudly instead of silently breaking
    /// buffer preview.
    /// </summary>
    public sealed class HmrSourceOverlayContractTests
    {
        private static string RepoRoot()
        {
            var dir = Directory.GetCurrentDirectory();
            while (dir != null && !File.Exists(Path.Combine(dir, "package.json")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir;
        }

        private static string CompilerSource() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Editor", "HMR", "UitkxHmrCompiler.cs"));

        private static string ControllerSource() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Editor", "HMR", "UitkxHmrController.cs"));

        [Fact]
        public void Compiler_HasSourceOverlaySeam()
        {
            string src = CompilerSource();
            Assert.Contains("internal Func<string, string> SourceOverlay { get; set; }", src);
            Assert.Contains("private string ReadUitkxText(string path)", src);
            Assert.Contains("private bool UitkxSourceExists(string path)", src);
        }

        [Fact]
        public void Compiler_UitkxReads_GoThroughOverlay()
        {
            string src = CompilerSource();
            int overlayReads = 0;
            int idx = 0;
            while ((idx = src.IndexOf("ReadUitkxText(", idx + 1, System.StringComparison.Ordinal)) >= 0)
                overlayReads++;
            Assert.True(
                overlayReads >= 6,
                $"expected the definition + at least 5 .uitkx read sites on ReadUitkxText, found {overlayReads} occurrences");
            Assert.DoesNotContain("string source = ReadTextWithRetry(uitkxPath);", src);
            Assert.DoesNotContain("string companionSource = ReadTextWithRetry(file);", src);
            Assert.DoesNotContain("string src = ReadTextWithRetry(targetFile);", src);
        }

        [Fact]
        public void Compiler_ImportTargetGates_UseOverlayAwareExists()
        {
            string src = CompilerSource();
            Assert.DoesNotContain(
                "if (string.IsNullOrEmpty(targetFile) || !File.Exists(targetFile))",
                src);
            Assert.Contains(
                "if (string.IsNullOrEmpty(targetFile) || !UitkxSourceExists(targetFile))",
                src);
        }

        [Fact]
        public void Controller_AssetCacheSync_HasSourceTextOverload()
        {
            string src = ControllerSource();
            Assert.Contains(
                "internal static void SyncAssetCacheForHmr(string uitkxPath, string sourceText)",
                src);
        }
    }
}
