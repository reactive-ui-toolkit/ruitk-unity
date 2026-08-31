using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;

namespace Ruitk.Language.Diagnostics
{
    /// <summary>
    /// Performs Tier-2 (structural) diagnostics on a parsed UITKX file.
    ///
    /// Tier 2 checks:
    /// <list type="bullet">
    ///   <item>UITKX0013 - Hook called inside <c>@if</c> / <c>@else</c> branch</item>
    ///   <item>UITKX0014 - Hook called inside <c>@foreach</c> / <c>@for</c> / <c>@while</c> loop</item>
    ///   <item>UITKX0015 - Hook called inside <c>@switch</c> case</item>
    ///   <item>UITKX0016 - Hook called inside event-handler attribute</item>
    ///   <item>UITKX0103 - <c>component</c> name does not match filename</item>
    ///   <item>UITKX0104 - Duplicate literal <c>key="-"</c> among siblings</item>
    ///   <item>UITKX0105 - Unknown PascalCase element (when index available)</item>
    ///   <item>UITKX0106 - Element inside <c>@foreach</c> body has no <c>key</c> (warning)</item>
    ///   <item>UITKX0107 - Unreachable code after top-level <c>return</c> in component body</item>
    ///   <item>UITKX0108 - Component has more than one root render node</item>
    ///   <item>UITKX0109 - Unknown attribute on a known element (when attribute map available)</item>
    ///   <item>UITKX0111 - Unused component parameter in function-style component</item>
    /// </list>
    ///
    /// Tier-1 (parser syntax) errors are already present in
    /// <see cref="ParseResult.Diagnostics"/> and should be passed through directly
    /// by the caller; this class never re-emits them.
    /// </summary>
    public sealed class DiagnosticsAnalyzer
    {
        /// <summary>
        /// Analyse the parse result and return all T2 structural diagnostics.
        /// </summary>
        /// <param name="parseResult">The complete parse result (directives + AST + T1 errors).</param>
        /// <param name="filePath">
        /// Absolute or relative path of the source file, used for the filename-mismatch check.
        /// May be empty or null - the check is simply skipped.
        /// </param>
        /// <param name="projectElements">
        /// Set of component names known in the project (suffix "Props" stripped).
        /// Pass <c>null</c> to skip the unknown-element check. Pass an empty set to
        /// report every unrecognised PascalCase element as a warning.
        /// </param>
        /// <param name="knownAttributes">
        /// Map of element name -> set of valid attribute names for that element.
        /// Pass <c>null</c> to skip the unknown-attribute check.
        /// </param>
        /// <param name="sourceText">
        /// Raw source text of the .uitkx file. When non-null, used for asset-path
        /// validation (<c>UITKX0120</c>).
        /// </param>
        public IReadOnlyList<ParseDiagnostic> Analyze(
            ParseResult parseResult,
            string? filePath,
            HashSet<string>? projectElements = null,
            IReadOnlyDictionary<string, ElementAttributeContract>? knownAttributes = null,
            string? sourceText = null
        )
        {
            var diags = new List<ParseDiagnostic>();
            var d = parseResult.Directives;

            // UITKX0103 (filename ≠ component name) is INTENTIONALLY not emitted. Under the
            // import/export model a file may declare several components in any order, so a
            // filename-match rule is meaningless — it is now a documentation convention
            // (see the "Conventions & best practices" docs), not a code-enforced warning.

            // -- T2: UITKX0108 - Multiple render roots ------------------------
            CheckSingleRenderRoot(parseResult.RootNodes, diags);

            // -- T2: AST walks -------------------------------------------------
            WalkNodeList(parseResult.RootNodes, insideForeach: false, HookContext.TopLevel, projectElements, knownAttributes, sourceText, diags);

            // -- T2: Function-style unreachable-after-return -------------------
            if (d.IsFunctionStyle)
            {
                // Site A - dim code between the render return's `;` and the
                // component closing `}`, but never the `}` itself.
                if (d.FunctionReturnEndLine > 0 && d.FunctionBodyEndLine > 0)
                {
                    int unreachStart = d.FunctionReturnEndLine + 1;
                    int unreachEnd = d.FunctionBodyEndLine - 1; // exclude `}`
                    if (unreachEnd >= unreachStart)
                    {
                        diags.Add(new ParseDiagnostic
                        {
                            Code = DiagnosticCodes.UnreachableAfterReturn,
                            Severity = ParseSeverity.Hint,
                            Message = "Unreachable code after 'return'.",
                            SourceLine = unreachStart,
                            SourceColumn = 0,
                            EndLine = unreachEnd,
                            EndColumn = 9999,
                        });
                    }
                }

                // Site B - early return in setup code (before the render return).
                // Dim from the line after the early return to the closing `}`.
                if (!string.IsNullOrEmpty(sourceText) && d.FunctionSetupStartOffset >= 0
                    && d.FunctionReturnEndLine > 0 && d.FunctionBodyEndLine > 0)
                {
                    int earlyReturnLine = FindEarlyReturnInSetup(
                        sourceText, d.FunctionSetupStartOffset, d.FunctionSetupStartLine,
                        d.FunctionReturnEndLine);
                    if (earlyReturnLine > 0)
                    {
                        int unreachStart = earlyReturnLine + 1;
                        int unreachEnd = d.FunctionBodyEndLine - 1;
                        if (unreachEnd >= unreachStart)
                        {
                            diags.Add(new ParseDiagnostic
                            {
                                Code = DiagnosticCodes.UnreachableAfterReturn,
                                Severity = ParseSeverity.Hint,
                                Message = "Unreachable code after 'return'.",
                                SourceLine = unreachStart,
                                SourceColumn = 0,
                                EndLine = unreachEnd,
                                EndColumn = 9999,
                            });
                        }
                    }
                }

            }

            // -- T2: UITKX0111 - Unused component parameter -------------------
            if (d.IsFunctionStyle && !d.FunctionParams.IsDefaultOrEmpty)
            {
                CheckUnusedParameters(d, parseResult.RootNodes, diags);
                CheckDuplicatePropNames(d, diags);
            }

            // -- T2: UITKX0120 - Asset path not found -------------------------
            if (!string.IsNullOrEmpty(sourceText) && !string.IsNullOrEmpty(filePath))
            {
                CheckAssetPaths(sourceText, filePath, diags);
            }

            // (UITKX0211 const-in-module retired with the legacy module grammar in
            // 0.16.0 — plain-dialect member bodies have no field-position const.)

            return diags;
        }

        // -----------------------------------------------------------------------
        //  RENDER-ROOT CHECK
        // -----------------------------------------------------------------------

        /// <summary>
        /// UITKX0108 - A component must have exactly one render root.
        ///
        /// "Render root" is any node that contributes to the rendered output:
        /// <see cref="ElementNode"/>, <see cref="IfNode"/>, <see cref="ForeachNode"/>,
        /// <see cref="ForNode"/>, <see cref="WhileNode"/>, <see cref="SwitchNode"/>,
        /// <see cref="ExpressionNode"/>, or a non-whitespace <see cref="TextNode"/>.
        ///
        /// Excluded from the count (they do not produce rendered output):
        /// <list type="bullet">
        ///   <item><see cref="CommentNode"/> - comment, not rendered.</item>
        ///   <item>Whitespace-only <see cref="TextNode"/>.</item>
        /// </list>
        ///
        /// An error is reported on every render-root node beyond the first,
        /// pointing authors to the exact offending node.
        /// </summary>
        private static void CheckSingleRenderRoot(
            ImmutableArray<AstNode> rootNodes,
            List<ParseDiagnostic> diags
        )
        {
            // Collect all nodes that actually contribute to the rendered output.
            var renderRoots = new List<AstNode>(rootNodes.Length);
            foreach (var node in rootNodes)
            {
                switch (node)
                {
                    // Non-rendering nodes - excluded from the count.
                    case CommentNode:
                        continue;
                    case TextNode tn when string.IsNullOrWhiteSpace(tn.Content):
                        continue;

                    // Everything else contributes a render tree node.
                    default:
                        renderRoots.Add(node);
                        break;
                }
            }

            // The first render root is the valid single root.  Every subsequent
            // one is an error.  Report each individually so the user sees a squiggle
            // on the exact offending node.
            for (int i = 1; i < renderRoots.Count; i++)
            {
                var extra = renderRoots[i];
                string label = DescribeRenderNode(extra);
                int endCol = extra is ElementNode el
                    ? extra.SourceColumn + 1 + el.TagName.Length
                    : extra.SourceColumn;

                diags.Add(
                    MakeDiag(
                        DiagnosticCodes.MultipleRenderRoots,
                        ParseSeverity.Error,
                        $"A component must have a single root element. '{label}' is an extra root - wrap all root nodes in a single container element.",
                        extra.SourceLine,
                        extra.SourceColumn,
                        endCol
                    )
                );
            }
        }

        /// <summary>
        /// Returns a concise human-readable label for a render-contributing node,
        /// used in diagnostic messages.
        /// </summary>
        private static string DescribeRenderNode(AstNode node) =>
            node switch
            {
                ElementNode el   => $"<{el.TagName}>",
                IfNode           => "@if",
                ForeachNode      => "@foreach",
                ForNode          => "@for",
                WhileNode        => "@while",
                SwitchNode       => "@switch",
                ExpressionNode en => $"{{{en.Expression}}}",
                TextNode tn      => $"text \"{ tn.Content.Trim() }\"",
                _                => node.GetType().Name,
            };

        /// <summary>
        /// Runs T2 element/attribute checks on an arbitrary set of AST nodes
        /// (e.g. markup embedded inside setup-code JSX blocks).
        /// </summary>
        public IReadOnlyList<ParseDiagnostic> AnalyzeNodes(
            ImmutableArray<AstNode> nodes,
            HashSet<string>? projectElements,
            IReadOnlyDictionary<string, ElementAttributeContract>? knownAttributes,
            string? sourceText = null)
        {
            var diags = new List<ParseDiagnostic>();
            WalkNodeList(nodes, insideForeach: false, HookContext.TopLevel, projectElements, knownAttributes, sourceText, diags);
            return diags;
        }

        // -----------------------------------------------------------------------
        //  AST WALKER
        // -----------------------------------------------------------------------

        /// <summary>
        /// Hook-context tracking for Rules of Hooks validation (UITKX0013-0016).
        /// Mirrors <c>HooksValidator.HookContext</c> in the SourceGenerator.
        /// </summary>
        private enum HookContext
        {
            TopLevel,
            Conditional,
            Loop,
            Switch,
        }

        private static void WalkNodeList(
            ImmutableArray<AstNode> nodes,
            bool insideForeach,
            HookContext hookCtx,
            HashSet<string>? projectElements,
            IReadOnlyDictionary<string, ElementAttributeContract>? knownAttributes,
            string? sourceText,
            List<ParseDiagnostic> diags
        )
        {
            // UITKX0104 - Duplicate literal key among siblings at this level.
            CheckDuplicateKeys(nodes, diags);

            for (int idx = 0; idx < nodes.Length; idx++)
            {
                var node = nodes[idx];
                WalkNode(node, insideForeach, hookCtx, projectElements, knownAttributes, sourceText, diags);
            }
        }

        /// <summary>
        /// Returns the maximum 1-based source line occupied by any descendant
        /// of <paramref name="node"/>.  Used to compute the end of a multi-line
        /// unreachable region for <see cref="DiagnosticTag"/>
        /// <c>Unnecessary</c> fading.
        /// </summary>
        private static int GetLastDescendantLine(AstNode node)
        {
            int max = node.SourceLine;

            switch (node)
            {
                case ElementNode el:
                    if (el.CloseTagLine > 0)
                        max = System.Math.Max(max, el.CloseTagLine);
                    foreach (var child in el.Children)
                        max = System.Math.Max(max, GetLastDescendantLine(child));
                    break;

                case IfNode ifn:
                    foreach (var branch in ifn.Branches)
                    {
                        max = System.Math.Max(max, branch.SourceLine);
                        if (branch.Payload.BodyCode != null)
                            max = System.Math.Max(max, branch.Payload.BodyCodeLine + CountNewlines(branch.Payload.BodyCode));
                    }
                    break;

                case ForeachNode fe:
                    if (fe.Payload.BodyCode != null)
                        max = System.Math.Max(max, fe.Payload.BodyCodeLine + CountNewlines(fe.Payload.BodyCode));
                    break;

                case ForNode fo:
                    if (fo.Payload.BodyCode != null)
                        max = System.Math.Max(max, fo.Payload.BodyCodeLine + CountNewlines(fo.Payload.BodyCode));
                    break;

                case WhileNode wh:
                    if (wh.Payload.BodyCode != null)
                        max = System.Math.Max(max, wh.Payload.BodyCodeLine + CountNewlines(wh.Payload.BodyCode));
                    break;

                case SwitchNode sw:
                    foreach (var c in sw.Cases)
                    {
                        max = System.Math.Max(max, c.SourceLine);
                        if (c.Payload.BodyCode != null)
                            max = System.Math.Max(max, c.Payload.BodyCodeLine + CountNewlines(c.Payload.BodyCode));
                    }
                    break;
            }

            // Add 1 to account for the closing brace/tag line that follows
            // the deepest child but isn't tracked in the AST.
            return max + 1;
        }

        private static int CountNewlines(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') count++;
            return count;
        }

        private static void WalkNode(
            AstNode node,
            bool insideForeach,
            HookContext hookCtx,
            HashSet<string>? projectElements,
            IReadOnlyDictionary<string, ElementAttributeContract>? knownAttributes,
            string? sourceText,
            List<ParseDiagnostic> diags
        )
        {
            switch (node)
            {
                case ElementNode el:
                    CheckElement(el, insideForeach, projectElements, knownAttributes, diags);
                    // Check attribute values for hook calls - always wrong, even at top-level
                    CheckAttributeHooks(el, diags);
                    WalkNodeList(el.Children, insideForeach: false, hookCtx, projectElements, knownAttributes, sourceText, diags);
                    break;

                case IfNode ifn:
                    foreach (var branch in ifn.Branches)
                    {
                        if (!string.IsNullOrWhiteSpace(branch.Payload.BodyCode))
                            ScanCodeForHooks(branch.Payload.BodyCode!, branch.Payload.BodyCodeOffset,
                                HookContext.Conditional, sourceText, diags,
                                GetPreambleEnd(branch.Payload.BodyCode!, branch.Payload.BodyCodeOffset, branch.Payload.BodyMarkupRanges, branch.Payload.BodyBareJsxRanges));
                        WalkNodeList(branch.Payload.Body, insideForeach: false, HookContext.Conditional,
                            projectElements, knownAttributes, sourceText, diags);
                    }
                    break;

                case ForeachNode fe:
                    if (!string.IsNullOrWhiteSpace(fe.Payload.BodyCode))
                        ScanCodeForHooks(fe.Payload.BodyCode!, fe.Payload.BodyCodeOffset,
                            HookContext.Loop, sourceText, diags,
                            GetPreambleEnd(fe.Payload.BodyCode!, fe.Payload.BodyCodeOffset, fe.Payload.BodyMarkupRanges, fe.Payload.BodyBareJsxRanges));
                    WalkNodeList(fe.Payload.Body, insideForeach: true, HookContext.Loop,
                        projectElements, knownAttributes, sourceText, diags);
                    break;

                case ForNode fn:
                    if (!string.IsNullOrWhiteSpace(fn.Payload.BodyCode))
                        ScanCodeForHooks(fn.Payload.BodyCode!, fn.Payload.BodyCodeOffset,
                            HookContext.Loop, sourceText, diags,
                            GetPreambleEnd(fn.Payload.BodyCode!, fn.Payload.BodyCodeOffset, fn.Payload.BodyMarkupRanges, fn.Payload.BodyBareJsxRanges));
                    WalkNodeList(fn.Payload.Body, insideForeach: true, HookContext.Loop,
                        projectElements, knownAttributes, sourceText, diags);
                    break;

                case WhileNode wh:
                    if (!string.IsNullOrWhiteSpace(wh.Payload.BodyCode))
                        ScanCodeForHooks(wh.Payload.BodyCode!, wh.Payload.BodyCodeOffset,
                            HookContext.Loop, sourceText, diags,
                            GetPreambleEnd(wh.Payload.BodyCode!, wh.Payload.BodyCodeOffset, wh.Payload.BodyMarkupRanges, wh.Payload.BodyBareJsxRanges));
                    WalkNodeList(wh.Payload.Body, insideForeach: true, HookContext.Loop,
                        projectElements, knownAttributes, sourceText, diags);
                    break;

                case SwitchNode sw:
                    foreach (var sc in sw.Cases)
                    {
                        if (!string.IsNullOrWhiteSpace(sc.Payload.BodyCode))
                            ScanCodeForHooks(sc.Payload.BodyCode!, sc.Payload.BodyCodeOffset,
                                HookContext.Switch, sourceText, diags,
                                GetPreambleEnd(sc.Payload.BodyCode!, sc.Payload.BodyCodeOffset, sc.Payload.BodyMarkupRanges, sc.Payload.BodyBareJsxRanges));
                        WalkNodeList(sc.Payload.Body, insideForeach: false, HookContext.Switch,
                            projectElements, knownAttributes, sourceText, diags);
                    }
                    break;

                case ExpressionNode ex:
                    if (hookCtx != HookContext.TopLevel)
                        CheckExpressionForHooks(ex.Expression, ex.SourceLine, hookCtx, diags);
                    break;

                // TextNode - nothing to check.
            }
        }

        // -----------------------------------------------------------------------
        //  RULES OF HOOKS (UITKX0013-0016)
        // -----------------------------------------------------------------------

        // Patterns that indicate a hook call.  Sourced from
        // Ruitk.Core.HookRegistry so this analyzer cannot drift from
        // the SourceGenerator's HooksValidator.s_hookPatterns.  Pre-0.5.23
        // both tables were missing useLayoutEffect entries; the registry
        // includes them, expanding UITKX0013-0016 to also catch
        // conditional/looping useLayoutEffect calls (pure coverage win, no
        // legitimate code breaks).
        //
        // This is a per-keystroke hot path; the registry guarantees a single
        // cached array reference and never reallocates per call.
        private static readonly string[] s_hookPatterns =
            global::Ruitk.Core.HookRegistry.GetValidationPatterns();

        /// <summary>
        /// Returns the end offset within <paramref name="bodyCode"/> marking the
        /// boundary of the setup preamble - the minimum start of any JSX range.
        /// Hooks found after this offset live inside nested directive bodies and
        /// will be caught when the tree walk visits those nested nodes directly.
        /// </summary>
        private static int GetPreambleEnd(
            string bodyCode,
            int bodyCodeOffset,
            ImmutableArray<(int Start, int End, int Line)> markupRanges,
            ImmutableArray<(int Start, int End, int Line)> bareRanges)
        {
            int min = bodyCode.Length;
            foreach (var (absStart, _, _) in markupRanges)
            {
                int rel = absStart - bodyCodeOffset;
                if (rel >= 0 && rel < min) min = rel;
            }
            foreach (var (absStart, _, _) in bareRanges)
            {
                int rel = absStart - bodyCodeOffset;
                if (rel >= 0 && rel < min) min = rel;
            }
            return min;
        }

        /// <summary>
        /// Scans a control-block SetupCode string for hook calls (UITKX0013-
        /// 0015).  Scans only up to <paramref name="scanEnd"/> characters to
        /// avoid reporting a deeply-nested hook once per ancestor directive body.
        /// </summary>
        private static void ScanCodeForHooks(
            string code,
            int setupCodeOffset,
            HookContext ctx,
            string? sourceText,
            List<ParseDiagnostic> diags,
            int scanEnd = int.MaxValue)
        {
            int limit = Math.Min(code.Length, scanEnd);
            foreach (var pattern in s_hookPatterns)
            {
                // U-10: raw IndexOf false-positives on `// TODO: useState(1) here` (comment)
                // and `_useState(`/`obj.useState(` (identifier/member-access boundary).
                int idx = CSharpLexFacts.FindHookCall(code, pattern, 0, limit);
                if (idx < 0)
                    continue;

                string hookName = pattern.TrimEnd('(');

                string? diagCode = ctx switch
                {
                    HookContext.Conditional => DiagnosticCodes.HookInConditional,
                    HookContext.Loop        => DiagnosticCodes.HookInLoop,
                    HookContext.Switch      => DiagnosticCodes.HookInSwitch,
                    _                       => null,
                };
                if (diagCode == null) continue;

                // Compute exact source position using absolute offset
                int absOffset = setupCodeOffset + idx;
                int line = 1;
                int col = 0;

                if (sourceText != null && absOffset <= sourceText.Length)
                {
                    // Count lines and find column from sourceText
                    for (int i = 0; i < absOffset; i++)
                    {
                        if (sourceText[i] == '\n') { line++; col = 0; }
                        else col++;
                    }
                }
                else
                {
                    // Fallback: approximate from SetupCode string
                    line = 1;
                    for (int i = 0; i < idx && i < code.Length; i++)
                        if (code[i] == '\n') line++;
                    col = 0;
                    for (int i = idx - 1; i >= 0; i--)
                    {
                        if (code[i] == '\n') break;
                        col++;
                    }
                }

                string context = ctx switch
                {
                    HookContext.Conditional => "a conditional (@if/@else) branch",
                    HookContext.Loop        => "a loop (@foreach/@for/@while)",
                    HookContext.Switch      => "a @switch case",
                    _                       => "a control block",
                };

                diags.Add(MakeDiag(
                    diagCode,
                    ParseSeverity.Error,
                    $"Hook '{hookName}' must not be called inside {context}. Hooks must be called unconditionally at the top level of the component.",
                    line,
                    col,
                    col + hookName.Length
                ));
                break; // one diagnostic per SetupCode block is enough
            }
        }

        /// <summary>
        /// Checks an inline expression node for hook calls when inside a
        /// control block (UITKX0013-0015).
        /// </summary>
        private static void CheckExpressionForHooks(
            string code,
            int sourceLine,
            HookContext ctx,
            List<ParseDiagnostic> diags)
        {
            foreach (var pattern in s_hookPatterns)
            {
                if (CSharpLexFacts.FindHookCall(code, pattern, 0, code.Length) < 0)
                    continue;

                string hookName = pattern.TrimEnd('(');

                string? diagCode = ctx switch
                {
                    HookContext.Conditional => DiagnosticCodes.HookInConditional,
                    HookContext.Loop        => DiagnosticCodes.HookInLoop,
                    HookContext.Switch      => DiagnosticCodes.HookInSwitch,
                    _                       => null,
                };
                if (diagCode == null) continue;

                string context = ctx switch
                {
                    HookContext.Conditional => "a conditional (@if/@else) branch",
                    HookContext.Loop        => "a loop (@foreach/@for/@while)",
                    HookContext.Switch      => "a @switch case",
                    _                       => "a control block",
                };

                diags.Add(MakeDiag(
                    diagCode,
                    ParseSeverity.Error,
                    $"Hook '{hookName}' must not be called inside {context}. Hooks must be called unconditionally at the top level of the component.",
                    sourceLine
                ));
                break;
            }
        }

        /// <summary>
        /// Checks element attribute values for hook calls (UITKX0016).
        /// Hook calls in onClick, text bindings, etc. are always wrong.
        /// </summary>
        private static void CheckAttributeHooks(
            ElementNode el,
            List<ParseDiagnostic> diags)
        {
            foreach (var attr in el.Attributes)
            {
                if (attr.Value is not CSharpExpressionValue attrExpr)
                    continue;

                foreach (var pattern in s_hookPatterns)
                {
                    if (CSharpLexFacts.FindHookCall(attrExpr.Expression, pattern, 0, attrExpr.Expression.Length) < 0)
                        continue;

                    string hookName = pattern.TrimEnd('(');
                    diags.Add(MakeDiag(
                        DiagnosticCodes.HookInEventHandler,
                        ParseSeverity.Error,
                        $"Hook '{hookName}' must not be called inside an event handler or attribute expression. Hooks must be called from the component body, not from callbacks.",
                        attr.SourceLine,
                        attr.SourceColumn,
                        attr.SourceColumn + attr.Name.Length
                    ));
                    break; // one diagnostic per attribute is enough
                }
            }
        }

        // -----------------------------------------------------------------------
        //  ELEMENT CHECKS
        // -----------------------------------------------------------------------

        private static void CheckElement(
            ElementNode el,
            bool insideForeach,
            HashSet<string>? projectElements,
            IReadOnlyDictionary<string, ElementAttributeContract>? knownAttributes,
            List<ParseDiagnostic> diags
        )
        {
            // UITKX0106 - Missing key inside a loop (@foreach / @for / @while).
            // U-12: must match the SourceGenerator's severity (UitkxDiagnostics.ForeachMissingKey
            // = Warning) - this was Error here, so the editor blocked on something the build
            // only warned about.
            if (insideForeach && !HasKeyAttribute(el))
            {
                diags.Add(
                    MakeDiag(
                        DiagnosticCodes.MissingKey,
                        ParseSeverity.Warning,
                        $"Element <{el.TagName}> inside a loop should have a 'key' attribute to help with reconciliation.",
                        el.SourceLine,
                        el.SourceColumn,
                        el.SourceColumn + 1 + el.TagName.Length
                    )
                );
            }

            // UITKX0105 - Unknown PascalCase element (custom component not in index).
            bool isPascalCase = el.TagName.Length > 0 && char.IsUpper(el.TagName[0]);
            bool elementKnown = true; // assume known unless we have an index and it's missing
            if (isPascalCase && projectElements != null && !projectElements.Contains(el.TagName))
            {
                elementKnown = false;
                diags.Add(
                    MakeDiag(
                        DiagnosticCodes.UnknownElement,
                        ParseSeverity.Error,
                        $"Unknown element '<{el.TagName}>'. No component with this name was found in the workspace.",
                        el.SourceLine,
                        el.SourceColumn + 1,  // +1 to point at the name, past '<'
                        el.SourceColumn + 1 + el.TagName.Length
                    )
                );
            }

            // UITKX0109 - Unknown attribute on a known element.
            // UITKX0115 - A required prop the call site did not supply.
            // Both only run when the element is known (no double-error on an
            // unknown element) and we hold a contract for it.
            if (elementKnown
                && knownAttributes != null
                && knownAttributes.TryGetValue(el.TagName, out var contract))
            {
                foreach (var attr in el.Attributes)
                {
                    if (contract.Known == null)
                        break;
                    if (!contract.Known.Contains(attr.Name))
                    {
                        diags.Add(
                            MakeDiag(
                                DiagnosticCodes.UnknownAttribute,
                                ParseSeverity.Error,
                                $"Unknown attribute '{attr.Name}' on <{el.TagName}>.",
                                attr.SourceLine,
                                attr.SourceColumn,
                                attr.NameEndColumn > 0
                                    ? attr.NameEndColumn
                                    : attr.SourceColumn + attr.Name.Length
                            )
                        );
                    }
                }

                if (contract.Required.Count > 0)
                {
                    var supplied = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                    foreach (var attr in el.Attributes)
                        supplied.Add(attr.Name);

                    foreach (var req in contract.Required)
                    {
                        if (supplied.Contains(req.Key))
                            continue;
                        diags.Add(
                            MakeDiag(
                                DiagnosticCodes.MissingRequiredProp,
                                ParseSeverity.Error,
                                $"<{el.TagName}> is missing required prop '{req.Key}' (parameter '{req.Value}' declares no default value).",
                                el.SourceLine,
                                el.SourceColumn + 1,
                                el.SourceColumn + 1 + el.TagName.Length
                            )
                        );
                    }
                }
            }
        }

        // -----------------------------------------------------------------------
        //  DUPLICATE-KEY CHECK
        // -----------------------------------------------------------------------

        private static void CheckDuplicateKeys(
            ImmutableArray<AstNode> siblings,
            List<ParseDiagnostic> diags
        )
        {
            // Collect elements that have a literal string key attribute.
            // Map: key-value -> first element that used it.
            var seen = new Dictionary<string, ElementNode>(System.StringComparer.Ordinal);

            foreach (var node in siblings)
            {
                if (node is not ElementNode el)
                    continue;

                var keyAttr = el.Attributes.FirstOrDefault(a =>
                    string.Equals(a.Name, "key", System.StringComparison.Ordinal)
                    && a.Value is StringLiteralValue
                );

                if (keyAttr is null)
                    continue;

                var keyVal = ((StringLiteralValue)keyAttr.Value).Value;

                if (seen.TryGetValue(keyVal, out var first))
                {
                    // Report on the duplicate (current element).
                    diags.Add(
                        MakeDiag(
                            DiagnosticCodes.DuplicateKey,
                            ParseSeverity.Error,
                            $"Duplicate sibling key \"{keyVal}\". Keys must be unique among sibling elements.",
                            el.SourceLine,
                            el.SourceColumn
                        )
                    );

                    // Also report on the first occurrence if not already done.
                    if (
                        !diags.Any(d =>
                            d.Code == DiagnosticCodes.DuplicateKey
                            && d.SourceLine == first.SourceLine
                        )
                    )
                    {
                        diags.Add(
                            MakeDiag(
                                DiagnosticCodes.DuplicateKey,
                                ParseSeverity.Error,
                                $"Duplicate sibling key \"{keyVal}\". Keys must be unique among sibling elements.",
                                first.SourceLine,
                                first.SourceColumn
                            )
                        );
                    }
                }
                else
                {
                    seen[keyVal] = el;
                }
            }
        }

        // -----------------------------------------------------------------------
        //  UNUSED PARAMETER CHECK
        // -----------------------------------------------------------------------

        /// <summary>
        /// UITKX0111 - For each function-style component parameter, check whether
        /// its name appears in the setup code (with scope-aware shadowing) or
        /// the markup AST (expressions, attribute bindings, conditions).
        /// </summary>
        private static void CheckUnusedParameters(
            DirectiveSet d,
            ImmutableArray<AstNode> rootNodes,
            List<ParseDiagnostic> diags)
        {
            // Collect C# text from markup only (expressions, attributes,
            // conditions).  Markup never has local declarations, so a
            // simple word-boundary check is safe here.
            var markupSb = new System.Text.StringBuilder();
            CollectCSharpText(rootNodes, markupSb);
            string markupText = markupSb.ToString();

            foreach (var p in d.FunctionParams)
            {
                // C# discard convention: an underscore-prefixed parameter is
                // deliberately unused (matches IDE0060's escape hatch).
                if (p.Name.StartsWith("_", StringComparison.Ordinal))
                    continue;

                var nameRx = new Regex(@"\b" + Regex.Escape(p.Name) + @"\b");

                // 1. Used in markup?  Simple text search is sufficient.
                if (nameRx.IsMatch(markupText))
                    continue;

                // 2. Used in setup code?  Scope-aware: skip usages inside
                //    scopes that declare their own local with the same name.
                if (IsUsedInSetupCode(d.FunctionSetupCode, p.Name, nameRx))
                    continue;

                int col    = p.NameColumn >= 0 ? p.NameColumn : 0;
                int endCol = col + p.Name.Length;
                int line   = p.SourceLine > 0 ? p.SourceLine : d.ComponentDeclarationLine;

                diags.Add(new ParseDiagnostic
                {
                    Code       = DiagnosticCodes.UnusedParameter,
                    Severity   = ParseSeverity.Error,
                    Message    = $"Parameter '{p.Name}' is declared but never used.",
                    SourceLine = line,
                    SourceColumn = col,
                    EndLine    = line,
                    EndColumn  = endCol,
                });
            }
        }

        /// <summary>
        /// UITKX0114 - Two parameters that collapse to the same PROP name
        /// (<c>_count</c> and <c>count</c> both expose the prop <c>count</c> —
        /// the leading underscore is the deliberately-unused marker, not part
        /// of the contract). Anchored at the LATER parameter.
        /// </summary>
        private static void CheckDuplicatePropNames(DirectiveSet d, List<ParseDiagnostic> diags)
        {
            var seen = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var p in d.FunctionParams)
            {
                string propName = p.PropSourceName;
                if (seen.TryGetValue(propName, out string firstName))
                {
                    int col    = p.NameColumn >= 0 ? p.NameColumn : 0;
                    int line   = p.SourceLine > 0 ? p.SourceLine : d.ComponentDeclarationLine;
                    diags.Add(new ParseDiagnostic
                    {
                        Code       = DiagnosticCodes.DuplicatePropName,
                        Severity   = ParseSeverity.Error,
                        Message    = $"Parameters '{firstName}' and '{p.Name}' both expose the prop '{propName}' — a leading underscore marks a parameter unused without renaming its prop.",
                        SourceLine = line,
                        SourceColumn = col,
                        EndLine    = line,
                        EndColumn  = col + p.Name.Length,
                    });
                    continue;
                }
                seen[propName] = p.Name;
            }
        }

        /// <summary>
        /// Scope-aware check: does <paramref name="paramName"/> appear in
        /// <paramref name="setupCode"/> at a brace depth where it is NOT
        /// shadowed by a local declaration (<c>var name</c>, <c>Type name</c>,
        /// or a function parameter with the same name)?
        /// </summary>
        private static bool IsUsedInSetupCode(
            string? setupCode, string paramName, Regex nameRx)
        {
            if (string.IsNullOrEmpty(setupCode))
                return false;
            if (!nameRx.IsMatch(setupCode))
                return false; // fast bail

            // Matches a local variable declaration that introduces `paramName`
            // as a new local: "var name", "int name", "List<T>? name", etc.
            var declRx = new Regex(
                @"(?:var|int|uint|long|ulong|short|ushort|byte|sbyte|char" +
                @"|float|double|decimal|bool|string|object|dynamic|nint|nuint" +
                @"|[A-Z]\w*(?:<[^>]*>)?(?:\?|\[\])*)" +
                @"\s+" + Regex.Escape(paramName) + @"\b");

            var lines = setupCode.Replace("\r\n", "\n").Split('\n');
            int depth = 0;
            // shadowAt[d] == true  ->  a local with this name was declared at depth d.
            // Shadows apply at depth d and all deeper levels until the scope exits.
            var shadowAt = new bool[128];

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                string trimmed = line.Trim();

                if (trimmed.Length == 0)
                {
                    // Still track braces on blank lines.
                    for (int c = 0; c < line.Length; c++)
                    {
                        if (line[c] == '{') { depth++; }
                        else if (line[c] == '}')
                        {
                            if (depth > 0 && depth < 128) shadowAt[depth] = false;
                            if (depth > 0) depth--;
                        }
                    }
                    continue;
                }

                // Check if this line contains a local declaration.
                if (declRx.IsMatch(line) && depth >= 0 && depth < 128)
                    shadowAt[depth] = true;

                // Check if the name is used on this line AND not shadowed.
                if (nameRx.IsMatch(line))
                {
                    bool shadowed = false;
                    for (int sd = 0; sd <= depth && sd < 128; sd++)
                        if (shadowAt[sd]) { shadowed = true; break; }

                    if (!shadowed)
                    {
                        // If the line also has a declaration, the name after
                        // "var/Type" is the declaration itself - remove it and
                        // re-check.  This way `var items = f(items)` still
                        // counts the RHS `items` as a parameter use.
                        string stripped = declRx.Replace(line, "");
                        if (nameRx.IsMatch(stripped))
                            return true;
                    }
                }

                // Update depth from braces on this line.
                for (int c = 0; c < line.Length; c++)
                {
                    if (line[c] == '{') { depth++; }
                    else if (line[c] == '}')
                    {
                        if (depth > 0 && depth < 128) shadowAt[depth] = false;
                        if (depth > 0) depth--;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Recursively collects C# expression text from markup nodes only
        /// (element attributes, inline expressions, control-flow conditions).
        /// Setup code is handled separately by <see cref="IsUsedInSetupCode"/> with
        /// scope-aware shadowing.
        /// </summary>
        private static void CollectCSharpText(ImmutableArray<AstNode> nodes, System.Text.StringBuilder sb)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case ExpressionNode en:
                        sb.Append(' ').Append(en.Expression).Append(' ');
                        break;

                    case ElementNode el:
                        CollectCSharpTextFromElement(el, sb);
                        break;

                    case IfNode ifn:
                        foreach (var br in ifn.Branches)
                        {
                            if (br.Condition != null)
                                sb.Append(' ').Append(br.Condition).Append(' ');
                            if (br.Payload.BodyCode != null)
                                sb.Append(' ').Append(br.Payload.BodyCode).Append(' ');
                        }
                        break;

                    case ForeachNode fe:
                        sb.Append(' ').Append(fe.CollectionExpression).Append(' ');
                        if (fe.Payload.BodyCode != null)
                            sb.Append(' ').Append(fe.Payload.BodyCode).Append(' ');
                        break;

                    case ForNode fn:
                        sb.Append(' ').Append(fn.ForExpression).Append(' ');
                        if (fn.Payload.BodyCode != null)
                            sb.Append(' ').Append(fn.Payload.BodyCode).Append(' ');
                        break;

                    case WhileNode wh:
                        sb.Append(' ').Append(wh.Condition).Append(' ');
                        if (wh.Payload.BodyCode != null)
                            sb.Append(' ').Append(wh.Payload.BodyCode).Append(' ');
                        break;

                    case SwitchNode sw:
                        sb.Append(' ').Append(sw.SwitchExpression).Append(' ');
                        foreach (var sc in sw.Cases)
                        {
                            if (sc.ValueExpression != null)
                                sb.Append(' ').Append(sc.ValueExpression).Append(' ');
                            if (sc.Payload.BodyCode != null)
                                sb.Append(' ').Append(sc.Payload.BodyCode).Append(' ');
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Collects C# text from an element's attributes and children.
        /// </summary>
        private static void CollectCSharpTextFromElement(ElementNode el, System.Text.StringBuilder sb)
        {
            foreach (var attr in el.Attributes)
            {
                switch (attr.Value)
                {
                    case CSharpExpressionValue cv:
                        sb.Append(' ').Append(cv.Expression).Append(' ');
                        break;
                    case JsxExpressionValue jv when jv.Element != null:
                        CollectCSharpTextFromElement(jv.Element, sb);
                        break;
                }
            }
            CollectCSharpText(el.Children, sb);
        }

        // -----------------------------------------------------------------------
        //  HELPERS
        // -----------------------------------------------------------------------

        private static bool HasKeyAttribute(ElementNode el) =>
            el.Attributes.Any(a =>
                string.Equals(a.Name, "key", System.StringComparison.OrdinalIgnoreCase)
            );

        private static ParseDiagnostic MakeDiag(
            string code,
            ParseSeverity severity,
            string message,
            int line,
            int column = 0,
            int endColumn = 0
        ) =>
            new ParseDiagnostic
            {
                Code = code,
                Severity = severity,
                Message = message,
                SourceLine = line,
                SourceColumn = column,
                EndLine = line,
                EndColumn = endColumn > 0 ? endColumn : column,
            };

        /// <summary>
        /// Scans the setup code region for a top-level <c>return -;</c> statement
        /// that appears before the render return. Returns the 1-based line of the
        /// <c>;</c> ending that early return, or -1 if none found.
        /// </summary>
        private static int FindEarlyReturnInSetup(
            string source, int setupStartOffset, int setupStartLine, int renderReturnEndLine)
        {
            int i = setupStartOffset;
            int braceDepth = 0;
            int parenDepth = 0;
            int currentLine = setupStartLine;

            while (i < source.Length)
            {
                char c = source[i];

                // Track line numbers
                if (c == '\n')
                {
                    currentLine++;
                    i++;
                    continue;
                }
                if (c == '\r')
                {
                    i++;
                    continue;
                }

                // Skip strings
                if (c == '"')
                {
                    if (i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
                    {
                        // Raw string literal - skip to closing """
                        i += 3;
                        while (i + 2 < source.Length)
                        {
                            if (source[i] == '\n') currentLine++;
                            if (source[i] == '"' && source[i + 1] == '"' && source[i + 2] == '"')
                            { i += 3; break; }
                            i++;
                        }
                        continue;
                    }
                    i++;
                    while (i < source.Length && source[i] != '"' && source[i] != '\n')
                    {
                        if (source[i] == '\\') i++; // skip escape
                        i++;
                    }
                    if (i < source.Length && source[i] == '"') i++;
                    continue;
                }
                if (c == '\'')
                {
                    i++;
                    while (i < source.Length && source[i] != '\'' && source[i] != '\n')
                    {
                        if (source[i] == '\\') i++;
                        i++;
                    }
                    if (i < source.Length && source[i] == '\'') i++;
                    continue;
                }

                // Skip line comments
                if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n') i++;
                    continue;
                }
                // Skip block comments
                if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length)
                    {
                        if (source[i] == '\n') currentLine++;
                        if (source[i] == '*' && source[i + 1] == '/')
                        { i += 2; break; }
                        i++;
                    }
                    continue;
                }

                // Track nesting
                if (c == '{') { braceDepth++; i++; continue; }
                if (c == '}')
                {
                    if (braceDepth > 0) braceDepth--;
                    else return -1; // hit component closing brace
                    i++;
                    continue;
                }
                if (c == '(') { parenDepth++; i++; continue; }
                if (c == ')')
                {
                    if (parenDepth > 0) parenDepth--;
                    i++;
                    continue;
                }

                // At top level, look for `return` keyword
                if (braceDepth == 0 && parenDepth == 0
                    && i + 6 <= source.Length
                    && source.Substring(i, 6) == "return"
                    && (i + 6 >= source.Length || !char.IsLetterOrDigit(source[i + 6]) && source[i + 6] != '_'))
                {
                    // Find the `;` that ends this return statement
                    int j = i + 6;
                    int retBraces = 0, retParens = 0;
                    int retLine = currentLine;
                    while (j < source.Length)
                    {
                        char rc = source[j];
                        if (rc == '\n') retLine++;
                        if (rc == '{') retBraces++;
                        else if (rc == '}') { if (retBraces > 0) retBraces--; else break; }
                        else if (rc == '(') retParens++;
                        else if (rc == ')') { if (retParens > 0) retParens--; }
                        else if (rc == ';' && retBraces == 0 && retParens == 0)
                        {
                            // Found the end of the return statement.
                            // Only report if this line is before the render return.
                            if (retLine < renderReturnEndLine)
                                return retLine;
                            return -1; // this IS the render return
                        }
                        j++;
                    }
                }

                i++;
            }

            return -1;
        }

        // -----------------------------------------------------------------------
        //  ASSET PATH VALIDATION
        // -----------------------------------------------------------------------

        private static readonly Regex s_assetCallRe = new Regex(
            @"(?:Asset|Ast)\s*<\s*(\w+)\s*>\s*\(\s*""([^""]+)""\s*\)",
            RegexOptions.Compiled);

        private static readonly Regex s_ussDirectiveRe = new Regex(
            @"@uss\s+""([^""]+)""",
            RegexOptions.Compiled);

        // -- Extension -> valid requested types ---------------------------------

        private static readonly Dictionary<string, HashSet<string>> s_extensionValidTypes =
            new Dictionary<string, HashSet<string>>(System.StringComparer.OrdinalIgnoreCase)
            {
                // Image files (TextureImporter) -> Texture2D or Sprite
                { ".png",  new HashSet<string> { "Texture2D", "Sprite" } },
                { ".jpg",  new HashSet<string> { "Texture2D", "Sprite" } },
                { ".jpeg", new HashSet<string> { "Texture2D", "Sprite" } },
                { ".bmp",  new HashSet<string> { "Texture2D", "Sprite" } },
                { ".tga",  new HashSet<string> { "Texture2D", "Sprite" } },
                { ".psd",  new HashSet<string> { "Texture2D", "Sprite" } },
                { ".gif",  new HashSet<string> { "Texture2D", "Sprite" } },
                { ".tif",  new HashSet<string> { "Texture2D", "Sprite" } },
                { ".tiff", new HashSet<string> { "Texture2D", "Sprite" } },
                { ".exr",  new HashSet<string> { "Texture2D", "Sprite" } },
                { ".hdr",  new HashSet<string> { "Texture2D", "Sprite" } },
                // SVG -> VectorImage
                { ".svg",  new HashSet<string> { "VectorImage" } },
                // Audio
                { ".wav",  new HashSet<string> { "AudioClip" } },
                { ".mp3",  new HashSet<string> { "AudioClip" } },
                { ".ogg",  new HashSet<string> { "AudioClip" } },
                { ".aiff", new HashSet<string> { "AudioClip" } },
                { ".flac", new HashSet<string> { "AudioClip" } },
                // Fonts
                { ".ttf",  new HashSet<string> { "Font" } },
                { ".otf",  new HashSet<string> { "Font" } },
                // Unity native
                { ".mat",  new HashSet<string> { "Material" } },
                { ".uss",  new HashSet<string> { "StyleSheet" } },
                { ".renderTexture", new HashSet<string> { "RenderTexture" } },
            };

        /// <summary>
        /// UITKX0120 - Check that every <c>Asset&lt;T&gt;("path")</c>,
        /// <c>Ast&lt;T&gt;("path")</c>, and <c>@uss "path"</c> references
        /// a file that exists on disk.
        /// </summary>
        private static void CheckAssetPaths(
            string sourceText,
            string filePath,
            List<ParseDiagnostic> diags)
        {
            string? uitkxDir = GetAssetDir(filePath);
            if (uitkxDir == null) return;

            string? projectRoot = GetProjectRoot(filePath);
            if (projectRoot == null) return;

            // @uss directives - path only, type is always StyleSheet
            CheckAssetPathMatches(s_ussDirectiveRe, sourceText, uitkxDir, projectRoot, diags,
                pathGroup: 1, typeGroup: -1, impliedType: "StyleSheet");

            // Asset<T>/Ast<T> calls - type in group[1], path in group[2]
            CheckAssetPathMatches(s_assetCallRe, sourceText, uitkxDir, projectRoot, diags,
                pathGroup: 2, typeGroup: 1, impliedType: null);
        }

        private static void CheckAssetPathMatches(
            Regex regex,
            string sourceText,
            string uitkxDir,
            string projectRoot,
            List<ParseDiagnostic> diags,
            int pathGroup,
            int typeGroup,
            string? impliedType)
        {
            foreach (Match m in regex.Matches(sourceText))
            {
                string rawPath = m.Groups[pathGroup].Value;
                string resolved = ResolveAssetPath(uitkxDir, rawPath);
                string absolute = System.IO.Path.Combine(projectRoot, resolved.Replace('/', System.IO.Path.DirectorySeparatorChar));

                var pathCapture = m.Groups[pathGroup];

                // UITKX0120 - file existence check
                if (!File.Exists(absolute))
                {
                    int line = 1, col = 0;
                    for (int i = 0; i < pathCapture.Index && i < sourceText.Length; i++)
                    {
                        if (sourceText[i] == '\n') { line++; col = 0; }
                        else col++;
                    }

                    diags.Add(new ParseDiagnostic
                    {
                        Code = DiagnosticCodes.AssetNotFound,
                        Severity = ParseSeverity.Error,
                        Message = $"Asset file not found: \"{rawPath}\" (resolved to \"{resolved}\").",
                        SourceLine = line,
                        SourceColumn = col,
                        EndLine = line,
                        EndColumn = col + pathCapture.Length,
                    });
                    continue; // no point checking type if file doesn't exist
                }

                // UITKX0121 - type mismatch check
                string requestedType = typeGroup >= 0 ? m.Groups[typeGroup].Value : impliedType!;
                string ext = System.IO.Path.GetExtension(rawPath);
                if (!string.IsNullOrEmpty(ext)
                    && s_extensionValidTypes.TryGetValue(ext, out var validTypes)
                    && !validTypes.Contains(requestedType))
                {
                    int line = 1, col = 0;
                    // Squiggle the type name (or the path if no type group)
                    var squiggle = typeGroup >= 0 ? m.Groups[typeGroup] : pathCapture;
                    for (int i = 0; i < squiggle.Index && i < sourceText.Length; i++)
                    {
                        if (sourceText[i] == '\n') { line++; col = 0; }
                        else col++;
                    }

                    string validList = string.Join(", ", validTypes);
                    diags.Add(new ParseDiagnostic
                    {
                        Code = DiagnosticCodes.AssetTypeMismatch,
                        Severity = ParseSeverity.Error,
                        Message = $"Type '{requestedType}' is not compatible with '{ext}' files. Valid types: {validList}.",
                        SourceLine = line,
                        SourceColumn = col,
                        EndLine = line,
                        EndColumn = col + squiggle.Length,
                    });
                }
            }
        }

        // H-03: the canonical rule (Assets//Packages/ absolute; everything else
        // uitkx-dir-relative) now lives once in AssetPathUtil, shared with
        // CSharpEmitter (SourceGenerator~) and mirrored in Editor/HMR (which
        // cannot reference language-lib directly - see AssetPathUtil's doc comment).
        private static string ResolveAssetPath(string uitkxDir, string rawPath)
            => Ruitk.Language.AssetPathUtil.ResolveAssetPath(uitkxDir, rawPath);

        /// <summary>
        /// Extracts the Unity project-relative directory from an absolute file path.
        /// Returns null if the path doesn't contain an <c>Assets/</c> segment.
        /// </summary>
        private static string? GetAssetDir(string filePath)
            => Ruitk.Language.AssetPathUtil.GetAssetDir(filePath);

        /// <summary>
        /// Extracts the Unity project root (the folder containing <c>Assets/</c>)
        /// from an absolute file path.  Returns null when <c>Assets/</c> is not found.
        /// </summary>
        private static string? GetProjectRoot(string filePath)
        {
            string normalized = filePath.Replace('\\', '/');
            int assetsIdx = normalized.IndexOf("/Assets/", System.StringComparison.OrdinalIgnoreCase);
            if (assetsIdx >= 0)
                return normalized.Substring(0, assetsIdx);
            // Relative path starting with "Assets/" - project root is CWD
            if (normalized.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                return ".";
            return null;
        }
    }
}
