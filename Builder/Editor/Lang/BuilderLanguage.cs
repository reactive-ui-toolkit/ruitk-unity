#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Ruitk.Language;
using Ruitk.Language.Formatter;
using Ruitk.Language.IntelliSense;
using Ruitk.Language.Parser;
using Ruitk.Language.SemanticTokens;

namespace Ruitk.Builder
{
    /// <summary>
    /// The builder's typed facade over <c>Ruitk.Language.Editor.dll</c> — the
    /// hot-path language services that must not pay an LSP round-trip: parse,
    /// print (VE-14), local T1+T2 diagnostics, semantic tokens, and cursor
    /// classification. Roslyn-tier intelligence (C# completion, T3 diagnostics)
    /// stays on the LSP client.
    ///
    /// Contracts enforced here so callers cannot get them wrong:
    /// - buffers are LF-normalized (the printer rejects CR loudly);
    /// - formatter options load via ConfigLoader per file directory, never
    ///   FormatterOptions.Default (uitkx.config.json fidelity — plan §4.2);
    /// - T1 comes from ParseResult.Diagnostics and is merged with T2 (the
    ///   analyzer never re-emits parse diagnostics).
    /// </summary>
    internal static class BuilderLanguage
    {
        public static ParseResult Parse(string bufferLf, string filePath)
        {
            if (bufferLf == null)
                throw new ArgumentNullException(nameof(bufferLf));
            if (bufferLf.IndexOf('\r') >= 0)
                throw new ArgumentException("builder buffers are LF-normalized", nameof(bufferLf));

            var diags = new List<ParseDiagnostic>();
            var directives = DirectiveParser.Parse(bufferLf, filePath, diags);
            var nodes = UitkxParser.Parse(bufferLf, filePath, directives, diags);
            return new ParseResult(directives, nodes, ImmutableArray.CreateRange(diags));
        }

        /// <summary>
        /// Renders an already-parsed buffer to canonical text (the VE-14 printer).
        /// Every non-<see cref="FormatOutcome.Formatted"/> outcome on a dirty
        /// transaction is a hard error for the caller — the identity fallbacks are
        /// data-loss guards, not successes.
        /// </summary>
        public static string Print(
            ParseResult parseResult,
            string bufferLf,
            string filePath,
            out FormatOutcome outcome
        )
        {
            var formatter = new AstFormatter(LoadOptionsFor(filePath));
            return formatter.Format(parseResult, bufferLf, filePath, out outcome);
        }

        /// <summary>Text-entry formatting (open flows, UXML import output).</summary>
        public static string FormatText(string bufferLf, string filePath, out FormatOutcome outcome)
        {
            var formatter = new AstFormatter(LoadOptionsFor(filePath));
            return formatter.Format(bufferLf, filePath, out outcome);
        }

        private static FormatterOptions LoadOptionsFor(string filePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    return ConfigLoader.LoadFormatterOptions(dir, null) ?? FormatterOptions.Default;
            }
            catch { }
            return FormatterOptions.Default;
        }

        /// <summary>
        /// T1 (parse) + T2 (analyzer) diagnostics for one buffer. T3 (Roslyn)
        /// arrives asynchronously from the LSP and is overlaid by the caller.
        /// </summary>
        public static IReadOnlyList<ParseDiagnostic> Diagnose(
            ParseResult parseResult,
            string filePath,
            HashSet<string> knownElements = null
        )
        {
            var merged = new List<ParseDiagnostic>(parseResult.Diagnostics);
            try
            {
                var analyzer = new Ruitk.Language.Diagnostics.DiagnosticsAnalyzer();
                merged.AddRange(analyzer.Analyze(parseResult, filePath, knownElements));
            }
            catch (Exception)
            {
                // Analyzer crash must never take squiggles down with it; T1 alone
                // still renders.
            }
            return merged;
        }

        public static SemanticTokenData[] Tokens(
            ParseResult parseResult,
            string bufferLf,
            HashSet<string> knownElements,
            string filePath
        )
        {
            var provider = new SemanticTokensProvider();
            return provider.GetTokens(parseResult, bufferLf, knownElements, filePath);
        }

        /// <summary>Cursor classification — 1-based line, 0-based column (upstream convention).</summary>
        public static CursorContext CursorAt(ParseResult parseResult, string bufferLf, int line1, int col0) =>
            AstCursorContext.Find(parseResult, bufferLf, line1, col0);
    }
}
#endif
