using System.Collections.Immutable;
using System.IO;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Ruitk.Language;
using Ruitk.Language.IntelliSense;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;
using System.Text.RegularExpressions;
using UitkxLanguageServer.Roslyn;

namespace UitkxLanguageServer;

public sealed class CompletionHandler : ICompletionHandler
{
    private readonly UitkxSchema _schema;
    private readonly DocumentStore _store;
    private readonly WorkspaceIndex _index;
    private readonly RoslynHost _roslynHost;
    private readonly RoslynCompletionProvider _roslynCompletion;

    public CompletionHandler(
        UitkxSchema schema,
        DocumentStore store,
        WorkspaceIndex index,
        RoslynHost roslynHost
    )
    {
        _schema = schema;
        _store = store;
        _index = index;
        _roslynHost = roslynHost;
        _roslynCompletion = new RoslynCompletionProvider(roslynHost);
    }

    public CompletionRegistrationOptions GetRegistrationOptions(
        CompletionCapability capability,
        ClientCapabilities clientCapabilities
    ) =>
        new CompletionRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.uitkx" }
            ),
            TriggerCharacters = new Container<string>("<", "@", "{", "."),
            ResolveProvider = false,
        };

    private static void Log(string msg) => ServerLog.Log(msg);

    public async Task<CompletionList> Handle(
        CompletionParams request,
        CancellationToken cancellationToken
    )
    {
        Log(
            $"completion request: {request.TextDocument.Uri}  pos={request.Position.Line}:{request.Position.Character}"
        );

        // Extract local path once; reused for disk-read fallback and AST parse.
        string localPath;
        try
        {
            localPath = new System.Uri(request.TextDocument.Uri.ToString()).LocalPath;
        }
        catch
        {
            localPath = string.Empty;
        }

        if (!_store.TryGet(request.TextDocument.Uri, out var text))
        {
            // VS2022 may have sent textDocument/didOpen before the server was ready.
            // Fall back to reading the file from disk.
            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
            {
                text = File.ReadAllText(localPath);
                _store.Set(request.TextDocument.Uri, text);
                Log($"completion: loaded from disk ({text.Length} chars)");
            }
            else
            {
                Log($"completion: store miss + disk miss — returning empty");
                return new CompletionList();
            }
        }

        // Parse the document with the language-lib AST pipeline so completions
        // are derived from the real syntax tree instead of text scanning.

        var parseDiags = new List<Ruitk.Language.ParseDiagnostic>();
        var directives = DirectiveParser.Parse(text, localPath, parseDiags);
        var nodes = UitkxParser.Parse(text, localPath, directives, parseDiags);
        var parseResult = new ParseResult(
            directives,
            nodes,
            ImmutableArray.CreateRange(parseDiags)
        );

        int line1 = (int)request.Position.Line + 1;
        int col0 = (int)request.Position.Character;

        // ── Inside `import { … } from "…"` → the target file's exported names ──
        // (import/export grammar, leg 3). Handled before the AST-cursor switch because a
        // half-typed import may not parse into DirectiveSet.Imports yet.
        {
            string importLine = GetLine(text, line1);
            var importNames = GetImportBraceCompletions(localPath, importLine, col0);
            if (importNames.Count > 0)
                return new CompletionList(importNames.Select(n => new CompletionItem
                {
                    Label = n,
                    Kind = CompletionItemKind.Value,
                    InsertText = n,
                    Detail = "exported by the import target",
                }));

            var specifiers = GetSpecifierCompletions(localPath, importLine, col0);
            if (specifiers.Count > 0)
                return new CompletionList(specifiers.Select(s => new CompletionItem
                {
                    Label = s,
                    Kind = CompletionItemKind.File,
                    InsertText = s,
                    Detail = "import specifier",
                }));
        }

        var ctx = AstCursorContext.Find(parseResult, text, line1, col0);
        int offset = ToOffset(text, request.Position);
        string? triggerChar = request.Context?.TriggerCharacter;

        // Preamble of a function-style file: lines before the component body opens.
        // FunctionSetupStartLine is the first line inside the { }, so anything before
        // it (including the `component Name {` declaration line) is preamble.
        bool inFunctionStylePreamble =
            parseResult.Directives.IsFunctionStyle
            && parseResult.Directives.FunctionSetupStartLine > 0
            && line1 < parseResult.Directives.FunctionSetupStartLine;
        bool inCodeBlockLine = IsInsideCodeBlockAtOffset(text, offset);
        bool inEmbeddedMarkupInCode =
            inCodeBlockLine && IsLikelyEmbeddedMarkupAtOffset(text, offset);

        // ── Ensure virtual document is up-to-date before any source-map query.
        // EnsureReadyAsync rebuilds the Roslyn workspace if the source has changed
        // since the last build (e.g. user just typed '.'). Without this the source-
        // map authority check below and the downstream Roslyn completion call both
        // operate on a stale virtual document, producing wrong virtual offsets.
        if (!string.IsNullOrEmpty(localPath))
        {
            try
            {
                await _roslynHost
                    .EnsureReadyAsync(localPath, text, parseResult, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            { /* workspace not ready — proceed with whatever state exists */
            }
        }

        // ── Source-map authority: is this cursor offset inside any mapped C# region?
        // This is the ground truth. It avoids false-positive completions that line-
        // number heuristics can produce (e.g. CSharpCodeBlock leaking into markup).
        bool offsetIsInCSharpRegion = false;
        if (!string.IsNullOrEmpty(localPath))
        {
            var vdoc = _roslynHost.GetVirtualDocument(localPath);
            if (vdoc != null)
                offsetIsInCSharpRegion = vdoc.Map.ToVirtualOffset(offset).HasValue;
        }

        // ── Roslyn C# completions ─────────────────────────────────────────────
        // Route to Roslyn when:
        //   - Cursor is in an inline {expr} expression
        //   - Cursor is in an attr={expr} attribute value (C# expression)
        //   - Source map confirms the offset is inside a C# region (replaces the
        //     old inCodeBlockLine / CSharpCodeBlock line-number heuristic which
        //     leaked into return() and markup lines)
        // Skip '<' trigger — that is always a tag-name completion.
        bool wantsRoslynCompletion =
            triggerChar != "<"
            && ctx.Kind != CursorKind.DirectiveName
            && ctx.Kind != CursorKind.ControlFlowName
            && ctx.Kind != CursorKind.TagName
            && (
                ctx.Kind == CursorKind.CSharpExpression
                || ctx.Kind == CursorKind.AttributeValue
                || offsetIsInCSharpRegion
            );

        if (wantsRoslynCompletion && !string.IsNullOrEmpty(localPath))
        {
            // Parse the trigger char for the CompletionService context.
            char? trigger = triggerChar?.Length == 1 ? triggerChar[0] : (char?)null;

            var roslynList = await _roslynCompletion
                .GetCompletionsAsync(
                    localPath,
                    text,
                    parseResult,
                    offset,
                    trigger,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (roslynList.Count > 0)
            {
                Log($"completion: Roslyn kind={ctx.Kind} → {roslynList.Count} items");
                return new CompletionList(roslynList);
            }

            // Roslyn workspace not yet ready or returned nothing.
            // For confirmed C# positions, return incomplete rather than falling
            // through to UITKX items (which would be meaningless in C# context).
            // isIncomplete: true tells VS Code to retry when the user types more,
            // so completions will appear once Roslyn finishes compiling.
            if (
                ctx.Kind == CursorKind.CSharpExpression
                || ctx.Kind == CursorKind.AttributeValue
                || offsetIsInCSharpRegion
            )
            {
                // Before giving up, check whether the cursor is inside a StyleKeys
                // tuple value (e.g. `(StyleKeys.FlexDirection, "|")`) and return
                // the known string values from the schema if so.
                var styleItems = TryGetStyleKeyValueItems(text, offset);
                if (styleItems != null)
                {
                    var styleList = styleItems.ToList();
                    if (styleList.Count > 0)
                    {
                        Log($"completion: style key value items ({styleList.Count})");
                        return new CompletionList(styleList);
                    }
                }

                Log($"completion: {ctx.Kind} — Roslyn not ready, returning incomplete");
                return new CompletionList(isIncomplete: true);
            }
        }

        // ── Asset / USS path completion — intercept before main dispatch ─────
        // When cursor is inside a quoted string on an @uss line or inside an
        // Asset<T>("...")/Ast<T>("...") call, offer filesystem path completions.
        {
            var pathItems = TryGetAssetPathItems(text, offset, localPath);
            if (pathItems != null)
            {
                var pathList = pathItems.ToList();
                if (pathList.Count > 0)
                {
                    Log($"completion: asset path items ({pathList.Count})");
                    return new CompletionList(pathList);
                }
            }
        }

        var items = ctx.Kind switch
        {
            CursorKind.DirectiveName when inFunctionStylePreamble => FunctionStylePreambleItems(
                ctx.Prefix
            ),
            CursorKind.ControlFlowName when inFunctionStylePreamble => FunctionStylePreambleItems(
                ctx.Prefix
            ),
            CursorKind.None when inFunctionStylePreamble => FunctionStylePreambleItems(ctx.Prefix),
            CursorKind.DirectiveName when inCodeBlockLine && !inEmbeddedMarkupInCode =>
                Enumerable.Empty<CompletionItem>(),
            CursorKind.DirectiveName => ControlFlowItems(ctx.Prefix, text, request.Position),
            CursorKind.ControlFlowName when inCodeBlockLine && !inEmbeddedMarkupInCode =>
                Enumerable.Empty<CompletionItem>(),
            CursorKind.ControlFlowName => ControlFlowItems(ctx.Prefix, text, request.Position),
            CursorKind.TagName => TagItems(ctx.Prefix, text, offset, parseResult.Directives.Backend),
            CursorKind.AttributeName => AttributeItems(
                ctx.TagName ?? "",
                ctx.Prefix,
                HasExistingBinding(text, offset),
                parseResult.Directives.Backend
            ),
            CursorKind.AttributeValue => AttributeValueItems(
                ctx.TagName ?? "",
                ctx.AttributeName ?? "",
                ctx.Prefix,
                parseResult.Directives.Backend
            ),
            CursorKind.None when inCodeBlockLine && triggerChar == "<" => TagItems(
                "",
                text,
                offset,
                parseResult.Directives.Backend
            ),
            _ => Enumerable.Empty<CompletionItem>(),
        };

        var list = items.ToList();
        Log($"completion: kind={ctx.Kind} prefix='{ctx.Prefix}' → {list.Count} items");
        return new CompletionList(list);
    }

    // ── Completion item builders ─────────────────────────────────────────────

    // ── Preamble + declaration items (directives, exports, imports) ────────

    private static IEnumerable<CompletionItem> FunctionStylePreambleItems(string prefix)
    {
        prefix ??= string.Empty;

        // (@namespace and @using still parse — @namespace as a rare escape hatch,
        // @using as a compatibility promise — but neither is offered here: new code
        // takes the file-keyed namespace and the unified import "@Ns" spelling.)
        var candidates = new[]
        {
            (
                label: "import \"@\"",
                insert: "import \"@${1:Some.Namespace}\"",
                detail: "Namespace import — the unified spelling of @using.",
                doc: "Imports a C# namespace using the unified import syntax, e.g. `import \"@Ruitk.Router\"`.\n\nEquivalent to `@using Ruitk.Router`. Braces + `from` (`import { X } from \"./file\"`) import a peer `.uitkx` file; a quoted `\"@Namespace\"` imports a C# namespace."
            ),
            (
                label: "@uss",
                insert: "@uss \"${1:./styles.uss}\"",
                detail: "Attaches a USS stylesheet to this component.",
                doc: "Loads a USS stylesheet and attaches it to the component's root element before panel attachment.\n\nPath is relative to the `.uitkx` file:\n```\n@uss \"./PlayerCard.uss\"\n@uss \"../shared/buttons.uss\"\n```"
            ),
            (
                label: "@backend",
                insert: "@backend ${1|ugui,uitk|}",
                detail: "Selects this file's render backend (ugui or uitk).",
                doc: "Chooses the element vocabulary for the whole file:\n```\n@backend ugui\n```\n`ugui` targets Unity UI (RectTransform anchors, sprites, TMP text) rendered by a `UguiRootRenderer`; `uitk` (the default — the directive can be omitted) targets UI Toolkit. One per file, before any declaration."
            ),
            (
                label: "export VirtualNode",
                insert: "export VirtualNode ${1:MyComponent}(${2}) {\n\treturn (\n\t\t$0\n\t);\n}",
                detail: "Declares an exported component (plain declaration).",
                doc: "Defines a component as a plain typed declaration (ES-modules syntax):\n```\nexport VirtualNode ScoreRow(string label) {\n    return ( <Label text={label} /> );\n}\n```\nA `VirtualNode` return type classifies the declaration as a component (PascalCase name enforced). Omit `export` for a file-private component."
            ),
            (
                label: "export hook (use…)",
                insert: "export ${1:(int value, Action reset)} ${2:useName}(${3}) {\n\t$0\n}",
                detail: "Declares an exported hook (plain declaration).",
                doc: "Defines a hook as a plain typed declaration — a `use`-prefixed name classifies it:\n```\nexport (int count, Action increment) useCounter(int initial) {\n    var (count, setCount) = useState(initial);\n    Action increment = () => setCount(c => c + 1);\n    return (count, increment);\n}\n```"
            ),
            (
                label: "export value",
                insert: "export ${1:Style} ${2:name} = ${3:new Style { }};",
                detail: "Declares an exported value (plain declaration).",
                doc: "Defines a value export:\n```\nexport Style container = new Style { Padding = 10f };\nexport int MaxItems = 5;\nexport theme = new Style { … };   // type inferred from `new T`\n```"
            ),
            (
                label: "export default",
                insert: "export default ${1:Name};",
                detail: "Marks a declaration as this file's default export.",
                doc: "A file has at most one default export; importers bind it with `import Name from \"./file\"`.\n```\nVirtualNode ScorePanel(string title) { … }\nexport default ScorePanel;\n```"
            ),
            (
                label: "export { … }",
                insert: "export { ${1:a, b} };",
                detail: "Deferred export list.",
                doc: "Marks already-declared names as exported:\n```\nint MaxItems = 5;\nstring Label = \"hi\";\nexport { MaxItems, Label };\n```"
            ),
            (
                label: "import * as",
                insert: "import * as ${1:Name} from \"${2:./file}\"",
                detail: "Namespace import of a peer .uitkx file.",
                doc: "Binds the target file's whole export surface to one name:\n```\nimport * as Tokens from \"../shared/Tokens\"\n…  Tokens.Gap  …  <Tokens.Circle />\n```"
            ),
            (
                label: "import default",
                insert: "import ${1:Name} from \"${2:./file}\"",
                detail: "Default import of a peer .uitkx file.",
                doc: "Binds the target file's default export to a local name:\n```\nimport ScorePanel from \"./ScorePanel\"\n…  <ScorePanel />\n```\nThe target marks its default with `export default Name;`."
            ),
            // (The component/hook/module wrapper-keyword items were removed with the
            // legacy grammar in 0.16.0 — UITKX2320 is now an error naming the codemod.)
        };

        return candidates
            .Where(c => c.label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(c => new CompletionItem
            {
                Label = c.label,
                Kind = CompletionItemKind.Keyword,
                InsertText = c.insert,
                InsertTextFormat = InsertTextFormat.Snippet,
                Detail = c.detail,
                Documentation = new MarkupContent { Kind = MarkupKind.Markdown, Value = c.doc },
            });
    }

    private IEnumerable<CompletionItem> ControlFlowItems(
        string prefix,
        string sourceText,
        Position position
    )
    {
        prefix ??= string.Empty;
        int offset = ToOffset(sourceText, position);
        int prefixStart = Math.Max(0, offset - (prefix?.Length ?? 0));
        int atIndex = Math.Max(0, prefixStart - 1);

        var allowed = GetAllowedControlFlowNames(sourceText, atIndex, offset);

        return _schema
            .Root.ControlFlow.Where(d =>
                allowed.Contains(d.Name ?? string.Empty)
                && (d.Name ?? string.Empty).StartsWith(
                    prefix ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Select(d => new CompletionItem
            {
                Label = "@" + d.Name,
                Kind = CompletionItemKind.Keyword,
                InsertText = BuildControlFlowSnippet(d.Name),
                InsertTextFormat = InsertTextFormat.Snippet,
                Detail = "UITKX control flow",
                Documentation = new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = d.Description,
                },
            });
    }

    private enum BlockKind
    {
        Unknown,
        Switch,
        Loop,
        If,
    }

    private static HashSet<string> GetAllowedControlFlowNames(
        string sourceText,
        int atIndex,
        int cursorOffset
    )
    {
        var stack = ScanBlockStack(sourceText, atIndex);
        BlockKind top = stack.Count > 0 ? stack[stack.Count - 1] : BlockKind.Unknown;

        // Always valid in markup positions
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "if",
            "for",
            "foreach",
            "while",
            "switch",
        };

        // @else is meaningful right after a closed branch, and also when cursor
        // is positioned just before the closing '}' of an if-branch body.
        char prevSig = PreviousSignificantChar(sourceText, atIndex);
        char nextSig = NextSignificantChar(sourceText, cursorOffset);
        if (prevSig == '}' || (nextSig == '}' && stack.Contains(BlockKind.If)))
            allowed.Add("else");

        // @case/@default should only appear at direct switch body level
        if (top == BlockKind.Switch)
        {
            allowed.Add("case");
            allowed.Add("default");
            allowed.Add("break"); // switch-case terminator style
        }

        // Loop-flow appears when inside loop body (including nested blocks inside loop)
        if (stack.Contains(BlockKind.Loop))
        {
            allowed.Add("break");
            allowed.Add("continue");
        }

        return allowed;
    }

    private static List<BlockKind> ScanBlockStack(string text, int limitExclusive)
    {
        var stack = new List<BlockKind>();
        BlockKind? pending = null;

        int limit = Math.Max(0, Math.Min(limitExclusive, text.Length));
        int i = 0;
        while (i < limit)
        {
            char c = text[i];

            // line comment
            if (c == '/' && i + 1 < limit && text[i + 1] == '/')
            {
                i += 2;
                while (i < limit && text[i] != '\n')
                    i++;
                continue;
            }

            // block comment
            if (c == '/' && i + 1 < limit && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < limit && !(text[i] == '*' && text[i + 1] == '/'))
                    i++;
                i = Math.Min(i + 2, limit);
                continue;
            }

            // string literal (rough skip)
            if (c == '"' || c == '\'')
            {
                char quote = c;
                i++;
                while (i < limit)
                {
                    if (text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (text[i] == quote)
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (c == '@')
            {
                int j = i + 1;
                while (j < limit && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
                    j++;

                if (j > i + 1)
                {
                    string kw = text.Substring(i + 1, j - (i + 1));
                    pending = kw switch
                    {
                        "switch" => BlockKind.Switch,
                        "for" => BlockKind.Loop,
                        "while" => BlockKind.Loop,
                        "foreach" => BlockKind.Loop,
                        "if" => BlockKind.If,
                        "else" => BlockKind.If,
                        _ => null,
                    };
                }

                i = j;
                continue;
            }

            if (c == '{')
            {
                stack.Add(pending ?? BlockKind.Unknown);
                pending = null;
                i++;
                continue;
            }

            if (c == '}')
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                pending = null;
                i++;
                continue;
            }

            i++;
        }

        return stack;
    }

    private static char PreviousSignificantChar(string text, int indexExclusive)
    {
        int i = Math.Min(indexExclusive - 1, text.Length - 1);
        while (i >= 0)
        {
            char c = text[i];
            if (!char.IsWhiteSpace(c))
                return c;
            i--;
        }
        return '\0';
    }

    private static char NextSignificantChar(string text, int indexInclusive)
    {
        int i = Math.Max(0, indexInclusive);
        while (i < text.Length)
        {
            char c = text[i];
            if (!char.IsWhiteSpace(c))
                return c;
            i++;
        }
        return '\0';
    }

    private static bool IsInsideCodeBlockAtOffset(string sourceText, int targetOffset)
    {
        bool inCode = false;
        bool awaitingCodeBrace = false;
        int codeBraceDepth = 0;

        bool inLineComment = false;
        bool inBlockComment = false;
        bool inString = false;
        bool inChar = false;

        int limit = Math.Max(0, Math.Min(targetOffset, sourceText.Length));
        for (int i = 0; i < limit; i++)
        {
            char ch = sourceText[i];
            char next = i + 1 < limit ? sourceText[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inString)
            {
                if (ch == '\\')
                {
                    i++;
                    continue;
                }
                if (ch == '"')
                    inString = false;
                continue;
            }

            if (inChar)
            {
                if (ch == '\\')
                {
                    i++;
                    continue;
                }
                if (ch == '\'')
                    inChar = false;
                continue;
            }

            if (ch == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (ch == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '\'')
            {
                inChar = true;
                continue;
            }

            if (!inCode)
            {
                if (
                    !awaitingCodeBrace
                    && ch == '@'
                    && i + 5 < limit
                    && sourceText.Substring(i + 1, 4) == "code"
                )
                {
                    char prev = i > 0 ? sourceText[i - 1] : '\0';
                    char after = i + 5 < limit ? sourceText[i + 5] : '\0';
                    bool prevOk = prev == '\0' || !(char.IsLetterOrDigit(prev) || prev == '_');
                    bool afterOk = after == '\0' || !(char.IsLetterOrDigit(after) || after == '_');
                    if (prevOk && afterOk)
                    {
                        awaitingCodeBrace = true;
                        i += 4;
                        continue;
                    }
                }

                if (awaitingCodeBrace)
                {
                    if (ch == '{')
                    {
                        inCode = true;
                        codeBraceDepth = 1;
                        awaitingCodeBrace = false;
                    }
                    continue;
                }

                continue;
            }

            if (ch == '{')
            {
                codeBraceDepth++;
                continue;
            }

            if (ch == '}')
            {
                codeBraceDepth--;
                if (codeBraceDepth <= 0)
                {
                    inCode = false;
                    codeBraceDepth = 0;
                }
            }
        }

        return inCode;
    }

    private static bool IsLikelyEmbeddedMarkupAtOffset(string sourceText, int offset)
    {
        var (lineText, lineStart) = GetLineAtOffset(sourceText, offset);
        int col = Math.Max(0, Math.Min(offset - lineStart, lineText.Length));
        string left = lineText.Substring(0, col).TrimStart();
        string full = lineText.TrimStart();

        if (
            left.StartsWith("<", StringComparison.Ordinal)
            || full.StartsWith("<", StringComparison.Ordinal)
        )
            return true;

        if (
            full.StartsWith("@if", StringComparison.Ordinal)
            || full.StartsWith("@else", StringComparison.Ordinal)
            || full.StartsWith("@for", StringComparison.Ordinal)
            || full.StartsWith("@foreach", StringComparison.Ordinal)
            || full.StartsWith("@while", StringComparison.Ordinal)
            || full.StartsWith("@switch", StringComparison.Ordinal)
            || full.StartsWith("@case", StringComparison.Ordinal)
            || full.StartsWith("@default", StringComparison.Ordinal)
        )
            return true;

        // If a previous nearby non-empty line looks like markup, treat this line as markup context too.
        int back = lineStart - 1;
        int examined = 0;
        while (back > 0 && examined < 25)
        {
            var (prev, prevStart) = GetLineAtOffset(sourceText, back);
            string t = prev.Trim();
            if (t.Length > 0)
            {
                if (
                    t.StartsWith("<", StringComparison.Ordinal)
                    || t.StartsWith("</", StringComparison.Ordinal)
                    || t.StartsWith("@if", StringComparison.Ordinal)
                    || t.StartsWith("@else", StringComparison.Ordinal)
                    || t.StartsWith("@for", StringComparison.Ordinal)
                    || t.StartsWith("@foreach", StringComparison.Ordinal)
                    || t.StartsWith("@while", StringComparison.Ordinal)
                    || t.StartsWith("@switch", StringComparison.Ordinal)
                    || t.StartsWith("@case", StringComparison.Ordinal)
                    || t.StartsWith("@default", StringComparison.Ordinal)
                )
                    return true;
                return false;
            }
            back = prevStart - 1;
            examined++;
        }

        return false;
    }

    private static (string lineText, int lineStartOffset) GetLineAtOffset(string text, int offset)
    {
        int o = Math.Max(0, Math.Min(offset, text.Length));
        int start = o;
        while (start > 0 && text[start - 1] != '\n')
            start--;

        int end = o;
        while (end < text.Length && text[end] != '\n')
            end++;

        string line = text.Substring(start, end - start).TrimEnd('\r');
        return (line, start);
    }

    private IEnumerable<CompletionItem> TagItems(
        string prefix,
        string text,
        int offset,
        string? backend
    )
    {
        bool existingTag = HasExistingTagBody(text, offset);

        // Dynamic elements from workspace (one item per known element)
        var knownElements = _index.KnownElements;

        var dynamicItems = knownElements
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(name =>
            {
                var props = _index.GetProps(name);
                var detail = $"{name}Props";
                var docMd =
                    props.Count == 0
                        ? $"Component `{name}`"
                        : $"Component `{name}` — {props.Count} prop(s):\n"
                            + string.Join(", ", props.Take(5).Select(p => $"`{p.Name}`"));
                var acceptsChildren = _schema.TryGetElement(name, backend)?.AcceptsChildren ?? true;
                return new CompletionItem
                {
                    Label = name,
                    Kind = CompletionItemKind.Class,
                    InsertText =
                        existingTag ? name
                        : acceptsChildren ? $"{name} "
                        : $"{name} $1 />",
                    InsertTextFormat = existingTag
                        ? InsertTextFormat.PlainText
                        : InsertTextFormat.Snippet,
                    Detail = detail,
                    Documentation = new MarkupContent { Kind = MarkupKind.Markdown, Value = docMd },
                };
            });

        // Schema built-ins not already covered by the workspace index
        var userVersion = _roslynHost.DetectedUnityVersion;
        var schemaItems = _schema
            .GetElements(backend)
            .Where(kv =>
                kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !knownElements.Contains(kv.Key)
            )
            .Select(kv =>
            {
                var va = GetVersionAnnotation(kv.Value.SinceUnity, userVersion);
                return new CompletionItem
                {
                    Label = va.HasValue ? $"{va.Value.LabelPrefix}{kv.Key}" : kv.Key,
                    Kind = CompletionItemKind.Class,
                    InsertText = existingTag ? kv.Key : BuildTagSnippet(kv.Key, kv.Value),
                    InsertTextFormat = existingTag
                        ? InsertTextFormat.PlainText
                        : InsertTextFormat.Snippet,
                    SortText = va.HasValue ? $"{va.Value.SortPrefix}{kv.Key}" : null,
                    Detail = va.HasValue
                        ? (kv.Value.PropsType ?? "") + va.Value.DetailSuffix
                        : kv.Value.PropsType,
                    Documentation = new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = kv.Value.Description,
                    },
                };
            });

        return dynamicItems.Concat(schemaItems);
    }

    private IEnumerable<CompletionItem> AttributeItems(
        string tagName,
        string prefix,
        bool hasExistingBinding,
        string? backend
    )
    {
        var workspaceProps = _index.GetProps(tagName);
        var coveredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in workspaceProps)
        {
            coveredNames.Add(p.Name);
            coveredNames.Add(CanonicalSchemaAttributeName(tagName, p.Name, backend));
        }

        // Props declared in *Props.cs (dynamic, workspace-specific)
        var dynItems = workspaceProps
            .Where(p => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                var canonicalName = CanonicalSchemaAttributeName(tagName, p.Name, backend);
                var isString = p.Type.Equals("string", StringComparison.OrdinalIgnoreCase);
                // When an existing ={value} binding follows the cursor, insert
                // only the attribute name — the binding is already there.
                var insert =
                    hasExistingBinding ? canonicalName
                    : isString ? $"{canonicalName}=\"$1\""
                    : $"{canonicalName}={{$1}}";
                var format = hasExistingBinding
                    ? InsertTextFormat.PlainText
                    : InsertTextFormat.Snippet;
                var doc = string.IsNullOrEmpty(p.XmlDoc) ? $"**{p.Type}** `{p.Name}`" : p.XmlDoc;
                return new CompletionItem
                {
                    Label = canonicalName,
                    Kind = CompletionItemKind.Property,
                    InsertText = insert,
                    InsertTextFormat = format,
                    Detail = p.Type,
                    Documentation = new MarkupContent { Kind = MarkupKind.Markdown, Value = doc },
                };
            });

        // Schema attrs for built-in elements + universal attrs not already covered
        var userVersion = _roslynHost.DetectedUnityVersion;
        var schemaItems = _schema
            .GetAttributesForElement(tagName, backend)
            .Where(a =>
                !coveredNames.Contains(a.Name)
                && a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !IsRemovedForVersion(a, userVersion)
            )
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(a =>
            {
                var va = GetVersionAnnotation(a.SinceUnity, userVersion);
                return new CompletionItem
                {
                    Label = va.HasValue ? $"{va.Value.LabelPrefix}{a.Name}" : a.Name,
                    Kind = CompletionItemKind.Property,
                    InsertText = hasExistingBinding ? a.Name : a.Name + "=\"$1\"",
                    InsertTextFormat = hasExistingBinding
                        ? InsertTextFormat.PlainText
                        : InsertTextFormat.Snippet,
                    SortText = va.HasValue ? $"{va.Value.SortPrefix}{a.Name}" : null,
                    Detail = va.HasValue ? (a.Type ?? "") + va.Value.DetailSuffix : a.Type,
                    Documentation = new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = a.Description,
                    },
                };
            });

        return dynItems.Concat(schemaItems);
    }

    private string CanonicalSchemaAttributeName(string tagName, string fallbackName, string? backend)
    {
        var schemaAttr = _schema
            .GetAttributesForElement(tagName, backend)
            .FirstOrDefault(a => a.Name.Equals(fallbackName, StringComparison.OrdinalIgnoreCase));

        return schemaAttr?.Name ?? fallbackName;
    }

    private IEnumerable<CompletionItem> AttributeValueItems(
        string tagName,
        string attributeName,
        string prefix,
        string? backend
    )
    {
        var attr = _schema
            .GetAttributesForElement(tagName, backend)
            .FirstOrDefault(a => a.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase));

        if (attr is null)
            return Enumerable.Empty<CompletionItem>();

        var type = attr.Type.ToLowerInvariant();

        if (type is "bool" or "boolean")
        {
            return new[]
            {
                ValueItem("true", "bool", "true"),
                ValueItem("false", "bool", "false"),
            }.Where(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        if (type is "int" or "long" or "short" or "byte" or "uint" or "ulong")
        {
            return new[] { ValueItem("0", attr.Type, "0") };
        }

        if (type is "float" or "double" or "decimal")
        {
            return new[] { ValueItem("0.0", attr.Type, "0.0") };
        }

        if (type == "action")
        {
            return new[]
            {
                new CompletionItem
                {
                    Label = "() => { }",
                    Kind = CompletionItemKind.Function,
                    InsertText = "() => { $0 }",
                    InsertTextFormat = InsertTextFormat.Snippet,
                    Detail = attr.Type,
                    Documentation = new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = "Inline callback expression.",
                    },
                },
            };
        }

        if (type is "object" or "style")
        {
            return new[] { ValueItem("{ }", attr.Type, "{ $0 }", true) };
        }

        // ── Enum-typed attribute shortcuts (CssHelpers) ──────────────────
        var enumItems = GetEnumShortcutItems(tagName, attributeName, type, prefix);
        if (enumItems != null)
            return enumItems;

        return Enumerable.Empty<CompletionItem>();
    }

    /// <summary>
    /// Maps enum-typed and string-enum attributes to their CssHelpers shortcut names.
    /// Returns null when the attribute has no known shortcuts.
    /// </summary>
    private IEnumerable<CompletionItem>? GetEnumShortcutItems(
        string tagName,
        string attributeName,
        string typeLC,
        string prefix
    )
    {
        var shortcuts = ResolveEnumShortcuts(tagName, attributeName, typeLC);
        if (shortcuts == null)
            return null;

        return shortcuts
            .Where(s => s.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(s => new CompletionItem
            {
                Label = s.Label,
                Kind = CompletionItemKind.EnumMember,
                InsertText = s.Label,
                InsertTextFormat = InsertTextFormat.PlainText,
                Detail = s.Detail,
                Documentation = new MarkupContent { Kind = MarkupKind.Markdown, Value = s.Doc },
            });
    }

    private record struct EnumShortcut(string Label, string Detail, string Doc);

    private static EnumShortcut[]? ResolveEnumShortcuts(
        string tagName,
        string attributeName,
        string typeLC
    )
    {
        // Typed enum attributes (schema type is the enum name)
        switch (typeLC)
        {
            case "pickingmode":
                return new[]
                {
                    new EnumShortcut("PickPosition", "PickingMode", "`PickingMode.Position`"),
                    new EnumShortcut("PickIgnore", "PickingMode", "`PickingMode.Ignore`"),
                };
            case "selectiontype":
                return new[]
                {
                    new EnumShortcut("SelectNone", "SelectionType", "`SelectionType.None`"),
                    new EnumShortcut("SelectSingle", "SelectionType", "`SelectionType.Single`"),
                    new EnumShortcut("SelectMultiple", "SelectionType", "`SelectionType.Multiple`"),
                };
            case "scrollervisibility":
                return new[]
                {
                    new EnumShortcut(
                        "ScrollerAuto",
                        "ScrollerVisibility",
                        "`ScrollerVisibility.Auto`"
                    ),
                    new EnumShortcut(
                        "ScrollerVisible",
                        "ScrollerVisibility",
                        "`ScrollerVisibility.AlwaysVisible`"
                    ),
                    new EnumShortcut(
                        "ScrollerHidden",
                        "ScrollerVisibility",
                        "`ScrollerVisibility.Hidden`"
                    ),
                };
            case "languagedirection":
                return new[]
                {
                    new EnumShortcut(
                        "DirInherit",
                        "LanguageDirection",
                        "`LanguageDirection.Inherit`"
                    ),
                    new EnumShortcut("DirLTR", "LanguageDirection", "`LanguageDirection.LTR`"),
                    new EnumShortcut("DirRTL", "LanguageDirection", "`LanguageDirection.RTL`"),
                };
        }

        // String-based attributes — use (tagName, attributeName) for disambiguation
        var attrLC = attributeName.ToLowerInvariant();
        var tagLC = tagName.ToLowerInvariant();

        if (attrLC == "direction" && (tagLC is "slider" or "sliderint" or "minmaxslider"))
        {
            return new[]
            {
                new EnumShortcut(
                    "SliderHorizontal",
                    "string",
                    "Slider `SliderDirection.Horizontal`"
                ),
                new EnumShortcut("SliderVertical", "string", "Slider `SliderDirection.Vertical`"),
            };
        }

        if (attrLC == "mode" && tagLC == "scrollview")
        {
            return new[]
            {
                new EnumShortcut("ScrollVertical", "string", "`ScrollViewMode.Vertical`"),
                new EnumShortcut("ScrollHorizontal", "string", "`ScrollViewMode.Horizontal`"),
                new EnumShortcut("ScrollBoth", "string", "`ScrollViewMode.VerticalAndHorizontal`"),
            };
        }

        if (attrLC == "scalemode" && tagLC == "image")
        {
            return new[]
            {
                new EnumShortcut("ScaleStretch", "string", "`ScaleMode.StretchToFill`"),
                new EnumShortcut("ScaleFit", "string", "`ScaleMode.ScaleToFit`"),
                new EnumShortcut("ScaleCrop", "string", "`ScaleMode.ScaleAndCrop`"),
            };
        }

        if (attrLC == "orientation" && tagLC == "twopanesplitview")
        {
            return new[]
            {
                new EnumShortcut(
                    "OrientHorizontal",
                    "string",
                    "`TwoPaneSplitViewOrientation.Horizontal`"
                ),
                new EnumShortcut(
                    "OrientVertical",
                    "string",
                    "`TwoPaneSplitViewOrientation.Vertical`"
                ),
            };
        }

        if (attrLC == "sortingmode" && tagLC is "multicolumnlistview" or "multicolumntreeview")
        {
            return new[]
            {
                new EnumShortcut("SortNone", "string", "`ColumnSortingMode.None`"),
                new EnumShortcut("SortDefault", "string", "`ColumnSortingMode.Default`"),
                new EnumShortcut("SortCustom", "string", "`ColumnSortingMode.Custom`"),
            };
        }

        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildTagSnippet(string tagName, UitkxSchema.ElementInfo info) =>
        info.AcceptsChildren ? $"{tagName} " : $"{tagName} $1 />";

    private static string BuildControlFlowSnippet(string name) =>
        name switch
        {
            "if" => "@if ($1)\n{\n\t$0\n}",
            "else" => "@else\n{\n\t$0\n}",
            "foreach" => "@foreach (var item in $1)\n{\n\t$0\n}",
            "switch" => "@switch ($1)\n{\n\t@case $2 => $0\n}",
            "case" => "@case $1 => $0",
            "default" => "@default => $0",
            "for" => "@for (int $1 = 0; $1 < $2; $1++)\n{\n\t$0\n}",
            "while" => "@while ($1)\n{\n\t$0\n}",
            _ => "@" + name,
        };

    private static CompletionItem ValueItem(
        string label,
        string detail,
        string insertText,
        bool snippet = false
    ) =>
        new()
        {
            Label = label,
            Kind = CompletionItemKind.Value,
            InsertText = insertText,
            InsertTextFormat = snippet ? InsertTextFormat.Snippet : InsertTextFormat.PlainText,
            Detail = detail,
        };

    /// <summary>
    /// Detects the pattern <c>(StyleKeys.SomeProp, "prefix</c> directly before
    /// <paramref name="offset"/> (on the same line) and returns completion items
    /// for the matching values from <see cref="_schema"/>.StyleKeyValues.
    /// Returns <c>null</c> if the pattern is not matched.
    /// </summary>
    private IEnumerable<CompletionItem>? TryGetStyleKeyValueItems(string text, int offset)
    {
        // Find start of current line.
        int lineStart = offset;
        while (lineStart > 0 && text[lineStart - 1] != '\n')
            lineStart--;

        string lineUpToCursor = text.Substring(lineStart, offset - lineStart);

        // Match:  ( StyleKeys.SomeKey ,  "prefix
        // Group 1 = PascalCase key name (e.g. "FlexDirection")
        // Group 2 = already-typed prefix inside the string literal (may be empty)
        var match = System.Text.RegularExpressions.Regex.Match(
            lineUpToCursor,
            @"StyleKeys\.([A-Za-z]+)\s*,\s*""([^""]*)$"
        );
        if (!match.Success)
            return null;

        string propName = match.Groups[1].Value; // e.g. "FlexDirection"
        string typedSoFar = match.Groups[2].Value; // e.g. "ro"

        // Schema keys are camelCase ("flexDirection"); StyleKeys constants are PascalCase.
        string camelKey = char.ToLowerInvariant(propName[0]) + propName.Substring(1);

        if (!_schema.Root.StyleKeyValues.TryGetValue(camelKey, out var values))
            return null;

        var userVersion = _roslynHost.DetectedUnityVersion;
        var versionInfo = _schema.GetStyleVersionInfo(camelKey);

        return values
            .Where(v => v.StartsWith(typedSoFar, StringComparison.OrdinalIgnoreCase))
            .Select(v =>
            {
                var va = GetVersionAnnotation(versionInfo?.SinceUnity, userVersion);
                return new CompletionItem
                {
                    Label = va.HasValue ? $"{va.Value.LabelPrefix}{v}" : v,
                    Kind = CompletionItemKind.Value,
                    SortText = va.HasValue ? $"{va.Value.SortPrefix}{v}" : null,
                    Detail = va.HasValue
                        ? $"StyleKeys.{propName}{va.Value.DetailSuffix}"
                        : $"StyleKeys.{propName}",
                    InsertText = v,
                };
            });
    }

    private static int ToOffset(string text, Position position)
    {
        var line = (int)position.Line;
        var column = (int)position.Character;
        var offset = 0;
        for (var i = 0; i < line && offset < text.Length; i++)
        {
            var nl = text.IndexOf('\n', offset);
            offset = nl < 0 ? text.Length : nl + 1;
        }
        return Math.Min(offset + column, text.Length);
    }

    /// <summary>
    /// Returns <c>true</c> when the text after the cursor already contains an
    /// <c>=</c> binding (e.g. <c>={bg}</c> or <c>="hello"</c>).  Skips past
    /// any remaining identifier chars (rest of the attribute name being replaced)
    /// and optional whitespace before checking for <c>=</c>.
    /// </summary>
    private static bool HasExistingBinding(string text, int offset)
    {
        int i = offset;

        // Skip remaining identifier chars (the rest of the word after cursor).
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i++;

        // Skip optional whitespace.
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            i++;

        return i < text.Length && text[i] == '=';
    }

    /// <summary>
    /// Returns true when the cursor is inside an existing open tag — i.e. after
    /// the tag name there are attributes, '>', or '/>' already present.
    /// When true, tag completion should replace only the name, not insert a
    /// closing tag snippet.
    /// </summary>
    private static bool HasExistingTagBody(string text, int offset)
    {
        int i = offset;

        // Skip remaining identifier chars (rest of old tag name after cursor).
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i++;

        // Skip optional whitespace.
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            i++;

        // If next non-whitespace is an attribute start, '>', or '/>' we're inside an existing tag.
        if (i >= text.Length)
            return false;
        char c = text[i];
        return c == '>' || c == '/' || char.IsLetter(c);
    }

    // ── Version-awareness helpers ─────────────────────────────────────────────

    /// <summary>
    /// Given a schema entry's <c>sinceUnity</c> annotation and the user's detected
    /// Unity version, returns version annotation info that callers should merge into
    /// their <see cref="CompletionItem"/> during object initialisation.
    /// Returns <c>null</c> when no annotation is needed (feature is available).
    /// </summary>
    private static VersionAnnotation? GetVersionAnnotation(
        string? sinceUnity,
        UnityVersion userVersion
    )
    {
        if (sinceUnity is null)
            return null;
        if (!UnityVersion.TryParse(sinceUnity, out var minVersion))
            return null;
        if (!userVersion.IsKnown || userVersion >= minVersion)
            return null;

        return new VersionAnnotation(minVersion);
    }

    /// <summary>Holds pre-computed display values for a version-annotated completion item.</summary>
    private readonly record struct VersionAnnotation(UnityVersion MinVersion)
    {
        /// <summary>Label prefix, e.g. <c>"⚠️ "</c>.</summary>
        public string LabelPrefix => "⚠️ ";

        /// <summary>Sort text prefix to push these items to the bottom.</summary>
        public string SortPrefix => "zz_";

        /// <summary>Detail suffix, e.g. <c>"  • Requires Unity 6.3+"</c>.</summary>
        public string DetailSuffix => $"  •  Requires {MinVersion.ToDisplayString()}+";
    }

    /// <summary>
    /// Returns <c>true</c> when the attribute has a <c>removedIn</c> annotation
    /// and the user's Unity version is at or past that version.
    /// </summary>
    private static bool IsRemovedForVersion(
        UitkxSchema.AttributeInfo attr,
        UnityVersion userVersion
    )
    {
        if (attr.RemovedIn is null || !userVersion.IsKnown)
            return false;
        return UnityVersion.TryParse(attr.RemovedIn, out var removedVersion)
            && userVersion >= removedVersion;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ASSET / USS PATH COMPLETION
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extension-to-CompletionItemKind mapping for asset file completions.
    /// </summary>
    private static readonly Dictionary<string, CompletionItemKind> s_assetExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".uss",  CompletionItemKind.File },
            { ".png",  CompletionItemKind.File },
            { ".jpg",  CompletionItemKind.File },
            { ".jpeg", CompletionItemKind.File },
            { ".svg",  CompletionItemKind.File },
            { ".ttf",  CompletionItemKind.File },
            { ".otf",  CompletionItemKind.File },
            { ".wav",  CompletionItemKind.File },
            { ".mp3",  CompletionItemKind.File },
            { ".ogg",  CompletionItemKind.File },
            { ".mat",  CompletionItemKind.File },
            { ".asset", CompletionItemKind.File },
            { ".renderTexture", CompletionItemKind.File },
        };

    /// <summary>
    /// Detects whether the cursor is inside a quoted string in an <c>@uss "..."</c>
    /// directive or <c>Asset&lt;T&gt;("...")</c> / <c>Ast&lt;T&gt;("...")</c> call,
    /// and returns filesystem path completions if so.
    /// Returns <c>null</c> when the cursor is not in a path context.
    /// </summary>
    private static List<CompletionItem>? TryGetAssetPathItems(
        string text, int offset, string uitkxPath)
    {
        if (string.IsNullOrEmpty(uitkxPath))
            return null;

        // Find the start of the current line.
        int lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        string lineText = text.Substring(lineStart, offset - lineStart);

        // Detect @uss "..." context  — cursor must be after the opening quote
        // Detect Asset<T>("...") / Ast<T>("...") context
        int quoteStart = -1;
        string? extensionFilter = null;

        // Pattern 1: @uss "<cursor>"
        var ussMatch = System.Text.RegularExpressions.Regex.Match(
            lineText, @"@uss\s+""([^""]*)$");
        if (ussMatch.Success)
        {
            quoteStart = ussMatch.Groups[1].Index + lineStart;
            extensionFilter = ".uss";
        }

        // Pattern 2: Asset<T>("...") or Ast<T>("...")
        if (quoteStart < 0)
        {
            var assetMatch = System.Text.RegularExpressions.Regex.Match(
                lineText, @"(?:Asset|Ast)\s*<\s*\w+\s*>\s*\(\s*""([^""]*)$");
            if (assetMatch.Success)
            {
                quoteStart = assetMatch.Groups[1].Index + lineStart;
                // No filter — could be any supported asset type
            }
        }

        if (quoteStart < 0)
            return null;

        // Extract the partial path typed so far (between opening quote and cursor)
        string partialPath = text.Substring(quoteStart, offset - quoteStart);

        // Resolve the directory to search
        string uitkxDir = Path.GetDirectoryName(uitkxPath) ?? "";
        if (string.IsNullOrEmpty(uitkxDir))
            return null;

        // Split partial path into directory prefix and filename prefix
        // e.g. "./styles/ma" → dir="./styles", prefix="ma"
        string searchDir;
        string filePrefix;
        int lastSlash = partialPath.LastIndexOfAny(new[] { '/', '\\' });
        if (lastSlash >= 0)
        {
            string relDir = partialPath.Substring(0, lastSlash);
            filePrefix = partialPath.Substring(lastSlash + 1);
            searchDir = Path.GetFullPath(Path.Combine(uitkxDir, relDir));
        }
        else
        {
            filePrefix = partialPath;
            searchDir = uitkxDir;
        }

        if (!Directory.Exists(searchDir))
            return null;

        var items = new List<CompletionItem>();

        // Add subdirectories as folder completions
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(searchDir))
            {
                string dirName = Path.GetFileName(dir);
                if (dirName.StartsWith(".") || dirName.EndsWith("~"))
                    continue; // skip hidden/Unity-ignored folders
                if (!string.IsNullOrEmpty(filePrefix) &&
                    !dirName.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                items.Add(new CompletionItem
                {
                    Label = dirName + "/",
                    Kind = CompletionItemKind.Folder,
                    InsertText = dirName + "/",
                    Detail = "Directory",
                    Command = new Command
                    {
                        Name = "editor.action.triggerSuggest",
                        Title = "Re-trigger completions",
                    },
                });
            }
        }
        catch { /* permission errors */ }

        // Add matching files
        try
        {
            foreach (var file in Directory.EnumerateFiles(searchDir))
            {
                string fileName = Path.GetFileName(file);
                string ext = Path.GetExtension(file);

                // Filter by extension when in @uss context
                if (extensionFilter != null &&
                    !string.Equals(ext, extensionFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                // For Asset<T> context, only show known asset types
                if (extensionFilter == null && !s_assetExtensions.ContainsKey(ext))
                    continue;

                if (!string.IsNullOrEmpty(filePrefix) &&
                    !fileName.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                items.Add(new CompletionItem
                {
                    Label = fileName,
                    Kind = CompletionItemKind.File,
                    InsertText = fileName,
                    Detail = ext.TrimStart('.').ToUpperInvariant() + " file",
                });
            }
        }
        catch { /* permission errors */ }

        return items;
    }

    /// <summary>1-based line text (without its terminator) from <paramref name="text"/>, or empty.</summary>
    private static string GetLine(string text, int oneBasedLine)
    {
        if (oneBasedLine < 1) return string.Empty;
        int start = 0;
        for (int ln = 1; ln < oneBasedLine; ln++)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0) return string.Empty;
            start = nl + 1;
        }
        int end = text.IndexOf('\n', start);
        string line = end < 0 ? text.Substring(start) : text.Substring(start, end - start);
        return line.TrimEnd('\r');
    }

    /// <summary>
    /// Completion inside an <c>import {{ … }} from "spec"</c> binding list (import/export grammar,
    /// leg 3): the exported names of the resolved target file, minus those already listed. Pure and
    /// filesystem-only, so it is directly unit-testable. Returns empty when the cursor is not inside
    /// an import brace list or the specifier is missing/unresolvable.
    /// </summary>
    public static IReadOnlyList<string> GetImportBraceCompletions(string importerPath, string lineText, int col0)
    {
        var empty = System.Array.Empty<string>();
        int braceOpen = lineText.IndexOf('{');
        if (braceOpen < 0) return empty;
        // Combined ES form (0.9.1): `import Def, { … } from` — an optional default binding
        // (plus comma) may precede the brace list.
        var prefix = Regex.Match(
            lineText.Substring(0, braceOpen),
            @"^\s*import\s*(?:(?<def>[A-Za-z_]\w*)\s*,\s*)?$");
        if (!prefix.Success) return empty;

        int braceClose = lineText.IndexOf('}', braceOpen);
        int regionEnd = braceClose < 0 ? lineText.Length : braceClose;
        if (col0 <= braceOpen || col0 > regionEnd) return empty;

        var spec = Regex.Match(lineText, "from\\s*\"([^\"]*)\"");
        if (!spec.Success) return empty;

        string importerDir = (Path.GetDirectoryName(importerPath) ?? string.Empty).Replace('\\', '/');
        string? projectRoot = AssetPathUtil.GetProjectRoot(importerPath);
        string rootDir = projectRoot != null ? projectRoot + "/" + UitkxConfig.LoadRoot(importerDir) : importerDir;
        string? target = ImportResolver.MapSpecifierToPath(importerDir, spec.Groups[1].Value, rootDir, out _);
        if (target is null || !File.Exists(target)) return empty;

        var diags = new List<Ruitk.Language.ParseDiagnostic>();
        var ds = DirectiveParser.Parse(File.ReadAllText(target), target, diags);

        // Each listed entry may be aliased (`a as b`): the imported NAME is what must not be
        // re-suggested, and the bound ALIAS would collide with a same-named export — track both.
        var already = new HashSet<string>(StringComparer.Ordinal);
        if (prefix.Groups["def"].Success)
        {
            // The default binding already covers the target's default export — re-suggesting
            // it as a named entry is noise; the alias itself would collide too.
            already.Add(prefix.Groups["def"].Value);
            if (ds.DefaultExportName != null)
                already.Add(ds.DefaultExportName);
        }
        foreach (var entry in lineText.Substring(braceOpen + 1, regionEnd - braceOpen - 1).Split(','))
        {
            var tokens = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3 && tokens[1] == "as")
            {
                already.Add(tokens[0]);
                already.Add(tokens[2]);
            }
            else if (tokens.Length > 0)
            {
                already.Add(tokens[0]);
            }
        }

        var names = new List<string>();
        void Add(string n, bool exported) { if (exported && !already.Contains(n) && !names.Contains(n)) names.Add(n); }
        if (!ds.ComponentDeclarations.IsDefaultOrEmpty)
            foreach (var c in ds.ComponentDeclarations) Add(c.Name, c.IsExported);
        // Plain member declarations (ES-modules campaign, M5): values/utils/hooks on __Exports.
        if (!ds.MemberDeclarations.IsDefaultOrEmpty)
            foreach (var mem in ds.MemberDeclarations) Add(mem.Name, mem.IsExported);
        return names;
    }

    /// <summary>
    /// Path completion inside an import <c>from "…"</c> specifier string (import/export grammar, leg
    /// 3): <c>./</c>-relative, extensionless specifiers to peer <c>.uitkx</c> files under the
    /// importer's directory tree (the common case; <c>../</c> / <c>~/</c> are typed by hand). Pure +
    /// filesystem-only, so it is directly unit-testable. Empty when the cursor is not inside the
    /// specifier quotes of an import line.
    /// </summary>
    public static IReadOnlyList<string> GetSpecifierCompletions(string importerPath, string lineText, int col0)
    {
        var empty = System.Array.Empty<string>();
        if (!Regex.IsMatch(lineText, @"^\s*import\b")) return empty;

        var m = Regex.Match(lineText, "from\\s*\"");
        if (!m.Success) return empty;
        int quoteStart = m.Index + m.Length;
        int quoteEnd = lineText.IndexOf('"', quoteStart);
        int end = quoteEnd < 0 ? lineText.Length : quoteEnd;
        if (col0 < quoteStart || col0 > end) return empty;

        string importerDir = Path.GetDirectoryName(importerPath) ?? string.Empty;
        if (!Directory.Exists(importerDir)) return empty;

        var results = new List<string>();
        try
        {
            string selfFull = Path.GetFullPath(importerPath);
            foreach (var f in Directory.EnumerateFiles(importerDir, "*.uitkx", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(f), selfFull, StringComparison.OrdinalIgnoreCase))
                    continue;
                string rel = Path.GetRelativePath(importerDir, f).Replace('\\', '/');
                if (rel.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(0, rel.Length - ".uitkx".Length);
                if (!rel.StartsWith("../", StringComparison.Ordinal))
                    rel = "./" + rel;
                results.Add(rel);
            }
        }
        catch { /* permission / IO — best effort */ }

        results.Sort(StringComparer.Ordinal);
        return results;
    }
}
