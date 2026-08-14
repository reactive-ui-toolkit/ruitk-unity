#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ruitk.Builder
{
    /// <summary>
    /// Turns the LSP's <c>ruitk/workspaceGraph</c> answer into the canvas model
    /// for ONE tree: resolve the clicked file to its tree root (walk usage edges
    /// upward to a file nobody imports), extract the connected subgraph, classify
    /// node kinds, seed default positions for files without persisted ones.
    /// </summary>
    internal static class BuilderGraphService
    {
        public static async Task<BuilderGraph> LoadTreeAsync(BuilderLspClient client, string focusFile)
        {
            JToken raw = await client.RequestWorkspaceGraph();
            var nodes = (raw?["nodes"] ?? raw?["Nodes"]) as JArray ?? new JArray();
            var edges = (raw?["edges"] ?? raw?["Edges"]) as JArray ?? new JArray();

            var exportsByFile = new Dictionary<string, (List<string> Names, BuilderNodeKind Kind)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes)
            {
                string file = Str(n, "file", "File");
                if (string.IsNullOrEmpty(file))
                    continue;
                var names = new List<string>();
                BuilderNodeKind kind = BuilderNodeKind.Unknown;
                if ((n["exports"] ?? n["Exports"]) is JArray exports)
                {
                    foreach (var e in exports)
                    {
                        string name = Str(e, "name", "Name");
                        if (!string.IsNullOrEmpty(name))
                            names.Add(name);
                        var k = ParseKind(Str(e, "kind", "Kind"));
                        if (kind == BuilderNodeKind.Unknown
                            || (kind != BuilderNodeKind.Component && k == BuilderNodeKind.Component))
                            kind = k;
                    }
                }
                exportsByFile[Path.GetFullPath(file)] = (names, kind);
            }

            var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var importedBy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var edgeList = new List<(string From, string To, string Specifier, List<string> Names)>();
            foreach (var e in edges)
            {
                string from = Str(e, "fromFile", "FromFile");
                string to = Str(e, "toFile", "ToFile");
                string spec = Str(e, "specifier", "Specifier");
                if (string.IsNullOrEmpty(from))
                    continue;
                from = Path.GetFullPath(from);
                string toFull = string.IsNullOrEmpty(to) ? "" : Path.GetFullPath(to);

                var names = new List<string>();
                if ((e["names"] ?? e["Names"]) is JArray arr)
                    foreach (var n in arr)
                        names.Add(n.Value<string>());

                edgeList.Add((from, toFull, spec, names));
                if (toFull.Length > 0)
                {
                    Link(adjacency, from, toFull);
                    Link(adjacency, toFull, from);
                    Link(importedBy, toFull, from);
                }
            }

            string focus = Path.GetFullPath(focusFile);
            var member = ConnectedComponent(adjacency, focus);
            member.Add(focus);

            string root = ResolveRoot(importedBy, member, focus);

            var graph = new BuilderGraph { RootPath = root };
            var indexByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in member)
            {
                exportsByFile.TryGetValue(file, out var info);
                var node = new BuilderCanvasNode
                {
                    FilePath = file,
                    Title = Path.GetFileNameWithoutExtension(file).Replace(".style", "").Replace(".hooks", ""),
                    Kind = ClassifyByPathAndExports(file, info.Kind),
                    IsReadOnly = BuilderWorkspace.IsReadOnlyLocation(file),
                    Exports = info.Names ?? new List<string>(),
                };
                indexByFile[file] = graph.Nodes.Count;
                graph.Nodes.Add(node);
            }

            foreach (var (from, to, spec, names) in edgeList)
            {
                if (!indexByFile.TryGetValue(from, out int fromIdx))
                    continue;
                int toIdx = to.Length > 0 && indexByFile.TryGetValue(to, out int t) ? t : -1;
                graph.Edges.Add(new BuilderCanvasEdge
                {
                    FromIndex = fromIdx,
                    ToIndex = toIdx,
                    Specifier = spec,
                    Names = names,
                    TargetKind = toIdx >= 0 ? graph.Nodes[toIdx].Kind : BuilderNodeKind.Unknown,
                });
            }

            SeedDefaultPositions(graph, indexByFile.TryGetValue(root, out int rootIdx) ? rootIdx : 0);
            return graph;
        }

        private static void Link(Dictionary<string, HashSet<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var set))
                map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(value);
        }

        private static HashSet<string> ConnectedComponent(
            Dictionary<string, HashSet<string>> adjacency, string start)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(start);
            seen.Add(start);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (!adjacency.TryGetValue(current, out var neighbors))
                    continue;
                foreach (string n in neighbors)
                    if (seen.Add(n))
                        queue.Enqueue(n);
            }
            return seen;
        }

        /// <summary>
        /// The tree root = a member file nobody in the member set imports,
        /// reachable from the focus by walking importers upward. Multi-root
        /// subgraphs pick deterministically (ordinal-smallest path) so the
        /// per-root config key is stable across sessions.
        /// </summary>
        private static string ResolveRoot(
            Dictionary<string, HashSet<string>> importedBy,
            HashSet<string> member,
            string focus)
        {
            string best = null;
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { focus };
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (current.Count > 0)
            {
                var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in current)
                {
                    if (!visited.Add(file))
                        continue;
                    bool hasImporter = importedBy.TryGetValue(file, out var importers)
                        && importers.Count > 0;
                    if (!hasImporter)
                    {
                        if (best == null || string.CompareOrdinal(file, best) < 0)
                            best = file;
                    }
                    else
                    {
                        foreach (string importer in importers)
                            if (member.Contains(importer))
                                next.Add(importer);
                    }
                }
                current = next;
            }
            return best ?? focus;
        }

        private static BuilderNodeKind ClassifyByPathAndExports(string file, BuilderNodeKind exportKind)
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".style.uitkx", StringComparison.OrdinalIgnoreCase))
                return BuilderNodeKind.Style;
            if (name.EndsWith(".hooks.uitkx", StringComparison.OrdinalIgnoreCase))
                return BuilderNodeKind.Hook;
            if (exportKind == BuilderNodeKind.Unknown)
                return BuilderNodeKind.Component;
            return exportKind;
        }

        private static BuilderNodeKind ParseKind(string kind) => kind switch
        {
            "Component" => BuilderNodeKind.Component,
            "Hook" => BuilderNodeKind.Hook,
            "Module" => BuilderNodeKind.Util,
            "Util" => BuilderNodeKind.Util,
            _ => BuilderNodeKind.Unknown,
        };

        /// <summary>Root at origin, importers fan right in BFS depth columns.</summary>
        private static void SeedDefaultPositions(BuilderGraph graph, int rootIndex)
        {
            var depth = new int[graph.Nodes.Count];
            for (int i = 0; i < depth.Length; i++)
                depth[i] = -1;
            var queue = new Queue<int>();
            depth[rootIndex] = 0;
            queue.Enqueue(rootIndex);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (var edge in graph.Edges)
                {
                    if (edge.FromIndex == current && edge.ToIndex >= 0 && depth[edge.ToIndex] < 0)
                    {
                        depth[edge.ToIndex] = depth[current] + 1;
                        queue.Enqueue(edge.ToIndex);
                    }
                }
            }

            var perColumn = new Dictionary<int, int>();
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                int d = depth[i] < 0 ? 0 : depth[i];
                perColumn.TryGetValue(d, out int row);
                perColumn[d] = row + 1;
                var node = graph.Nodes[i];
                if (node.X == 0f && node.Y == 0f)
                {
                    node.X = 80f + d * 420f;
                    node.Y = 80f + row * 260f;
                }
            }
        }

        private static string Str(JToken token, string camel, string pascal) =>
            token?.Value<string>(camel) ?? token?.Value<string>(pascal) ?? "";
    }
}
#endif
