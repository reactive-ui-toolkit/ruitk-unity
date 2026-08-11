using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ruitk.Language;
using UitkxLanguageServer;
using Xunit;

namespace UitkxLanguageServer.Tests
{
    /// <summary>
    /// The value-import cycle detection (UITKX2306): the pure graph algorithm
    /// (<see cref="UitkxImportGraph.FindCycle"/>) and its integration with the workspace index's
    /// import/export tables (hooks/modules form value edges; components are exempt).
    /// </summary>
    public sealed class ValueCycleTests : IDisposable
    {
        private readonly string _root;
        public ValueCycleTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "uitkx-cycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }
        public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

        private string Write(string rel, string body)
        {
            string full = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
            return full;
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> Edges(
            params (string From, string[] To)[] e)
            => e.ToDictionary(x => x.From, x => (IReadOnlyList<string>)x.To, StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void FindCycle_DetectsTwoNodeCycle()
        {
            var cycle = UitkxImportGraph.FindCycle(Edges(("A", new[] { "B" }), ("B", new[] { "A" })));
            Assert.NotNull(cycle);
            Assert.Equal(cycle![0], cycle[cycle.Count - 1]); // closes the loop
            Assert.Contains("A", cycle);
            Assert.Contains("B", cycle);
        }

        [Fact]
        public void FindCycle_AcyclicGraph_ReturnsNull()
        {
            var cycle = UitkxImportGraph.FindCycle(Edges(("A", new[] { "B" }), ("B", new[] { "C" })));
            Assert.Null(cycle);
        }

        [Fact]
        public void FindCycle_SelfLoop_Detected()
        {
            var cycle = UitkxImportGraph.FindCycle(Edges(("A", new[] { "A" })));
            Assert.NotNull(cycle);
        }

        [Fact]
        public void IndexTables_BuildValueCycle_BetweenMutuallyImportingHookFiles()
        {
            var idx = new WorkspaceIndex();
            string a = Write("A.hooks.uitkx",
                "import { useB } from \"./B.hooks\"\n\nexport void useA() { return useB(); }\n");
            string b = Write("B.hooks.uitkx",
                "import { useA } from \"./A.hooks\"\n\nexport void useB() { return useA(); }\n");
            idx.Refresh(a);
            idx.Refresh(b);

            var edges = BuildValueEdges(idx);

            var cycle = UitkxImportGraph.FindCycle(edges);
            Assert.NotNull(cycle);
            Assert.Contains(cycle!, c => c.EndsWith("A.hooks.uitkx"));
            Assert.Contains(cycle!, c => c.EndsWith("B.hooks.uitkx"));
        }

        /// <summary>Build value edges the way DiagnosticsPublisher does (normalize both endpoints).</summary>
        private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildValueEdges(WorkspaceIndex idx)
        {
            var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, specifier, names) in idx.GetImportEdges(null))
            {
                string dir = Path.GetDirectoryName(file)!.Replace('\\', '/');
                string? tgt = ImportResolver.MapSpecifierToPath(dir, specifier, dir, out _);
                if (tgt is null) continue;
                bool value = names.Any(n =>
                {
                    var k = idx.GetExportKind(tgt, n);
                    return k == StrictImportDetector.ExportKind.Hook
                        || k == StrictImportDetector.ExportKind.Module
                        || k == StrictImportDetector.ExportKind.Util;
                });
                if (!value) continue;
                string key = file.Replace('\\', '/');
                if (!edges.TryGetValue(key, out var l)) edges[key] = l = new List<string>();
                ((List<string>)l).Add(tgt);
            }
            return edges;
        }

        [Fact]
        public void ComponentImports_AreNotValueEdges_NoCycle()
        {
            var idx = new WorkspaceIndex();
            string a = Write("A.uitkx", "import { B } from \"./B\"\n\nexport VirtualNode A() { return (<B />); }\n");
            string b = Write("B.uitkx", "import { A } from \"./A\"\n\nexport VirtualNode B() { return (<A />); }\n");
            idx.Refresh(a);
            idx.Refresh(b);

            // Components are exempt — no value edges, so no cycle.
            Assert.Null(UitkxImportGraph.FindCycle(BuildValueEdges(idx)));
        }
    }
}
