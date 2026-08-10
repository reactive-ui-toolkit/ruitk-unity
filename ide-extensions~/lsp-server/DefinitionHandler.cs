using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Ruitk.Language;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Ruitk.Language.IntelliSense;
using Ruitk.Language.Parser;
using Ruitk.Language.Roslyn;
using UitkxLanguageServer.Roslyn;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace UitkxLanguageServer;

/// <summary>
/// Handles <c>textDocument/definition</c> for <c>.uitkx</c> files.
///
/// Supported navigation targets:
/// <list type="bullet">
///   <item>Element tag name → jump to the corresponding <c>*Props.cs</c> class declaration.</item>
///   <item>Attribute name → jump to the property declaration inside <c>*Props.cs</c>.</item>
/// </list>
/// </summary>
public sealed class DefinitionHandler : IDefinitionHandler
{
    private readonly DocumentStore  _store;
    private readonly WorkspaceIndex _index;
    private readonly RoslynHost     _roslynHost;

    public DefinitionHandler(DocumentStore store, WorkspaceIndex index, RoslynHost roslynHost)
    {
        _store = store;
        _index = index;
        _roslynHost = roslynHost;
    }

    public DefinitionRegistrationOptions GetRegistrationOptions(
        DefinitionCapability capability,
        ClientCapabilities clientCapabilities) =>
        new DefinitionRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.uitkx" }),
        };

    public Task<LocationOrLocationLinks?> Handle(
        DefinitionParams request,
        CancellationToken cancellationToken)
    {
        ServerLog.Log(
            $"definition request: {request.TextDocument.Uri}  "
            + $"pos={request.Position.Line}:{request.Position.Character}");

        // Extract local path once; reused for disk-read fallback and AST parse.
        string localPath;
        try { localPath = new Uri(request.TextDocument.Uri.ToString()).LocalPath; }
        catch { localPath = string.Empty; }

        if (!_store.TryGet(request.TextDocument.Uri, out var text) || text is null)
        {
            if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
            {
                text = File.ReadAllText(localPath);
                _store.Set(request.TextDocument.Uri, text);
            }
            else
            {
                return Task.FromResult<LocationOrLocationLinks?>(null);
            }
        }

        var result = HandleCore(request, text, localPath, cancellationToken);
        return Task.FromResult(result);
    }

    private LocationOrLocationLinks? HandleCore(
        DefinitionParams request, string text, string localPath, CancellationToken ct)
    {

        // Parse the document with the language-lib AST pipeline so go-to-definition
        // targets are derived from the real syntax tree instead of text scanning.

        var parseDiags  = new List<Ruitk.Language.ParseDiagnostic>();
        var directives  = DirectiveParser.Parse(text, localPath, parseDiags);
        var nodes       = UitkxParser.Parse(text, localPath, directives, parseDiags);
        var parseResult = new ParseResult(
            directives,
            nodes,
            ImmutableArray.CreateRange(parseDiags));

        int line1 = (int)request.Position.Line + 1;
        int col0  = (int)request.Position.Character;
        var ctx = AstCursorContext.Find(parseResult, text, line1, col0);

        string word       = ctx.Word;
        string? tagContext = ctx.Kind == CursorKind.AttributeName ? ctx.TagName : null;

        // ── Case 0: cursor is on an import line (specifier or an imported name) ──
        // import/export grammar (leg 3): clicking an imported name jumps to its
        // declaration in the target file; clicking anywhere else on the line (e.g. the
        // specifier string) jumps to the target file.
        if (TryResolveImportNavigation(localPath, directives.Imports, line1, col0,
                out string? importFile, out int importLine))
        {
            ServerLog.Log($"definition: import on line {line1} → {importFile}:{importLine}");
            return importFile is not null ? MakeLocation(importFile, importLine) : null;
        }

        if (string.IsNullOrEmpty(word))
            return null;

        // ── Case 1: cursor is on an element tag name ─────────────────────────
        var elementInfo = _index.TryGetElementInfo(word);
        if (elementInfo is not null && File.Exists(elementInfo.FilePath))
        {
            // When the definition is on the same line in the same file (i.e. the
            // user clicked the component's own declaration), return the exact
            // cursor position.  VS Code detects "definition == current location"
            // and automatically shows Find All References instead — matching the
            // JSX/TSX behaviour.
            bool isSameLocation =
                string.Equals(
                    Path.GetFullPath(elementInfo.FilePath),
                    Path.GetFullPath(localPath),
                    StringComparison.OrdinalIgnoreCase)
                && elementInfo.FileLine == line1;

            if (isSameLocation)
            {
                ServerLog.Log(
                    $"definition: '{word}' is own declaration – returning cursor pos for references fallback");
                return MakeLocation(localPath, line1, col0);
            }

            ServerLog.Log($"definition: '{word}' → {elementInfo.FilePath}:{elementInfo.FileLine}");
            return MakeLocation(elementInfo.FilePath, elementInfo.FileLine);
        }

        // ── Case 2: cursor is on an attribute name inside a known element tag ─
        if (tagContext is not null)
        {
            var parentInfo = _index.TryGetElementInfo(tagContext)
                          ?? _index.TryGetElementInfo(
                                 char.ToUpperInvariant(tagContext[0]) + tagContext[1..]);

            if (parentInfo is not null && File.Exists(parentInfo.FilePath))
            {
                var prop = parentInfo.Props.Find(p =>
                    p.Name.Equals(word, StringComparison.OrdinalIgnoreCase));

                if (prop is not null)
                {
                    ServerLog.Log(
                        $"definition: prop '{word}' on '{tagContext}' → "
                        + $"{parentInfo.FilePath}:{prop.Line}");
                    return MakeLocation(parentInfo.FilePath, prop.Line);
                }

                // Jump to the class declaration even if the exact prop wasn't parsed.
                ServerLog.Log(
                    $"definition: attr '{word}' on '{tagContext}' → class at "
                    + $"{parentInfo.FilePath}:{parentInfo.FileLine}");
                return MakeLocation(parentInfo.FilePath, parentInfo.FileLine);
            }
        }

        // ―― Case 3: variable declared in a @code block ――――――――――――――――――――――――――
        // Handles {identifier} — scan the document for a @code-block variable
        // declaration matching the word (e.g. "var component = ..." or "Type word =").
        var (codeVarLine, codeVarCol) = FindCodeVarDeclaration(text, word);
        if (codeVarLine > 0)
        {
            ServerLog.Log($"definition: '{word}' \u2192 @code var declaration at line {codeVarLine} col {codeVarCol}");
            return MakeLocation(localPath, codeVarLine, codeVarCol);
        }

        if (directives.IsFunctionStyle)
        {
            var setupStart = directives.FunctionSetupStartLine > 0
                ? directives.FunctionSetupStartLine
                : 1;
            var setupEnd = Math.Max(setupStart, directives.MarkupStartLine - 1);

            var (setupVarLine, setupVarCol) = FindVarDeclarationInLineRange(
                text,
                word,
                setupStart,
                setupEnd
            );
            if (setupVarLine > 0)
            {
                ServerLog.Log($"definition: '{word}' \u2192 function-style setup var at line {setupVarLine} col {setupVarCol}");
                return MakeLocation(localPath, setupVarLine, setupVarCol);
            }
        }

        // ── Case 4b: member declaration name ────────────────────────────────
        // When the cursor is on a member (value/util/hook) declaration name,
        // return the same position.  VS Code detects "definition == current
        // location" and shows Find All References — matching JSX/TSX behaviour.
        if (!parseResult.Directives.MemberDeclarations.IsDefaultOrEmpty)
        {
            foreach (var member in parseResult.Directives.MemberDeclarations)
            {
                if (word == member.Name)
                {
                    ServerLog.Log(
                        $"definition: '{word}' is own member declaration – returning cursor pos for references fallback");
                    return MakeLocation(localPath, line1, col0);
                }
            }
        }

        // ── Case 5: Roslyn symbol resolution ─────────────────────────────────
        // Covers symbols defined in companion .cs files and declarations that
        // the text-regex heuristic above cannot find.
        var roslynResult = TryResolveViaRoslyn(localPath, text, parseResult, request.Position, word, ct);
        if (roslynResult != null)
            return roslynResult;

        ServerLog.Log($"definition: no target found for '{word}'");
        return null;
    }

    /// <summary>
    /// Scans the document source for a line that declares a variable named
    /// <paramref name="varName"/> inside a <c>@code { }</c> block.
    /// Matches patterns like <c>var word = ...</c>, <c>var (word, ...) = ...</c>,
    /// and <c>TypeName word = ...</c>.
    /// Returns (1-based line, 0-based character), or (0, 0) if not found.
    /// </summary>
    private static (int Line, int Col) FindCodeVarDeclaration(string text, string varName)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(varName))
            return (0, 0);

        bool insideCode = false;
        int lineNum = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNum++;
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (!insideCode)
            {
                if (trimmed.StartsWith("@code", StringComparison.Ordinal)) insideCode = true;
                continue;
            }

            if (trimmed == "}") { insideCode = false; continue; }

            int col = -1;
            var escaped = System.Text.RegularExpressions.Regex.Escape(varName);

            // var word = …  or  var word;
            var m = System.Text.RegularExpressions.Regex.Match(
                line, $@"\bvar\s+({escaped})\b");
            if (m.Success) col = m.Groups[1].Index;

            // var (word, …) = …  tuple deconstruction
            if (col < 0 && trimmed.StartsWith("var (", StringComparison.Ordinal))
            {
                // Only search inside the var (...) portion, not the RHS.
                int parenStart = line.IndexOf("var (", StringComparison.Ordinal) + 4;
                int parenEnd = line.IndexOf(')', parenStart);
                if (parenEnd > parenStart)
                {
                    var tupleInner = line.Substring(parenStart, parenEnd - parenStart);
                    m = System.Text.RegularExpressions.Regex.Match(tupleInner, $@"\b({escaped})\b");
                    if (m.Success) col = parenStart + m.Groups[1].Index;
                }
            }

            // TypeName word = …
            if (col < 0)
            {
                m = System.Text.RegularExpressions.Regex.Match(
                    line, $@"\b\w+\s+({escaped})\s*[=;]");
                if (m.Success) col = m.Groups[1].Index;
            }

            if (col >= 0) return (lineNum, col);
        }
        return (0, 0);
    }

    private static (int Line, int Col) FindVarDeclarationInLineRange(
        string text,
        string varName,
        int startLine1,
        int endLine1
    )
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(varName))
            return (0, 0);

        var lines = text.Split('\n');
        int start = Math.Max(1, startLine1);
        int end = Math.Min(lines.Length, Math.Max(start, endLine1));

        for (int lineNum = start; lineNum <= end; lineNum++)
        {
            var line = lines[lineNum - 1].TrimEnd('\r');
            int col = -1;
            var escaped = System.Text.RegularExpressions.Regex.Escape(varName);

            var m = System.Text.RegularExpressions.Regex.Match(line, $@"\bvar\s+({escaped})\b");
            if (m.Success) col = m.Groups[1].Index;

            if (col < 0 && line.Contains("var (", StringComparison.Ordinal))
            {
                // Only search inside the var (...) portion, not the RHS of the assignment.
                int parenStart = line.IndexOf("var (", StringComparison.Ordinal) + 4;
                int parenEnd = line.IndexOf(')', parenStart);
                if (parenEnd > parenStart)
                {
                    var tupleInner = line.Substring(parenStart, parenEnd - parenStart);
                    m = System.Text.RegularExpressions.Regex.Match(tupleInner, $@"\b({escaped})\b");
                    if (m.Success) col = parenStart + m.Groups[1].Index;
                }
            }

            if (col < 0)
            {
                m = System.Text.RegularExpressions.Regex.Match(
                    line,
                    $@"\b\w+\s+({escaped})\s*[=;]"
                );
                if (m.Success) col = m.Groups[1].Index;
            }

            if (col >= 0)
                return (lineNum, col);
        }

        return (0, 0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Go-to-definition for the import/export grammar (pure — no LSP/RoslynHost deps, so it is
    /// directly unit-testable). When <paramref name="line1"/> is on one of <paramref name="imports"/>:
    /// a cursor on an imported name resolves to that declaration's line in the target file; anywhere
    /// else on the line (e.g. the specifier) resolves to the target file's top. Returns <c>true</c>
    /// when the cursor is on an import line (even if unresolvable, in which case
    /// <paramref name="targetFile"/> is <c>null</c> so the caller stops); <c>false</c> to fall through
    /// to the other definition cases.
    /// </summary>
    public static bool TryResolveImportNavigation(
        string localPath,
        ImmutableArray<ImportDeclaration> imports,
        int line1,
        int col0,
        out string? targetFile,
        out int targetLine)
    {
        targetFile = null;
        targetLine = 1;
        if (imports.IsDefaultOrEmpty)
            return false;

        foreach (var imp in imports)
        {
            if (imp.Line != line1)
                continue;

            string? resolved = LspHelpers.ResolveImportTarget(localPath, imp.Specifier);
            if (resolved is null || !File.Exists(resolved))
                return true; // on an import line but unresolvable — caller returns null

            for (int k = 0; k < imp.Names.Length; k++)
            {
                int nameCol = k < imp.NameColumns.Length ? imp.NameColumns[k] : -1;
                if (nameCol >= 0 && col0 >= nameCol && col0 <= nameCol + imp.Names[k].Length)
                {
                    int declLine = FindDeclarationLine(resolved, imp.Names[k]);
                    targetFile = resolved;
                    targetLine = declLine > 0 ? declLine : 1;
                    return true;
                }
            }

            targetFile = resolved;
            targetLine = 1;
            return true;
        }
        return false;
    }

    /// <summary>1-based line of the declaration of <paramref name="name"/> in a file, or 0. BOTH
    /// grammars (ES-modules campaign, M5): the legacy wrapper head
    /// (<c>[export] component|hook|module Name</c>) and the plain-declaration head
    /// (<c>[export] &lt;Type&gt; Name(</c> / <c>[export] [&lt;Type&gt;] Name =</c>).</summary>
    private static int FindDeclarationLine(string filePath, string name)
    {
        try
        {
            // A plain DECLARATION head requires a type token before the name (audit B1):
            // the previous optional-prefix shape let a statement-shaped `name(…)` call line
            // above the real declaration win the top-down scan. Untyped value declarations
            // (`export theme = …`) are the only type-less form and always carry `export`.
            string n = Regex.Escape(name);
            var re = new Regex(
                $@"^(?:(?:export\s+)?(?:component|hook|module)\s+{n}\b"
                + $@"|(?:export\s+)?{LspHelpers.DeclTypePattern}\s+{n}\s*(?:<[\w,\s]+>)?\s*[=\(]"
                + $@"|export\s+{n}\s*=(?!=))");
            string[] lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
                if (re.IsMatch(lines[i]))
                    return i + 1;
        }
        catch { /* unreadable target — fall back to the file top */ }
        return 0;
    }

    private static LocationOrLocationLinks MakeLocation(string filePath, int oneBasedLine, int zeroBasedCol = 0)
    {
        var targetLine = Math.Max(0, oneBasedLine - 1);
        var targetCol  = Math.Max(0, zeroBasedCol);
        var location   = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Location
        {
            Uri   = DocumentUri.FromFileSystemPath(filePath),
            Range = new LspRange(
                new Position(targetLine, targetCol),
                new Position(targetLine, targetCol)),
        };
        return new LocationOrLocationLinks(new LocationOrLocationLink(location));
    }

    private static int ToOffset(string text, Position position)
        => LspHelpers.ToOffset(text, position);

    // ── Roslyn-based symbol resolution ──────────────────────────────────────

    /// <summary>
    /// Maps the cursor position through the SourceMap into the Roslyn virtual
    /// document, resolves the symbol, and returns the definition location.
    /// Handles both same-file definitions (mapped back via SourceMap) and
    /// companion .cs file definitions.
    /// </summary>
    private LocationOrLocationLinks? TryResolveViaRoslyn(
        string localPath,
        string text,
        ParseResult parseResult,
        Position position,
        string word,
        CancellationToken ct)
    {
        var vdoc = _roslynHost.GetVirtualDocument(localPath);
        if (vdoc == null)
            return null;

        var offset = ToOffset(text, position);
        var virtualResult = vdoc.Map.ToVirtualOffset(offset);
        if (!virtualResult.HasValue)
            return null;

        var roslynDoc = _roslynHost.GetRoslynDocument(localPath);
        if (roslynDoc == null)
            return null;

        ISymbol? symbol;
        try
        {
#pragma warning disable VSTHRD002
            var syntaxRoot = roslynDoc.GetSyntaxRootAsync(ct).GetAwaiter().GetResult();
            var semanticModel = roslynDoc.GetSemanticModelAsync(ct).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            if (syntaxRoot == null || semanticModel == null)
                return null;

            int pos = virtualResult.Value.VirtualOffset;
            var token = syntaxRoot.FindToken(pos > 0 ? pos - 1 : 0);
            if (token.Parent == null)
                return null;

            // Try declared symbol first (cursor on a declaration)
            symbol = semanticModel.GetDeclaredSymbol(token.Parent, ct);
            if (symbol == null)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(token.Parent, ct);
                symbol = symbolInfo.Symbol
                    ?? (symbolInfo.CandidateSymbols.Length > 0 ? symbolInfo.CandidateSymbols[0] : null);
            }
        }
        catch (Exception ex)
        {
            ServerLog.Log($"definition: Roslyn resolve error: {ex.Message}");
            return null;
        }

        if (symbol == null)
            return null;

        // Find the definition location from the symbol's declaring syntax references.
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var defDoc = roslynDoc.Project.Solution.GetDocument(syntaxRef.SyntaxTree);
            if (defDoc == null)
                continue;

            var span = syntaxRef.Span;

            if (defDoc.Id == roslynDoc.Id)
            {
                // Definition is in the main virtual document → map back to .uitkx
                var uitkxResult = vdoc.Map.ToUitkxOffset(span.Start);
                if (uitkxResult.HasValue)
                {
                    // Compute the line of the mapped-back definition.
                    var defPos = LspHelpers.OffsetToPosition(text, uitkxResult.Value.UitkxOffset);
                    // When the definition is on the SAME line as the cursor,
                    // the user is already on the declaration.  Return the
                    // exact cursor position so VS Code detects
                    // "definition == current location" and shows
                    // Find All References — matching JSX/TSX behaviour.
                    if (defPos.Line == position.Line)
                    {
                        ServerLog.Log(
                            $"definition: Roslyn '{word}' → same line – returning cursor pos for references fallback");
                        return MakeLocation(localPath, (int)position.Line + 1, (int)position.Character);
                    }

                    ServerLog.Log($"definition: Roslyn '{word}' → {localPath} (via SourceMap)");
                    return MakeLocationFromOffset(localPath, text, uitkxResult.Value.UitkxOffset);
                }
            }
            else
            {
                // ── Check if definition is in a peer virtual document ────
                var peerInfo = _roslynHost.TryGetPeerVirtualDocument(localPath, defDoc.Id);
                if (peerInfo.HasValue)
                {
                    var (peerPath, peerVDoc) = peerInfo.Value;
                    var peerResult = peerVDoc.Map.ToUitkxOffset(span.Start);
                    if (peerResult.HasValue)
                    {
                        // U-42: prefer the store (an open, possibly-unsaved buffer) over disk —
                        // matching the primary-document read above — so navigating into a peer
                        // file with unsaved edits doesn't compute the jump against stale text.
                        string peerSource = _store.TryGetByPath(peerPath, out var live)
                            ? live
                            : File.Exists(peerPath) ? File.ReadAllText(peerPath) : "";
                        ServerLog.Log(
                            $"definition: Roslyn '{word}' → {peerPath} (via peer SourceMap)");
                        return MakeLocationFromOffset(peerPath, peerSource, peerResult.Value.UitkxOffset);
                    }

                    // The definition position is in the scaffold (e.g. a hook/module
                    // method signature).  Fall back to searching the peer .uitkx
                    // source for the symbol name (hook/module declaration keyword line).
                    if (_store.TryGetByPath(peerPath, out var peerSourceLive) || File.Exists(peerPath))
                    {
                        var peerSource = peerSourceLive ?? File.ReadAllText(peerPath);
                        var (declLine, declCol) = FindDeclarationInUitkx(peerSource, word);
                        if (declLine > 0)
                        {
                            ServerLog.Log(
                                $"definition: Roslyn '{word}' → {peerPath}:{declLine} (via peer text search)");
                            return MakeLocation(peerPath, declLine, declCol);
                        }
                    }
                }

                // ── Definition is in a companion .cs document ────────────
                var dir = Path.GetDirectoryName(localPath);
                if (dir == null) continue;

                var companionPath = Path.Combine(dir, defDoc.Name);
                if (!File.Exists(companionPath)) continue;

#pragma warning disable VSTHRD002
                var defText = defDoc.GetTextAsync(ct).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                var linePos = defText.Lines.GetLinePosition(span.Start);

                ServerLog.Log(
                    $"definition: Roslyn '{word}' → {companionPath}:{linePos.Line + 1}:{linePos.Character}");
                return MakeLocation(companionPath, linePos.Line + 1, linePos.Character);
            }
        }

        ServerLog.Log($"definition: Roslyn found '{symbol.Kind}:{symbol.Name}' but no source location");
        return null;
    }

    private static LocationOrLocationLinks MakeLocationFromOffset(
        string filePath, string text, int offset)
    {
        int line = 0, col = 0;
        for (int i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n') { line++; col = 0; }
            else if (text[i] != '\r') { col++; }
        }
        return MakeLocation(filePath, line + 1, col);
    }

    /// <summary>
    /// Searches a .uitkx source for a <c>hook</c> or <c>module</c> declaration
    /// whose name matches <paramref name="symbolName"/>.  Returns the 1-based
    /// line and 0-based column of the name, or (0, 0) if not found.
    /// </summary>
    internal static (int Line, int Col) FindDeclarationInUitkx(string source, string symbolName)
    {
        int lineNum = 0;
        foreach (var rawLine in source.Split('\n'))
        {
            lineNum++;
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();

            // Optional `export ` prefix (import/export grammar, 0.7.0+) — every
            // consumable hook/module carries it now; without stripping it the
            // StartsWith below never matches and go-to-definition on any exported
            // hook/module silently dead-ends.
            if (trimmed.StartsWith("export ", StringComparison.Ordinal))
                trimmed = trimmed.Substring("export ".Length).TrimStart();

            // Match:  hook symbolName(...)  or  module symbolName(...)
            foreach (var keyword in new[] { "hook ", "module " })
            {
                if (!trimmed.StartsWith(keyword, StringComparison.Ordinal))
                    continue;

                int nameStart = trimmed.IndexOf(symbolName, keyword.Length, StringComparison.Ordinal);
                if (nameStart < 0)
                    continue;

                // Whole-word check
                int nameEnd = nameStart + symbolName.Length;
                bool rightOk = nameEnd >= trimmed.Length
                    || !char.IsLetterOrDigit(trimmed[nameEnd]) && trimmed[nameEnd] != '_';
                if (!rightOk)
                    continue;

                int col = line.IndexOf(symbolName, StringComparison.Ordinal);
                return (lineNum, col >= 0 ? col : 0);
            }

            // Plain-declaration head (ES-modules campaign, M5): `[export] <Type> Name(` /
            // `[export] [<Type>] Name =`. A declaration head requires a type token before
            // the name (audit B1 — statement-shaped `name(…)` lines must never match), and
            // is checked on the RAW line: top-level declarations start at column 0, so
            // indented body statements never match.
            if (s_plainDeclHeadRe.Match(line) is { Success: true } pm
                && string.Equals(pm.Groups["name"].Value, symbolName, StringComparison.Ordinal))
            {
                int col = line.IndexOf(symbolName, StringComparison.Ordinal);
                return (lineNum, col >= 0 ? col : 0);
            }
        }
        return (0, 0);
    }

    private static readonly Regex s_plainDeclHeadRe = new(
        @"^(?:export\s+)?" + LspHelpers.DeclTypePattern + @"\s+(?<name>[A-Za-z_]\w*)\s*(?:<[\w,\s]+>)?\s*[=\(]"
        + @"|^export\s+(?<name>[A-Za-z_]\w*)\s*=(?!=)",
        RegexOptions.CultureInvariant);
}
