#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// <summary><paramref name="isHidden"/> drops a file from the tree
        /// entirely — nodes and every edge touching them. UB-88 uses it for
        /// files marked for deletion: the card has to leave the canvas the
        /// moment the user deletes it, while the file itself stays on disk
        /// until Save.</summary>
        public static async Task<BuilderGraph> LoadTreeAsync(
            BuilderLspClient client, string focusFile, Func<string, string> readText = null,
            Func<string, bool> isHidden = null,
            IEnumerable<string> pendingFiles = null)
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
                if (isHidden != null && isHidden(file))
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
                if (isHidden != null && (isHidden(from) || (!string.IsNullOrEmpty(to) && isHidden(to))))
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

            // UB-133: the adjacency comes only from the language server, which
            // knows files on DISK. A module the builder holds as a pending
            // buffer - a new one, or the new half of a rename - has no edges
            // there, so the reachability walk below found NOTHING connected to
            // it and the whole tree collapsed to a single card. Pending modules
            // contribute their own imports first, so the walk sees the tree the
            // user is actually looking at.
            LinkPendingImports(adjacency, importedBy, pendingFiles, readText, isHidden);

            string focus = Path.GetFullPath(focusFile);
            var member = ConnectedComponent(adjacency, focus);
            member.Add(focus);

            string root = ResolveRoot(importedBy, member, focus);

            var graph = new BuilderGraph { RootPath = root };
            var indexByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var sourceCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in member)
            {
                // TryGetValue's out on a miss is default((List<string>, BuilderNodeKind)),
                // and default(BuilderNodeKind) is Component — so every file the export
                // snapshot did not cover was silently badged "component". The miss is
                // seeded explicitly and resolved from the source instead.
                if (!exportsByFile.TryGetValue(file, out var info))
                    info = (new List<string>(), BuilderNodeKind.Unknown);
                var node = new BuilderCanvasNode
                {
                    FilePath = file,
                    Title = Path.GetFileNameWithoutExtension(file).Replace(".style", "").Replace(".hooks", ""),
                    Kind = ClassifyByPathAndExports(file, info.Kind, ReadSource(file, readText, sourceCache)),
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

        /// <summary>The head of an exported declaration, up to and including the
        /// opening parenthesis of its parameter list — every export form the
        /// grammar has: <c>export VirtualNode Name(</c>, <c>export (ret) useX(</c>
        /// and <c>export Type Name(</c>. s_hookCall's whole "var x =" prefix is
        /// OPTIONAL, so it also fires on a DECLARATION head like
        /// <c>) useGalagaGame(</c> — which put a phantom "useGalagaGame" chip at the
        /// front of the hook module's own BODY section, ahead of the hooks the body
        /// really calls. A hook-call match landing inside one of these spans is the
        /// declaration, not a call.</summary>
        private static readonly Regex s_exportHeadSpan = new Regex(
            @"(?:^|\n)[ \t]*export\s+(?:VirtualNode\s+|\([^)]*\)\s*|[\w<>,\.\[\]\?]+\s+)\w+\s*(?:<[^>\n]*>)?\s*\(",
            RegexOptions.Compiled);

        private static List<(int Start, int End)> ExportHeadSpans(string text)
        {
            var spans = new List<(int, int)>();
            foreach (Match m in s_exportHeadSpan.Matches(text))
                spans.Add((m.Index, m.Index + m.Length));
            return spans;
        }

        private static bool InsideExportHead(List<(int Start, int End)> spans, int index)
        {
            foreach (var span in spans)
                if (index >= span.Start && index < span.End)
                    return true;
            return false;
        }

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
            node.CachedHeight = 0f;
            FillExportsFromSource(node, text);
            node.Signature = ExtractSignature(text, node);
            node.ExposedSignature = ExtractExposedSignature(text, node);
            ExtractIslandLines(text, node);

            var parsed = BuilderLanguage.Parse(text, node.FilePath);

            // POC cardHtml emits one .imp-row per entry in n.imports — EVERY import
            // line in the file, because the card IS the editing surface. A namespace
            // import ("@UnityEngine.UIElements") is filed by the parser in
            // Directives.UsingDirectives, NOT in Directives.Imports, so walking the
            // module imports alone dropped those rows and the card disagreed with the
            // SOURCE pane. Both sequences are merged back into source-line order.
            var importRows = new List<BuilderCardLine>();
            foreach (var ns in parsed.Directives.UsingDirectives)
            {
                string payload = ns.Payload ?? "";
                // Namespace imports resolve to no graph node, so they carry
                // BadgeKind 8: a text-only row with no anchor dot and no edge.
                importRows.Add(new BuilderCardLine
                {
                    Text = ns.FromImportSyntax
                        ? "import \"@" + payload + "\""
                        : "@using " + payload,
                    AttrsText = "@" + payload,
                    Kind = BuilderCardLineKind.Import,
                    BadgeKind = BuilderCanvasDrawing.NamespaceImportBadge,
                    SourceLine = ns.Line,
                });
            }
            foreach (var import in parsed.Directives.Imports)
            {
                string spec = import.Specifier ?? "";
                if (spec.StartsWith("@", StringComparison.Ordinal))
                {
                    importRows.Add(new BuilderCardLine
                    {
                        Text = "import \"" + spec + "\"",
                        AttrsText = spec,
                        Kind = BuilderCardLineKind.Import,
                        BadgeKind = BuilderCanvasDrawing.NamespaceImportBadge,
                        SourceLine = import.Line,
                    });
                    continue;
                }
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
                importRows.Add(new BuilderCardLine
                {
                    Text = "import " + clause + " from \"" + spec + "\"",
                    AttrsText = spec,
                    Kind = BuilderCardLineKind.Import,
                    BadgeKind = dotKind,
                    SourceLine = import.Line,
                });
            }
            // File order is what the SOURCE pane shows; OrderBy is stable, so two
            // rows the parser gave the same line keep their sequence order.
            foreach (var row in importRows.OrderBy(r => r.SourceLine))
                node.Imports.Add(row);

            string[] allLines = text.Split('\n');
            // POC cardHtml walks the WHOLE model — every hook chip, every JSX row
            // and every island line is on the card, because the card IS the
            // editing surface. An elided tail would be unselectable, undroppable
            // and un-right-clickable at every LOD.
            var declSpans = ExportHeadSpans(text);
            foreach (Match m in s_hookCall.Matches(text))
            {
                if (InsideExportHead(declSpans, m.Index))
                    continue;
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

        /// <summary>s_exportHeader read over the WHOLE buffer, up to and including
        /// the opening parenthesis of the parameter list, so a component or hook
        /// whose head wraps across lines still resolves its body. The return-tuple
        /// group tolerates ONE nesting level, or a hook exposing a tuple element
        /// (<c>List&lt;(int x, int y)&gt; snake</c>) closes the group early and the
        /// declaration goes unrecognised.</summary>
        private static readonly Regex s_exportHeadWide = new Regex(
            @"(?:^|\n)[ \t]*export\s+(?:VirtualNode\s+|\((?:[^()]|\([^()]*\))*\)\s*)\w+\s*(?:<[^>\n]*>)?\s*\(",
            RegexOptions.Compiled);

        /// <summary>Index of the '{' that opens the body of the declaration whose
        /// head ends at <paramref name="afterOpenParen"/> (the character just past
        /// its '('), or -1 when the declaration has no braced body.</summary>
        private static int BodyBraceIndex(string text, int afterOpenParen)
        {
            int depth = 1;
            int i = afterOpenParen;
            for (; i < text.Length && depth > 0; i++)
            {
                char ch = text[i];
                if (ch == '(')
                    depth++;
                else if (ch == ')')
                    depth--;
            }
            if (depth > 0)
                return -1;
            for (; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '{')
                    return i;
                if (!char.IsWhiteSpace(ch))
                    return -1;
            }
            return -1;
        }

        private static int BraceDelta(string line)
        {
            int delta = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '{')
                    delta++;
                else if (line[i] == '}')
                    delta--;
            }
            return delta;
        }

        private static int NestDelta(string line)
        {
            int delta = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '{' || ch == '(' || ch == '[')
                    delta++;
                else if (ch == '}' || ch == ')' || ch == ']')
                    delta--;
            }
            return delta;
        }

        private static int LineIndexOf(string text, int charIndex)
        {
            int line = 0;
            for (int i = 0; i < charIndex && i < text.Length; i++)
                if (text[i] == '\n')
                    line++;
            return line;
        }

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

        private static readonly Regex s_exportReturns = new Regex(
            @"(?:^|\n)\s*export\s+\((?<ret>[^)]*)\)\s*(?<name>\w+)\s*\((?<args>[^)]*)\)",
            RegexOptions.Compiled);

        /// <summary>POC ".nopreview" for a hook module: "useCart(gold, setGold) →
        /// (Count, Buy)" — the declaration head PLUS the names of the return tuple,
        /// which the card's own signature row does not carry.</summary>
        private static string ExtractExposedSignature(string text, BuilderCanvasNode node)
        {
            if (string.IsNullOrEmpty(node.Signature))
                return "";
            var match = s_exportReturns.Match(text);
            if (!match.Success)
                return node.Signature;
            var names = new List<string>();
            foreach (string part in match.Groups["ret"].Value.Split(','))
            {
                string piece = Collapse(part);
                if (piece.Length == 0)
                    continue;
                int space = piece.LastIndexOf(' ');
                names.Add(space >= 0 ? piece.Substring(space + 1) : piece);
            }
            if (names.Count == 0)
                return node.Signature;
            return node.Signature + " → (" + string.Join(", ", names) + ")";
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
            // The head is matched over the WHOLE buffer, not per line: every
            // component or hook whose parameter list wraps
            // ("export VirtualNode Name(" + params + ") {") never matched the
            // single-line header, so its body was never entered and the L2 code
            // island — and the write-back range behind it — stayed empty.
            var head = s_exportHeadWide.Match(text);
            if (!head.Success)
                return;
            int brace = BodyBraceIndex(text, head.Index + head.Length);
            if (brace < 0)
                return;
            string[] rawLines = text.Split('\n');
            int pendingBlanks = 0;
            // Brace depth relative to the body: the declaration's OWN return is the
            // terminator, so a nested "return () => {" inside a useEffect callback
            // no longer cuts the island in half, and the closing brace of the
            // declaration ends it even when there is no return statement at all.
            int depth = 1;
            for (int i = LineIndexOf(text, brace) + 1; i < rawLines.Length; i++)
            {
                string raw = rawLines[i].TrimEnd();
                string line = raw.Trim();
                int outer = depth;
                depth += BraceDelta(raw);
                if (outer == 1 && line.StartsWith("return (", StringComparison.Ordinal))
                    break;
                if (depth <= 0)
                    break;
                if (line.Length == 0)
                {
                    // POC ".code-island { white-space: pre }" keeps the block
                    // verbatim, blank separator lines included — but never a
                    // leading or trailing run, so blanks are held until the next
                    // kept line proves they are interior.
                    if (node.IslandLines.Count > 0)
                        pendingBlanks++;
                    continue;
                }
                if (line.StartsWith("import ", StringComparison.Ordinal))
                {
                    pendingBlanks = 0;
                    continue;
                }
                if (s_hookCall.IsMatch(line))
                {
                    // A hook declaration is a CHIP, never an island line — and a
                    // multi-line one (useEffect(() => { … }, deps)) is skipped as a
                    // whole statement, or its callback body would be left in the
                    // island orphaned from the header that opened it.
                    int nest = NestDelta(raw);
                    while (nest > 0 && i + 1 < rawLines.Length)
                    {
                        i++;
                        nest += NestDelta(rawLines[i]);
                    }
                    depth = outer;
                    pendingBlanks = 0;
                    continue;
                }
                if (node.IslandStartLine == 0)
                    node.IslandStartLine = i + 1;
                for (; pendingBlanks > 0; pendingBlanks--)
                    node.IslandLines.Add("");
                node.IslandEndLine = i + 1;
                node.IslandLines.Add(raw);
            }
            StripCommonIndent(node.IslandLines);
        }

        /// <summary>The POC's island bodies carry their own relative indentation
        /// ("void Buy(Item it) {" then "  if (gold &lt; it.Price) return;"). Trimming every line
        /// flattened the block; only the indentation the whole block SHARES is
        /// removed, so nesting survives.</summary>
        internal static void StripCommonIndent(List<string> lines)
        {
            if (lines == null || lines.Count == 0)
                return;
            int common = int.MaxValue;
            foreach (string line in lines)
            {
                if (line.Length == 0)
                    continue;
                int w = BuilderText.LeadingSpaceCount(line);
                if (w < common)
                    common = w;
            }
            if (common <= 0 || common == int.MaxValue)
                return;
            for (int i = 0; i < lines.Count; i++)
                lines[i] = lines[i].Length <= common ? "" : lines[i].Substring(common);
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
                int eq = line.IndexOf('=');
                int par = line.IndexOf('(');
                if (eq >= 0 && (par < 0 || eq < par))
                {
                    node.ExportDetail.Add(new BuilderCardLine
                    {
                        Text = line,
                        Kind = BuilderCardLineKind.Export,
                        SourceLine = i + 1,
                    });
                    continue;
                }
                if (par < 0)
                    continue;
                // A wrapped parameter list is collapsed back to the one head line
                // the POC's ".usig" row shows, and the body starts after the '{'
                // that closes it. Reading the first line alone rendered
                // "export int Foo( {" and swallowed the remaining parameter lines
                // into the body island as if they were statements.
                int headEnd = i;
                int headDepth = 0;
                var headLines = new List<string>();
                for (int k = i; k < lines.Length; k++)
                {
                    headLines.Add(lines[k]);
                    foreach (char ch in lines[k])
                    {
                        if (ch == '(')
                            headDepth++;
                        else if (ch == ')')
                            headDepth--;
                    }
                    headEnd = k;
                    if (headDepth <= 0)
                        break;
                }
                if (headDepth > 0)
                    continue;
                string sig = CollapseHead(string.Join("\n", headLines)).TrimEnd('{', ' ');
                int braceLine = -1;
                if (lines[headEnd].TrimEnd().EndsWith("{", StringComparison.Ordinal))
                {
                    braceLine = headEnd;
                }
                else
                {
                    int k = headEnd + 1;
                    while (k < lines.Length && lines[k].Trim().Length == 0)
                        k++;
                    if (k < lines.Length && lines[k].Trim() == "{")
                        braceLine = k;
                }
                if (braceLine < 0)
                {
                    // POC "u.body === null": an expression-bodied export renders
                    // as its signature row alone, with no island and no brace.
                    node.ExportDetail.Add(new BuilderCardLine
                    {
                        Text = sig,
                        Kind = BuilderCardLineKind.Export,
                        SourceLine = i + 1,
                    });
                    i = headEnd;
                    continue;
                }
                node.ExportDetail.Add(new BuilderCardLine
                {
                    Text = sig + " {",
                    Kind = BuilderCardLineKind.Export,
                    SourceLine = i + 1,
                });
                int j = braceLine + 1;
                var body = new List<string>();
                int bodyStart = 0;
                int bodyEnd = 0;
                for (; j < lines.Length; j++)
                {
                    string bodyRaw = lines[j].TrimEnd();
                    string bodyLine = bodyRaw.Trim();
                    if (bodyLine.StartsWith("}", StringComparison.Ordinal))
                        break;
                    if (bodyLine.Length == 0)
                        continue;
                    if (bodyStart == 0)
                        bodyStart = j + 1;
                    bodyEnd = j + 1;
                    body.Add(bodyRaw);
                }
                // The POC renders a util body as a ".code-island util-body" with
                // white-space:pre — its nesting is part of the picture.
                StripCommonIndent(body);
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
            // POC cardHtml gates the EXPORTS section on `if (n.utils)` and then
            // ALWAYS appends "+ export": a util module whose exports did not parse
            // — or one that is still empty — keeps the affordance that creates its
            // first one.
            node.ExportDetail.Add(new BuilderCardLine
            {
                Text = "+ export",
                Kind = BuilderCardLineKind.Plain,
                BadgeKind = 11,
                SourceLine = lines.Length,
            });
        }

        /// <summary>A declaration head spread over several lines, folded back to
        /// the single line the card row shows.</summary>
        private static string CollapseHead(string head)
        {
            string collapsed = Collapse(head);
            collapsed = Regex.Replace(collapsed, @"\(\s+", "(");
            collapsed = Regex.Replace(collapsed, @"\s+([),])", "$1");
            return collapsed;
        }

        /// <summary>The opening brace is no longer required on the declaration
        /// line: a style export written as "= new Style" with the brace on the
        /// next line used to match nothing, which cost the card its whole EXPORTS
        /// section.</summary>
        private static readonly Regex s_styleExport = new Regex(
            @"^export\s+Style\s+(\w+)\s*=\s*new\s+Style\b", RegexOptions.Compiled);

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
                int open = i;
                if (!lines[i].TrimEnd().EndsWith("{", StringComparison.Ordinal))
                {
                    int k = i + 1;
                    while (k < lines.Length && lines[k].Trim().Length == 0)
                        k++;
                    if (k >= lines.Length || lines[k].Trim() != "{")
                        continue;
                    open = k;
                }
                node.ExportDetail.Add(new BuilderCardLine
                {
                    Text = styleName + " = new Style {",
                    Kind = BuilderCardLineKind.Export,
                    BadgeKind = 13,
                    AttrsText = styleName,
                    SourceLine = i + 1,
                });
                int j = open + 1;
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
            // POC cardHtml appends "+ style" outside the per-export loop, so an
            // empty (or unparsed) style module still offers the row that creates
            // its first export.
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
                    var branches = ifNode.Branches;
                    if (branches.Length == 0)
                        break;
                    int constructClose = ClauseCloseLine(
                        allLines, branches[branches.Length - 1].SourceLine);
                    for (int bi = 0; bi < branches.Length; bi++)
                    {
                        var branch = branches[bi];
                        bool isElse = bi > 0;
                        int headLine = bi == 0 ? ifNode.SourceLine : branch.SourceLine;
                        int clauseClose = bi + 1 < branches.Length
                            ? branches[bi + 1].SourceLine
                            : constructClose;
                        lines.Add(HeadRow(
                            !isElse ? "@if" : branch.Condition == null ? "@else" : "@else if",
                            branch.Condition == null ? "" : "(" + branch.Condition + ")",
                            isElse ? 3 : 1, depth, headLine,
                            bi == 0 ? constructClose : clauseClose, bi));
                        foreach (var child in branch.Payload.Body)
                            WalkMarkup(child, depth + 1, lines, registered, allLines);
                    }
                    break;
                }
                case ForeachNode fe:
                {
                    string header = string.IsNullOrEmpty(fe.ForeachExpression)
                        ? fe.IteratorDeclaration + " in " + fe.CollectionExpression
                        : fe.ForeachExpression;
                    lines.Add(HeadRow("@foreach", "(" + header + ")", 2, depth,
                        fe.SourceLine, ClauseCloseLine(allLines, fe.SourceLine), 0));
                    foreach (var child in fe.Payload.Body)
                        WalkMarkup(child, depth + 1, lines, registered, allLines);
                    break;
                }
                case ForNode f:
                {
                    lines.Add(HeadRow("@for", "(" + f.ForExpression + ")", 4, depth,
                        f.SourceLine, ClauseCloseLine(allLines, f.SourceLine), 0));
                    foreach (var child in f.Payload.Body)
                        WalkMarkup(child, depth + 1, lines, registered, allLines);
                    break;
                }
                case WhileNode w:
                {
                    lines.Add(HeadRow("@while", "(" + w.Condition + ")", 4, depth,
                        w.SourceLine, ClauseCloseLine(allLines, w.SourceLine), 0));
                    foreach (var child in w.Payload.Body)
                        WalkMarkup(child, depth + 1, lines, registered, allLines);
                    break;
                }
                case SwitchNode s:
                {
                    int constructClose = ClauseCloseLine(allLines, s.SourceLine);
                    lines.Add(HeadRow("@switch", "(" + s.SwitchExpression + ")", 14, depth,
                        s.SourceLine, constructClose, 0));
                    for (int ci = 0; ci < s.Cases.Length; ci++)
                    {
                        var c = s.Cases[ci];
                        int clauseClose = ci + 1 < s.Cases.Length
                            ? s.Cases[ci + 1].SourceLine - 1
                            : constructClose > 0 ? constructClose - 1 : c.SourceLine;
                        lines.Add(HeadRow(
                            c.ValueExpression == null ? "@default" : "@case",
                            c.ValueExpression == null ? "" : c.ValueExpression + ":",
                            15, depth + 1, c.SourceLine, clauseClose, ci + 1));
                        foreach (var child in c.Payload.Body)
                            WalkMarkup(child, depth + 2, lines, registered, allLines);
                    }
                    break;
                }
            }
        }

        private static BuilderCardLine HeadRow(
            string keyword, string headerRest, int badgeKind, int depth,
            int headLine, int closeLine, int clauseIndex) => new BuilderCardLine
        {
            Text = headerRest,
            Depth = depth,
            Kind = BuilderCardLineKind.Directive,
            BadgeKind = badgeKind,
            BadgeText = keyword,
            DirectiveLine = headLine,
            SourceLine = headLine,
            EndLine = closeLine,
            CloseLine = closeLine,
            ClauseIndex = clauseIndex,
        };

        /// <summary>1-based line of the '}' closing the block whose head is
        /// <paramref name="headLine1"/>. Counting starts at the first '{' so the
        /// shared "} @else {" head form skips its leading closer. Braces inside
        /// double-quoted strings are skipped — a '}' in an attribute value must
        /// not truncate the clause (the register's review caught exactly that
        /// corrupting Add-@else/Delete-block).</summary>
        private static int ClauseCloseLine(string[] lines, int headLine1)
        {
            int depth = 0;
            bool opened = false;
            for (int i = headLine1 - 1; i >= 0 && i < lines.Length; i++)
            {
                bool inString = false;
                foreach (char c in lines[i])
                {
                    if (inString)
                    {
                        if (c == '"')
                            inString = false;
                        continue;
                    }
                    if (c == '"')
                    {
                        inString = true;
                        continue;
                    }
                    if (c == '{')
                    {
                        depth++;
                        opened = true;
                    }
                    else if (c == '}' && opened && --depth <= 0)
                        return i + 1;
                }
            }
            return 0;
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

        /// <summary>Fills DirectiveText from the buffer so the badge's inline
        /// editor seeds the real header ("@foreach (var item in items)"). Brace
        /// heads strip the trailing '{' and any leading '}' (the shared
        /// "} @else {" form); colon heads (@case/@default) seed only the label
        /// up to the single ':' — the rest of the line can carry the case body
        /// and must never round-trip through the header editor.</summary>
        private static void FillDirectiveText(BuilderCanvasNode node, string[] lines)
        {
            foreach (var row in node.Markup)
            {
                if (row.DirectiveLine <= 0 || row.DirectiveLine > lines.Length)
                    continue;
                string raw = lines[row.DirectiveLine - 1].Trim();
                if (row.BadgeKind == 15)
                {
                    int colon = SingleColonIndex(raw);
                    row.DirectiveText = colon >= 0 ? raw.Substring(0, colon) : raw;
                    continue;
                }
                if (raw.StartsWith("}", StringComparison.Ordinal))
                    raw = raw.Substring(1).TrimStart();
                if (raw.EndsWith("{", StringComparison.Ordinal))
                    raw = raw.Substring(0, raw.Length - 1).TrimEnd();
                row.DirectiveText = raw;
            }
        }

        /// <summary>Index of the first single ':' — a '::' pair (global::
        /// qualifier in a case label) is skipped whole, matching the parser's
        /// case-label termination rule.</summary>
        internal static int SingleColonIndex(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != ':')
                    continue;
                if (i + 1 < text.Length && text[i + 1] == ':')
                {
                    i++;
                    continue;
                }
                return i;
            }
            return -1;
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


        /// <summary>Adds adjacency for modules that exist only as buffers, by
        /// parsing their own import directives. Resolution mirrors the canvas
        /// side: a specifier carries no extension, so each module suffix is
        /// tried and the first that names a real file wins. A specifier that
        /// resolves to nothing is simply skipped - a half-typed import must not
        /// break the tree.</summary>
        private static void LinkPendingImports(
            Dictionary<string, HashSet<string>> adjacency,
            Dictionary<string, HashSet<string>> importedBy,
            IEnumerable<string> pendingFiles,
            Func<string, string> readText,
            Func<string, bool> isHidden)
        {
            if (pendingFiles == null || readText == null)
                return;
            foreach (string pending in pendingFiles)
            {
                if (string.IsNullOrEmpty(pending))
                    continue;
                string from = Path.GetFullPath(pending);
                if (isHidden != null && isHidden(from))
                    continue;
                string text = readText(from);
                if (string.IsNullOrEmpty(text))
                    continue;
                string dir = Path.GetDirectoryName(from);
                if (string.IsNullOrEmpty(dir))
                    continue;
                Ruitk.Language.Parser.ParseResult parsed;
                try
                {
                    parsed = BuilderLanguage.Parse(text, from);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var import in parsed.Directives.Imports)
                {
                    string spec = import.Specifier ?? "";
                    if (spec.Length == 0 || spec.StartsWith("@", StringComparison.Ordinal))
                        continue;
                    string target = ResolvePendingSpecifier(dir, spec);
                    if (target == null || (isHidden != null && isHidden(target)))
                        continue;
                    Link(adjacency, from, target);
                    Link(adjacency, target, from);
                    Link(importedBy, target, from);
                }
            }
        }

        private static readonly string[] s_pendingSuffixes =
        {
            ".style.uitkx", ".hooks.uitkx", ".uitkx",
        };

        private static string ResolvePendingSpecifier(string fromDir, string specifier)
        {
            foreach (string suffix in s_pendingSuffixes)
            {
                string candidate;
                try
                {
                    candidate = Path.GetFullPath(Path.Combine(fromDir, specifier + suffix));
                }
                catch (Exception)
                {
                    return null;
                }
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
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

        private static string ReadSource(
            string file, Func<string, string> readText, Dictionary<string, string> cache)
        {
            if (cache.TryGetValue(file, out string cached))
                return cached;
            string text = null;
            try
            {
                text = readText?.Invoke(file);
                if (text == null && File.Exists(file))
                    text = File.ReadAllText(file);
            }
            catch (Exception)
            {
                text = null;
            }
            cache[file] = text ?? "";
            return cache[file];
        }

        private static readonly Regex s_exportComponentDecl = new Regex(
            @"(?:^|\n)\s*export\s+VirtualNode\s+\w+\s*\(", RegexOptions.Compiled);

        private static readonly Regex s_exportHookDecl = new Regex(
            @"(?:^|\n)\s*export\s+(?:\([^)]*\)\s*|[\w<>,\.\[\]\?]+\s+)(?:use[A-Z][A-Za-z0-9]*)\s*(?:<[^>\n]*>)?\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex s_exportAnything = new Regex(
            @"(?:^|\n)\s*export\s+\S", RegexOptions.Compiled);

        /// <summary>POC kindClass()/kindLabel(): only a file that exports a
        /// VirtualNode is a "component" (blue); a module that exports plain
        /// values/functions is a "utils" (orange). Falling back to Component
        /// whenever the language server answered with no kind badged every
        /// util module — GameLogic.uitkx, which exports only
        /// <c>export GameState CreateInitialState()</c> and
        /// <c>export GameState Update(...)</c> — as a component. The fallback now
        /// reads the declaration heads, which is the same rule the LSP's own
        /// DirectiveParser applies.</summary>
        /// <summary>Module kind from the filename suffix alone, for a module the
        /// graph has never seen (UB-111: a new one, still only a buffer). A
        /// plain ".uitkx" is treated as a component; its real kind settles from
        /// the exports as soon as the card is populated.</summary>
        public static BuilderNodeKind KindFromFileName(string file) =>
            ClassifyByPathAndExports(file, BuilderNodeKind.Component, null);

        private static BuilderNodeKind ClassifyByPathAndExports(
            string file, BuilderNodeKind exportKind, string source)
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".style.uitkx", StringComparison.OrdinalIgnoreCase))
                return BuilderNodeKind.Style;
            if (name.EndsWith(".hooks.uitkx", StringComparison.OrdinalIgnoreCase))
                return BuilderNodeKind.Hook;
            if (!string.IsNullOrEmpty(source))
            {
                if (s_exportComponentDecl.IsMatch(source))
                    return BuilderNodeKind.Component;
                if (s_exportHookDecl.IsMatch(source))
                    return BuilderNodeKind.Hook;
                if (s_exportAnything.IsMatch(source))
                    return BuilderNodeKind.Util;
            }
            return exportKind == BuilderNodeKind.Unknown ? BuilderNodeKind.Component : exportKind;
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
                    // The POC's authored sample positions are the only spec for the
                    // intended grid: columns at x = 60 / 620 / 1180, i.e. origin 60
                    // and a 560 pitch = the 340 card plus a 220 gutter.
                    node.X = 60f + d * (BuilderCanvasDrawing.CardWidth + 220f);
                    node.Y = y;
                }
                columnY[d] = Math.Max(y, node.Y) + EstimateCardHeight(node) + 48f;
            }
        }

        /// <summary>Consolas advance at the two card font sizes (0.6 em), used to
        /// predict which rows WRAP — the flat per-row charges the estimate used
        /// before counted one line for an 8-line signature and two chips per row
        /// for chips that take a row each, so two column neighbours could be
        /// packed with no gutter at all between them and the upper card's bottom
        /// border, radii and drop shadow were never drawn.</summary>
        private const float MonoAdvance12 = 7.2f;

        private const float MonoAdvance115 = 6.9f;

        private static int WrapLines(int chars, float available, float advance)
        {
            if (chars <= 0)
                return 1;
            int perLine = Math.Max(1, (int)(available / advance));
            return (chars + perLine - 1) / perLine;
        }

        /// <summary>Card height at the TALLEST lod, so the 48px gutter survives
        /// every zoom: L1 (340) is the narrowest box that still draws every
        /// section, and L2 (430) is the only one that rides the attribute run on
        /// the markup rows.</summary>
        /// <summary>Cached because the canvas now consults it for every node on
        /// every render to decide culling (UB-81), and the inputs only change
        /// when the node is re-parsed. Invalidated by clearing the field.</summary>
        internal static float CardHeightOf(BuilderCanvasNode node)
        {
            if (node == null)
                return 0f;
            if (node.CachedHeight <= 0f)
                node.CachedHeight = EstimateCardHeight(node);
            return node.CachedHeight;
        }

        internal static float EstimateCardHeight(BuilderCanvasNode node)
        {
            const float cardWidth = 340f;
            const float sectionInner = cardWidth - 24f;
            bool hasBody = node.Kind == BuilderNodeKind.Component || node.Kind == BuilderNodeKind.Hook;

            float height = 38f;

            if (!string.IsNullOrEmpty(node.Signature))
                height += 15f + WrapLines(node.Signature.Length, sectionInner, MonoAdvance12) * 17.4f;

            if (node.Imports.Count > 0)
                height += 34.5f + node.Imports.Count * 19.7f;

            if (node.Body.Count > 0 || hasBody)
            {
                height += 34.5f;
                float chipMax = cardWidth - 30f;
                float used = 0f;
                int rows = 1;
                for (int i = 0; i <= node.Body.Count; i++)
                {
                    // 18 padding + 2 border + 5 right margin around the chip text;
                    // the trailing entry is the "+ hook" affordance.
                    float w = i == node.Body.Count
                        ? (hasBody ? 71f : 0f)
                        : Math.Min(chipMax, 25f + (node.Body[i].Text?.Length ?? 0) * MonoAdvance115);
                    if (w <= 0f)
                        continue;
                    if (used > 0f && used + w > sectionInner)
                    {
                        rows++;
                        used = 0f;
                    }
                    used += w;
                }
                height += rows * 27.7f;
                if (node.IslandLines.Count > 0)
                    height += 20f + node.IslandLines.Count * 16.7f;
            }

            if (node.Markup.Count > 0)
            {
                height += 34.5f;
                foreach (var row in node.Markup)
                {
                    int chars = (row.Text?.Length ?? 0) + (row.AttrsText?.Length ?? 0)
                        + (string.IsNullOrEmpty(row.BadgeText) ? 0 : row.BadgeText.Length + 3);
                    float available = 430f - 26f - row.Depth * 14f;
                    height += WrapLines(chars, available, MonoAdvance12) * 17.4f + 5f;
                }
            }

            if (node.ExportDetail.Count > 0)
            {
                height += 34.5f;
                foreach (var line in node.ExportDetail)
                {
                    int lines = 1;
                    string text = line.Text ?? "";
                    for (int c = 0; c < text.Length; c++)
                        if (text[c] == '\n')
                            lines++;
                    height += lines * 18f;
                }
            }

            return height;
        }

        private static string Str(JToken token, string camel, string pascal) =>
            token?.Value<string>(camel) ?? token?.Value<string>(pascal) ?? "";
    }
}
#endif
