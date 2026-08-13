using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// TD-009 family-corpus drift gate (leg 3). The scanner corpus
    /// (<c>ide-extensions~/lsp-server/test-fixtures/uitkx-scanner-cases.json</c>) is mirrored
    /// byte-identically family-wide across the three engine repos; its <c>_tiers.familyCore</c>
    /// sections hash (via <c>scripts/corpus-hash.mjs</c>, prefix-normalized UETKX|GUITKX|UITKX → TKX)
    /// to a single frozen value that ALL three repos must agree on. The canonical hash algorithm
    /// lives ONCE in <c>corpus-hash.mjs</c> (adopted verbatim from the Unreal leg — its
    /// <c>localeCompare('en')</c> row ordering makes a divergent C# reimplementation a byte-hazard),
    /// so this suite (a) pins the frozen constant self-contained, and (b) drives that one canonical
    /// script when node is on PATH.
    /// </summary>
    public sealed class ImportCorpusManifestTests
    {
        /// <summary>The family-frozen hash (plans/family-corpus.hash). Re-frozen by the Unity
        /// leg's 0.16.0 legacy-syntax removal (D4): the 16 wrapper-scaffolded fileScan cases
        /// modernized in place (plain declaration heads; the two module-subject cases became
        /// exported-value subjects). Siblings adopt the staged case edits
        /// (Plans~/family-corpus-0.16-legacy-removal.patch) on their own removal waves and
        /// re-converge on this value — until then the cross-repo hash match is EXPECTED to
        /// diverge (recorded in Plans~/REMAINING_WORK.md).</summary>
        private const string FrozenFamilyHash =
            "f8c06ee642bd0a0d021ba17cbe7e2739762bc5c47be8e09dfb2c330a50bab251";

        [Fact]
        public void PinnedHash_EqualsFrozenFamilyValue()
        {
            string repo = FindRepoRoot();
            string pinnedPath = Path.Combine(repo, "Plans~", "family-corpus.hash");
            Assert.True(File.Exists(pinnedPath), $"missing pinned hash file: {pinnedPath}");

            string pinned = File.ReadAllText(pinnedPath).Trim();
            Assert.Equal(FrozenFamilyHash, pinned);
        }

        [Fact]
        public void MirroredCorpus_HasFamilyCoreTierPartition()
        {
            string repo = FindRepoRoot();
            string corpus = Path.Combine(
                repo, "ide-extensions~", "lsp-server", "test-fixtures", "uitkx-scanner-cases.json");
            Assert.True(File.Exists(corpus), $"missing mirrored corpus: {corpus}");

            using var doc = JsonDocument.Parse(File.ReadAllText(corpus));
            Assert.True(doc.RootElement.TryGetProperty("_tiers", out var tiers),
                "corpus is missing `_tiers`");
            Assert.True(tiers.TryGetProperty("familyCore", out var fam) &&
                        fam.ValueKind == JsonValueKind.Array,
                "corpus is missing `_tiers.familyCore` — cannot partition/hash");

            // The three frozen family-core sections must be present and non-empty (byte-identical mirror).
            foreach (var section in new[] { "skipNoncodeMarkup", "findMatchingMarkup", "fileScan" })
            {
                Assert.True(doc.RootElement.TryGetProperty(section, out var arr) &&
                            arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0,
                    $"family-core section `{section}` missing/empty in the mirrored corpus");
            }
        }

        /// <summary>
        /// Recompute gate: runs the canonical <c>scripts/corpus-hash.mjs --check</c> (the SAME script
        /// wired into the family CI job) and asserts it exits 0 (computed == pinned). Skipped only when
        /// node is not on PATH — the family CI gate always has node, and the two self-contained tests
        /// above still fence the pinned constant + tier partition.
        /// </summary>
        [Fact]
        public void CorpusHashScript_Recomputes_Matches()
        {
            string repo = FindRepoRoot();
            string? node = ResolveNode();
            if (node == null)
                return; // node not on PATH (dev box without node) — CI covers this via the same script

            var psi = new ProcessStartInfo
            {
                FileName = node,
                Arguments = "scripts/corpus-hash.mjs --check",
                WorkingDirectory = repo,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null)
                throw new Xunit.Sdk.XunitException("failed to start node process");
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            Assert.True(p.HasExited, "corpus-hash.mjs did not exit within 60s");
            Assert.True(p.ExitCode == 0,
                $"corpus-hash.mjs --check failed (exit {p.ExitCode}):\n{stderr}");
        }

        private static string? ResolveNode()
        {
            string exe = OperatingSystem.IsWindows() ? "node.exe" : "node";
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string cand = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(cand)) return cand;
                }
                catch { /* malformed PATH entry */ }
            }
            return null;
        }

        /// <summary>Walk up from the test assembly to the repo root (folder holding Plans~/family-corpus.hash).</summary>
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Plans~", "family-corpus.hash")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                "could not locate repo root (Plans~/family-corpus.hash) from " + AppContext.BaseDirectory);
        }
    }
}
