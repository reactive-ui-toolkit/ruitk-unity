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
            FillExportsFromSource(node, text);
            node.Signature = ExtractSignature(text, node);
            ExtractIslandLines(text, node);

            var parsed = BuilderLanguage.Parse(text, node.FilePath);

            foreach (var import in parsed.Directives.Imports)
            {
                string spec = import.Specifier ?? "";
                if (spec.StartsWith("@", StringComparison.Ordinal))
                    continue;
                string clause;
                if (import.IsStar)
                    clause = "* as " + (import.StarAlias ?? "Module");
                else if (import.IsDefault)
                    clause = import.DefaultAlias ?? "Default";
                else if (import.Names.IsDefaultOrEmpty || import.Names.Length == 0)
                    clause = "{ }";
                else
                {
                    var rendered = new List<string>();
                    for (int i = 0; i < import.Names.Length; i++)
                    {
                        string alias = import.Aliases.IsDefaultOrEmpty || import.Aliases.Length <= i
                            ? null
                            : import.Aliases[i];
                        rendered.Add(string.IsNullOrEmpty(alias)
                            ? import.Names[i]
                            : import.Names[i] + " as " + alias);
                    }
                    clause = "{ " + string.Join(", ", rendered) + " }";
                }
                int dotKind = spec.EndsWith(".style", StringComparison.OrdinalIgnoreCase) ? 7
                    : spec.EndsWith(".hooks", StringComparison.OrdinalIgnoreCase) ? 6
                    : 5;
                node.Imports.Add(new BuilderCardLine
                {
                    Text = "import " + clause + " from \"" + spec + "\"",
                    AttrsText = spec,
                    Kind = BuilderCardLineKind.Import,
                    BadgeKind = dotKind,
                    SourceLine = import.Line,
                });
            }

            string[] allLines = text.Split('\n');
            // POC cardHtml walks the WHOLE model — every hook chip, every JSX row
            // and every island line is on the card, because the card IS the
            // editing surface. An elided tail would be unselectable, undroppable
            // and un-right-clickable at every LOD.
            foreach (Match m in s_hookCall.Matches(text))
            {
                string lhs = m.Groups[1].Success ? m.Groups[1].Value.Trim()
                    : m.Groups[2].Success ? m.Groups[2].Value.Trim()
                    : null;
                string hook = m.Groups[3].Value;
                int line1 = 1;
                for (int c = 0; c < m.Index && c < text.Length; c++)
                    if (text[c] == '\n')
                        line1++;
                node.Body.Add(new BuilderCardLine
                {
                    Text = lhs == null ? hook : hook + "  →  " + lhs,
                    Kind = BuilderCardLineKind.Hook,
                    SourceLine = line1,
                    SourceText = line1 >= 1 && line1 <= allLines.Length
                        ? allLines[line1 - 1].Trim()
                        : "",
                });
            }

            var registered = Ruitk.Elements.ElementRegistryProvider.GetDefaultRegistry().RegisteredNames;
            var registeredSet = new HashSet<string>(registered, StringComparer.Ordinal);
            foreach (var root in parsed.RootNodes)
                WalkMarkup(root, 0, node.Markup, registeredSet, allLines);
            FillDirectiveText(node, allLines);

            node.ExportDetail.Clear();
            if (node.Kind == BuilderNodeKind.Style)
                ParseStyleDetail(text, node);
            else if (node.Kind == BuilderNodeKind.Util)
                ParseUtilDetail(text, node);
            else if (node.Kind == BuilderNodeKind.Unknown
                && node.Markup.Count == 0 && node.Exports.Count > 0)
            {
                // POC: a hook card's BODY holds hook chips and "+ hook" only —
                // the module's own export name is never a chip. Only an
                // unclassifiable file falls back to listing its exports.
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

        /// <summary>The same declaration read over the WHOLE buffer, so a hook
        /// module whose return tuple is spread across lines still resolves:
        /// <c>export (\n GameState state,\n … \n) useGalagaGame(\n … \n)</c> never
        /// matched the per-line header, which cost the card its real signature and
        /// left the library's HOOKS section without its "(module)" row whenever the
        /// language server answered with no exports for the file.</summary>
        private static readonly Regex s_exportDecl = new Regex(
            @"(?:^|\n)\s*export\s+(?:VirtualNode\s+|\([^)]*\)\s*)(?<name>\w+)\s*\((?<args>[^)]*)\)",
            RegexOptions.Compiled);

        private static string Collapse(string text) =>
            Regex.Replace(text ?? "", @"\s+", " ").Trim();

        /// <summary>Declared component/hook export names read from the source, used
        /// only when the workspace graph hands the file back with none.</summary>
        private static void FillExportsFromSource(BuilderCanvasNode node, string text)
        {
            if (node.Exports.Count > 0)
                return;
            if (node.Kind != BuilderNodeKind.Component && node.Kind != BuilderNodeKind.Hook)
                return;
            foreach (Match m in s_exportDecl.Matches(text))
            {
                string name = m.Groups["name"].Value;
                if (name.Length > 0 && !node.Exports.Contains(name))
                    node.Exports.Add(name);
            }
        }

        /// <summary>POC cardHtml: the props-row section exists ONLY for component
        /// and hook cards — style/util cards go straight from the title bar to
        /// EXPORTS, so their signature stays empty.</summary>
        private static string ExtractSignature(string text, BuilderCanvasNode node)
        {
            if (node.Kind != BuilderNodeKind.Component && node.Kind != BuilderNodeKind.Hook)
                return "";
            foreach (string raw in text.Split('\n'))
            {
                var match = s_exportHeader.Match(raw.Trim());
                if (match.Success)
                    return match.Groups[1].Value + match.Groups[2].Value;
            }
            var wide = s_exportDecl.Match(text);
            if (wide.Success)
                return wide.Groups["name"].Value + "(" + Collapse(wide.Groups["args"].Value) + ")";
            return node.Title + "()";
        }

        /// <summary>POC code island: every setup statement between the header and
        /// the return that is not a hook declaration. Uncapped — the displayed
        /// text and the [IslandStartLine, IslandEndLine] write-back range are the
        /// same block, so a cap here would delete the lines it hid.</summary>
        private static void ExtractIslandLines(string text, BuilderCanvasNode node)
        {
            node.IslandStartLine = 0;
            node.IslandEndLine = 0;
            if (node.Kind != BuilderNodeKind.Component && node.Kind != BuilderNodeKind.Hook)
                return;
            string[] rawLines = text.Split('\n');
            bool inBody = false;
            for (int i = 0; i < rawLines.Length; i++)
            {
                string line = rawLines[i].Trim();
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
                if (node.IslandStartLine == 0)
                    node.IslandStartLine = i + 1;
                node.IslandEndLine = i + 1;
                node.IslandLines.Add(line);
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
                var body = new List<string>();
                int bodyStart = 0;
                int bodyEnd = 0;
                for (; j < lines.Length; j++)
                {
                    string bodyLine = lines[j].Trim();
                    if (bodyLine.StartsWith("}", StringComparison.Ordinal))
                        break;
                    if (bodyLine.Length == 0)
                        continue;
                    if (bodyStart == 0)
                        bodyStart = j + 1;
                    bodyEnd = j + 1;
                    body.Add(bodyLine);
                }
                if (body.Count > 0)
                    node.ExportDetail.Add(new BuilderCardLine
                    {
                        Text = string.Join("\n", body),
                        Kind = BuilderCardLineKind.Plain,
                        Depth = 1,
                        BadgeKind = 12,
                        SourceLine = bodyStart,
                        EndLine = bodyEnd,
                    });
                node.ExportDetail.Add(new BuilderCardLine
                {
                    Text = "}",
                    Kind = BuilderCardLineKind.Plain,
                    SourceLine = j + 1,
                });
                i = j;
            }
            if (node.ExportDetail.Count > 0)
                node.ExportDetail.Add(new BuilderCardLine
                {
                    Text = "+ export",
                    Kind = BuilderCardLineKind.Plain,
                    BadgeKind = 11,
                    SourceLine = lines.Length,
                });
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
                    BadgeKind = 13,
                    AttrsText = styleName,
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
            if (node.ExportDetail.Count > 0)
                node.ExportDetail.Add(new BuilderCardLine
                {
                    Text = "+ style",
                    Kind = BuilderCardLineKind.Plain,
                    BadgeKind = 10,
                    SourceLine = lines.Length,
                });
        }

        private static void WalkMarkup(
            AstNode node, int depth, List<BuilderCardLine> lines,
            HashSet<string> registered, string[] allLines)
        {
            switch (node)
            {
                case ElementNode element:
                {
                    int endLine = Math.Max(element.SourceLine,
                        Math.Max(element.CloseTagLine, element.EndLine));
                    bool selfClosing = false;
                    if (endLine <= element.SourceLine)
                    {
                        // The AST records no closing tag for a self-closing element,
                        // so its span (which may wrap across lines) comes from the
                        // buffer — every insert / delete / move consumes EndLine.
                        endLine = TagSpanEnd(allLines, element.SourceLine, out selfClosing);
                    }
                    lines.Add(new BuilderCardLine
                    {
                        Text = "<" + element.TagName + ">",
                        Depth = depth,
                        Kind = registered.Contains(element.TagName)
                            ? BuilderCardLineKind.Element
                            : BuilderCardLineKind.Component,
                        AttrsText = AttrsDisplay(element),
                        AttrPairs = AttrPairsOf(element),
                        SourceLine = element.SourceLine,
                        EndLine = endLine,
                        SelfClosing = selfClosing,
                    });
                    foreach (var child in element.Children)
                        WalkMarkup(child, depth + 1, lines, registered, allLines);
                    break;
                }
                case IfNode ifNode:
                {
                    for (int bi = 0; bi < ifNode.Branches.Length; bi++)
                    {
                        var branch = ifNode.Branches[bi];
                        int start = lines.Count;
                        foreach (var child in branch.Payload.Body)
                            WalkMarkup(child, depth, lines, registered, allLines);
                        bool isElse = bi > 0;
                        Attach(lines, start, isElse ? 3 : 1,
                            isElse ? (branch.Condition == null ? "@else" : "@else if") : "@if",
                            bi == 0 ? ifNode.SourceLine : branch.SourceLine);
                    }
                    break;
                }
                case ForeachNode fe:
                {
                    int start = lines.Count;
                    foreach (var child in fe.Payload.Body)
                        WalkMarkup(child, depth, lines, registered, allLines);
                    Attach(lines, start, 2, "@foreach", fe.SourceLine);
                    break;
                }
                case ForNode f:
                {
                    int start = lines.Count;
                    foreach (var child in f.Payload.Body)
                        WalkMarkup(child, depth, lines, registered, allLines);
                    Attach(lines, start, 4, "@for", f.SourceLine);
                    break;
                }
                case WhileNode w:
                {
                    int start = lines.Count;
                    foreach (var child in w.Payload.Body)
                        WalkMarkup(child, depth, lines, registered, allLines);
                    Attach(lines, start, 4, "@while", w.SourceLine);
                    break;
                }
                case SwitchNode s:
                {
                    foreach (var c in s.Cases)
                    {
                        int start = lines.Count;
                        foreach (var child in c.Payload.Body)
                            WalkMarkup(child, depth, lines, registered, allLines);
                        Attach(lines, start, 4,
                            c.ValueExpression == null ? "@default" : "@case", c.SourceLine);
                    }
                    break;
                }
            }
        }

        /// <summary>1-based line on which the open tag starting at
        /// <paramref name="startLine1"/> closes — the first '&gt;' outside a string
        /// or a braced expression — and whether that close was "/&gt;".</summary>
        private static int TagSpanEnd(string[] lines, int startLine1, out bool selfClosing)
        {
            selfClosing = false;
            int start = startLine1 - 1;
            if (lines == null || start < 0 || start >= lines.Length)
                return startLine1;
            int braces = 0;
            bool inString = false;
            for (int i = start; i < lines.Length && i < start + 24; i++)
            {
                string line = lines[i];
                for (int c = 0; c < line.Length; c++)
                {
                    char ch = line[c];
                    if (inString)
                    {
                        if (ch == '"')
                            inString = false;
                        continue;
                    }
                    if (ch == '"')
                        inString = true;
                    else if (ch == '{')
                        braces++;
                    else if (ch == '}')
                        braces--;
                    else if (ch == '>' && braces <= 0)
                    {
                        selfClosing = c > 0 && line[c - 1] == '/';
                        return i + 1;
                    }
                }
            }
            return startLine1;
        }

        /// <summary>POC jsxRowHtml: a directive is a PROPERTY of the element node,
        /// so the badge rides the FIRST element row the branch produced — never a
        /// row of its own, and never an extra indent level.</summary>
        private static void Attach(
            List<BuilderCardLine> lines, int start, int badgeKind, string badgeText, int directiveLine)
        {
            if (start >= lines.Count)
                return;
            var row = lines[start];
            if (row.BadgeKind != 0)
                return;
            row.BadgeKind = badgeKind;
            row.BadgeText = badgeText;
            row.DirectiveLine = directiveLine;
        }

        /// <summary>Fills DirectiveText from the buffer so the badge's inline
        /// editor seeds the real header ("@foreach (var item in items)").</summary>
        private static void FillDirectiveText(BuilderCanvasNode node, string[] lines)
        {
            foreach (var row in node.Markup)
            {
                if (row.DirectiveLine <= 0 || row.DirectiveLine > lines.Length)
                    continue;
                string raw = lines[row.DirectiveLine - 1].Trim();
                if (raw.EndsWith("{", StringComparison.Ordinal))
                    raw = raw.Substring(0, raw.Length - 1).TrimEnd();
                row.DirectiveText = raw;
            }
        }

        private static List<string> AttrPairsOf(ElementNode element)
        {
            var pairs = new List<string>();
            if (element.Attributes.IsDefaultOrEmpty)
                return pairs;
            foreach (var attr in element.Attributes)
            {
                switch (attr.Value)
                {
                    case StringLiteralValue s:
                        pairs.Add(attr.Name + "=\"" + s.Value + "\"");
                        break;
                    case CSharpExpressionValue e:
                        pairs.Add(attr.Name + "={" + e.Expression + "}");
                        break;
                }
            }
            return pairs;
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
            bool hasBody = node.Kind == BuilderNodeKind.Component || node.Kind == BuilderNodeKind.Hook;
            int sections = (node.Imports.Count > 0 ? 1 : 0)
                + (hasBody ? 1 : 0)
                + (node.Markup.Count > 0 ? 1 : 0)
                + (node.ExportDetail.Count > 0 ? 1 : 0);
            float height = 35f + (string.IsNullOrEmpty(node.Signature) ? 0f : 30f) + sections * 33f;
            height += node.Imports.Count * 17f;
            height += ((node.Body.Count + 2) / 2) * 26f;
            height += node.Markup.Count * 21f;
            height += node.ExportDetail.Count * 18f;
            return height;
        }

        private static string Str(JToken token, string camel, string pascal) =>
            token?.Value<string>(camel) ?? token?.Value<string>(pascal) ?? "";
    }
}
#endif
