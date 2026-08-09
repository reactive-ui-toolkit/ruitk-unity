using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Ruitk.Language;
using Ruitk.Language.Parser;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace UitkxLanguageServer;

/// <summary>
/// Shared helpers used by <see cref="RenameHandler"/>,
/// <see cref="DefinitionHandler"/>, and <see cref="ReferencesHandler"/>.
/// </summary>
internal static class LspHelpers
{
    // ── Offset / Position conversion ──────────────────────────────────────

    public static int ToOffset(string text, Position position)
    {
        int line = (int)position.Line;
        int column = (int)position.Character;
        int offset = 0;
        for (int i = 0; i < line && offset < text.Length; i++)
        {
            var nl = text.IndexOf('\n', offset);
            offset = nl < 0 ? text.Length : nl + 1;
        }
        return Math.Min(offset + column, text.Length);
    }

    public static Position OffsetToPosition(string text, int offset)
    {
        int line = 0, col = 0;
        for (int i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n') { line++; col = 0; }
            else if (text[i] != '\r') { col++; }
        }
        return new Position(line, col);
    }

    public static LspRange OffsetRangeToLspRange(string text, int start, int end)
    {
        return new LspRange(OffsetToPosition(text, start), OffsetToPosition(text, end));
    }

    // ── Plain-declaration head matching ───────────────────────────────────

    /// <summary>
    /// Regex fragment for the TYPE position of a plain declaration head (ES-modules
    /// grammar): a dotted/generic/array/nullable identifier type or a tuple type.
    /// C# statement keywords are excluded, so statement-shaped lines (<c>if (…</c>,
    /// <c>return foo(…</c>, <c>var c = …</c>, a bare <c>name(…)</c> call) can never
    /// classify as declarations — a declaration head requires a real type token
    /// BEFORE the name.
    /// </summary>
    internal const string DeclTypePattern =
        @"(?:(?!(?:if|while|for|foreach|switch|using|return|lock|var|new|else|do|case|throw|await|yield)\b)"
        + @"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*(?:<(?:[^<>]|<[^<>]*>)*>)?(?:\[[^\]]*\])*\??"
        + @"|\((?:[^()]|\([^()]*\))*\))";

    // ── Import-specifier resolution ───────────────────────────────────────

    /// <summary>
    /// Resolve an <c>import</c> specifier to the absolute target <c>.uitkx</c> path, using the same
    /// rule as the build (<see cref="ImportResolver.MapSpecifierToPath"/>). Returns <c>null</c> for
    /// engine-native/unresolvable specifiers.
    /// </summary>
    internal static string? ResolveImportTarget(string localPath, string specifier)
    {
        string importerDir = (Path.GetDirectoryName(localPath) ?? string.Empty).Replace('\\', '/');
        string? projectRoot = AssetPathUtil.GetProjectRoot(localPath);
        string rootDir = projectRoot != null ? projectRoot + "/" + UitkxConfig.LoadRoot(importerDir) : importerDir;
        // Absolute dirs in → absolute candidate path out (MapSpecifierToPath is pure path arithmetic).
        return ImportResolver.MapSpecifierToPath(importerDir, specifier, rootDir, out _);
    }

    // ── Word / identifier helpers ─────────────────────────────────────────

    public static (string Word, int Start, int End) GetWordAtOffset(string text, int offset)
    {
        if (offset < 0 || offset >= text.Length)
            return ("", offset, offset);

        if (!IsIdentChar(text[offset]) && offset > 0 && IsIdentChar(text[offset - 1]))
            offset--;

        if (!IsIdentChar(text[offset]))
            return ("", offset, offset);

        int start = offset;
        while (start > 0 && IsIdentChar(text[start - 1]))
            start--;

        int end = offset;
        while (end < text.Length && IsIdentChar(text[end]))
            end++;

        return (text.Substring(start, end - start), start, end);
    }

    public static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static bool IsOnTagName(string text, int offset, string word)
    {
        var (_, wordStart, _) = GetWordAtOffset(text, offset);
        int i = wordStart - 1;
        while (i >= 0 && text[i] == ' ')
            i--;
        if (i >= 0 && text[i] == '<')
            return true;
        if (i >= 1 && text[i] == '/' && text[i - 1] == '<')
            return true;
        return false;
    }

    public static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (!char.IsLetter(name[0]) && name[0] != '_')
            return false;
        for (int i = 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
                return false;
        }
        return true;
    }

    public static bool IsInsideTildeFolder(string filePath)
    {
        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var seg in segments)
        {
            if (seg.Length > 1 && seg[seg.Length - 1] == '~')
                return true;
        }
        return false;
    }

    public static int CountOccurrences(string text, string word)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]) && text[idx - 1] != '_';
            int end = idx + word.Length;
            bool rightOk = end >= text.Length || !char.IsLetterOrDigit(text[end]) && text[end] != '_';
            if (leftOk && rightOk) count++;
            idx = end;
        }
        return count;
    }

    // ── URI / path helpers ────────────────────────────────────────────────

    public static string? UriToPath(DocumentUri uri)
    {
        try { return new Uri(uri.ToString()).LocalPath; }
        catch { return null; }
    }

    public static bool TryGetText(DocumentStore store, DocumentUri uri, string localPath, out string text)
    {
        if (store.TryGet(uri, out text!) && text != null)
            return true;

        if (File.Exists(localPath))
        {
            text = File.ReadAllText(localPath);
            store.Set(uri, text);
            return true;
        }

        text = "";
        return false;
    }

    // ── Parse helper ──────────────────────────────────────────────────────

    public static ParseResult ParseText(string text, string filePath)
    {
        var parseDiags = new List<Ruitk.Language.ParseDiagnostic>();
        var directives = DirectiveParser.Parse(text, filePath, parseDiags);
        var nodes = UitkxParser.Parse(text, filePath, directives, parseDiags);
        return new ParseResult(directives, nodes, ImmutableArray.CreateRange(parseDiags));
    }

    // ── Symbol resolution ─────────────────────────────────────────────────

    public static ISymbol? ResolveSymbol(Document doc, int virtualOffset, CancellationToken ct)
    {
        try
        {
#pragma warning disable VSTHRD002
            var syntaxRoot = doc.GetSyntaxRootAsync(ct).GetAwaiter().GetResult();
            var semanticModel = doc.GetSemanticModelAsync(ct).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            if (syntaxRoot == null || semanticModel == null)
                return null;

            int pos = virtualOffset > 0 ? virtualOffset - 1 : 0;
            var token = syntaxRoot.FindToken(pos);
            if (token.Parent == null)
                return null;

            var declared = semanticModel.GetDeclaredSymbol(token.Parent, ct);
            if (declared != null)
                return declared;

            var symbolInfo = semanticModel.GetSymbolInfo(token.Parent, ct);
            return symbolInfo.Symbol
                ?? (symbolInfo.CandidateSymbols.Length > 0 ? symbolInfo.CandidateSymbols[0] : null);
        }
        catch (Exception ex)
        {
            ServerLog.Log($"[LspHelpers] ResolveSymbol error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns true when the symbol is user-defined (not metadata, not scaffolded).
    /// </summary>
    public static bool IsUserSymbol(ISymbol symbol)
    {
        if (symbol.Locations.Length > 0 && symbol.Locations[0].IsInMetadata)
            return false;
        if (symbol.IsImplicitlyDeclared)
            return false;
        if (symbol.Name.StartsWith("__", StringComparison.Ordinal))
            return false;
        return true;
    }
}
