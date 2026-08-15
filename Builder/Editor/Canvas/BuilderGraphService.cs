#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ruitk.Language.Nodes;

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
        public static async Task<BuilderGraph> LoadTreeAsync(
            BuilderLspClient client, string focusFile, Func<string, string> readText = null)
        {
            JToken raw = await RequestGraphWithRetry(client);
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

            foreach (var node in graph.Nodes)
            {
                try
                {
                    PopulateCardDetail(node, readText);
                }
                catch (Exception)
                {
                    // A file that cannot be read/parsed still gets its header card.
                }
            }

            SeedDefaultPositions(graph, indexByFile.TryGetValue(root, out int rootIdx) ? rootIdx : 0);
            return graph;
        }

        /// <summary>OmniSharp cancels in-flight requests with -32801 (Content
        /// Modified) whenever a didOpen/didChange lands — which the builder's
        /// own preview didOpen does while the graph request waits out the
        /// initial scan. The error is transient BY DEFINITION, so retry with
        /// backoff instead of surfacing an empty window.</summary>
        private static async Task<JToken> RequestGraphWithRetry(BuilderLspClient client)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    return await client.RequestWorkspaceGraph();
                }
                catch (Exception ex)
                {
                    string text = ex.ToString();
                    bool transient = text.Contains("-32801") || text.Contains("Content Modified");
                    last = ex;
                    if (!transient)
                        throw;
                    await Task.Delay(400 * (attempt + 1));
                }
            }
            throw last ?? new InvalidOperationException("workspace graph request failed");
        }

        private static readonly Regex s_hookCall = new Regex(
            @"(?:var\s*\(([^)]*)\)\s*=\s*|var\s+(\w+)\s*=\s*)?\b(use[A-Z][A-Za-z0-9]*)\s*(?:<[^>\n]*>)?\s*\(",
            RegexOptions.Compiled);

        /// <summary>Fills the POC-card sections: imports, hooks-and-state lines,
        /// and the flattened return-markup tree, from the live buffer when one
        /// is open (readText) or disk otherwise.</summary>
        public static void PopulateCardDetail(BuilderCanvasNode node, Func<string, string> readText)
        {
            string text = readText?.Invoke(node.FilePath);
            if (text == null && File.Exists(node.FilePath))
                text = File.ReadAllText(node.FilePath);
            if (text == null)
                return;
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            node.Imports.Clear();
            node.Body.Clear();
            node.Markup.Clear();
            node.IslandLines.Clear();
            node.Signature = ExtractSignature(text, node);
            ExtractIslandLines(text, node);

            var parsed = BuilderLanguage.Parse(text, node.FilePath);

            foreach (var import in parsed.Directives.Imports)
            {
                string spec = import.Specifier ?? "";
                if (spec.StartsWith("@", StringComparison.Ordinal))
                    continue;
                string names = import.Names.IsDefaultOrEmpty || import.Names.Length == 0
                    ? "*"
                    : "{ " + string.Join(", ", import.Names) + " }";
                int dotKind = spec.EndsWith(".style", StringComparison.OrdinalIgnoreCase) ? 7
                    : spec.EndsWith(".hooks", StringComparison.OrdinalIgnoreCase) ? 6
                    : 5;
                node.Imports.Add(new BuilderCardLine
                {
                    Text = names + "  ←  " + spec,
                    Kind = BuilderCardLineKind.Import,
                    BadgeKind = dotKind,
                });
            }

            int hookCount = 0;
            foreach (Match m in s_hookCall.Matches(text))
            {
                if (hookCount == 8)
                {
                    node.Body.Add(new BuilderCardLine { Text = "…", Kind = BuilderCardLineKind.Plain });
                    break;
                }
                string lhs = m.Groups[1].Success ? m.Groups[1].Value.Trim()
                    : m.Groups[2].Success ? m.Groups[2].Value.Trim()
                    : null;
                string hook = m.Groups[3].Value;
                node.Body.Add(new BuilderCardLine
                {
                    Text = lhs == null ? hook : hook + "  →  " + lhs,
                    Kind = BuilderCardLineKind.Hook,
                });
                hookCount++;
            }

            var registered = Ruitk.Elements.ElementRegistryProvider.GetDefaultRegistry().RegisteredNames;
            var registeredSet = new HashSet<string>(registered, StringComparer.Ordinal);
            int budget = 14;
            foreach (var root in parsed.RootNodes)
                WalkMarkup(root, 0, node.Markup, registeredSet, ref budget);
            if (budget <= 0)
                node.Markup.Add(new BuilderCardLine { Text = "…", Kind = BuilderCardLineKind.Plain });

            node.ExportDetail.Clear();
            if (node.Kind == BuilderNodeKind.Style)
                ParseStyleDetail(text, node);
            else if (node.Kind == BuilderNodeKind.Util)
                ParseUtilDetail(text, node);
            else if (node.Markup.Count == 0 && node.Exports.Count > 0)
            {
                foreach (string export in node.Exports)
                    node.Body.Add(new BuilderCardLine
                    {
                        Text = export,
                        Kind = BuilderCardLineKind.Export,
                    });
            }
        }

        private static readonly Regex s_exportHeader = new Regex(
            @"^export\s+(?:VirtualNode|\([^)]*\))\s+(\w+)\s*(\([^)]*\))", RegexOptions.Compiled);

        /// <summary>POC signature line: bold name + dimmed props signature,
        /// pulled from the export header (component and hook files).</summary>
        private static string ExtractSignature(string text, BuilderCanvasNode node)
        {
            if (node.Kind != BuilderNodeKind.Component && node.Kind != BuilderNodeKind.Hook)
                return node.Title;
            foreach (string raw in text.Split('\n'))
            {
                var match = s_exportHeader.Match(raw.Trim());
                if (match.Success)
                    return match.Groups[1].Value + match.Groups[2].Value;
            }
            return node.Title + "()";
        }

        /// <summary>POC code island: the setup statements between the header and
        /// the return that are not hook declarations (capped at 5).</summary>
        private static void ExtractIslandLines(string text, BuilderCanvasNode node)
        {
            if (node.Kind != BuilderNodeKind.Component && node.Kind != BuilderNodeKind.Hook)
                return;
            bool inBody = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (!inBody)
                {
                    if (s_exportHeader.IsMatch(line))
                        inBody = true;
                    continue;
                }
                if (line.StartsWith("return (", StringComparison.Ordinal))
                    break;
                if (line.Length == 0
                    || s_hookCall.IsMatch(line)
                    || line.StartsWith("import ", StringComparison.Ordinal))
                    continue;
                node.IslandLines.Add(line);
                if (node.IslandLines.Count == 5)
                {
                    node.IslandLines.Add("…");
                    break;
                }
            }
        }

        /// <summary>POC util card: value exports as-is; function exports as
        /// "sig {" + body lines (L2) + "}".</summary>
        private static void ParseUtilDetail(string text, BuilderCanvasNode node)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("export ", StringComparison.Ordinal))
                    continue;
                if (line.Contains("=") && !line.Contains("("))
                {
                    node.ExportDetail.Add(new BuilderCardLine
                    {
                        Text = line,
                        Kind = BuilderCardLineKind.Export,
                        SourceLine = i + 1,
                    });
                    continue;
                }
                if (!line.Contains("("))
                    continue;
                node.ExportDetail.Add(new BuilderCardLine
                {
                    Text = line.TrimEnd('{', ' ') + " {",
                    Kind = BuilderCardLineKind.Export,
                    SourceLine = i + 1,
                });
                int j = i + 1;
                for (; j < lines.Length; j++)
                {
                    string body = lines[j].Trim();
                    if (body.StartsWith("}", StringComparison.Ordinal))
                        break;
                    if (body.Length == 0)
                        continue;
                    node.ExportDetail.Add(new BuilderCardLine
                    {
                        Text = body,
                        Kind = BuilderCardLineKind.Plain,
                        Depth = 1,
                        SourceLine = j + 1,
                    });
                }
                node.ExportDetail.Add(new BuilderCardLine
                {
                    Text = "}",
                    Kind = BuilderCardLineKind.Plain,
                    SourceLine = j + 1,
                });
                i = j;
            }
        }

        private static readonly Regex s_styleExport = new Regex(
            @"^export\s+Style\s+(\w+)\s*=\s*new\s+Style\s*\{", RegexOptions.Compiled);

        /// <summary>POC style card: per export a "name = new Style {" header,
        /// its entries (L2), a "+ entry" affordance, and the closing brace.</summary>
        private static void ParseStyleDetail(string text, BuilderCanvasNode node)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var match = s_styleExport.Match(lines[i].Trim());
                if (!match.Success)
                    continue;
                string styleName = match.Groups[1].Value;
                node.ExportDetail.Add(new BuilderCardLine
                {
                    Text = styleName + " = new Style {",
                    Kind = BuilderCardLineKind.Export,
                    SourceLine = i + 1,
                });
                int j = i + 1;
                for (; j < lines.Length; j++)
                {
                    string entry = lines[j].Trim();
                    if (entry.StartsWith("}", StringComparison.Ordinal))
                        break;
                    if (entry.Length == 0)
                        continue;
                    node.ExportDetail.Add(new BuilderCardLine
                    {
                        Text = entry,
                        Kind = BuilderCardLineKind.Plain,
                        Depth = 1,
                        SourceLine = j + 1,
                    });
                }
                node.ExportDetail.Add(new BuilderCardLine
                {
                    Text = "+ entry",
                    Kind = BuilderCardLineKind.Plain,
                    Depth = 1,
                    BadgeKind = 9,
                    AttrsText = styleName,
                    SourceLine = j + 1,
                });
                node.ExportDetail.Add(new BuilderCardLine
                {
                    Text = "}",
                    Kind = BuilderCardLineKind.Plain,
                    SourceLine = j + 1,
                });
                i = j;
            }
        }

        private static void WalkMarkup(
            AstNode node, int depth, List<BuilderCardLine> lines,
            HashSet<string> registered, ref int budget)
        {
            if (budget <= 0)
                return;
            switch (node)
            {
                case ElementNode element:
                    budget--;
                    lines.Add(new BuilderCardLine
                    {
                        Text = "<" + element.TagName + ">",
                        Depth = depth,
                        Kind = registered.Contains(element.TagName)
                            ? BuilderCardLineKind.Element
                            : BuilderCardLineKind.Component,
                        AttrsText = AttrsDisplay(element),
                        SourceLine = element.SourceLine,
                        EndLine = Math.Max(element.SourceLine,
                            Math.Max(element.CloseTagLine, element.EndLine)),
                    });
                    foreach (var child in element.Children)
                        WalkMarkup(child, depth + 1, lines, registered, ref budget);
                    break;
                case IfNode ifNode:
                    budget--;
                    lines.Add(new BuilderCardLine
                    {
                        Text = "@if", Depth = depth, Kind = BuilderCardLineKind.Directive,
                        BadgeKind = 1, SourceLine = ifNode.SourceLine,
                    });
                    foreach (var branch in ifNode.Branches)
                        foreach (var child in branch.Payload.Body)
                            WalkMarkup(child, depth + 1, lines, registered, ref budget);
                    break;
                case ForeachNode fe:
                    budget--;
                    lines.Add(new BuilderCardLine
                    {
                        Text = "@foreach", Depth = depth, Kind = BuilderCardLineKind.Directive,
                        BadgeKind = 2, SourceLine = fe.SourceLine,
                    });
                    foreach (var child in fe.Payload.Body)
                        WalkMarkup(child, depth + 1, lines, registered, ref budget);
                    break;
                case ForNode f:
                    budget--;
                    lines.Add(new BuilderCardLine
                    {
                        Text = "@for", Depth = depth, Kind = BuilderCardLineKind.Directive,
                        BadgeKind = 4, SourceLine = f.SourceLine,
                    });
                    foreach (var child in f.Payload.Body)
                        WalkMarkup(child, depth + 1, lines, registered, ref budget);
                    break;
                case WhileNode w:
                    budget--;
                    lines.Add(new BuilderCardLine
                    {
                        Text = "@while", Depth = depth, Kind = BuilderCardLineKind.Directive,
                        BadgeKind = 4, SourceLine = w.SourceLine,
                    });
                    foreach (var child in w.Payload.Body)
                        WalkMarkup(child, depth + 1, lines, registered, ref budget);
                    break;
                case SwitchNode s:
                    budget--;
                    lines.Add(new BuilderCardLine
                    {
                        Text = "@switch", Depth = depth, Kind = BuilderCardLineKind.Directive,
                        BadgeKind = 4, SourceLine = s.SourceLine,
                    });
                    foreach (var c in s.Cases)
                        foreach (var child in c.Payload.Body)
                            WalkMarkup(child, depth + 1, lines, registered, ref budget);
                    break;
            }
        }

        private static string AttrsDisplay(ElementNode element)
        {
            if (element.Attributes.IsDefaultOrEmpty)
                return "";
            var sb = new System.Text.StringBuilder();
            foreach (var attr in element.Attributes)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(attr.Name).Append('=');
                switch (attr.Value)
                {
                    case StringLiteralValue s:
                        sb.Append('"').Append(s.Value).Append('"');
                        break;
                    case CSharpExpressionValue e:
                        sb.Append('{').Append(e.Expression).Append('}');
                        break;
                }
            }
            return sb.ToString();
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

        /// <summary>Root at origin, imports fan right in BFS depth columns.
        /// Column Y advances by each card's ESTIMATED height (section line
        /// counts), so tall detail cards never overlap their column neighbors.</summary>
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

            var columnY = new Dictionary<int, float>();
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                int d = depth[i] < 0 ? 0 : depth[i];
                var node = graph.Nodes[i];
                columnY.TryGetValue(d, out float y);
                if (y == 0f)
                    y = 80f;
                if (node.X == 0f && node.Y == 0f)
                {
                    node.X = 80f + d * (BuilderCanvasDrawing.CardWidth + 160f);
                    node.Y = y;
                }
                columnY[d] = Math.Max(y, node.Y) + EstimateCardHeight(node) + 48f;
            }
        }

        private static float EstimateCardHeight(BuilderCanvasNode node)
        {
            int lines = node.Imports.Count + node.Body.Count + node.Markup.Count;
            int sections = (node.Imports.Count > 0 ? 1 : 0)
                + (node.Body.Count > 0 ? 1 : 0)
                + (node.Markup.Count > 0 ? 1 : 0);
            return 46f + sections * 16f + lines * 13f;
        }

        private static string Str(JToken token, string camel, string pascal) =>
            token?.Value<string>(camel) ?? token?.Value<string>(pascal) ?? "";
    }
}
#endif
