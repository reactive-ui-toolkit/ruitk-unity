using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.RegularExpressions;

namespace Ruitk.Language.Parser
{
    /// <summary>
    /// Parses the function-style component declaration from a .uitkx source file.
    ///
    /// A function-style component has the form:
    /// <code>
    ///   component PlayerHUD(string title = "HUD") {
    ///       var (count, setCount) = useState(0);
    ///       return (
    ///           &lt;Label text={title} /&gt;
    ///       )
    ///   }
    /// </code>
    ///
    /// The returned <see cref="DirectiveSet"/> carries the character index and
    /// 1-based line number where the markup begins, which the parser reads from.
    /// </summary>
    public static class DirectiveParser
    {
        // The default namespace for a .uitkx file that has no @namespace. The pipeline
        // OVERRIDES this with the path-derived namespace (see
        // UitkxPipeline.ResolveEffectiveNamespace); it survives only as the no-project-root
        // fallback. The generator no longer reads a companion .cs to infer the
        // namespace — a target's identity must never flip on a .cs edit (plan §4).
        private const string FunctionStyleDefaultNamespace = "Ruitk.FunctionStyle";

        private static readonly HashSet<string> s_topLevelKeywords = new HashSet<string>(
            StringComparer.Ordinal
        )
        {
            "namespace",
            "component",
            "hook",
            "module",
            "using",
            "uss",
            "props",
            "key",
            "inject",
            "backend",
        };

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Parses all top-level directives from <paramref name="source"/> and
        /// appends any validation diagnostics to <paramref name="diagnosticBag"/>.
        /// </summary>
        public static DirectiveSet Parse(
            string source,
            string filePath,
            List<ParseDiagnostic> diagnosticBag,
            bool useLastReturn = true
        )
        {
            if (source.Length > 0 && source[0] == '\uFEFF')
                source = source.Substring(1);

            if (TryParseFunctionStyle(source, filePath, diagnosticBag, out var functionStyleSet, useLastReturn))
                return functionStyleSet;

            if (LooksLikeFunctionStyleComponent(source, 0))
            {
                int fsI = 0;
                int fsLine = 1;
                SkipLeadingFunctionStyleTrivia(source, ref fsI, ref fsLine);

                string fallbackComponent = Path.GetFileNameWithoutExtension(filePath);
                if (TryReadKeyword(source, ref fsI, "component"))
                {
                    SkipSpaces(source, ref fsI);
                    if (TryReadIdentifier(source, ref fsI, out string parsedComponentName))
                        fallbackComponent = parsedComponentName;
                }

                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2105",
                    Severity = ParseSeverity.Error,
                    SourceLine = fsLine,
                    Message = "Invalid function-style component declaration. Expected 'component PascalCaseName { ... return (...) ... }'.",
                });

                return new DirectiveSet(
                    Namespace: FunctionStyleDefaultNamespace,
                    ComponentName: fallbackComponent,
                    PropsTypeName: null,
                    DefaultKey: null,
                    Usings: ImmutableArray<string>.Empty,
                    UssFiles: ImmutableArray<string>.Empty,
                    Injects: ImmutableArray<(string Type, string Name)>.Empty,
                    MarkupStartLine: fsLine,
                    MarkupStartIndex: source.Length,
                    MarkupEndIndex: source.Length,
                    IsFunctionStyle: true,
                    HasExplicitNamespace: false,
                    FunctionSetupCode: string.Empty,
                    FunctionSetupStartLine: fsLine
                );
            }

            // File does not contain a valid function-style component declaration.
            string fallbackName = Path.GetFileNameWithoutExtension(filePath);
            diagnosticBag.Add(new ParseDiagnostic
            {
                Code = "UITKX2105",
                Severity = ParseSeverity.Error,
                SourceLine = 1,
                Message = $"'{fallbackName}.uitkx' does not contain a valid function-style component declaration. Expected 'component PascalCaseName {{ ... return (...) ... }}'.",
            });

            return new DirectiveSet(
                Namespace: FunctionStyleDefaultNamespace,
                ComponentName: fallbackName,
                PropsTypeName: null,
                DefaultKey: null,
                Usings: ImmutableArray<string>.Empty,
                UssFiles: ImmutableArray<string>.Empty,
                Injects: ImmutableArray<(string Type, string Name)>.Empty,
                MarkupStartLine: 1,
                MarkupStartIndex: 0,
                MarkupEndIndex: source.Length,
                IsFunctionStyle: true,
                HasExplicitNamespace: false,
                FunctionSetupCode: string.Empty,
                FunctionSetupStartLine: 1
            );
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool TryParseFunctionStyle(
            string source,
            string filePath,
            List<ParseDiagnostic> diagnosticBag,
            out DirectiveSet directiveSet,
            bool useLastReturn = true
        )
        {
            directiveSet = default!;

            if (source.Length > 0 && source[0] == '\uFEFF')
                source = source.Substring(1);

            int i = 0;
            int line = 1;
            var leadingTrivia = new List<(string Text, bool IsBlock, int Line)>();
            SkipLeadingFunctionStyleTrivia(source, ref i, ref line, leadingTrivia);

            // Parse optional leading `using X.Y.Z;` lines, `@uss "path"` lines,
            // AND an optional `@namespace X.Y` directive before the component keyword,
            // in any order.
            var usings = new List<string>();
            var usingDirectives = new List<UsingDirective>();
            var ussFiles = new List<string>();
            var imports = new List<ImportDeclaration>();
            string? inlineNamespace = null;
            string? backendName = null;
            bool parsedPreambleLine;
            do
            {
                parsedPreambleLine = false;
                if (TryReadFunctionStyleUsing(source, ref i, ref line, usings, usingDirectives))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line, leadingTrivia);
                    parsedPreambleLine = true;
                }
                if (TryReadFunctionStyleBackendDirective(source, ref i, ref line, out string? parsedBackend))
                {
                    // Same consume-even-when-rejecting rule as @namespace (U-09):
                    // a bad or duplicate @backend line must still advance the
                    // cursor or the whole file fails with a misleading UITKX2105.
                    if (parsedBackend != "ugui" && parsedBackend != "uitk")
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2111",
                            Severity = ParseSeverity.Error,
                            SourceLine = line,
                            Message = $"Unknown backend '{parsedBackend}' — @backend accepts 'ugui' or 'uitk' (the default). The directive is ignored.",
                        });
                    }
                    else if (backendName == null)
                    {
                        backendName = parsedBackend == "uitk" ? null : parsedBackend;
                    }
                    else
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2111",
                            Severity = ParseSeverity.Error,
                            SourceLine = line,
                            Message = "Duplicate @backend directive — only one is allowed. The first one is used.",
                        });
                    }
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line, leadingTrivia);
                    parsedPreambleLine = true;
                }
                if (TryReadFunctionStyleUss(source, ref i, ref line, ussFiles))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line, leadingTrivia);
                    parsedPreambleLine = true;
                }
                if (TryReadFunctionStyleNamespaceDirective(source, ref i, ref line, out string? parsedNs))
                {
                    // U-09: a duplicate `@namespace` must still be CONSUMED (else the
                    // preamble loop exits here, leaving the cursor stuck before the second
                    // directive line — the component/hook/module keyword dispatch below then
                    // finds neither, and the whole file fails with a misleading UITKX2105
                    // rather than a targeted "duplicate @namespace" diagnostic).
                    if (inlineNamespace == null)
                    {
                        inlineNamespace = parsedNs;
                    }
                    else
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2105",
                            Severity = ParseSeverity.Error,
                            SourceLine = line,
                            Message = "Duplicate @namespace directive — only one is allowed. The first one is used.",
                        });
                    }
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line, leadingTrivia);
                    parsedPreambleLine = true;
                }
                if (TryReadFunctionStyleNamespaceImport(source, ref i, ref line, usings, usingDirectives))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line, leadingTrivia);
                    parsedPreambleLine = true;
                }
                if (TryReadFunctionStyleImport(source, ref i, ref line, imports))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line, leadingTrivia);
                    parsedPreambleLine = true;
                }
                if (TryReadFunctionStyleStarImport(source, ref i, ref line, imports))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line, leadingTrivia);
                    parsedPreambleLine = true;
                }
                if (TryReadFunctionStyleDefaultImport(source, ref i, ref line, imports))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line, leadingTrivia);
                    parsedPreambleLine = true;
                }
            } while (parsedPreambleLine);

            if (backendName == "ugui" && ussFiles.Count > 0)
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2112",
                    Severity = ParseSeverity.Warning,
                    SourceLine = 1,
                    Message = "@uss has no effect in a '@backend ugui' file — uGUI elements are styled with sprites, colors, and materials, not USS.",
                });
            }

            // Import/export grammar (leg 3): a name imported twice anywhere in the file → UITKX2303
            // (scan-side family diagnostic, per the frozen emit split).
            ReportDuplicateImports(imports, diagnosticBag);

            // ── Keyword dispatch: [export] component / hook / module ─────────
            // Peek past an optional leading `export ` to decide the declaration kind. Hook/module
            // files consume the per-declaration `export` inside their own loop; the component path
            // consumes it here.
            int dispatchPeek = i;
            if (TryReadKeyword(source, ref dispatchPeek, "export"))
                SkipSpaces(source, ref dispatchPeek);

            // ── New-mode dispatch: plain declarations (ES-modules campaign, U-04/U-08) ──
            // The FIRST post-preamble token decides the file's mode. If it is NOT a wrapper
            // keyword, try the plain-declaration file parser; it only commits (returns true)
            // when the very first thing at the cursor is recognizably `export default`,
            // `export { … }`, or a plain declaration head — anything else falls through
            // unchanged to the legacy dispatch/error paths below, preserving byte-identical
            // behavior for non-ES-modules content (garbage input, truly empty preambles that
            // aren't meant as new-mode stub files, etc. — see the reader's own doc comment).
            if (!TryReadKeywordAt(source, dispatchPeek, "hook")
                && !TryReadKeywordAt(source, dispatchPeek, "module")
                && !TryReadKeywordAt(source, dispatchPeek, "component"))
            {
                if (TryParsePlainDeclarationsFile(
                        source, filePath, diagnosticBag, out DirectiveSet plainSet,
                        ref i, ref line, usings, usingDirectives, ussFiles, imports, leadingTrivia,
                        inlineNamespace, useLastReturn))
                {
                    directiveSet = backendName != null
                        ? plainSet with { Backend = backendName }
                        : plainSet;
                    return true;
                }
            }

            if (TryReadKeywordAt(source, dispatchPeek, "hook") || TryReadKeywordAt(source, dispatchPeek, "module"))
            {
                bool hookModuleOk = TryParseHookModuleFile(
                    source, filePath, diagnosticBag, ref directiveSet,
                    ref i, ref line,
                    usings, usingDirectives, ussFiles, inlineNamespace
                );
                // A failed hook/module parse (e.g. `hook 123 {}`) can return false without
                // assigning the set — the enrichment below must not dereference null.
                if (directiveSet != null)
                {
                    directiveSet = directiveSet with
                    {
                        LeadingTrivia = leadingTrivia.ToImmutableArray(),
                        Imports = imports.ToImmutableArray(),
                        UsesLegacySyntax = true,
                    };
                }
                if (backendName != null && directiveSet != null)
                    directiveSet = directiveSet with { Backend = backendName };
                return hookModuleOk;
            }

            bool componentExported = false;
            if (dispatchPeek != i && TryReadKeywordAt(source, dispatchPeek, "component"))
            {
                // A leading `export` applies to this component; consume up to `component`.
                componentExported = true;
                i = dispatchPeek;
            }
            if (!TryReadKeyword(source, ref i, "component"))
                return false;

            // UITKX2320 (deprecation, G-10): every wrapper-keyword declaration in a legacy-mode
            // file warns. This is the file's FIRST declaration — it also sets the file's mode.
            diagnosticBag.Add(new ParseDiagnostic
            {
                Code = "UITKX2320",
                Severity = ParseSeverity.Warning,
                SourceLine = line,
                Message = "the 'component' wrapper keyword is deprecated — write a plain 'export' declaration (the UitkxMigrateImports --es-modules codemod rewrites it); the wrapper is removed in a later minor",
            });

            int componentLine = line;

            SkipSpaces(source, ref i);
            int nameStartI = i; // column anchor — position of first char of component name
            if (!TryReadIdentifier(source, ref i, out string componentName))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2105",
                    Severity = ParseSeverity.Error,
                    SourceLine = componentLine,
                    Message = "Expected PascalCase component name after 'component'.",
                });
                componentName = Path.GetFileNameWithoutExtension(filePath);
            }

            if (!IsPascalCase(componentName))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2100",
                    Severity = ParseSeverity.Error,
                    SourceLine = componentLine,
                    Message = $"Function-style component name '{componentName}' must be PascalCase.",
                });
            }

            string functionNamespace = inlineNamespace ?? FunctionStyleDefaultNamespace;
            int componentNameCol = ColAtPos(source, nameStartI);

            // ── Optional typed-props parameter list ───────────────────────────
            // Supports: component Name(Type param = default, ...)
            SkipSpaces(source, ref i);
            var functionParams = ImmutableArray<FunctionParam>.Empty;
            string? functionPropsTypeName = null;
            if (i < source.Length && source[i] == '(')
            {
                functionParams = ParseFunctionParamList(source, ref i, ref line, componentLine, diagnosticBag);
                if (!functionParams.IsEmpty)
                    functionPropsTypeName = componentName + "Props";
            }

            SkipWhitespaceAndNewlines(source, ref i, ref line);
            if (i >= source.Length || source[i] != '{')
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2105",
                    Severity = ParseSeverity.Error,
                    SourceLine = componentLine,
                    Message = "Expected '{' after function-style component declaration.",
                });

                directiveSet = new DirectiveSet(
                    Namespace: functionNamespace,
                    ComponentName: componentName,
                    PropsTypeName: functionPropsTypeName,
                    DefaultKey: null,
                    Usings: usings.ToImmutableArray(),
                    UssFiles: ussFiles.ToImmutableArray(),
                    Injects: ImmutableArray<(string Type, string Name)>.Empty,
                    MarkupStartLine: componentLine,
                    MarkupStartIndex: source.Length,
                    MarkupEndIndex: source.Length,
                    IsFunctionStyle: true,
                    HasExplicitNamespace: inlineNamespace != null,
                    FunctionSetupCode: string.Empty,
                    FunctionSetupStartLine: componentLine,
                    FunctionParams: functionParams,
                    ComponentDeclarationLine: componentLine,
                    ComponentNameColumn: componentNameCol
                )
                { LeadingTrivia = leadingTrivia.ToImmutableArray(), UsingDirectives = usingDirectives.ToImmutableArray(), UsesLegacySyntax = true };
                if (backendName != null) directiveSet = directiveSet with { Backend = backendName };
                return true;
            }

            int bodyOpen = i;
            if (!TryReadBalancedBlock(source, bodyOpen, out int bodyCloseExclusive))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2105",
                    Severity = ParseSeverity.Error,
                    SourceLine = componentLine,
                    Message = "Unclosed function-style component body. Missing '}'.",
                });

                directiveSet = new DirectiveSet(
                    Namespace: functionNamespace,
                    ComponentName: componentName,
                    PropsTypeName: functionPropsTypeName,
                    DefaultKey: null,
                    Usings: usings.ToImmutableArray(),
                    UssFiles: ussFiles.ToImmutableArray(),
                    Injects: ImmutableArray<(string Type, string Name)>.Empty,
                    MarkupStartLine: componentLine,
                    MarkupStartIndex: source.Length,
                    MarkupEndIndex: source.Length,
                    IsFunctionStyle: true,
                    HasExplicitNamespace: inlineNamespace != null,
                    FunctionSetupCode: string.Empty,
                    FunctionSetupStartLine: componentLine,
                    FunctionParams: functionParams,
                    ComponentDeclarationLine: componentLine,
                    ComponentNameColumn: componentNameCol
                )
                { LeadingTrivia = leadingTrivia.ToImmutableArray(), UsingDirectives = usingDirectives.ToImmutableArray(), UsesLegacySyntax = true };
                if (backendName != null) directiveSet = directiveSet with { Backend = backendName };
                return true;
            }

            int bodyStart = bodyOpen + 1;
            int bodyEndExclusive = bodyCloseExclusive - 1;

            if (
                !TryFindTopLevelReturn(
                    source,
                    bodyStart,
                    bodyEndExclusive,
                    out int returnStart,
                    out int returnOpenParen,
                    out int returnCloseParen,
                    out int returnStmtEndExclusive,
                    useLastReturn
                )
            )
            {
                // Null-only component (React case 2): an explicit top-level
                // `return null;` with no markup return is valid — the component
                // always renders nothing. Only bodies with neither shape error.
                int nullOnlyReturnPos = FindTopLevelReturnNull(source, bodyStart, bodyEndExclusive);
                if (nullOnlyReturnPos < 0)
                {
                    int malformedReturnPos = FindTopLevelReturnAfter(source, bodyStart, bodyEndExclusive);
                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = malformedReturnPos >= 0 ? "UITKX2102" : "UITKX2101",
                        Severity = ParseSeverity.Error,
                        SourceLine = malformedReturnPos >= 0 ? LineAtPos(source, malformedReturnPos) : componentLine,
                        // For UITKX2101 point the squiggle at the component name, matching UITKX0103.
                        SourceColumn = malformedReturnPos >= 0 ? 0 : componentNameCol,
                        EndColumn    = malformedReturnPos >= 0 ? 0 : componentNameCol + componentName.Length,
                        Message = malformedReturnPos >= 0
                            ? "'return' must return UITKX markup using 'return (...)'."
                            : "Function-style component must contain exactly one top-level 'return (...)' statement (or an explicit 'return null;' for a component that renders nothing).",
                    });
                }

                int fseTrimStart1 = FirstNonWhitespaceAt(source, bodyStart);
                var setupMarkupRanges1 = FindJsxBlockRanges(source, bodyStart, bodyEndExclusive);
                var bareJsxRanges1 = FindBareJsxRanges(source, bodyStart, bodyEndExclusive);
                ScanAtExprInSetupCode(source, bodyStart, bodyEndExclusive, diagnosticBag, setupMarkupRanges1, bareJsxRanges1);
                CheckMissingSemicolonAfterJsxParenBlocks(source, setupMarkupRanges1, diagnosticBag);
                directiveSet = new DirectiveSet(
                    Namespace: functionNamespace,
                    ComponentName: componentName,
                    PropsTypeName: functionPropsTypeName,
                    DefaultKey: null,
                    Usings: usings.ToImmutableArray(),
                    UssFiles: ussFiles.ToImmutableArray(),
                    Injects: ImmutableArray<(string Type, string Name)>.Empty,
                    MarkupStartLine: componentLine,
                    MarkupStartIndex: source.Length,
                    MarkupEndIndex: source.Length,
                    IsFunctionStyle: true,
                    FunctionSetupCode: source.Substring(bodyStart, Math.Max(0, bodyEndExclusive - bodyStart)).Trim(),
                    FunctionSetupStartLine: LineAtPos(source, fseTrimStart1),
                    FunctionSetupStartOffset: fseTrimStart1,
                    FunctionParams: functionParams,
                    ComponentDeclarationLine: componentLine,
                    ComponentNameColumn: componentNameCol,
                    HasExplicitNamespace: inlineNamespace != null,
                    SetupCodeMarkupRanges: setupMarkupRanges1,
                    SetupCodeBareJsxRanges: bareJsxRanges1
                )
                {
                    LeadingTrivia = leadingTrivia.ToImmutableArray(),
                    UsingDirectives = usingDirectives.ToImmutableArray(),
                    UsesLegacySyntax = true,
                    HasNullReturn = nullOnlyReturnPos >= 0,
                };
                if (backendName != null) directiveSet = directiveSet with { Backend = backendName };
                return true;
            }

            int markupStart = returnOpenParen + 1;
            int markupEnd = returnCloseParen;
            int markupLine = LineAtPos(source, markupStart);

            if (!LooksLikeMarkupRoot(source, markupStart, markupEnd))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2102",
                    Severity = ParseSeverity.Error,
                    SourceLine = markupLine,
                    Message = "'return' must return UITKX markup.",
                });

                markupStart = markupEnd;
            }

            string setupCode =
                source.Substring(bodyStart, Math.Max(0, returnStart - bodyStart))
                + source.Substring(
                    returnStmtEndExclusive,
                    Math.Max(0, bodyEndExclusive - returnStmtEndExclusive)
                );

            int fseTrimStart2 = FirstNonWhitespaceAt(source, bodyStart);
            int setupGapOffset = returnStart - fseTrimStart2;
            int setupGapLength = returnStmtEndExclusive - returnStart;
            var setupMarkupRanges2 = FindJsxBlockRanges(
                    source,
                    bodyStart,       returnStart,
                    returnStmtEndExclusive, bodyEndExclusive);
            var bareJsxRanges2 = FindBareJsxRanges(
                    source,
                    bodyStart,       returnStart,
                    returnStmtEndExclusive, bodyEndExclusive);

            // Scan setup code ranges for @( tokens — emit UITKX0306 per occurrence,
            // but skip @( inside embedded JSX markup where it is re-parsed by UitkxParser
            // (which emits its own UITKX0306 from the markup-context branch).
            ScanAtExprInSetupCode(source, bodyStart, returnStart, diagnosticBag, setupMarkupRanges2, bareJsxRanges2);
            ScanAtExprInSetupCode(source, returnStmtEndExclusive, bodyEndExclusive, diagnosticBag, setupMarkupRanges2, bareJsxRanges2);

            CheckMissingSemicolonAfterJsxParenBlocks(source, setupMarkupRanges2, diagnosticBag);
            directiveSet = new DirectiveSet(
                Namespace: functionNamespace,
                ComponentName: componentName,
                PropsTypeName: functionPropsTypeName,
                DefaultKey: null,
                Usings: usings.ToImmutableArray(),
                UssFiles: ussFiles.ToImmutableArray(),
                Injects: ImmutableArray<(string Type, string Name)>.Empty,
                MarkupStartLine: markupLine,
                MarkupStartIndex: markupStart,
                MarkupEndIndex: markupEnd,
                IsFunctionStyle: true,
                FunctionSetupCode: setupCode.Trim(),
                FunctionSetupStartLine: LineAtPos(source, fseTrimStart2),
                FunctionSetupStartOffset: fseTrimStart2,
                FunctionParams: functionParams,
                ComponentDeclarationLine: componentLine,
                ComponentNameColumn: componentNameCol,
                HasExplicitNamespace: inlineNamespace != null,
                FunctionReturnEndLine: LineAtPos(source, returnStmtEndExclusive - 1),
                FunctionBodyEndLine: LineAtPos(source, bodyEndExclusive),
                SetupCodeMarkupRanges: setupMarkupRanges2,
                SetupCodeBareJsxRanges: bareJsxRanges2,
                FunctionSetupGapOffset: setupGapOffset,
                FunctionSetupGapLength: setupGapLength
            )
            {
                LeadingTrivia = leadingTrivia.ToImmutableArray(),
                UsingDirectives = usingDirectives.ToImmutableArray(),
                Imports = imports.ToImmutableArray(),
                ComponentDeclarations = ImmutableArray.Create(
                    new ComponentDeclaration(
                        Name: componentName,
                        IsExported: componentExported,
                        PropsTypeName: functionPropsTypeName,
                        DefaultKey: null,
                        FunctionParams: functionParams,
                        FunctionSetupCode: setupCode.Trim(),
                        FunctionSetupStartLine: LineAtPos(source, fseTrimStart2),
                        FunctionSetupStartOffset: fseTrimStart2,
                        MarkupStartLine: markupLine,
                        MarkupStartIndex: markupStart,
                        MarkupEndIndex: markupEnd,
                        DeclarationLine: componentLine,
                        NameColumn: componentNameCol,
                        ReturnEndLine: LineAtPos(source, returnStmtEndExclusive - 1),
                        BodyEndLine: LineAtPos(source, bodyEndExclusive))
                    {
                        SetupCodeMarkupRanges = setupMarkupRanges2,
                        SetupCodeBareJsxRanges = bareJsxRanges2,
                        FunctionSetupGapOffset = setupGapOffset,
                        FunctionSetupGapLength = setupGapLength,
                    }),
                UsesLegacySyntax = true,
            };

            // ── Continuation: additional [export] component/hook/module decls ──
            // Mixed-decl v1 (leg 3): a file is a SEQUENCE of declarations in any order.
            // Parse every subsequent top-level declaration into the plural arrays. The
            // historical "one component per file" limitation was the trailing-2105 error
            // below; a valid second declaration is now ACCEPTED (TD-02 family reconcile).
            // A trailing NON-declaration keeps the historical diagnostics
            // (2309 import-after-decl, 2104 directive-header-mix, 2105 invalid statement).
            i = bodyCloseExclusive;
            line = LineAtPos(source, bodyCloseExclusive);
            var tailComponents = new List<ComponentDeclaration>();
            var tailHooks = new List<HookDeclaration>();
            var tailModules = new List<ModuleDeclaration>();
            while (true)
            {
                SkipLeadingFunctionStyleTrivia(source, ref i, ref line);
                if (i >= source.Length)
                    break;

                bool tailExported = false;
                {
                    int afterExport = i;
                    if (TryReadKeyword(source, ref afterExport, "export"))
                    {
                        SkipSpaces(source, ref afterExport);
                        if (TryReadKeywordAt(source, afterExport, "component")
                            || TryReadKeywordAt(source, afterExport, "hook")
                            || TryReadKeywordAt(source, afterExport, "module"))
                        {
                            tailExported = true;
                            i = afterExport;
                        }
                    }
                }

                if (TryReadKeywordAt(source, i, "component"))
                {
                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = "UITKX2320",
                        Severity = ParseSeverity.Warning,
                        SourceLine = line,
                        Message = "the 'component' wrapper keyword is deprecated — write a plain 'export' declaration (the UitkxMigrateImports --es-modules codemod rewrites it); the wrapper is removed in a later minor",
                    });
                    var tailDecl = ParseSingleComponent(
                        source, filePath, diagnosticBag,
                        ref i, ref line, tailExported, useLastReturn, out bool tailHardStop);
                    if (tailDecl != null)
                        tailComponents.Add(tailDecl);
                    if (tailHardStop)
                        break;
                }
                else if (TryReadKeywordAt(source, i, "hook"))
                {
                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = "UITKX2320",
                        Severity = ParseSeverity.Warning,
                        SourceLine = line,
                        Message = "the 'hook' wrapper keyword is deprecated — write a plain 'export' declaration (the UitkxMigrateImports --es-modules codemod rewrites it); the wrapper is removed in a later minor",
                    });
                    TryReadKeyword(source, ref i, "hook");
                    ParseSingleHook(source, filePath, diagnosticBag, tailHooks, ref i, ref line, tailExported);
                }
                else if (TryReadKeywordAt(source, i, "module"))
                {
                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = "UITKX2320",
                        Severity = ParseSeverity.Warning,
                        SourceLine = line,
                        Message = "the 'module' wrapper keyword is deprecated — write a plain 'export' declaration (the UitkxMigrateImports --es-modules codemod rewrites it); the wrapper is removed in a later minor",
                    });
                    TryReadKeyword(source, ref i, "module");
                    ParseSingleModule(source, filePath, diagnosticBag, tailModules, ref i, ref line, tailExported);
                }
                else
                {
                    if (TryReadKeywordAt(source, i, "import"))
                    {
                        // Frozen family code: imports are preamble-only.
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2309",
                            Severity = ParseSeverity.Error,
                            SourceLine = LineAtPos(source, i),
                            Message = "import must appear in the preamble, before the first declaration",
                        });
                    }
                    else if (IsDirectiveHeaderAt(source, i))
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2104",
                            Severity = ParseSeverity.Error,
                            SourceLine = LineAtPos(source, i),
                            Message = "Function-style form cannot be mixed with directive header form.",
                        });
                    }
                    else if (LooksLikePlainDeclarationHeadAt(source, i))
                    {
                        // U-08 mirror direction: a PLAIN declaration after legacy wrapper
                        // declarations is the same mixed-mode error as a wrapper after plain.
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2108",
                            Severity = ParseSeverity.Error,
                            SourceLine = LineAtPos(source, i),
                            Message = "legacy wrapper declarations and plain declarations cannot be mixed in one file — the file's first declaration sets its style",
                        });
                    }
                    else
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2105",
                            Severity = ParseSeverity.Error,
                            SourceLine = LineAtPos(source, i),
                            Message = "Invalid top-level statement after function-style component declaration.",
                        });
                    }
                    break;
                }
            }

            if (tailComponents.Count > 0 || tailHooks.Count > 0 || tailModules.Count > 0)
            {
                directiveSet = directiveSet with
                {
                    ComponentDeclarations = directiveSet.ComponentDeclarations.AddRange(tailComponents),
                    HookDeclarations = tailHooks.ToImmutableArray(),
                    ModuleDeclarations = tailModules.ToImmutableArray(),
                };
            }

            if (backendName != null) directiveSet = directiveSet with { Backend = backendName };
            return true;
        }

        /// <summary>
        /// Parses a single <c>component</c> declaration at the cursor (which must be at the
        /// <c>component</c> keyword; the optional <c>export</c> prefix is consumed by the caller
        /// and passed via <paramref name="isExported"/>). Captures the full setup/markup span set
        /// into a <see cref="ComponentDeclaration"/> and advances <paramref name="i"/> past the
        /// component body. Returns <c>null</c> on a structural error that prevents forming a
        /// declaration; sets <paramref name="hardStop"/> when the error is terminal for the rest
        /// of the file (missing/unbalanced braces) so the declaration loop must stop.
        ///
        /// This mirrors <see cref="ParseSingleHook"/> / <see cref="ParseSingleModule"/> for the
        /// mixed-decl v1 continuation loop (leg 3). The FIRST component of a component-first file
        /// is still parsed inline by <see cref="TryParseFunctionStyle"/> (it also populates the
        /// singular back-compat <see cref="DirectiveSet"/> fields); this handles the 2nd+ ones.
        /// </summary>
        private static ComponentDeclaration? ParseSingleComponent(
            string source,
            string filePath,
            List<ParseDiagnostic> diagnosticBag,
            ref int i,
            ref int line,
            bool isExported,
            bool useLastReturn,
            out bool hardStop)
        {
            hardStop = false;
            if (!TryReadKeyword(source, ref i, "component"))
            {
                hardStop = true;
                return null;
            }

            int componentLine = line;

            SkipSpaces(source, ref i);
            int nameStartI = i;
            if (!TryReadIdentifier(source, ref i, out string componentName))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2105",
                    Severity = ParseSeverity.Error,
                    SourceLine = componentLine,
                    Message = "Expected PascalCase component name after 'component'.",
                });
                componentName = Path.GetFileNameWithoutExtension(filePath);
            }

            if (!IsPascalCase(componentName))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2100",
                    Severity = ParseSeverity.Error,
                    SourceLine = componentLine,
                    Message = $"Function-style component name '{componentName}' must be PascalCase.",
                });
            }

            int componentNameCol = ColAtPos(source, nameStartI);

            SkipSpaces(source, ref i);
            var functionParams = ImmutableArray<FunctionParam>.Empty;
            string? functionPropsTypeName = null;
            if (i < source.Length && source[i] == '(')
            {
                functionParams = ParseFunctionParamList(source, ref i, ref line, componentLine, diagnosticBag);
                if (!functionParams.IsEmpty)
                    functionPropsTypeName = componentName + "Props";
            }

            return ParseComponentBodyAt(
                source, diagnosticBag, componentName, isExported, functionParams, functionPropsTypeName,
                componentLine, componentNameCol, ref i, ref line, useLastReturn, out hardStop);
        }

        /// <summary>
        /// Parses a component BODY at the cursor (which must be positioned right after the
        /// component's name/parameter-list header — at the <c>{</c>, or the whitespace before it).
        /// Shared by <see cref="ParseSingleComponent"/> (legacy <c>component Name(...) {...}</c>
        /// header) and the plain-declaration path (ES-modules campaign, U-04: <c>export VirtualNode
        /// Name(...) {...}</c> header) — the body machinery (<see cref="TryFindTopLevelReturn"/>,
        /// setup-code split, markup ranges) is IDENTICAL regardless of which header form produced
        /// the name/params/declaration line/name column passed in here.
        /// </summary>
        private static ComponentDeclaration? ParseComponentBodyAt(
            string source,
            List<ParseDiagnostic> diagnosticBag,
            string componentName,
            bool isExported,
            ImmutableArray<FunctionParam> functionParams,
            string? functionPropsTypeName,
            int componentLine,
            int componentNameCol,
            ref int i,
            ref int line,
            bool useLastReturn,
            out bool hardStop)
        {
            hardStop = false;

            SkipWhitespaceAndNewlines(source, ref i, ref line);
            if (i >= source.Length || source[i] != '{')
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2105",
                    Severity = ParseSeverity.Error,
                    SourceLine = componentLine,
                    Message = "Expected '{' after function-style component declaration.",
                });
                hardStop = true;
                return null;
            }

            int bodyOpen = i;
            if (!TryReadBalancedBlock(source, bodyOpen, out int bodyCloseExclusive))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2105",
                    Severity = ParseSeverity.Error,
                    SourceLine = componentLine,
                    Message = "Unclosed function-style component body. Missing '}'.",
                });
                hardStop = true;
                return null;
            }

            int bodyStart = bodyOpen + 1;
            int bodyEndExclusive = bodyCloseExclusive - 1;

            if (
                !TryFindTopLevelReturn(
                    source,
                    bodyStart,
                    bodyEndExclusive,
                    out int returnStart,
                    out int returnOpenParen,
                    out int returnCloseParen,
                    out int returnStmtEndExclusive,
                    useLastReturn
                )
            )
            {
                // Null-only component (React case 2): an explicit top-level
                // `return null;` with no markup return is valid — the component
                // always renders nothing. Only bodies with neither shape error.
                int nullOnlyReturnPos = FindTopLevelReturnNull(source, bodyStart, bodyEndExclusive);
                if (nullOnlyReturnPos < 0)
                {
                    int malformedReturnPos = FindTopLevelReturnAfter(source, bodyStart, bodyEndExclusive);
                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = malformedReturnPos >= 0 ? "UITKX2102" : "UITKX2101",
                        Severity = ParseSeverity.Error,
                        SourceLine = malformedReturnPos >= 0 ? LineAtPos(source, malformedReturnPos) : componentLine,
                        SourceColumn = malformedReturnPos >= 0 ? 0 : componentNameCol,
                        EndColumn    = malformedReturnPos >= 0 ? 0 : componentNameCol + componentName.Length,
                        Message = malformedReturnPos >= 0
                            ? "'return' must return UITKX markup using 'return (...)'."
                            : "Function-style component must contain exactly one top-level 'return (...)' statement (or an explicit 'return null;' for a component that renders nothing).",
                    });
                }

                int fseTrimStart1 = FirstNonWhitespaceAt(source, bodyStart);
                var setupMarkupRanges1 = FindJsxBlockRanges(source, bodyStart, bodyEndExclusive);
                var bareJsxRanges1 = FindBareJsxRanges(source, bodyStart, bodyEndExclusive);
                ScanAtExprInSetupCode(source, bodyStart, bodyEndExclusive, diagnosticBag, setupMarkupRanges1, bareJsxRanges1);
                CheckMissingSemicolonAfterJsxParenBlocks(source, setupMarkupRanges1, diagnosticBag);
                i = bodyCloseExclusive;
                line = LineAtPos(source, i);
                return new ComponentDeclaration(
                    Name: componentName,
                    IsExported: isExported,
                    PropsTypeName: functionPropsTypeName,
                    DefaultKey: null,
                    FunctionParams: functionParams,
                    FunctionSetupCode: source.Substring(bodyStart, Math.Max(0, bodyEndExclusive - bodyStart)).Trim(),
                    FunctionSetupStartLine: LineAtPos(source, fseTrimStart1),
                    FunctionSetupStartOffset: fseTrimStart1,
                    MarkupStartLine: componentLine,
                    MarkupStartIndex: source.Length,
                    MarkupEndIndex: source.Length,
                    DeclarationLine: componentLine,
                    NameColumn: componentNameCol,
                    ReturnEndLine: -1,
                    BodyEndLine: LineAtPos(source, bodyEndExclusive))
                {
                    SetupCodeMarkupRanges = setupMarkupRanges1,
                    SetupCodeBareJsxRanges = bareJsxRanges1,
                    HasNullReturn = nullOnlyReturnPos >= 0,
                };
            }

            int markupStart = returnOpenParen + 1;
            int markupEnd = returnCloseParen;
            int markupLine = LineAtPos(source, markupStart);

            if (!LooksLikeMarkupRoot(source, markupStart, markupEnd))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2102",
                    Severity = ParseSeverity.Error,
                    SourceLine = markupLine,
                    Message = "'return' must return UITKX markup.",
                });
                markupStart = markupEnd;
            }

            string setupCode =
                source.Substring(bodyStart, Math.Max(0, returnStart - bodyStart))
                + source.Substring(
                    returnStmtEndExclusive,
                    Math.Max(0, bodyEndExclusive - returnStmtEndExclusive)
                );

            int fseTrimStart2 = FirstNonWhitespaceAt(source, bodyStart);
            int setupGapOffset = returnStart - fseTrimStart2;
            int setupGapLength = returnStmtEndExclusive - returnStart;
            var setupMarkupRanges2 = FindJsxBlockRanges(
                    source,
                    bodyStart,       returnStart,
                    returnStmtEndExclusive, bodyEndExclusive);
            var bareJsxRanges2 = FindBareJsxRanges(
                    source,
                    bodyStart,       returnStart,
                    returnStmtEndExclusive, bodyEndExclusive);

            ScanAtExprInSetupCode(source, bodyStart, returnStart, diagnosticBag, setupMarkupRanges2, bareJsxRanges2);
            ScanAtExprInSetupCode(source, returnStmtEndExclusive, bodyEndExclusive, diagnosticBag, setupMarkupRanges2, bareJsxRanges2);
            CheckMissingSemicolonAfterJsxParenBlocks(source, setupMarkupRanges2, diagnosticBag);

            i = bodyCloseExclusive;
            line = LineAtPos(source, i);
            return new ComponentDeclaration(
                Name: componentName,
                IsExported: isExported,
                PropsTypeName: functionPropsTypeName,
                DefaultKey: null,
                FunctionParams: functionParams,
                FunctionSetupCode: setupCode.Trim(),
                FunctionSetupStartLine: LineAtPos(source, fseTrimStart2),
                FunctionSetupStartOffset: fseTrimStart2,
                MarkupStartLine: markupLine,
                MarkupStartIndex: markupStart,
                MarkupEndIndex: markupEnd,
                DeclarationLine: componentLine,
                NameColumn: componentNameCol,
                ReturnEndLine: LineAtPos(source, returnStmtEndExclusive - 1),
                BodyEndLine: LineAtPos(source, bodyEndExclusive))
            {
                SetupCodeMarkupRanges = setupMarkupRanges2,
                SetupCodeBareJsxRanges = bareJsxRanges2,
                FunctionSetupGapOffset = setupGapOffset,
                FunctionSetupGapLength = setupGapLength,
            };
        }

        // ── Import preamble reader (import/export grammar, leg 3) ─────────────

        /// <summary>
        /// Reads a single preamble <c>import { A, B } from "specifier"</c> line at the cursor,
        /// appending an <see cref="ImportDeclaration"/> to <paramref name="imports"/> and advancing
        /// the cursor past it. Named imports only; single-line; extensionless specifier captured
        /// verbatim (resolution/validation is a later stage). Returns false (restoring the cursor)
        /// when the cursor is not at a well-formed <c>import</c> line, so the caller's dispatch can
        /// report it as it would any non-declaration.
        /// </summary>
        private static bool TryReadFunctionStyleImport(
            string source, ref int i, ref int line, List<ImportDeclaration> imports)
        {
            if (!TryReadKeywordAt(source, i, "import"))
                return false;

            int savedI = i, savedLine = line;
            int importLine = line;
            int importCol = ColAtPos(source, i);

            i += "import".Length;
            SkipSpaces(source, ref i);
            if (i >= source.Length || source[i] != '{')
            {
                i = savedI; line = savedLine; return false;
            }
            i++; // past '{'

            var names = new List<string>();
            var nameCols = new List<int>();
            var aliases = new List<string?>();
            while (true)
            {
                SkipSpaces(source, ref i);
                if (i < source.Length && source[i] == '}') { i++; break; }
                int nameCol = ColAtPos(source, i);
                if (!TryReadIdentifier(source, ref i, out string name))
                {
                    i = savedI; line = savedLine; return false;
                }
                names.Add(name);
                nameCols.Add(nameCol);
                SkipSpaces(source, ref i);
                // Rename-on-import (G-05): `import { a as b }`.
                string? alias = null;
                if (TryReadKeyword(source, ref i, "as"))
                {
                    SkipSpaces(source, ref i);
                    if (!TryReadIdentifier(source, ref i, out string aliasName))
                    {
                        i = savedI; line = savedLine; return false;
                    }
                    alias = aliasName;
                    SkipSpaces(source, ref i);
                }
                aliases.Add(alias);
                if (i < source.Length && source[i] == ',') { i++; continue; }
                if (i < source.Length && source[i] == '}') { i++; break; }
                i = savedI; line = savedLine; return false;
            }

            SkipSpaces(source, ref i);
            if (!TryReadKeyword(source, ref i, "from"))
            {
                i = savedI; line = savedLine; return false;
            }
            SkipSpaces(source, ref i);
            if (i >= source.Length || source[i] != '"')
            {
                i = savedI; line = savedLine; return false;
            }
            int specQuoteCol = ColAtPos(source, i);
            i++; // past opening quote
            int specStart = i;
            while (i < source.Length && source[i] != '"' && source[i] != '\n')
                i++;
            if (i >= source.Length || source[i] != '"')
            {
                i = savedI; line = savedLine; return false; // unterminated specifier
            }
            string specifier = source.Substring(specStart, i - specStart);
            i++; // past closing quote

            // Skip to end of line + newline (parity with the other preamble readers:
            // the namespace-import form and `@using` both consume the rest of the line).
            // In particular this tolerates the JS-canonical trailing `;` — without it the
            // cursor stalls on the `;`, the preamble loop exits, and the whole file fails
            // with a misleading UITKX2105 (same pathology as the U-09 duplicate-@namespace
            // case). The formatter re-emits the canonical, semicolon-less form.
            while (i < source.Length && !IsNewline(source[i]))
                i++;
            if (i < source.Length && IsNewline(source[i]))
                ConsumeNewline(source, ref i, ref line);

            imports.Add(new ImportDeclaration(
                names.ToImmutableArray(),
                specifier,
                importLine,
                importCol,
                nameCols.ToImmutableArray(),
                specQuoteCol,
                Aliases: aliases.ToImmutableArray()));
            return true;
        }

        /// <summary>
        /// Reads a namespace-import line <c>import * as X from "specifier"</c> (G-05) at the
        /// cursor. Same cursor-restore + trailing-line-tolerance discipline as the sibling readers.
        /// </summary>
        private static bool TryReadFunctionStyleStarImport(
            string source, ref int i, ref int line, List<ImportDeclaration> imports)
        {
            if (!TryReadKeywordAt(source, i, "import"))
                return false;

            int savedI = i, savedLine = line;
            int importLine = line;
            int importCol = ColAtPos(source, i);

            i += "import".Length;
            SkipSpaces(source, ref i);
            if (i >= source.Length || source[i] != '*')
            {
                i = savedI; line = savedLine; return false;
            }
            i++; // past '*'
            SkipSpaces(source, ref i);
            if (!TryReadKeyword(source, ref i, "as"))
            {
                i = savedI; line = savedLine; return false;
            }
            SkipSpaces(source, ref i);
            if (!TryReadIdentifier(source, ref i, out string starAlias))
            {
                i = savedI; line = savedLine; return false;
            }
            SkipSpaces(source, ref i);
            if (!TryReadKeyword(source, ref i, "from"))
            {
                i = savedI; line = savedLine; return false;
            }
            SkipSpaces(source, ref i);
            if (i >= source.Length || source[i] != '"')
            {
                i = savedI; line = savedLine; return false;
            }
            int specQuoteCol = ColAtPos(source, i);
            i++; // past opening quote
            int specStart = i;
            while (i < source.Length && source[i] != '"' && source[i] != '\n')
                i++;
            if (i >= source.Length || source[i] != '"')
            {
                i = savedI; line = savedLine; return false; // unterminated specifier
            }
            string specifier = source.Substring(specStart, i - specStart);
            i++; // past closing quote

            while (i < source.Length && !IsNewline(source[i]))
                i++;
            if (i < source.Length && IsNewline(source[i]))
                ConsumeNewline(source, ref i, ref line);

            imports.Add(new ImportDeclaration(
                ImmutableArray<string>.Empty,
                specifier,
                importLine,
                importCol,
                ImmutableArray<int>.Empty,
                specQuoteCol,
                Aliases: ImmutableArray<string?>.Empty,
                IsStar: true,
                StarAlias: starAlias));
            return true;
        }

        /// <summary>
        /// Reads a default-import line <c>import X from "specifier"</c> (G-05) at the cursor. Must
        /// be tried AFTER the named/namespace/star readers so its bare-identifier lookahead does
        /// not shadow them (they lead with <c>{</c>, <c>"</c>, and <c>*</c> respectively).
        /// </summary>
        private static bool TryReadFunctionStyleDefaultImport(
            string source, ref int i, ref int line, List<ImportDeclaration> imports)
        {
            if (!TryReadKeywordAt(source, i, "import"))
                return false;

            int savedI = i, savedLine = line;
            int importLine = line;
            int importCol = ColAtPos(source, i);

            i += "import".Length;
            SkipSpaces(source, ref i);
            if (i >= source.Length || source[i] == '{' || source[i] == '"' || source[i] == '*')
            {
                i = savedI; line = savedLine; return false;
            }
            if (!TryReadIdentifier(source, ref i, out string defaultAlias))
            {
                i = savedI; line = savedLine; return false;
            }
            SkipSpaces(source, ref i);

            // ES combined forms (field find, 0.9.0 F5 battery): `import Def, { a, b } from`
            // and `import Def, * as X from` — one ImportDeclaration carrying the default
            // binding PLUS the named/star surface (the record models them independently).
            var combinedNames = new List<string>();
            var combinedNameCols = new List<int>();
            var combinedAliases = new List<string?>();
            bool combinedStar = false;
            string? combinedStarAlias = null;
            if (i < source.Length && source[i] == ',')
            {
                i++;
                SkipSpaces(source, ref i);
                if (i < source.Length && source[i] == '{')
                {
                    i++; // past '{'
                    while (true)
                    {
                        SkipSpaces(source, ref i);
                        if (i < source.Length && source[i] == '}') { i++; break; }
                        int nameCol = ColAtPos(source, i);
                        if (!TryReadIdentifier(source, ref i, out string name))
                        {
                            i = savedI; line = savedLine; return false;
                        }
                        combinedNames.Add(name);
                        combinedNameCols.Add(nameCol);
                        SkipSpaces(source, ref i);
                        string? alias = null;
                        if (TryReadKeyword(source, ref i, "as"))
                        {
                            SkipSpaces(source, ref i);
                            if (!TryReadIdentifier(source, ref i, out string aliasName))
                            {
                                i = savedI; line = savedLine; return false;
                            }
                            alias = aliasName;
                            SkipSpaces(source, ref i);
                        }
                        combinedAliases.Add(alias);
                        if (i < source.Length && source[i] == ',') { i++; continue; }
                        if (i < source.Length && source[i] == '}') { i++; break; }
                        i = savedI; line = savedLine; return false;
                    }
                }
                else if (i < source.Length && source[i] == '*')
                {
                    i++;
                    SkipSpaces(source, ref i);
                    if (!TryReadKeyword(source, ref i, "as"))
                    {
                        i = savedI; line = savedLine; return false;
                    }
                    SkipSpaces(source, ref i);
                    if (!TryReadIdentifier(source, ref i, out string starAliasName))
                    {
                        i = savedI; line = savedLine; return false;
                    }
                    combinedStar = true;
                    combinedStarAlias = starAliasName;
                }
                else
                {
                    i = savedI; line = savedLine; return false;
                }
                SkipSpaces(source, ref i);
            }

            if (!TryReadKeyword(source, ref i, "from"))
            {
                i = savedI; line = savedLine; return false;
            }
            SkipSpaces(source, ref i);
            if (i >= source.Length || source[i] != '"')
            {
                i = savedI; line = savedLine; return false;
            }
            int specQuoteCol = ColAtPos(source, i);
            i++; // past opening quote
            int specStart = i;
            while (i < source.Length && source[i] != '"' && source[i] != '\n')
                i++;
            if (i >= source.Length || source[i] != '"')
            {
                i = savedI; line = savedLine; return false; // unterminated specifier
            }
            string specifier = source.Substring(specStart, i - specStart);
            i++; // past closing quote

            while (i < source.Length && !IsNewline(source[i]))
                i++;
            if (i < source.Length && IsNewline(source[i]))
                ConsumeNewline(source, ref i, ref line);

            imports.Add(new ImportDeclaration(
                combinedNames.ToImmutableArray(),
                specifier,
                importLine,
                importCol,
                combinedNameCols.ToImmutableArray(),
                specQuoteCol,
                Aliases: combinedAliases.ToImmutableArray(),
                IsStar: combinedStar,
                StarAlias: combinedStarAlias,
                IsDefault: true,
                DefaultAlias: defaultAlias));
            return true;
        }

        /// <summary>
        /// Reads a namespace-import line <c>import "@Namespace"</c> at the cursor (namespace-import
        /// unification plan) and desugars it to a <see cref="UsingDirective"/> — the same model a
        /// <c>@using</c> line produces, so every emitter/HMR path downstream is unaffected. The
        /// leading <c>@</c> inside the string is the disambiguator from a file specifier (<c>import
        /// { … } from "…"</c>) and is stripped from the payload. Accepts the full <c>@using</c>
        /// payload grammar for round-trip parity: plain <c>@Ns</c>, <c>@static Type</c>, and
        /// <c>@Alias = Ns.Type</c>. Returns false (restoring the cursor) when the cursor is not at a
        /// well-formed namespace-import, so the caller's dispatch handles it as usual.
        /// </summary>
        private static bool TryReadFunctionStyleNamespaceImport(
            string source, ref int i, ref int line,
            List<string> usings, List<UsingDirective> usingDirectives)
        {
            if (!TryReadKeywordAt(source, i, "import"))
                return false;

            int savedI = i, savedLine = line;
            int importLine = line;
            int importCol = ColAtPos(source, i);

            i += "import".Length;
            SkipSpaces(source, ref i);
            // A file import continues with `{`; a namespace import continues with a `"@…"` string.
            if (i >= source.Length || source[i] != '"')
            {
                i = savedI; line = savedLine; return false;
            }
            i++; // past opening quote
            if (i >= source.Length || source[i] != '@')
            {
                // A bare `import "Ns"` (no @) is reserved/ambiguous — not a namespace import.
                i = savedI; line = savedLine; return false;
            }
            i++; // past the '@' sigil
            int payloadStart = i;
            while (i < source.Length && source[i] != '"' && source[i] != '\n')
                i++;
            if (i >= source.Length || source[i] != '"')
            {
                i = savedI; line = savedLine; return false; // unterminated
            }
            string payload = source.Substring(payloadStart, i - payloadStart).Trim();
            i++; // past closing quote

            if (payload.Length == 0)
            {
                i = savedI; line = savedLine; return false; // `import "@"` is not a namespace import
            }

            // Skip to end of line + newline (parity with the other preamble readers).
            while (i < source.Length && !IsNewline(source[i]))
                i++;
            if (i < source.Length && IsNewline(source[i]))
                ConsumeNewline(source, ref i, ref line);

            usings.Add(payload);
            usingDirectives.Add(new UsingDirective(
                Payload: payload,
                Line: importLine,
                Column: importCol,
                PayloadColumn: ColAtPos(source, payloadStart),
                FromImportSyntax: true));
            return true;
        }

        /// <summary>
        /// UITKX2303: a name imported more than once anywhere in the file. Keyed on the imported
        /// NAME (the frozen family semantics — duplicate binding, not duplicate specifier).
        /// </summary>
        private static void ReportDuplicateImports(
            List<ImportDeclaration> imports, List<ParseDiagnostic> diagnosticBag)
        {
            if (imports.Count == 0) return;
            var seen = new System.Collections.Generic.Dictionary<string, (string Spec, bool WasAlias)>(System.StringComparer.Ordinal);
            foreach (var imp in imports)
            {
                for (int k = 0; k < imp.Names.Length; k++)
                {
                    // G-05: duplicate-import keying is on the BOUND name — `import { a as b }`
                    // collides with another `b`, not with another `a`.
                    string? alias = k < imp.Aliases.Length ? imp.Aliases[k] : null;
                    string bound = alias ?? imp.Names[k];
                    int nameCol = k < imp.NameColumns.Length ? imp.NameColumns[k] : imp.Column;
                    ReportIfDuplicateImportBinding(bound, alias != null, imp, nameCol, diagnosticBag, seen);
                }
                if (imp.IsStar && imp.StarAlias != null)
                    ReportIfDuplicateImportBinding(imp.StarAlias, true, imp, imp.Column, diagnosticBag, seen);
                if (imp.IsDefault && imp.DefaultAlias != null)
                    ReportIfDuplicateImportBinding(imp.DefaultAlias, true, imp, imp.Column, diagnosticBag, seen);
            }
        }

        private static void ReportIfDuplicateImportBinding(
            string bound, bool isAlias, ImportDeclaration imp, int col,
            List<ParseDiagnostic> diagnosticBag,
            System.Collections.Generic.Dictionary<string, (string Spec, bool WasAlias)> seen)
        {
            if (seen.TryGetValue(bound, out var first))
            {
                // Family §3.1: a collision with an ALIAS binding on either side is UITKX2325's
                // "another import" arm; a plain name imported twice stays classic 2303.
                bool aliasInvolved = isAlias || first.WasAlias;
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = aliasInvolved ? "UITKX2325" : "UITKX2303",
                    Severity = ParseSeverity.Error,
                    SourceLine = imp.Line,
                    SourceColumn = col,
                    EndLine = imp.Line,
                    EndColumn = col + bound.Length,
                    Message = aliasInvolved
                        ? $"import alias '{bound}' collides with another import — rename the import"
                        : $"duplicate import of `{bound}` (already imported from {first.Spec})",
                });
            }
            else
            {
                seen[bound] = (imp.Specifier, isAlias);
            }
        }

        // ── Hook / Module file parser ─────────────────────────────────────────

        /// <summary>
        /// Parses a .uitkx file containing one or more <c>hook</c> and/or
        /// <c>module</c> declarations (no component).
        /// Called from <see cref="TryParseFunctionStyle"/> when the first
        /// top-level keyword after the preamble is <c>hook</c> or <c>module</c>.
        /// </summary>
        private static bool TryParseHookModuleFile(
            string source,
            string filePath,
            List<ParseDiagnostic> diagnosticBag,
            ref DirectiveSet directiveSet,
            ref int i,
            ref int line,
            List<string> usings,
            List<UsingDirective> usingDirectives,
            List<string> ussFiles,
            string? inlineNamespace
        )
        {
            string functionNamespace = inlineNamespace ?? FunctionStyleDefaultNamespace;

            // @uss is legal in ANY file (import/export grammar, leg 3, §5): a stylesheet attaches to
            // every component declared in the file. A hook/module-only file has no component, so the
            // sheet attaches to nothing — a lint-tier concern (2313 "uss without component"), not the
            // hard 2210 error it used to be.

            var hooks = new List<HookDeclaration>();
            var modules = new List<ModuleDeclaration>();

            // Parse multiple declarations in sequence
            while (i < source.Length)
            {
                SkipLeadingFunctionStyleTrivia(source, ref i, ref line);
                if (i >= source.Length) break;

                // Optional per-declaration `export` prefix (import/export grammar, leg 3).
                bool declExported = false;
                {
                    int afterExport = i;
                    if (TryReadKeyword(source, ref afterExport, "export"))
                    {
                        SkipSpaces(source, ref afterExport);
                        if (TryReadKeywordAt(source, afterExport, "hook") ||
                            TryReadKeywordAt(source, afterExport, "module"))
                        {
                            declExported = true;
                            i = afterExport;
                        }
                    }
                }

                if (TryReadKeywordAt(source, i, "hook"))
                {
                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = "UITKX2320",
                        Severity = ParseSeverity.Warning,
                        SourceLine = line,
                        Message = "the 'hook' wrapper keyword is deprecated — write a plain 'export' declaration (the UitkxMigrateImports --es-modules codemod rewrites it); the wrapper is removed in a later minor",
                    });
                    TryReadKeyword(source, ref i, "hook");
                    ParseSingleHook(source, filePath, diagnosticBag, hooks, ref i, ref line, declExported);
                }
                else if (TryReadKeywordAt(source, i, "module"))
                {
                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = "UITKX2320",
                        Severity = ParseSeverity.Warning,
                        SourceLine = line,
                        Message = "the 'module' wrapper keyword is deprecated — write a plain 'export' declaration (the UitkxMigrateImports --es-modules codemod rewrites it); the wrapper is removed in a later minor",
                    });
                    TryReadKeyword(source, ref i, "module");
                    ParseSingleModule(source, filePath, diagnosticBag, modules, ref i, ref line, declExported);
                }
                else
                {
                    break; // unknown content after declarations
                }
            }

            if (hooks.Count == 0 && modules.Count == 0)
                return false;

            // Check for trailing content after declarations
            if (TryFindNextNonWhitespace(source, i, out int trailingPos))
            {
                bool plainHead = LooksLikePlainDeclarationHeadAt(source, trailingPos);
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = plainHead ? "UITKX2108" : "UITKX2105",
                    Severity = ParseSeverity.Error,
                    SourceLine = LineAtPos(source, trailingPos),
                    Message = plainHead
                        ? "legacy wrapper declarations and plain declarations cannot be mixed in one file — the file's first declaration sets its style"
                        : "Invalid top-level statement after hook/module declarations.",
                });
            }

            int firstDeclLine = hooks.Count > 0
                ? hooks[0].DeclarationLine
                : modules[0].DeclarationLine;

            directiveSet = new DirectiveSet(
                Namespace: functionNamespace,
                ComponentName: null,
                PropsTypeName: null,
                DefaultKey: null,
                Usings: usings.ToImmutableArray(),
                UssFiles: ImmutableArray<string>.Empty,
                Injects: ImmutableArray<(string Type, string Name)>.Empty,
                MarkupStartLine: firstDeclLine,
                MarkupStartIndex: source.Length,
                MarkupEndIndex: source.Length,
                IsFunctionStyle: true,
                HasExplicitNamespace: inlineNamespace != null,
                FunctionSetupCode: string.Empty,
                FunctionSetupStartLine: firstDeclLine,
                HookDeclarations: hooks.ToImmutableArray(),
                ModuleDeclarations: modules.ToImmutableArray()
            )
            { UsingDirectives = usingDirectives.ToImmutableArray() };
            return true;
        }

        /// <summary>
        /// Parses a single hook declaration starting after the <c>hook</c> keyword
        /// has already been consumed.
        /// </summary>
        private static void ParseSingleHook(
            string source,
            string filePath,
            List<ParseDiagnostic> diagnosticBag,
            List<HookDeclaration> hooks,
            ref int i,
            ref int line,
            bool isExported
        )
        {
            int hookLine = line;
            SkipSpaces(source, ref i);

            // Hook name (camelCase)
            if (!TryReadIdentifier(source, ref i, out string hookName))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2200",
                    Severity = ParseSeverity.Error,
                    SourceLine = hookLine,
                    Message = "Expected hook name after 'hook' keyword.",
                });
                return;
            }

            // Warning if not starting with "use"
            if (!hookName.StartsWith("use", StringComparison.Ordinal))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2203",
                    Severity = ParseSeverity.Warning,
                    SourceLine = hookLine,
                    Message = $"Hook name '{hookName}' should start with 'use' (e.g. 'use{char.ToUpper(hookName[0])}{hookName.Substring(1)}').",
                });
            }

            // Optional generic parameters: <T> or <T, U>
            string? genericParams = null;
            if (i < source.Length && source[i] == '<')
            {
                genericParams = ReadGenericParams(source, ref i);
            }

            // Parameter list
            SkipSpaces(source, ref i);
            var hookParams = ImmutableArray<FunctionParam>.Empty;
            if (i < source.Length && source[i] == '(')
            {
                hookParams = ParseFunctionParamList(source, ref i, ref line, hookLine, diagnosticBag);
            }
            else
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2201",
                    Severity = ParseSeverity.Error,
                    SourceLine = hookLine,
                    Message = $"Hook '{hookName}' is missing a parameter list. Add '()' after the name.",
                });
            }

            // Optional -> ReturnType
            SkipWhitespaceAndNewlines(source, ref i, ref line);
            string? returnType = TryReadArrowReturnType(source, ref i, ref line);

            // Body { ... }
            SkipWhitespaceAndNewlines(source, ref i, ref line);
            if (i >= source.Length || source[i] != '{')
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2202",
                    Severity = ParseSeverity.Error,
                    SourceLine = hookLine,
                    Message = $"Hook '{hookName}' is missing a body. Expected '{{' after declaration.",
                });
                return;
            }

            int bodyOpen = i;
            if (!TryReadBalancedBlock(source, bodyOpen, out int bodyCloseExclusive))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2202",
                    Severity = ParseSeverity.Error,
                    SourceLine = hookLine,
                    Message = $"Unclosed hook body for '{hookName}'. Missing '}}'.",
                });
                return;
            }

            int bodyStart = bodyOpen + 1;
            int bodyEnd = bodyCloseExclusive - 1;
            string rawBody = source.Substring(bodyStart, Math.Max(0, bodyEnd - bodyStart));
            string body = rawBody.Trim();

            // BodyStartOffset must point to where the trimmed body text
            // actually starts (first non-whitespace char), NOT to bodyStart
            // which includes leading \r\n and indentation stripped by Trim().
            // This offset is used by VDG to build SourceMap entries, so a
            // mismatch here shifts ALL mapped positions (rename, go-to-def, etc.).
            int trimmedLeading = rawBody.Length - rawBody.TrimStart().Length;
            int actualBodyStart = bodyStart + trimmedLeading;

            int bodyStartLine = LineAtPos(source, FirstNonWhitespaceAt(source, bodyStart));

            hooks.Add(new HookDeclaration(
                Name: hookName,
                GenericParams: genericParams,
                Params: hookParams,
                ReturnType: returnType,
                Body: body,
                DeclarationLine: hookLine,
                BodyStartLine: bodyStartLine,
                BodyStartOffset: actualBodyStart,
                BodyEndOffset: bodyEnd
            )
            { IsExported = isExported });

            i = bodyCloseExclusive; // advance past '}'
            // Resync the line counter — TryReadBalancedBlock consumed the body without
            // advancing `line`, so every LATER declaration's DeclarationLine (and any
            // diagnostic anchored to it) would be stale. The migration codemod prepends
            // `export` by DeclarationLine, so a stale value silently skipped the 2nd+
            // hook/module in a file.
            line = LineAtPos(source, i);
        }

        /// <summary>
        /// Parses a single module declaration starting after the <c>module</c>
        /// keyword has already been consumed.
        /// </summary>
        private static void ParseSingleModule(
            string source,
            string filePath,
            List<ParseDiagnostic> diagnosticBag,
            List<ModuleDeclaration> modules,
            ref int i,
            ref int line,
            bool isExported
        )
        {
            int moduleLine = line;
            SkipSpaces(source, ref i);

            // Module name (PascalCase)
            if (!TryReadIdentifier(source, ref i, out string moduleName))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2204",
                    Severity = ParseSeverity.Error,
                    SourceLine = moduleLine,
                    Message = "Expected module name after 'module' keyword.",
                });
                return;
            }

            // Body { ... }
            SkipWhitespaceAndNewlines(source, ref i, ref line);
            if (i >= source.Length || source[i] != '{')
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2205",
                    Severity = ParseSeverity.Error,
                    SourceLine = moduleLine,
                    Message = $"Module '{moduleName}' is missing a body. Expected '{{' after name.",
                });
                return;
            }

            int bodyOpen = i;
            if (!TryReadBalancedBlock(source, bodyOpen, out int bodyCloseExclusive))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2205",
                    Severity = ParseSeverity.Error,
                    SourceLine = moduleLine,
                    Message = $"Unclosed module body for '{moduleName}'. Missing '}}'.",
                });
                return;
            }

            int bodyStart = bodyOpen + 1;
            int bodyEnd = bodyCloseExclusive - 1;
            string rawBody = source.Substring(bodyStart, Math.Max(0, bodyEnd - bodyStart));
            string body = rawBody.Trim();

            // BodyStartOffset must point to where the trimmed body text
            // actually starts (first non-whitespace char), NOT to bodyStart
            // which includes leading \r\n and indentation stripped by Trim().
            int trimmedLeading = rawBody.Length - rawBody.TrimStart().Length;
            int actualBodyStart = bodyStart + trimmedLeading;

            int bodyStartLine = LineAtPos(source, FirstNonWhitespaceAt(source, bodyStart));

            modules.Add(new ModuleDeclaration(
                Name: moduleName,
                Body: body,
                DeclarationLine: moduleLine,
                BodyStartLine: bodyStartLine,
                BodyStartOffset: actualBodyStart,
                BodyEndOffset: bodyEnd
            )
            { IsExported = isExported });

            i = bodyCloseExclusive; // advance past '}'
            // Resync the line counter across the consumed body (see the hook parser's
            // matching note) — later declarations' DeclarationLine must be accurate.
            line = LineAtPos(source, i);
        }

        // ── Plain declarations (ES-modules campaign, U-04) ─────────────────────

        /// <summary>True for a hook-shaped name: <c>use</c> followed by an uppercase letter
        /// (G-03's <c>^use\p{Lu}</c> rule, written as a fast manual scan — this is a per-declaration
        /// hot-path check, not a per-keystroke one, but the file's existing readers avoid Regex for
        /// this class of check throughout).</summary>
        private static bool LooksLikeHookName(string name)
            => name.Length > 3
                && name[0] == 'u' && name[1] == 's' && name[2] == 'e'
                && char.IsUpper(name[3]);

        /// <summary>Strips <c>global::</c> and namespace qualifiers so a fully-qualified
        /// <c>Ruitk.Core.VirtualNode</c> return type still classifies as a component
        /// (G-03: "normalized return-type token"). Not applied to tuple return types (they never
        /// equal <c>VirtualNode</c> textually, so passing one through here is harmless).</summary>
        private static string NormalizeReturnTypeForClassification(string? typeText)
        {
            if (string.IsNullOrEmpty(typeText))
                return string.Empty;
            string t = typeText!.Trim();
            if (t.StartsWith("global::", StringComparison.Ordinal))
                t = t.Substring("global::".Length);
            int lastDot = t.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < t.Length - 1)
                t = t.Substring(lastDot + 1);
            return t;
        }

        /// <summary>Reads a balanced tuple-type text starting at a <c>(</c>, e.g.
        /// <c>(int value, Action reset)</c>. Does not track <c>line</c> (matches the file's other
        /// inline-expression readers, e.g. <see cref="ReadDefaultValue"/> — callers recompute the
        /// line via <see cref="LineAtPos"/> once the whole declaration head is consumed).</summary>
        private static bool TryReadTupleTypeText(string source, ref int i, out string tupleText)
        {
            tupleText = string.Empty;
            if (i >= source.Length || source[i] != '(')
                return false;
            int start = i;
            int depth = 0;
            while (i < source.Length)
            {
                if (TrySkipNonCodeSpan(source, ref i, source.Length))
                    continue;
                char c = source[i];
                if (c == '(') { depth++; i++; continue; }
                if (c == ')') { depth--; i++; if (depth == 0) break; continue; }
                i++;
            }
            if (depth != 0)
                return false;
            tupleText = source.Substring(start, i - start);
            return true;
        }

        /// <summary>
        /// Reads a plain declaration HEAD: <c>[Type] Name</c> immediately followed by <c>(</c>
        /// (function-shaped) or <c>=</c> (value-shaped) — U-04 point 3. <paramref name="typeText"/>
        /// is <c>null</c> when the head has no separate type token before the name (legal only for
        /// value declarations using inference sugar, checked by the caller). Does NOT consume the
        /// delimiter itself — the cursor is left pointing AT <c>(</c>/<c>=</c> so the caller can
        /// dispatch on it (mirrors how the legacy component header leaves the cursor at <c>{</c>).
        /// Cursor-restore discipline matches the other readers in this file: returns false with
        /// <paramref name="i"/>/<paramref name="line"/> untouched when nothing head-shaped is here.
        /// </summary>
        private static bool TryReadDeclarationHead(
            string source, ref int i, ref int line,
            out string? typeText, out string name, out int nameLine, out int nameColumn, out char delimiter,
            out bool genericHead)
        {
            typeText = null;
            name = string.Empty;
            nameLine = line;
            nameColumn = -1;
            delimiter = '\0';
            genericHead = false;

            int savedI = i, savedLine = line;

            SkipWhitespaceAndNewlines(source, ref i, ref line);
            if (i >= source.Length) { i = savedI; line = savedLine; return false; }

            int token1StartI = i;
            int token1StartLine = line;
            string token1;
            if (source[i] == '(')
            {
                if (!TryReadTupleTypeText(source, ref i, out token1))
                { i = savedI; line = savedLine; return false; }
            }
            else
            {
                if (!TryReadTypeName(source, ref i, ref line, out token1))
                { i = savedI; line = savedLine; return false; }
            }

            // Tuple/generic type scans can cross newlines without tracking `line` — resync so
            // nameLine (and every diagnostic anchored to it) lands on the name's REAL line.
            line = LineAtPos(source, i);

            SkipSpaces(source, ref i);

            if (i < source.Length && (char.IsLetter(source[i]) || source[i] == '_'))
            {
                // token1 was the Type; the name follows separately.
                typeText = token1;
                nameColumn = ColAtPos(source, i);
                nameLine = line;
                if (!TryReadIdentifier(source, ref i, out name))
                { i = savedI; line = savedLine; return false; }
                SkipSpaces(source, ref i);
            }
            else
            {
                // No separate name token followed — token1 IS the name (inference form). Only
                // legal when token1 is itself a bare identifier (a tuple head with no following
                // name is malformed, e.g. a stray `(int, int)` at top level).
                if (token1.Length == 0 || !(char.IsLetter(token1[0]) || token1[0] == '_'))
                { i = savedI; line = savedLine; return false; }
                name = token1;
                nameColumn = ColAtPos(source, token1StartI);
                nameLine = token1StartLine;
                typeText = null;
            }

            if (i >= source.Length || (source[i] != '(' && source[i] != '='))
            {
                genericHead = i < source.Length && source[i] == '<' && name.Length > 0;
                i = savedI; line = savedLine; return false;
            }
            // Reject `==` — an equality expression starting right after the head is not a
            // declaration (defensive; well-formed input never reaches this in practice).
            if (source[i] == '=' && i + 1 < source.Length && source[i + 1] == '=')
            { i = savedI; line = savedLine; return false; }

            delimiter = source[i];
            return true;
        }

        /// <summary>
        /// Pure look-ahead used for the U-08 mixed-mode diagnostic's LEGACY direction: does the
        /// text at <paramref name="pos"/> read as a plain declaration (optionally exported), an
        /// <c>export default</c>, or an <c>export { … }</c> list? Requires either an explicit
        /// <c>export</c> or a two-token (typed) head so bare statements (<c>DoThing();</c>,
        /// <c>x = 1;</c>) keep their generic invalid-statement diagnostic.
        /// </summary>
        private static bool LooksLikePlainDeclarationHeadAt(string source, int pos)
        {
            int p = pos;
            int l = 1;
            bool sawExport = TryReadKeyword(source, ref p, "export");
            if (sawExport)
            {
                SkipSpaces(source, ref p);
                if (TryReadKeywordAt(source, p, "default"))
                    return true;
                if (p < source.Length && source[p] == '{')
                    return true;
            }
            if (!TryReadDeclarationHead(source, ref p, ref l,
                    out string? probeType, out _, out _, out _, out _, out _))
                return false;
            return sawExport || probeType != null;
        }

        /// <summary>
        /// Reads a value declaration's initializer expression up to (and consuming) the
        /// terminating top-level <c>;</c>. Tracks paren/brace/bracket depth and skips string/char
        /// literals (<see cref="TrySkipNonCodeSpan"/>) so a semicolon inside <c>new Style { … }</c>
        /// or a string literal is not mistaken for the terminator. Returns false (leaving
        /// <paramref name="endExclusive"/> at EOF) when no top-level <c>;</c> is found.
        /// </summary>
        private static bool TryReadValueInitializer(string source, ref int i, ref int line, out int endExclusive)
        {
            int parenDepth = 0, braceDepth = 0, bracketDepth = 0;
            while (i < source.Length)
            {
                if (TrySkipNonCodeSpan(source, ref i, source.Length))
                    continue;
                char c = source[i];
                if (c == '(') { parenDepth++; i++; continue; }
                if (c == ')') { if (parenDepth > 0) { parenDepth--; i++; continue; } break; }
                if (c == '{') { braceDepth++; i++; continue; }
                if (c == '}') { if (braceDepth > 0) { braceDepth--; i++; continue; } break; }
                if (c == '[') { bracketDepth++; i++; continue; }
                if (c == ']') { if (bracketDepth > 0) { bracketDepth--; i++; continue; } break; }
                if (c == ';' && parenDepth == 0 && braceDepth == 0 && bracketDepth == 0)
                {
                    endExclusive = i;
                    i++; // consume ';'
                    line = LineAtPos(source, i);
                    return true;
                }
                i++;
            }
            endExclusive = i;
            line = LineAtPos(source, i);
            return false;
        }

        /// <summary>
        /// True when a value initializer "names the type" (G-04's inference-sugar rule):
        /// <c>= new T { … }</c> / <c>= new T(...)</c>. Anything else with no explicit declared type
        /// is UITKX2322.
        /// </summary>
        private static bool LooksLikeTypedInitializer(string initText)
        {
            string t = initText.TrimStart();
            if (!t.StartsWith("new", StringComparison.Ordinal))
                return false;
            int afterNew = 3;
            if (afterNew < t.Length && (char.IsLetterOrDigit(t[afterNew]) || t[afterNew] == '_'))
                return false; // "newFoo" — not the `new` keyword
            int p = afterNew;
            while (p < t.Length && (t[p] == ' ' || t[p] == '\t')) p++;
            return p < t.Length && (char.IsLetter(t[p]) || t[p] == '_');
        }

        /// <summary>Reads <c>export default &lt;Ident&gt;[;]</c> at the cursor (G-05).</summary>
        private static bool TryReadExportDefaultDeclaration(
            string source, ref int i, ref int line, out string name, out int declLine)
        {
            name = string.Empty;
            declLine = line;
            if (!TryReadKeywordAt(source, i, "export"))
                return false;

            int afterExport = i + "export".Length;
            SkipSpaces(source, ref afterExport);
            if (!TryReadKeywordAt(source, afterExport, "default"))
                return false; // not `export default` — caller tries other readers next

            int savedI = i, savedLine = line;
            int afterDefault = afterExport + "default".Length;
            SkipSpaces(source, ref afterDefault);
            if (!TryReadIdentifier(source, ref afterDefault, out string ident))
            { i = savedI; line = savedLine; return false; }

            declLine = line;
            i = afterDefault;
            SkipSpaces(source, ref i);
            if (i < source.Length && source[i] == ';')
                i++;
            while (i < source.Length && !IsNewline(source[i]))
                i++;
            if (i < source.Length && IsNewline(source[i]))
                ConsumeNewline(source, ref i, ref line);

            name = ident;
            return true;
        }

        /// <summary>Reads a deferred <c>export { a, b, c as d }[;]</c> list at the cursor (G-05).
        /// Rename-on-export is NOT part of this campaign's surface (only rename-on-IMPORT, G-05) —
        /// an <c>as</c> inside the list is rejected here so it falls through to the generic
        /// invalid-statement diagnostic rather than being silently ignored.</summary>
        private static bool TryReadExportListDeclaration(
            string source, ref int i, ref int line, out List<(string Name, int Column, int Line)> names)
        {
            names = new List<(string, int, int)>();
            if (!TryReadKeywordAt(source, i, "export"))
                return false;

            int p = i + "export".Length;
            SkipSpaces(source, ref p);
            if (p >= source.Length || source[p] != '{')
                return false; // not an export-list — `export default` / a plain decl tried next

            int savedI = i, savedLine = line;
            int pLine = line;
            p++; // past '{'
            while (true)
            {
                SkipWhitespaceAndNewlines(source, ref p, ref pLine);
                if (p < source.Length && source[p] == '}') { p++; break; }
                int nameCol = ColAtPos(source, p);
                if (!TryReadIdentifier(source, ref p, out string nm))
                { i = savedI; line = savedLine; names.Clear(); return false; }
                names.Add((nm, nameCol, pLine));
                SkipWhitespaceAndNewlines(source, ref p, ref pLine);
                if (p < source.Length && source[p] == ',') { p++; continue; }
                if (p < source.Length && source[p] == '}') { p++; break; }
                i = savedI; line = savedLine; names.Clear(); return false;
            }

            i = p;
            line = pLine;
            SkipSpaces(source, ref i);
            if (i < source.Length && source[i] == ';')
                i++;
            while (i < source.Length && !IsNewline(source[i]))
                i++;
            if (i < source.Length && IsNewline(source[i]))
                ConsumeNewline(source, ref i, ref line);
            return true;
        }

        /// <summary>
        /// Parses a whole file as a sequence of plain (wrapper-keyword-free) top-level
        /// declarations (ES-modules campaign, U-04/U-08). Called from <see cref="TryParseFunctionStyle"/>
        /// only when the first post-preamble token is NOT a <c>component</c>/<c>hook</c>/<c>module</c>
        /// keyword (optionally after <c>export</c>) — the file's FIRST declaration decides its mode.
        /// Returns false (no durable side effects — <paramref name="i"/>/<paramref name="line"/> are
        /// local to the caller's failed attempt regardless) when nothing recognizable as a plain
        /// declaration, <c>export default</c>, or <c>export { … }</c> can be read at the very first
        /// position, so the caller's legacy fallback diagnostics own genuinely invalid content.
        /// </summary>
        private static bool TryParsePlainDeclarationsFile(
            string source,
            string filePath,
            List<ParseDiagnostic> diagnosticBag,
            out DirectiveSet directiveSet,
            ref int i,
            ref int line,
            List<string> usings,
            List<UsingDirective> usingDirectives,
            List<string> ussFiles,
            List<ImportDeclaration> imports,
            List<(string Text, bool IsBlock, int Line)> leadingTrivia,
            string? inlineNamespace,
            bool useLastReturn)
        {
            directiveSet = default!;
            string functionNamespace = inlineNamespace ?? FunctionStyleDefaultNamespace;

            var components = new List<ComponentDeclaration>();
            var members = new List<MemberDeclaration>();
            string? defaultExportName = null;
            int defaultExportLine = line;
            var exportListNames = new List<(string Name, int Column, int Line)>();
            var seenExportListNames = new HashSet<string>(StringComparer.Ordinal);
            bool parsedAnyDeclaration = false;

            while (true)
            {
                SkipLeadingFunctionStyleTrivia(source, ref i, ref line, leadingTrivia);
                if (i >= source.Length)
                    break;

                if (TryReadExportDefaultDeclaration(source, ref i, ref line, out string defName, out int defLine))
                {
                    parsedAnyDeclaration = true;
                    if (defaultExportName != null)
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2327",
                            Severity = ParseSeverity.Error,
                            SourceLine = defLine,
                            Message = "duplicate 'export default' — a file has at most one default export",
                        });
                    }
                    else
                    {
                        defaultExportName = defName;
                        defaultExportLine = defLine;
                    }
                    continue;
                }

                if (TryReadExportListDeclaration(source, ref i, ref line, out var listNames))
                {
                    parsedAnyDeclaration = true;
                    foreach (var entry in listNames)
                    {
                        if (!seenExportListNames.Add(entry.Name))
                        {
                            diagnosticBag.Add(new ParseDiagnostic
                            {
                                Code = "UITKX2324",
                                Severity = ParseSeverity.Error,
                                SourceLine = entry.Line,
                                SourceColumn = entry.Column,
                                EndLine = entry.Line,
                                EndColumn = entry.Column + entry.Name.Length,
                                Message = $"'{entry.Name}' is already exported — remove the duplicate export",
                            });
                        }
                        else
                        {
                            exportListNames.Add(entry);
                        }
                    }
                    continue;
                }

                // Legacy wrapper keyword in a new-mode file (U-08 / matrix row 3): UITKX2108
                // (Unity-local), parsed best-effort via the existing machinery for IDE resilience,
                // then STOP — the file's mode is broken past this point.
                {
                    int wrapperPeek = i;
                    bool wrapperExported = false;
                    if (TryReadKeyword(source, ref wrapperPeek, "export"))
                    {
                        SkipSpaces(source, ref wrapperPeek);
                        if (TryReadKeywordAt(source, wrapperPeek, "component")
                            || TryReadKeywordAt(source, wrapperPeek, "hook")
                            || TryReadKeywordAt(source, wrapperPeek, "module"))
                            wrapperExported = true;
                        else
                            wrapperPeek = i;
                    }
                    int wrapperKeywordPos = wrapperExported ? wrapperPeek : i;
                    if (TryReadKeywordAt(source, wrapperKeywordPos, "component")
                        || TryReadKeywordAt(source, wrapperKeywordPos, "hook")
                        || TryReadKeywordAt(source, wrapperKeywordPos, "module"))
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2108",
                            Severity = ParseSeverity.Error,
                            SourceLine = line,
                            Message = "legacy wrapper declarations and plain declarations cannot be mixed in one file — the file's first declaration sets its style",
                        });
                        i = wrapperKeywordPos;
                        if (TryReadKeyword(source, ref i, "component"))
                        {
                            var wrapperComponent = ParseSingleComponent(
                                source, filePath, diagnosticBag, ref i, ref line, wrapperExported, useLastReturn, out _);
                            if (wrapperComponent != null)
                                components.Add(wrapperComponent);
                        }
                        else if (TryReadKeyword(source, ref i, "hook"))
                        {
                            var wrapperHooks = new List<HookDeclaration>();
                            ParseSingleHook(source, filePath, diagnosticBag, wrapperHooks, ref i, ref line, wrapperExported);
                        }
                        else if (TryReadKeyword(source, ref i, "module"))
                        {
                            var wrapperModules = new List<ModuleDeclaration>();
                            ParseSingleModule(source, filePath, diagnosticBag, wrapperModules, ref i, ref line, wrapperExported);
                        }
                        break;
                    }
                }

                // Plain declaration head.
                int declStart = i;
                int declStartLine = line;
                bool isExported = false;
                {
                    int afterExport = i;
                    if (TryReadKeyword(source, ref afterExport, "export"))
                    {
                        isExported = true;
                        i = afterExport;
                    }
                }

                if (!TryReadDeclarationHead(source, ref i, ref line,
                        out string? typeText, out string declName, out int declLine, out int declNameCol, out char delimiter,
                        out bool genericHead))
                {
                    if (genericHead)
                    {
                        // `export T Identity<T>(…)` / `hook useX<T>` shapes: the plain dialect has
                        // no generic-declaration form (family grammar, G-03). A precise error beats
                        // the misleading whole-file line-1 fallback; generic hooks stay legacy
                        // through the deprecation window (the codemod reports them).
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2105",
                            Severity = ParseSeverity.Error,
                            SourceLine = line,
                            Message = "generic declarations are not supported in plain-declaration syntax — keep the generic in a legacy 'hook' file or in ambient C#",
                        });
                        parsedAnyDeclaration = true;
                        break;
                    }

                    if (!parsedAnyDeclaration)
                    {
                        i = declStart; line = declStartLine;
                        return false;
                    }

                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = "UITKX2105",
                        Severity = ParseSeverity.Error,
                        SourceLine = LineAtPos(source, i),
                        Message = "Invalid top-level statement after plain declaration.",
                    });
                    break;
                }

                parsedAnyDeclaration = true;

                if (delimiter == '=')
                {
                    i++; // past '='
                    SkipWhitespaceAndNewlines(source, ref i, ref line);
                    int initStart = i;
                    if (!TryReadValueInitializer(source, ref i, ref line, out int initEndExclusive))
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2105",
                            Severity = ParseSeverity.Error,
                            SourceLine = declLine,
                            Message = $"Value export '{declName}' is missing a terminating ';'.",
                        });
                        break;
                    }
                    string initText = source.Substring(initStart, Math.Max(0, initEndExclusive - initStart)).Trim();

                    if (typeText == null && !LooksLikeTypedInitializer(initText))
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2322",
                            Severity = ParseSeverity.Error,
                            SourceLine = declLine,
                            SourceColumn = declNameCol,
                            EndLine = declLine,
                            EndColumn = declNameCol + declName.Length,
                            Message = $"value export '{declName}' cannot infer its type — the initializer must name the type ('= new T {{ … }}'); otherwise declare 'export <Type> {declName} = …'",
                        });
                    }

                    members.Add(new MemberDeclaration(
                        Name: declName,
                        Kind: DeclKind.Value,
                        IsExported: isExported,
                        ReturnTypeText: typeText,
                        ParamsText: null,
                        BodyText: initText,
                        IsExpressionBodied: false,
                        DeclarationLine: declLine,
                        NameColumn: declNameCol,
                        BodyStartLine: LineAtPos(source, initStart),
                        BodyStartOffset: initStart,
                        BodyEndOffset: initEndExclusive));
                    continue;
                }

                // Function-shaped: '(' params ')' then '{ body }' or '=> expr;'.
                int paramsOpenPos = i;
                var declParams = ParseFunctionParamList(source, ref i, ref line, declLine, diagnosticBag);
                int paramsCloseExclusive = i;
                string rawParamsText = paramsCloseExclusive - paramsOpenPos >= 2
                    ? source.Substring(paramsOpenPos + 1, paramsCloseExclusive - paramsOpenPos - 2).Trim()
                    : string.Empty;

                string normalizedType = NormalizeReturnTypeForClassification(typeText);
                bool isVirtualNodeReturn = normalizedType == "VirtualNode";
                bool looksLikeHook = LooksLikeHookName(declName);

                if (isVirtualNodeReturn)
                {
                    if (looksLikeHook)
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2321",
                            Severity = ParseSeverity.Error,
                            SourceLine = declLine,
                            SourceColumn = declNameCol,
                            EndLine = declLine,
                            EndColumn = declNameCol + declName.Length,
                            Message = $"'{declName}' is 'use'-prefixed but returns VirtualNode — did you mean a component? (components are PascalCase and return VirtualNode)",
                        });
                    }
                    else if (!IsPascalCase(declName))
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2100",
                            Severity = ParseSeverity.Error,
                            SourceLine = declLine,
                            Message = $"Function-style component name '{declName}' must be PascalCase.",
                        });
                    }

                    string? propsTypeName = declParams.IsEmpty ? null : declName + "Props";
                    var componentDecl = ParseComponentBodyAt(
                        source, diagnosticBag, declName, isExported, declParams, propsTypeName,
                        declLine, declNameCol, ref i, ref line, useLastReturn, out bool hardStop);
                    if (componentDecl != null)
                        components.Add(componentDecl);
                    if (hardStop)
                        break;
                    continue;
                }

                DeclKind kind = looksLikeHook ? DeclKind.Hook : DeclKind.Util;

                SkipWhitespaceAndNewlines(source, ref i, ref line);
                if (i + 1 < source.Length && source[i] == '=' && source[i + 1] == '>')
                {
                    i += 2; // past '=>'
                    SkipWhitespaceAndNewlines(source, ref i, ref line);
                    int exprStart = i;
                    if (!TryReadValueInitializer(source, ref i, ref line, out int exprEndExclusive))
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2105",
                            Severity = ParseSeverity.Error,
                            SourceLine = declLine,
                            Message = $"'{declName}' is missing a terminating ';'.",
                        });
                        break;
                    }
                    string exprBody = source.Substring(exprStart, Math.Max(0, exprEndExclusive - exprStart)).Trim();
                    members.Add(new MemberDeclaration(
                        Name: declName,
                        Kind: kind,
                        IsExported: isExported,
                        ReturnTypeText: typeText,
                        ParamsText: rawParamsText,
                        BodyText: exprBody,
                        IsExpressionBodied: true,
                        DeclarationLine: declLine,
                        NameColumn: declNameCol,
                        BodyStartLine: LineAtPos(source, exprStart),
                        BodyStartOffset: exprStart,
                        BodyEndOffset: exprEndExclusive)
                    { Params = declParams });
                    continue;
                }

                if (i >= source.Length || source[i] != '{')
                {
                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = "UITKX2105",
                        Severity = ParseSeverity.Error,
                        SourceLine = declLine,
                        Message = $"'{declName}' is missing a body. Expected '{{' or '=>' after its parameter list.",
                    });
                    break;
                }

                int declBodyOpen = i;
                if (!TryReadBalancedBlock(source, declBodyOpen, out int declBodyCloseExclusive))
                {
                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = "UITKX2105",
                        Severity = ParseSeverity.Error,
                        SourceLine = declLine,
                        Message = $"Unclosed body for '{declName}'. Missing '}}'.",
                    });
                    break;
                }

                int declBodyStart = declBodyOpen + 1;
                int declBodyEnd = declBodyCloseExclusive - 1;
                string rawDeclBody = source.Substring(declBodyStart, Math.Max(0, declBodyEnd - declBodyStart));
                string trimmedDeclBody = rawDeclBody.Trim();
                int declTrimmedLeading = rawDeclBody.Length - rawDeclBody.TrimStart().Length;
                int declActualBodyStart = declBodyStart + declTrimmedLeading;
                int declBodyStartLine = LineAtPos(source, FirstNonWhitespaceAt(source, declBodyStart));

                members.Add(new MemberDeclaration(
                    Name: declName,
                    Kind: kind,
                    IsExported: isExported,
                    ReturnTypeText: typeText,
                    ParamsText: rawParamsText,
                    BodyText: trimmedDeclBody,
                    IsExpressionBodied: false,
                    DeclarationLine: declLine,
                    NameColumn: declNameCol,
                    BodyStartLine: declBodyStartLine,
                    BodyStartOffset: declActualBodyStart,
                    BodyEndOffset: declBodyEnd)
                { Params = declParams });

                i = declBodyCloseExclusive;
                line = LineAtPos(source, i);
            }

            // A file that declared NOTHING (empty, or imports-only) is not a plain-declarations
            // module — fall back to the legacy dispatch so its baseline diagnostics (2105) own
            // the file instead of committing an empty new-mode set the formatter would then
            // "reconstruct" into a fabricated component.
            if (!parsedAnyDeclaration)
            {
                directiveSet = default!;
                return false;
            }

            var declaredNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in components) declaredNames.Add(c.Name);
            foreach (var m in members) declaredNames.Add(m.Name);

            if (defaultExportName != null && !declaredNames.Contains(defaultExportName))
            {
                diagnosticBag.Add(new ParseDiagnostic
                {
                    Code = "UITKX2323",
                    Severity = ParseSeverity.Error,
                    SourceLine = defaultExportLine,
                    Message = $"'export default' names '{defaultExportName}', which is not a top-level declaration in this file",
                });
            }
            foreach (var entry in exportListNames)
            {
                if (!declaredNames.Contains(entry.Name))
                {
                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = "UITKX2323",
                        Severity = ParseSeverity.Error,
                        SourceLine = entry.Line,
                        SourceColumn = entry.Column,
                        EndLine = entry.Line,
                        EndColumn = entry.Column + entry.Name.Length,
                        Message = $"'export {{ … }}' names '{entry.Name}', which is not a top-level declaration in this file",
                    });
                }
            }

            if (exportListNames.Count > 0)
            {
                // U-04: a list name whose declaration ALREADY carries an inline `export` is a
                // duplicate export (2324) — inline + list, like twice-in-a-list/across-lists.
                // (`export default` is exempt: ES legally combines it with a named export.)
                foreach (var entry in exportListNames)
                {
                    bool inlineExported =
                        components.Exists(c => c.Name == entry.Name && c.IsExported)
                        || members.Exists(mm => mm.Name == entry.Name && mm.IsExported);
                    if (inlineExported)
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = "UITKX2324",
                            Severity = ParseSeverity.Error,
                            SourceLine = entry.Line,
                            SourceColumn = entry.Column,
                            EndLine = entry.Line,
                            EndColumn = entry.Column + entry.Name.Length,
                            Message = $"'{entry.Name}' is already exported — remove the duplicate export",
                        });
                    }
                }
            }

            if (exportListNames.Count > 0 || defaultExportName != null)
            {
                // `export { a, b };` marks the matching declarations exported — and so does
                // `export default X;` (ES semantics: a default export IS an export; the
                // default-marked declaration is part of the file's public surface).
                // IsExportImplied marks declarations whose export came ONLY from the list/default
                // marking (no inline `export` written in source) — the formatter must not add an
                // inline prefix the author never wrote.
                var boundNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in exportListNames) boundNames.Add(entry.Name);
                if (defaultExportName != null) boundNames.Add(defaultExportName);
                for (int ci = 0; ci < components.Count; ci++)
                    if (boundNames.Contains(components[ci].Name) && !components[ci].IsExported)
                        components[ci] = components[ci] with { IsExported = true, IsExportImplied = true };
                for (int mi = 0; mi < members.Count; mi++)
                    if (boundNames.Contains(members[mi].Name) && !members[mi].IsExported)
                        members[mi] = members[mi] with { IsExported = true, IsExportImplied = true };
            }

            int firstLine = components.Count > 0 ? components[0].DeclarationLine
                : members.Count > 0 ? members[0].DeclarationLine
                : line;

            // Mirror the FIRST component's fields onto the singular back-compat DirectiveSet
            // slots (M1 audit item, §1.5): DiagnosticsAnalyzer's UITKX0107 (unreachable-after-
            // return) and UITKX0111 (unused parameter) checks read d.FunctionReturnEndLine/
            // FunctionBodyEndLine/FunctionParams/FunctionSetupCode/FunctionSetupStartOffset
            // directly off DirectiveSet, not off ComponentDeclarations — exactly parity with
            // what the legacy first-component path already does (tail/2nd+ components in EITHER
            // mode share that same pre-existing limitation; not a new gap introduced here).
            var primary = components.Count > 0 ? components[0] : null;

            directiveSet = new DirectiveSet(
                Namespace: functionNamespace,
                ComponentName: primary?.Name,
                PropsTypeName: primary?.PropsTypeName,
                DefaultKey: null,
                Usings: usings.ToImmutableArray(),
                UssFiles: ussFiles.ToImmutableArray(),
                Injects: ImmutableArray<(string Type, string Name)>.Empty,
                MarkupStartLine: primary?.MarkupStartLine ?? firstLine,
                MarkupStartIndex: primary?.MarkupStartIndex ?? source.Length,
                MarkupEndIndex: primary?.MarkupEndIndex ?? source.Length,
                IsFunctionStyle: true,
                HasExplicitNamespace: inlineNamespace != null,
                FunctionSetupCode: primary?.FunctionSetupCode ?? string.Empty,
                FunctionSetupStartLine: primary?.FunctionSetupStartLine ?? firstLine,
                FunctionSetupStartOffset: primary?.FunctionSetupStartOffset ?? -1,
                FunctionParams: primary?.FunctionParams ?? ImmutableArray<FunctionParam>.Empty,
                ComponentDeclarationLine: primary?.DeclarationLine ?? -1,
                ComponentNameColumn: primary?.NameColumn ?? -1,
                FunctionReturnEndLine: primary?.ReturnEndLine ?? -1,
                FunctionBodyEndLine: primary?.BodyEndLine ?? -1,
                SetupCodeMarkupRanges: primary?.SetupCodeMarkupRanges ?? ImmutableArray<(int Start, int End, int Line)>.Empty,
                SetupCodeBareJsxRanges: primary?.SetupCodeBareJsxRanges ?? ImmutableArray<(int Start, int End, int Line)>.Empty,
                FunctionSetupGapOffset: primary?.FunctionSetupGapOffset ?? -1,
                FunctionSetupGapLength: primary?.FunctionSetupGapLength ?? 0
            )
            {
                LeadingTrivia = leadingTrivia.ToImmutableArray(),
                UsingDirectives = usingDirectives.ToImmutableArray(),
                Imports = imports.ToImmutableArray(),
                ComponentDeclarations = components.ToImmutableArray(),
                MemberDeclarations = members.ToImmutableArray(),
                DefaultExportName = defaultExportName,
                ExportListNames = exportListNames.ConvertAll(e => e.Name).ToImmutableArray(),
                UsesLegacySyntax = false,
                HasNullReturn = primary?.HasNullReturn ?? false,
            };
            return true;
        }

        // ── Arrow return type reader ──────────────────────────────────────────

        /// <summary>
        /// Reads an optional <c>-&gt; ReturnType</c> after a hook parameter list.
        /// Handles tuples <c>(int, Action)</c>, generics <c>List&lt;int&gt;</c>,
        /// arrays, and nullable types by scanning until <c>{</c> at depth zero.
        /// Returns <c>null</c> if no arrow is present (void hook).
        /// </summary>
        private static string? TryReadArrowReturnType(string source, ref int i, ref int line)
        {
            if (i + 1 >= source.Length || source[i] != '-' || source[i + 1] != '>')
                return null;

            i += 2; // consume '->'
            SkipWhitespaceAndNewlines(source, ref i, ref line);

            int start = i;
            int parenDepth = 0;
            int angleDepth = 0;

            while (i < source.Length)
            {
                char c = source[i];

                if (c == '(') { parenDepth++; i++; continue; }
                if (c == ')') { parenDepth--; i++; continue; }
                if (c == '<') { angleDepth++; i++; continue; }
                if (c == '>') { angleDepth--; i++; continue; }

                // Stop at '{' only when all parens and angles are balanced
                if (c == '{' && parenDepth == 0 && angleDepth == 0)
                    break;

                // Track newlines
                if (IsNewline(c))
                {
                    ConsumeNewline(source, ref i, ref line);
                    continue;
                }

                i++;
            }

            string returnType = source.Substring(start, i - start).Trim();
            return returnType.Length > 0 ? returnType : null;
        }

        // ── Generic params reader ─────────────────────────────────────────────

        /// <summary>
        /// Reads generic type parameters including angle brackets from the current
        /// position, e.g. <c>&lt;T&gt;</c> or <c>&lt;TKey, TValue&gt;</c>.
        /// Returns the full string including <c>&lt;</c> and <c>&gt;</c>.
        /// </summary>
        private static string ReadGenericParams(string source, ref int i)
        {
            if (i >= source.Length || source[i] != '<')
                return string.Empty;

            int start = i;
            int depth = 0;

            while (i < source.Length)
            {
                if (source[i] == '<') { depth++; i++; }
                else if (source[i] == '>') { depth--; i++; if (depth == 0) break; }
                else i++;
            }

            return source.Substring(start, i - start);
        }

        // ── Function param-list parser ────────────────────────────────────────

        /// <summary>
        /// Parses a comma-separated parameter list that follows a function-style
        /// component name: <c>component Foo(int X = 0, string Label = "hi")</c>.
        ///
        /// The opening <c>(</c> must be the character at <paramref name="i"/> on entry;
        /// on exit <paramref name="i"/> points past the closing <c>)</c>.
        /// </summary>
        private static ImmutableArray<FunctionParam> ParseFunctionParamList(
            string source,
            ref int i,
            ref int line,
            int componentLine,
            List<ParseDiagnostic> diagnosticBag
        )
        {
            // Consume '('
            i++;

            var result = ImmutableArray.CreateBuilder<FunctionParam>();

            while (i < source.Length)
            {
                SkipWhitespaceAndNewlines(source, ref i, ref line);

                if (i >= source.Length)
                    break;

                if (source[i] == ')')
                {
                    i++; // consume ')'
                    break;
                }

                // Parameter modifiers (audit F12): ref/out/in/params/readonly prefix the TYPE
                // text so every consumer of the parsed view (bridges, trampolines, header
                // re-emission) re-states and forwards them faithfully — `params object[] xs`
                // must never parse as type "params", name "object".
                string paramModifiers = string.Empty;
                while (true)
                {
                    int modStart = i;
                    if (TryReadKeyword(source, ref i, "ref") || TryReadKeyword(source, ref i, "out")
                        || TryReadKeyword(source, ref i, "in") || TryReadKeyword(source, ref i, "params")
                        || TryReadKeyword(source, ref i, "readonly"))
                    {
                        paramModifiers += source.Substring(modStart, i - modStart).Trim() + " ";
                        SkipSpaces(source, ref i);
                        continue;
                    }
                    break;
                }

                // Parse type name (may include generics: List<int>, Dictionary<string,int>)
                if (!TryReadTypeName(source, ref i, ref line, out string typeName))
                {
                    // Skip to next comma or closing paren
                    while (i < source.Length && source[i] != ',' && source[i] != ')')
                        i++;
                    if (i < source.Length && source[i] == ',')
                        i++;
                    continue;
                }
                typeName = paramModifiers + typeName;

                SkipSpaces(source, ref i);

                // Capture position before reading the identifier so we can
                // attach squiggles to unused-parameter diagnostics later.
                int paramNameLine = line;
                int paramNameCol  = ComputeColumn(source, i);

                // Parse parameter name
                if (!TryReadIdentifier(source, ref i, out string paramName))
                {
                    diagnosticBag.Add(new ParseDiagnostic
                    {
                        Code = "UITKX2106",
                        Severity = ParseSeverity.Warning,
                        SourceLine = componentLine,
                        Message = $"Expected parameter name after type '{typeName}' in component parameter list.",
                    });
                    // Skip to next comma or closing paren
                    while (i < source.Length && source[i] != ',' && source[i] != ')')
                        i++;
                    if (i < source.Length && source[i] == ',')
                        i++;
                    continue;
                }

                SkipSpaces(source, ref i);

                string? defaultValue = null;
                if (i < source.Length && source[i] == '=')
                {
                    i++; // consume '='
                    SkipSpaces(source, ref i);
                    defaultValue = ReadDefaultValue(source, ref i);
                }

                result.Add(new FunctionParam(typeName, paramName, defaultValue)
                {
                    SourceLine = paramNameLine,
                    NameColumn = paramNameCol,
                });

                SkipSpaces(source, ref i);
                if (i < source.Length && source[i] == ',')
                    i++; // consume comma, loop continues
            }

            return result.ToImmutable();
        }

        /// <summary>
        /// Reads a C# type name, including optional generic type arguments
        /// (balanced &lt; … &gt; pairs), arrays (<c>[]</c>), and nullable markers
        /// (<c>?</c>).  Does NOT handle tuple types or complex pointer types.
        /// </summary>
        private static bool TryReadTypeName(
            string source,
            ref int i,
            ref int line,
            out string typeName
        )
        {
            typeName = string.Empty;
            int start = i;

            // Leading identifier (required)
            if (i >= source.Length || !(char.IsLetter(source[i]) || source[i] == '_'))
                return false;

            while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                i++;

            // Dotted qualifier: System.Collections.Generic.List
            while (i < source.Length && source[i] == '.')
            {
                i++; // consume '.'
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                    i++;
            }

            // Generic type arguments: <T1, T2>
            if (i < source.Length && source[i] == '<')
            {
                int depth = 1;
                i++; // consume '<'
                while (i < source.Length && depth > 0)
                {
                    if (source[i] == '<') { depth++; i++; }
                    else if (source[i] == '>') { depth--; i++; }
                    else i++;
                }
            }

            // Array suffix: [], [,], etc.
            while (i + 1 < source.Length && source[i] == '[')
            {
                i++;
                while (i < source.Length && source[i] != ']')
                    i++;
                if (i < source.Length)
                    i++; // consume ']'
            }

            // Nullable marker (allow optional whitespace before '?', e.g.
            // `Texture2D ? iconName` — Roslyn accepts the spacing, so we must
            // too. Canonicalize the captured typeName by appending '?' with no
            // intervening whitespace so the formatter re-emits a clean
            // `Texture2D? iconName`.
            int beforeNullable = i;
            int peek = i;
            while (peek < source.Length && (source[peek] == ' ' || source[peek] == '\t'))
                peek++;
            if (peek < source.Length && source[peek] == '?')
            {
                i = peek + 1;
                typeName = source.Substring(start, beforeNullable - start) + "?";
                return true;
            }

            typeName = source.Substring(start, i - start);
            return typeName.Length > 0;
        }

        /// <summary>
        /// Reads the default-value expression for a parameter, stopping at the
        /// first unbalanced <c>,</c> or <c>)</c> (i.e., the next parameter or the
        /// closing paren of the list).
        /// Handles nested parentheses, brackets, braces, string literals, and
        /// char literals correctly so that commas inside them are not mistaken for
        /// parameter separators.
        /// </summary>
        private static string ReadDefaultValue(string source, ref int i)
        {
            int start = i;
            int parenDepth = 0;
            int braceDepth = 0;
            int bracketDepth = 0;

            while (i < source.Length)
            {
                // Skip string / char literals so commas inside them are ignored
                if (TrySkipNonCodeSpan(source, ref i, source.Length))
                    continue;

                char c = source[i];

                if (c == '(') { parenDepth++; i++; continue; }
                if (c == ')') { if (parenDepth > 0) { parenDepth--; i++; continue; } break; }
                if (c == '{') { braceDepth++; i++; continue; }
                if (c == '}') { if (braceDepth > 0) { braceDepth--; i++; continue; } break; }
                if (c == '[') { bracketDepth++; i++; continue; }
                if (c == ']') { if (bracketDepth > 0) { bracketDepth--; i++; continue; } break; }
                if (c == ',' && parenDepth == 0 && braceDepth == 0 && bracketDepth == 0) break;

                i++;
            }

            return source.Substring(start, i - start).Trim();
        }


        private static bool LooksLikeMarkupRoot(string source, int start, int endExclusive)
        {
            int i = start;
            while (i < endExclusive)
            {
                if (TrySkipNonCodeSpan(source, ref i, endExclusive))
                    continue;

                if (char.IsWhiteSpace(source[i]))
                {
                    i++;
                    continue;
                }

                // Accept '<' (element), '@' (directive / control flow), or '{' which
                // is either a brace expression `{expr}` (canonical inline expression
                // syntax) or a block comment `{/* ... */}` opener.
                return source[i] == '<' || source[i] == '@' || source[i] == '{';
            }

            return false;
        }

        private static bool LooksLikeFunctionStyleComponent(string source, int start)
        {
            int i = start;
            int line = 1;
            SkipLeadingFunctionStyleTrivia(source, ref i, ref line);
            // Skip any leading `using X.Y.Z;`, `@uss "path"`, `@namespace X.Y`,
            // `import { … } from "…"`, and `import "@Ns"` lines, in any order.
            var dummy = new List<string>();
            var dummyUsingDirectives = new List<UsingDirective>();
            var dummyImports = new List<ImportDeclaration>();
            bool skippedSomething;
            do
            {
                skippedSomething = false;
                if (TryReadFunctionStyleUsing(source, ref i, ref line, dummy))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line);
                    skippedSomething = true;
                }
                if (TryReadFunctionStyleUss(source, ref i, ref line, dummy))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line);
                    skippedSomething = true;
                }
                if (TryReadFunctionStyleNamespaceDirective(source, ref i, ref line, out _))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line);
                    skippedSomething = true;
                }
                if (TryReadFunctionStyleBackendDirective(source, ref i, ref line, out _))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line);
                    skippedSomething = true;
                }
                if (TryReadFunctionStyleNamespaceImport(source, ref i, ref line, dummy, dummyUsingDirectives))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line);
                    skippedSomething = true;
                }
                if (TryReadFunctionStyleImport(source, ref i, ref line, dummyImports))
                {
                    SkipLeadingFunctionStyleTrivia(source, ref i, ref line);
                    skippedSomething = true;
                }
            } while (skippedSomething);
            return TryReadKeywordAt(source, i, "component")
                || TryReadKeywordAt(source, i, "hook")
                || TryReadKeywordAt(source, i, "module");
        }

        /// <summary>
        /// Tries to read a single <c>@namespace X.Y</c> line at the current position.
        /// On success advances <paramref name="i"/> past the line terminator and sets
        /// <paramref name="namespaceName"/>. On failure restores <paramref name="i"/>
        /// and returns false.
        /// </summary>
        private static bool TryReadFunctionStyleNamespaceDirective(
            string source,
            ref int i,
            ref int line,
            out string? namespaceName
        )
        {
            namespaceName = null;
            int savedI = i;
            int savedLine = line;

            // Allow leading spaces/tabs.
            while (i < source.Length && (source[i] == ' ' || source[i] == '\t'))
                i++;

            // Must start with '@'.
            if (i >= source.Length || source[i] != '@')
            {
                i = savedI;
                line = savedLine;
                return false;
            }

            i++; // consume '@'

            if (!TryReadKeyword(source, ref i, "namespace"))
            {
                i = savedI;
                line = savedLine;
                return false;
            }

            SkipSpaces(source, ref i);

            // Read namespace name up to ';' or end-of-line.
            int nameStart = i;
            while (i < source.Length && source[i] != ';' && !IsNewline(source[i]))
                i++;

            string ns = source.Substring(nameStart, i - nameStart).Trim();

            // Consume optional ';'.
            if (i < source.Length && source[i] == ';')
                i++;

            // Skip remainder of line and the newline.
            while (i < source.Length && !IsNewline(source[i]))
                i++;
            if (i < source.Length && IsNewline(source[i]))
                ConsumeNewline(source, ref i, ref line);

            if (string.IsNullOrWhiteSpace(ns))
            {
                i = savedI;
                line = savedLine;
                return false;
            }

            namespaceName = ns;
            return true;
        }

        /// <summary>
        /// Tries to read a single <c>@backend ugui</c> line at the current position.
        /// On success advances <paramref name="i"/> past the line terminator and sets
        /// <paramref name="backendName"/> to the raw (trimmed) payload — validation of
        /// the value happens at the call site so invalid values still CONSUME the line
        /// (the U-09 rule: a rejected directive must not stall the preamble cursor).
        /// On failure restores <paramref name="i"/> and returns false.
        /// </summary>
        private static bool TryReadFunctionStyleBackendDirective(
            string source,
            ref int i,
            ref int line,
            out string? backendName
        )
        {
            backendName = null;
            int savedI = i;
            int savedLine = line;

            while (i < source.Length && (source[i] == ' ' || source[i] == '\t'))
                i++;

            if (i >= source.Length || source[i] != '@')
            {
                i = savedI;
                line = savedLine;
                return false;
            }

            i++; // consume '@'

            if (!TryReadKeyword(source, ref i, "backend"))
            {
                i = savedI;
                line = savedLine;
                return false;
            }

            SkipSpaces(source, ref i);

            int nameStart = i;
            while (i < source.Length && source[i] != ';' && !IsNewline(source[i]))
                i++;

            string value = source.Substring(nameStart, i - nameStart).Trim();

            if (i < source.Length && source[i] == ';')
                i++;

            while (i < source.Length && !IsNewline(source[i]))
                i++;
            if (i < source.Length && IsNewline(source[i]))
                ConsumeNewline(source, ref i, ref line);

            if (string.IsNullOrWhiteSpace(value))
            {
                i = savedI;
                line = savedLine;
                return false;
            }

            backendName = value;
            return true;
        }

        /// <summary>
        /// Tries to read a single <c>using Namespace.Name;</c> line (or the <c>@using</c> directive
        /// form) at the current position. On success advances <paramref name="i"/> past the line
        /// terminator, appends the namespace string to <paramref name="usings"/>, and (when supplied)
        /// appends a positioned <see cref="UsingDirective"/> to <paramref name="usingDirectives"/>.
        /// On failure restores <paramref name="i"/> and returns false.
        /// </summary>
        private static bool TryReadFunctionStyleUsing(
            string source,
            ref int i,
            ref int line,
            List<string> usings,
            List<UsingDirective>? usingDirectives = null
        )
        {
            int savedI = i;
            int savedLine = line;

            // Allow leading spaces/tabs — newlines are already consumed by trivia before each call.
            while (i < source.Length && (source[i] == ' ' || source[i] == '\t'))
                i++;

            int keywordPos = i; // start of `@using`/`using` — the directive column anchor

            // Accept both `using X.Y.Z;` (C# form) and `@using X.Y.Z` (directive form).
            if (i < source.Length && source[i] == '@')
                i++;

            if (!TryReadKeyword(source, ref i, "using"))
            {
                i = savedI;
                line = savedLine;
                return false;
            }

            SkipSpaces(source, ref i);

            // Read namespace name up to ';' or end-of-line.
            int nameStart = i;
            while (i < source.Length && source[i] != ';' && !IsNewline(source[i]))
                i++;

            string namespaceName = source.Substring(nameStart, i - nameStart).Trim();

            // Consume optional ';'.
            if (i < source.Length && source[i] == ';')
                i++;

            // Skip rest of line and the newline.
            while (i < source.Length && !IsNewline(source[i]))
                i++;
            if (i < source.Length && IsNewline(source[i]))
                ConsumeNewline(source, ref i, ref line);

            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                usings.Add(namespaceName);
                usingDirectives?.Add(new UsingDirective(
                    Payload: namespaceName,
                    Line: savedLine,
                    Column: ColAtPos(source, keywordPos),
                    PayloadColumn: ColAtPos(source, nameStart),
                    FromImportSyntax: false));
            }

            return true;
        }

        /// <summary>
        /// Attempts to read an <c>@uss "path"</c> directive.
        /// On failure restores <paramref name="i"/> and returns false.
        /// </summary>
        private static bool TryReadFunctionStyleUss(
            string source,
            ref int i,
            ref int line,
            List<string> ussFiles
        )
        {
            int savedI = i;
            int savedLine = line;

            while (i < source.Length && (source[i] == ' ' || source[i] == '\t'))
                i++;

            // @uss is always directive-form (requires '@')
            if (i >= source.Length || source[i] != '@')
            {
                i = savedI;
                line = savedLine;
                return false;
            }
            i++;

            if (!TryReadKeyword(source, ref i, "uss"))
            {
                i = savedI;
                line = savedLine;
                return false;
            }

            SkipSpaces(source, ref i);

            // Expect a quoted path: "..." or '...'
            if (i >= source.Length || (source[i] != '"' && source[i] != '\''))
            {
                i = savedI;
                line = savedLine;
                return false;
            }

            char quote = source[i];
            i++; // skip opening quote
            int pathStart = i;
            while (i < source.Length && source[i] != quote && !IsNewline(source[i]))
                i++;

            string path = source.Substring(pathStart, i - pathStart).Trim();

            // Consume closing quote
            if (i < source.Length && source[i] == quote)
                i++;

            // Consume optional ';'
            if (i < source.Length && source[i] == ';')
                i++;

            // Skip rest of line and the newline.
            while (i < source.Length && !IsNewline(source[i]))
                i++;
            if (i < source.Length && IsNewline(source[i]))
                ConsumeNewline(source, ref i, ref line);

            if (!string.IsNullOrWhiteSpace(path))
                ussFiles.Add(path);

            return true;
        }

        private static void SkipLeadingFunctionStyleTrivia(string source, ref int i, ref int line)
            => SkipLeadingFunctionStyleTrivia(source, ref i, ref line, trivia: null);

        /// <summary>
        /// Skips whitespace and comments before the preamble/<c>component</c> keyword.
        /// When <paramref name="trivia"/> is non-null, every consumed <c>//</c>, <c>/* */</c>,
        /// or <c>&lt;!-- --&gt;</c> comment is appended (raw text incl. delimiters, whether it
        /// is block-form, and its 1-based line) so callers can re-emit it verbatim on format
        /// instead of silently discarding it (see finding U-01). Pass <c>null</c> for
        /// lookahead-only probes (e.g. <see cref="LooksLikeFunctionStyleComponent"/>) that
        /// never construct a <see cref="DirectiveSet"/>.
        /// </summary>
        private static void SkipLeadingFunctionStyleTrivia(
            string source, ref int i, ref int line,
            List<(string Text, bool IsBlock, int Line)>? trivia)
        {
            while (i < source.Length)
            {
                if (source[i] == ' ' || source[i] == '\t')
                {
                    i++;
                    continue;
                }

                if (IsNewline(source[i]))
                {
                    ConsumeNewline(source, ref i, ref line);
                    continue;
                }

                // // line comment
                if (
                    source[i] == '/'
                    && i + 1 < source.Length
                    && source[i + 1] == '/'
                )
                {
                    int start = i;
                    int startLine = line;
                    i += 2;
                    while (i < source.Length && !IsNewline(source[i]))
                        i++;
                    trivia?.Add((source.Substring(start, i - start), false, startLine));
                    continue;
                }

                // /* block comment */
                if (
                    source[i] == '/'
                    && i + 1 < source.Length
                    && source[i + 1] == '*'
                )
                {
                    int start = i;
                    int startLine = line;
                    i += 2;
                    while (i < source.Length)
                    {
                        if (IsNewline(source[i]))
                        {
                            ConsumeNewline(source, ref i, ref line);
                            continue;
                        }

                        if (
                            source[i] == '*'
                            && i + 1 < source.Length
                            && source[i + 1] == '/'
                        )
                        {
                            i += 2;
                            break;
                        }

                        i++;
                    }
                    trivia?.Add((source.Substring(start, i - start), true, startLine));
                    continue;
                }

                // <!-- html comment -->
                if (
                    source[i] == '<'
                    && i + 3 < source.Length
                    && source[i + 1] == '!'
                    && source[i + 2] == '-'
                    && source[i + 3] == '-'
                )
                {
                    int start = i;
                    int startLine = line;
                    i += 4;
                    while (i < source.Length)
                    {
                        if (IsNewline(source[i]))
                        {
                            ConsumeNewline(source, ref i, ref line);
                            continue;
                        }

                        if (
                            source[i] == '-'
                            && i + 2 < source.Length
                            && source[i + 1] == '-'
                            && source[i + 2] == '>'
                        )
                        {
                            i += 3;
                            break;
                        }

                        i++;
                    }
                    trivia?.Add((source.Substring(start, i - start), true, startLine));
                    continue;
                }

                break;
            }
        }

        private static bool TryFindNextNonWhitespace(string source, int start, out int pos)
        {
            pos = start;
            while (pos < source.Length && char.IsWhiteSpace(source[pos]))
                pos++;

            return pos < source.Length;
        }

        private static bool IsDirectiveHeaderAt(string source, int atPos)
        {
            if (atPos < 0 || atPos >= source.Length || source[atPos] != '@')
                return false;

            int i = atPos + 1;
            int start = i;
            while (i < source.Length && char.IsLetter(source[i]))
                i++;

            if (i <= start)
                return false;

            string keyword = source.Substring(start, i - start).ToLowerInvariant();
            return s_topLevelKeywords.Contains(keyword);
        }

        private static bool TryFindTopLevelReturn(
            string source,
            int start,
            int endExclusive,
            out int returnStart,
            out int openParen,
            out int closeParen,
            out int stmtEndExclusive,
            bool useLastReturn = true
        )
        {
            returnStart = -1;
            openParen = -1;
            closeParen = -1;
            stmtEndExclusive = -1;

            int i = start;
            int braceDepth = 0;
            int parenDepth = 0;
            int bracketDepth = 0;

            while (i < endExclusive)
            {
                if (TrySkipNonCodeSpan(source, ref i, endExclusive))
                    continue;

                char c = source[i];

                if (c == '{')
                {
                    braceDepth++;
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    if (braceDepth > 0)
                        braceDepth--;
                    i++;
                    continue;
                }
                if (c == '(')
                {
                    parenDepth++;
                    i++;
                    continue;
                }
                if (c == ')')
                {
                    if (parenDepth > 0)
                        parenDepth--;
                    i++;
                    continue;
                }
                if (c == '[')
                {
                    bracketDepth++;
                    i++;
                    continue;
                }
                if (c == ']')
                {
                    if (bracketDepth > 0)
                        bracketDepth--;
                    i++;
                    continue;
                }

                if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                {
                    if (TryReadKeywordAt(source, i, "return"))
                    {
                        int candidateStart = i;
                        int j = i + "return".Length;
                        SkipWhitespace(source, ref j);

                        if (j < endExclusive && source[j] == '(')
                        {
                            int candidateOpenParen = j;
                            if (TryReadBalancedParen(source, candidateOpenParen, endExclusive, out int closeParenExclusive))
                            {
                                int candidateCloseParen = closeParenExclusive - 1;
                                j = closeParenExclusive;
                                SkipWhitespace(source, ref j);
                                if (j < endExclusive && source[j] == ';')
                                {
                                    returnStart = candidateStart;
                                    openParen = candidateOpenParen;
                                    closeParen = candidateCloseParen;
                                    stmtEndExclusive = j + 1;

                                    if (!useLastReturn)
                                        return true;

                                    // Continue scanning to find the last match
                                    i = stmtEndExclusive;
                                    continue;
                                }
                            }
                        }

                        if (!useLastReturn)
                            return false;
                    }
                }

                i++;
            }

            return returnStart >= 0;
        }

        /// <summary>
        /// Scans for a top-level (brace/paren/bracket depth 0) <c>return null;</c>
        /// statement. A component whose body has one — and no top-level
        /// <c>return (…);</c> — is a valid null-only component (React case 2:
        /// always renders nothing); the caller accepts it instead of raising
        /// UITKX2101/2102. Returns the index of the <c>return</c> keyword, or -1.
        /// </summary>
        private static int FindTopLevelReturnNull(string source, int start, int endExclusive)
        {
            int i = start;
            int braceDepth = 0;
            int parenDepth = 0;
            int bracketDepth = 0;

            while (i < endExclusive)
            {
                if (TrySkipNonCodeSpan(source, ref i, endExclusive))
                    continue;

                char c = source[i];
                if (c == '{') { braceDepth++; i++; continue; }
                if (c == '}') { if (braceDepth > 0) braceDepth--; i++; continue; }
                if (c == '(') { parenDepth++; i++; continue; }
                if (c == ')') { if (parenDepth > 0) parenDepth--; i++; continue; }
                if (c == '[') { bracketDepth++; i++; continue; }
                if (c == ']') { if (bracketDepth > 0) bracketDepth--; i++; continue; }

                if (
                    braceDepth == 0
                    && parenDepth == 0
                    && bracketDepth == 0
                    && TryReadKeywordAt(source, i, "return")
                )
                {
                    int j = i + "return".Length;
                    SkipWhitespace(source, ref j);
                    if (TryReadKeywordAt(source, j, "null"))
                    {
                        j += "null".Length;
                        SkipWhitespace(source, ref j);
                        if (j < endExclusive && source[j] == ';')
                            return i;
                    }
                }

                i++;
            }

            return -1;
        }

        private static int FindTopLevelReturnAfter(string source, int start, int endExclusive)
        {
            int i = start;
            int braceDepth = 0;
            int parenDepth = 0;
            int bracketDepth = 0;

            while (i < endExclusive)
            {
                if (TrySkipNonCodeSpan(source, ref i, endExclusive))
                    continue;

                char c = source[i];
                if (c == '{')
                {
                    braceDepth++;
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    if (braceDepth > 0)
                        braceDepth--;
                    i++;
                    continue;
                }
                if (c == '(')
                {
                    parenDepth++;
                    i++;
                    continue;
                }
                if (c == ')')
                {
                    if (parenDepth > 0)
                        parenDepth--;
                    i++;
                    continue;
                }
                if (c == '[')
                {
                    bracketDepth++;
                    i++;
                    continue;
                }
                if (c == ']')
                {
                    if (bracketDepth > 0)
                        bracketDepth--;
                    i++;
                    continue;
                }

                if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                {
                    if (TryReadKeywordAt(source, i, "return"))
                        return i;
                }

                i++;
            }

            return -1;
        }

        private static bool TryReadBalancedBlock(string source, int openBracePos, out int closeExclusive)
        {
            closeExclusive = -1;
            if (openBracePos < 0 || openBracePos >= source.Length || source[openBracePos] != '{')
                return false;

            int i = openBracePos + 1;
            int depth = 1;
            while (i < source.Length)
            {
                if (TrySkipNonCodeSpan(source, ref i, source.Length))
                    continue;

                if (source[i] == '{')
                {
                    depth++;
                    i++;
                    continue;
                }

                if (source[i] == '}')
                {
                    depth--;
                    i++;
                    if (depth == 0)
                    {
                        closeExclusive = i;
                        return true;
                    }
                    continue;
                }

                i++;
            }

            return false;
        }

        private static bool TryReadBalancedParen(
            string source,
            int openParenPos,
            int endExclusive,
            out int closeExclusive
        )
        {
            closeExclusive = -1;
            if (openParenPos < 0 || openParenPos >= source.Length || source[openParenPos] != '(')
                return false;

            int i = openParenPos + 1;
            int depth = 1;
            while (i < endExclusive)
            {
                if (TrySkipNonCodeSpan(source, ref i, endExclusive))
                    continue;

                if (source[i] == '(')
                {
                    depth++;
                    i++;
                    continue;
                }

                if (source[i] == ')')
                {
                    depth--;
                    i++;
                    if (depth == 0)
                    {
                        closeExclusive = i;
                        return true;
                    }
                    continue;
                }

                i++;
            }

            return false;
        }

        private static bool TrySkipNonCodeSpan(string source, ref int i, int limit)
            => CSharpLexFacts.TrySkipNonCode(source, ref i, limit);

        private static bool TryReadKeyword(string source, ref int i, string keyword)
        {
            if (!TryReadKeywordAt(source, i, keyword))
                return false;
            i += keyword.Length;
            return true;
        }

        private static bool TryReadKeywordAt(string source, int i, string keyword)
        {
            if (i < 0 || i + keyword.Length > source.Length)
                return false;

            if (!string.Equals(source.Substring(i, keyword.Length), keyword, StringComparison.Ordinal))
                return false;

            char prev = i > 0 ? source[i - 1] : '\0';
            char next = i + keyword.Length < source.Length ? source[i + keyword.Length] : '\0';
            bool prevOk = prev == '\0' || !(char.IsLetterOrDigit(prev) || prev == '_');
            bool nextOk = next == '\0' || !(char.IsLetterOrDigit(next) || next == '_');
            return prevOk && nextOk;
        }

        private static bool TryReadIdentifier(string source, ref int i, out string ident)
        {
            ident = string.Empty;
            if (i >= source.Length || !(char.IsLetter(source[i]) || source[i] == '_'))
                return false;

            int start = i;
            i++;
            while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                i++;

            ident = source.Substring(start, i - start);
            return true;
        }

        private static bool IsPascalCase(string ident)
        {
            if (string.IsNullOrEmpty(ident) || !char.IsUpper(ident[0]))
                return false;

            for (int i = 1; i < ident.Length; i++)
                if (!char.IsLetterOrDigit(ident[i]))
                    return false;

            return true;
        }

        private static void SkipSpaces(string source, ref int i)
        {
            while (i < source.Length && (source[i] == ' ' || source[i] == '\t'))
                i++;
        }

        /// <summary>
        /// Returns the 0-based column of position <paramref name="pos"/> in
        /// <paramref name="source"/> by scanning backwards to the nearest newline.
        /// </summary>
        private static int ComputeColumn(string source, int pos)
        {
            int lineStart = source.LastIndexOf('\n', pos > 0 ? pos - 1 : 0);
            return lineStart < 0 ? pos : pos - lineStart - 1;
        }

        private static void SkipWhitespace(string source, ref int i)
        {
            while (i < source.Length && char.IsWhiteSpace(source[i]))
                i++;
        }

        private static void SkipWhitespaceAndNewlines(string source, ref int i, ref int line)
        {
            while (i < source.Length)
            {
                if (source[i] == ' ' || source[i] == '\t')
                {
                    i++;
                    continue;
                }
                if (IsNewline(source[i]))
                {
                    ConsumeNewline(source, ref i, ref line);
                    continue;
                }
                break;
            }
        }

        /// <summary>
        /// Returns the first position in <paramref name="source"/> at or after
        /// <paramref name="start"/> that is not ASCII whitespace.  Returns
        /// <paramref name="start"/> if the character there is already non-whitespace
        /// or if <paramref name="start"/> is at/past the end of the string.
        /// </summary>
        private static int FirstNonWhitespaceAt(string source, int start)
        {
            while (start < source.Length && (source[start] == ' ' || source[start] == '\t'
                                          || source[start] == '\r' || source[start] == '\n'))
                start++;
            return start;
        }

        // ── @( scanner ───────────────────────────────────────────────────────

        /// <summary>
        /// Scans <paramref name="source"/> between <paramref name="rangeStart"/> and
        /// <paramref name="rangeEnd"/> for <c>@(</c> tokens that are outside strings,
        /// comments, and embedded JSX markup ranges, emitting
        /// <see cref="DiagnosticCodes.AtExprNotSupported"/> for each occurrence.
        /// </summary>
        private static void ScanAtExprInSetupCode(
            string source,
            int rangeStart,
            int rangeEnd,
            List<ParseDiagnostic> diagnosticBag,
            ImmutableArray<(int Start, int End, int Line)> jsxRanges = default,
            ImmutableArray<(int Start, int End, int Line)> bareJsxRanges = default)
        {
            int i = rangeStart;
            while (i < rangeEnd)
            {
                char ch = source[i];

                // Skip verbatim strings @"..."
                if (ch == '@' && i + 1 < rangeEnd && source[i + 1] == '"')
                {
                    i += 2;
                    while (i < rangeEnd)
                    {
                        if (source[i] == '"')
                        {
                            if (i + 1 < rangeEnd && source[i + 1] == '"')
                                i += 2; // escaped ""
                            else { i++; break; }
                        }
                        else i++;
                    }
                    continue;
                }

                // Skip regular strings "..."
                if (ch == '"')
                {
                    i++;
                    while (i < rangeEnd && source[i] != '"')
                    {
                        if (source[i] == '\\') i++;
                        i++;
                    }
                    if (i < rangeEnd) i++;
                    continue;
                }

                // Skip character literals '.'
                if (ch == '\'')
                {
                    i++;
                    if (i < rangeEnd && source[i] == '\\') i++;
                    if (i < rangeEnd) i++; // the char
                    if (i < rangeEnd && source[i] == '\'') i++;
                    continue;
                }

                // Skip single-line comments //
                if (ch == '/' && i + 1 < rangeEnd && source[i + 1] == '/')
                {
                    i += 2;
                    while (i < rangeEnd && source[i] != '\n') i++;
                    continue;
                }

                // Skip block comments /* ... */
                if (ch == '/' && i + 1 < rangeEnd && source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < rangeEnd && !(source[i] == '*' && source[i + 1] == '/'))
                        i++;
                    if (i + 1 < rangeEnd) i += 2;
                    continue;
                }

                // Detect @(
                if (ch == '@' && i + 1 < rangeEnd && source[i + 1] == '(')
                {
                    // @( inside embedded JSX markup is re-parsed by UitkxParser,
                    // which emits UITKX0306 from its markup-context branch — skip
                    // here to avoid double-firing.
                    if (!IsInsideJsxRange(i, jsxRanges) && !IsInsideJsxRange(i, bareJsxRanges))
                    {
                        diagnosticBag.Add(new ParseDiagnostic
                        {
                            Code = Diagnostics.DiagnosticCodes.AtExprNotSupported,
                            Severity = ParseSeverity.Error,
                            SourceLine = LineAtPos(source, i),
                            SourceColumn = ColAtPos(source, i),
                            EndColumn = ColAtPos(source, i) + 2,
                            Message = "'@(...)' syntax is not supported in setup code. " +
                                      "Use a local variable instead: var x = (...); then reference x.",
                        });
                    }
                }

                i++;
            }
        }

        private static bool IsInsideJsxRange(int pos, ImmutableArray<(int Start, int End, int Line)> ranges)
        {
            if (ranges.IsDefaultOrEmpty) return false;
            foreach (var (s, e, _) in ranges)
                if (pos >= s && pos < e) return true;
            return false;
        }

        private static int LineAtPos(string source, int pos)
        {
            int line = 1;
            for (int i = 0; i < pos && i < source.Length; i++)
                if (source[i] == '\n')
                    line++;
            return line;
        }

        /// <summary>
        /// Returns the 0-based column of <paramref name="pos"/> within its line
        /// (number of characters after the last '\n' before <paramref name="pos"/>).
        /// </summary>
        private static int ColAtPos(string source, int pos)
        {
            int col = 0;
            while (pos > 0 && source[pos - 1] != '\n')
            {
                pos--;
                col++;
            }
            return col;
        }

        private static bool IsNewline(char c) => c == '\r' || c == '\n';

        // ── JSX block range finder ────────────────────────────────────────────

        /// <summary>
        /// For each paren-wrapped JSX block, checks whether a semicolon follows the
        /// closing <c>)</c>.  If not, emits a <c>CS1002</c> diagnostic.
        /// </summary>
        private static void CheckMissingSemicolonAfterJsxParenBlocks(
            string source,
            ImmutableArray<(int Start, int End, int Line)> ranges,
            List<ParseDiagnostic> diagnosticBag)
        {
            if (ranges.IsDefaultOrEmpty) return;
            foreach (var (start, end, _) in ranges)
            {
                // Only check paren-wrapped blocks — the char before Start is '('.
                if (start <= 0 || source[start - 1] != '(') continue;

                // 'end' is position of ')'. Scan past whitespace and comments.
                int pos = SkipWhitespaceAndComments(source, end + 1);

                if (pos >= source.Length)
                {
                    AddSemicolonDiagnostic(source, end, diagnosticBag);
                    continue;
                }

                char next = source[pos];
                // Valid continuations after ')' — operators, ternary, comma, braces, etc.
                if (next == ';' || next == ':' || next == ',' || next == ')' ||
                    next == '.' || next == '?' || next == '!' || next == '[' ||
                    next == '}' || next == '{' ||
                    next == '+' || next == '-' || next == '*' || next == '/' ||
                    next == '%' || next == '&' || next == '|' || next == '^' ||
                    next == '<' || next == '>' || next == '=' || next == '~')
                    continue;

                AddSemicolonDiagnostic(source, end, diagnosticBag);
            }
        }

        /// <summary>
        /// Advances past whitespace, <c>// line comments</c>, and <c>/* block comments */</c>.
        /// </summary>
        private static int SkipWhitespaceAndComments(string source, int pos)
        {
            while (pos < source.Length)
            {
                char c = source[pos];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    pos++;
                    continue;
                }
                if (c == '/' && pos + 1 < source.Length)
                {
                    if (source[pos + 1] == '/')
                    {
                        // Line comment — skip to end of line
                        pos += 2;
                        while (pos < source.Length && source[pos] != '\n')
                            pos++;
                        continue;
                    }
                    if (source[pos + 1] == '*')
                    {
                        // Block comment — skip to */
                        pos += 2;
                        while (pos + 1 < source.Length &&
                               !(source[pos] == '*' && source[pos + 1] == '/'))
                            pos++;
                        pos += 2; // skip */
                        continue;
                    }
                }
                break;
            }
            return pos;
        }

        private static void AddSemicolonDiagnostic(
            string source, int parenPos, List<ParseDiagnostic> diagnosticBag)
        {
            diagnosticBag.Add(new ParseDiagnostic
            {
                Code     = "CS1002",
                Severity = ParseSeverity.Error,
                SourceLine   = LineAtPos(source, parenPos),
                SourceColumn = ColAtPos(source, parenPos) + 1,
                Message  = "; expected",
            });
        }

        /// <summary>
        /// Scans <paramref name="source"/> between <paramref name="rangeStart"/> and
        /// <paramref name="rangeEnd"/> for UITKX JSX paren blocks of the form
        /// <c>(&lt;Element&gt;...&lt;/Element&gt;)</c> and returns a list of
        /// <c>(Start, End, Line)</c> tuples for each one found.
        /// <para>
        /// <c>Start</c> = char index just inside the opening <c>(</c>;<br/>
        /// <c>End</c>   = exclusive index at the closing <c>)</c>;<br/>
        /// <c>Line</c>  = 1-based source line of <c>Start</c>.
        /// </para>
        /// </summary>
        public static ImmutableArray<(int Start, int End, int Line)> FindJsxBlockRanges(
            string source, int rangeStart, int rangeEnd)
        {
            var result = ImmutableArray.CreateBuilder<(int, int, int)>();
            int i = rangeStart;
            while (i < rangeEnd)
            {
                // Skip // line comments
                if (source[i] == '/' && i + 1 < rangeEnd && source[i + 1] == '/')
                {
                    while (i < rangeEnd && source[i] != '\n') i++;
                    continue;
                }

                // Skip /* ... */ block comments (U-07: this method was previously
                // block-comment-blind, unlike the sibling FindBareJsxRanges below,
                // so a commented-out "(<Tag/>)" was misread as live JSX content).
                if (source[i] == '/' && i + 1 < rangeEnd && source[i + 1] == '*')
                {
                    int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end >= 0 ? end + 2 : rangeEnd;
                    continue;
                }

                // Skip string and char literals
                if (TrySkipStringOrCharLiteral(source, rangeEnd, ref i))
                    continue;

                // ── Bare arrow: => <Tag ──────────────────────────────────
                if (source[i] == '=' && i + 1 < rangeEnd && source[i + 1] == '>')
                {
                    int peek = i + 2;
                    while (peek < rangeEnd &&
                           (source[peek] == ' '  || source[peek] == '\t' ||
                            source[peek] == '\r' || source[peek] == '\n'))
                        peek++;

                    if (peek < rangeEnd && source[peek] == '<'
                        && peek + 1 < rangeEnd && char.IsLetter(source[peek + 1]))
                    {
                        int jsxEnd = FindJsxElementEnd(source, peek, rangeEnd);
                        if (jsxEnd > peek)
                        {
                            int blockLine = LineAtPos(source, peek);
                            result.Add((peek, jsxEnd, blockLine));
                            i = jsxEnd;
                            continue;
                        }
                    }
                }

                // ── Paren-wrapped: ( <Tag ────────────────────────────────
                if (source[i] != '(')
                {
                    i++;
                    continue;
                }

                // Peek past whitespace / newlines to see if the next token is '<' or '@directive'
                int peek2 = i + 1;
                while (peek2 < rangeEnd &&
                       (source[peek2] == ' '  || source[peek2] == '\t' ||
                        source[peek2] == '\r' || source[peek2] == '\n'))
                    peek2++;

                bool isJsx = peek2 < rangeEnd && source[peek2] == '<';
                bool isDirective = !isJsx && peek2 < rangeEnd && source[peek2] == '@'
                    && StartsWithDirectiveKeyword(source, peek2 + 1, rangeEnd);

                if (!isJsx && !isDirective)
                {
                    i++;
                    continue;
                }

                // Balance parens to find the matching ')'
                int depth = 1;
                int j     = i + 1;
                while (j < rangeEnd && depth > 0)
                {
                    if (TrySkipStringOrCharLiteral(source, rangeEnd, ref j))
                        continue;
                    if      (source[j] == '(') depth++;
                    else if (source[j] == ')') depth--;
                    j++;
                }

                if (depth == 0)
                {
                    int blockStart = i + 1;   // content starts after '('
                    int blockEnd   = j - 1;   // exclusive — at the ')'
                    int blockLine  = LineAtPos(source, blockStart);
                    result.Add((blockStart, blockEnd, blockLine));
                    i = j; // hop past the entire block
                }
                else
                {
                    i++; // unbalanced — skip
                }
            }
            return result.ToImmutable();
        }

        /// <summary>
        /// Returns true if <paramref name="source"/> at <paramref name="pos"/>
        /// starts with a UITKX directive keyword (if, foreach, for, while, switch)
        /// followed by a non-identifier character.
        /// </summary>
        private static bool StartsWithDirectiveKeyword(string source, int pos, int end)
        {
            // Ordered longest-first to avoid "for" matching "foreach"
            ReadOnlySpan<char> text = source.AsSpan();
            return MatchKeyword(text, pos, end, "switch")
                || MatchKeyword(text, pos, end, "foreach")
                || MatchKeyword(text, pos, end, "while")
                || MatchKeyword(text, pos, end, "for")
                || MatchKeyword(text, pos, end, "if");
        }

        private static bool MatchKeyword(ReadOnlySpan<char> source, int pos, int end, string keyword)
        {
            if (pos + keyword.Length > end)
                return false;
            for (int k = 0; k < keyword.Length; k++)
                if (source[pos + k] != keyword[k])
                    return false;
            // Must be followed by a non-identifier char (or EOF) to avoid partial matches
            return pos + keyword.Length >= end
                || !char.IsLetterOrDigit(source[pos + keyword.Length]);
        }

        /// <summary>
        /// Scans two disjoint (start, end) ranges and returns the combined list.
        /// Useful when setup code spans a gap where <c>return (...)</c> was removed.
        /// </summary>
        private static ImmutableArray<(int Start, int End, int Line)> FindJsxBlockRanges(
            string source,
            int range1Start, int range1End,
            int range2Start, int range2End)
        {
            var r1 = FindJsxBlockRanges(source, range1Start, range1End);
            var r2 = FindJsxBlockRanges(source, range2Start, range2End);
            if (r2.IsDefaultOrEmpty) return r1;
            if (r1.IsDefaultOrEmpty) return r2;
            return r1.AddRange(r2);
        }

        /// <summary>
        /// Finds bare (non-paren-wrapped) JSX ranges in setup code:
        /// <c>return &lt;Tag/&gt;</c>, <c>? &lt;Tag/&gt;</c>,
        /// <c>: &lt;Tag/&gt;</c>, <c>= &lt;Tag/&gt;</c>.
        /// These are NOT detected by <see cref="FindJsxBlockRanges"/> and are
        /// stored separately to avoid breaking the formatter's block-index
        /// alignment.
        /// </summary>
        public static ImmutableArray<(int Start, int End, int Line)> FindBareJsxRanges(
            string source, int rangeStart, int rangeEnd)
        {
            var result = ImmutableArray.CreateBuilder<(int, int, int)>();
            int i = rangeStart;
            while (i < rangeEnd)
            {
                // Skip // line comments
                if (source[i] == '/' && i + 1 < rangeEnd && source[i + 1] == '/')
                {
                    while (i < rangeEnd && source[i] != '\n') i++;
                    continue;
                }

                // Skip /* ... */ block comments
                if (source[i] == '/' && i + 1 < rangeEnd && source[i + 1] == '*')
                {
                    int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end >= 0 ? end + 2 : rangeEnd;
                    continue;
                }

                // Skip string and char literals
                if (TrySkipStringOrCharLiteral(source, rangeEnd, ref i))
                    continue;

                // ── Bare return: return <Tag ─────────────────────────────
                if (source[i] == 'r' && i + 5 < rangeEnd
                    && source.Substring(i, 6) == "return"
                    && (i == 0 || !(char.IsLetterOrDigit(source[i - 1]) || source[i - 1] == '_'))
                    && (i + 6 >= rangeEnd || !(char.IsLetterOrDigit(source[i + 6]) || source[i + 6] == '_')))
                {
                    int peek = i + 6;
                    while (peek < rangeEnd &&
                           (source[peek] == ' '  || source[peek] == '\t' ||
                            source[peek] == '\r' || source[peek] == '\n'))
                        peek++;

                    if (peek < rangeEnd && source[peek] == '<'
                        && peek + 1 < rangeEnd && char.IsLetter(source[peek + 1]))
                    {
                        int jsxEnd = FindJsxElementEnd(source, peek, rangeEnd);
                        if (jsxEnd > peek)
                        {
                            int blockLine = LineAtPos(source, peek);
                            result.Add((peek, jsxEnd, blockLine));
                            i = jsxEnd;
                            continue;
                        }
                    }
                }

                // ── Ternary true branch: ? <Tag  (but NOT ?. or ??) ─────
                if (source[i] == '?' && i + 1 < rangeEnd
                    && source[i + 1] != '.' && source[i + 1] != '?')
                {
                    int peek = i + 1;
                    while (peek < rangeEnd &&
                           (source[peek] == ' '  || source[peek] == '\t' ||
                            source[peek] == '\r' || source[peek] == '\n'))
                        peek++;

                    if (peek < rangeEnd && source[peek] == '<'
                        && peek + 1 < rangeEnd && char.IsLetter(source[peek + 1]))
                    {
                        int jsxEnd = FindJsxElementEnd(source, peek, rangeEnd);
                        if (jsxEnd > peek)
                        {
                            int blockLine = LineAtPos(source, peek);
                            result.Add((peek, jsxEnd, blockLine));
                            i = jsxEnd;
                            continue;
                        }
                    }
                }

                // ── Ternary false branch: : <Tag  (but NOT ::) ──────────
                if (source[i] == ':' && i + 1 < rangeEnd
                    && source[i + 1] != ':')
                {
                    int peek = i + 1;
                    while (peek < rangeEnd &&
                           (source[peek] == ' '  || source[peek] == '\t' ||
                            source[peek] == '\r' || source[peek] == '\n'))
                        peek++;

                    if (peek < rangeEnd && source[peek] == '<'
                        && peek + 1 < rangeEnd && char.IsLetter(source[peek + 1]))
                    {
                        int jsxEnd = FindJsxElementEnd(source, peek, rangeEnd);
                        if (jsxEnd > peek)
                        {
                            int blockLine = LineAtPos(source, peek);
                            result.Add((peek, jsxEnd, blockLine));
                            i = jsxEnd;
                            continue;
                        }
                    }
                }

                // ── Bare assignment: = <Tag  (but NOT ==, =>, !=, <=, >=)
                if (source[i] == '=' && i + 1 < rangeEnd
                    && source[i + 1] != '=' && source[i + 1] != '>'
                    && (i == 0 || (source[i - 1] != '!' && source[i - 1] != '<' && source[i - 1] != '>')))
                {
                    int peek = i + 1;
                    while (peek < rangeEnd &&
                           (source[peek] == ' '  || source[peek] == '\t' ||
                            source[peek] == '\r' || source[peek] == '\n'))
                        peek++;

                    if (peek < rangeEnd && source[peek] == '<'
                        && peek + 1 < rangeEnd && char.IsLetter(source[peek + 1]))
                    {
                        int jsxEnd = FindJsxElementEnd(source, peek, rangeEnd);
                        if (jsxEnd > peek)
                        {
                            int blockLine = LineAtPos(source, peek);
                            result.Add((peek, jsxEnd, blockLine));
                            i = jsxEnd;
                            continue;
                        }
                    }
                }

                // ── Logical-AND short-circuit: && <Tag  ───────────────────
                // Recognises the React idiom `cond && <Tag/>`. The splice
                // layer (CSharpEmitter.SpliceExpressionMarkup) desugars this
                // to a ternary because `bool && VirtualNode` is a hard CS0019
                // (no operator overload exists on VirtualNode).
                //
                // Lookahead: must be `&&` (not `&`, `&=`, `&&&`).
                // Lookbehind: must not be preceded by another `&` (avoids
                // re-firing on the second `&` of `&&` if a degenerate input
                // somehow walked one char at a time, and ignores `&&&`).
                if (source[i] == '&' && i + 1 < rangeEnd && source[i + 1] == '&'
                    && (i == 0 || source[i - 1] != '&')
                    && (i + 2 >= rangeEnd || source[i + 2] != '&'))
                {
                    int peek = i + 2;
                    while (peek < rangeEnd &&
                           (source[peek] == ' '  || source[peek] == '\t' ||
                            source[peek] == '\r' || source[peek] == '\n'))
                        peek++;

                    if (peek < rangeEnd && source[peek] == '<'
                        && peek + 1 < rangeEnd && char.IsLetter(source[peek + 1]))
                    {
                        int jsxEnd = FindJsxElementEnd(source, peek, rangeEnd);
                        if (jsxEnd > peek)
                        {
                            int blockLine = LineAtPos(source, peek);
                            result.Add((peek, jsxEnd, blockLine));
                            i = jsxEnd;
                            continue;
                        }
                    }
                }

                i++;
            }
            return result.ToImmutable();
        }

        /// <summary>Two-range overload for <see cref="FindBareJsxRanges"/>.</summary>
        private static ImmutableArray<(int Start, int End, int Line)> FindBareJsxRanges(
            string source,
            int range1Start, int range1End,
            int range2Start, int range2End)
        {
            var r1 = FindBareJsxRanges(source, range1Start, range1End);
            var r2 = FindBareJsxRanges(source, range2Start, range2End);
            if (r2.IsDefaultOrEmpty) return r1;
            if (r1.IsDefaultOrEmpty) return r2;
            return r1.AddRange(r2);
        }

        /// <summary>
        /// Finds the start position of the LHS of a logical-AND (<c>&amp;&amp;</c>)
        /// operator located at <paramref name="ampStart"/>, scanning forward
        /// through <c>source[sliceStart..ampStart]</c>. Used by the splice
        /// layer to desugar the React idiom <c>cond &amp;&amp; &lt;Tag/&gt;</c>
        /// into a ternary <c>(cond) ? V.Tag(...) : (VirtualNode?)null</c>.
        ///
        /// <para>The walker is precedence-aware: it tracks paren / bracket
        /// depth and records boundary tokens at depth 0 that have lower
        /// precedence than <c>&amp;&amp;</c> (and thus terminate the LHS
        /// expression). The LHS starts immediately after the LAST recorded
        /// boundary, with leading whitespace skipped. If no boundary is
        /// found, the LHS spans the full slice.</para>
        ///
        /// <para>Boundary tokens recognised at depth 0:</para>
        /// <list type="bullet">
        ///   <item><c>?</c> ternary (excluding <c>??</c>, <c>?.</c>)</item>
        ///   <item><c>:</c> ternary (excluding <c>::</c>)</item>
        ///   <item><c>??</c> null-coalescing</item>
        ///   <item><c>||</c> logical-or</item>
        ///   <item><c>,</c> argument / element separator</item>
        ///   <item><c>;</c> statement separator</item>
        /// </list>
        ///
        /// <para>String / char literals and line / block comments are skipped
        /// using the same lexer helpers as <see cref="FindBareJsxRanges"/>.
        /// The walker is O(n) on the slice length and allocation-free.</para>
        ///
        /// <para>Returns -1 if the resulting LHS slice (after trimming) would
        /// be empty — caller should treat this as "could not desugar" and
        /// emit a UITKX0026 diagnostic.</para>
        /// </summary>
        public static int FindLhsStartForLogicalAnd(string source, int sliceStart, int ampStart)
        {
            if (sliceStart < 0) sliceStart = 0;
            if (ampStart > source.Length) ampStart = source.Length;
            if (sliceStart >= ampStart) return -1;

            // Boundary tracking is per-paren-depth, not absolute. The `&&`
            // operator lives at whatever depth the walker reaches when it
            // arrives at `ampStart`. Boundaries (`?`, `:`, `??`, `||`, `,`,
            // `;`) only constrain the LHS when they sit at the SAME depth
            // as the `&&`. Example:
            //   `(a ? b : c && X)` — outer `(` enters depth 1; the `?:`
            //   tokens live at depth 1, the `&&` also at depth 1 → they ARE
            //   boundaries (LHS = `c`).
            //   `f(a, b) && X`     — `,` is at depth 1, `&&` at depth 0 →
            //   the `,` is NOT a boundary (LHS = `f(a, b)`).
            //
            // Stack semantics: index 0 holds the outer-scope boundary;
            // entering `(`/`[` pushes a fresh slot for the new scope; closing
            // discards it. The slot at depth 0 (top of stack) when we exit
            // the loop is the LHS start at `&&`'s depth.
            var boundaries = new int[16];
            int depth = 0;
            boundaries[0] = sliceStart;
            int i = sliceStart;

            while (i < ampStart)
            {
                // Skip strings and chars (lexer-aware)
                if (TrySkipStringOrCharLiteral(source, ampStart, ref i))
                    continue;

                // Skip // line comments
                if (i + 1 < ampStart && source[i] == '/' && source[i + 1] == '/')
                {
                    while (i < ampStart && source[i] != '\n') i++;
                    continue;
                }

                // Skip /* ... */ block comments
                if (i + 1 < ampStart && source[i] == '/' && source[i + 1] == '*')
                {
                    int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = (end >= 0 && end + 2 <= ampStart) ? end + 2 : ampStart;
                    continue;
                }

                char c = source[i];

                // Track bracket depth — push/pop a per-depth boundary slot
                if (c == '(' || c == '[')
                {
                    depth++;
                    if (depth >= boundaries.Length)
                        Array.Resize(ref boundaries, boundaries.Length * 2);
                    // Inside the new scope, the LHS-of-`&&` boundary starts
                    // immediately after the opening bracket.
                    boundaries[depth] = i + 1;
                    i++;
                    continue;
                }
                if (c == ')' || c == ']')
                {
                    if (depth > 0) depth--;
                    i++;
                    continue;
                }

                // `??` null-coalescing — must check before single `?`
                if (c == '?' && i + 1 < ampStart && source[i + 1] == '?')
                {
                    i += 2;
                    boundaries[depth] = i;
                    continue;
                }

                // `?` ternary (excluding `??` handled above, and `?.`)
                if (c == '?' && i + 1 < ampStart && source[i + 1] != '.' && source[i + 1] != '?')
                {
                    i++;
                    boundaries[depth] = i;
                    continue;
                }

                // `:` ternary (excluding `::` namespace alias)
                if (c == ':' && i + 1 < ampStart && source[i + 1] != ':')
                {
                    i++;
                    boundaries[depth] = i;
                    continue;
                }

                // `||` logical-or
                if (c == '|' && i + 1 < ampStart && source[i + 1] == '|')
                {
                    i += 2;
                    boundaries[depth] = i;
                    continue;
                }

                // `=` assignment (U-06/U-23: excluding ==, !=, <=, >=, => — the
                // multi-char operators containing '=' that are NOT an assignment
                // boundary) so `x = cond && <T/>` yields LHS `cond`, not `x = cond`.
                if (c == '=')
                {
                    char prevCh = i > sliceStart ? source[i - 1] : '\0';
                    char nextCh = i + 1 < ampStart ? source[i + 1] : '\0';
                    bool isCompoundEq =
                        nextCh == '=' || nextCh == '>'
                        || prevCh == '=' || prevCh == '!' || prevCh == '<' || prevCh == '>';
                    if (!isCompoundEq)
                    {
                        i++;
                        boundaries[depth] = i;
                        continue;
                    }
                }

                // `,` and `;`
                if (c == ',' || c == ';')
                {
                    i++;
                    boundaries[depth] = i;
                    continue;
                }

                i++;
            }

            int lastBoundary = boundaries[depth];

            // Skip leading whitespace from the boundary
            while (lastBoundary < ampStart &&
                   (source[lastBoundary] == ' '  || source[lastBoundary] == '\t' ||
                    source[lastBoundary] == '\r' || source[lastBoundary] == '\n'))
            {
                lastBoundary++;
            }

            // Empty LHS — caller should fall back to UITKX0026
            if (lastBoundary >= ampStart) return -1;

            return lastBoundary;
        }

        /// <summary>
        /// Finds the end position (exclusive) of a JSX element starting at
        /// <paramref name="start"/> (which must point to <c>&lt;</c>).
        /// </summary>
        private static int FindJsxElementEnd(string text, int start, int limit)
            => ReturnFinder.FindJsxElementEnd(text, start, limit);

        private static void ConsumeNewline(string source, ref int i, ref int line)
        {
            if (i >= source.Length)
                return;
            if (source[i] == '\r')
                i++;
            if (i < source.Length && source[i] == '\n')
                i++;
            line++;
        }

        /// <summary>
        /// If <c>source[i]</c> starts a C# string or char literal, advances
        /// <paramref name="i"/> past its closing delimiter and returns <c>true</c>.
        /// Handles regular <c>"..."</c>, verbatim <c>@"..."</c>,
        /// interpolated <c>$"..."</c>, combined <c>$@"..."</c> / <c>@$"..."</c>,
        /// and char <c>'...'</c> literals.  Inside interpolated strings the
        /// method tracks brace depth and recursively skips nested string literals
        /// within interpolation holes.
        /// </summary>
        internal static bool TrySkipStringOrCharLiteral(string source, int rangeEnd, ref int i)
            => CSharpLexFacts.TrySkipStringOrCharLiteral(source, rangeEnd, ref i);
    }
}
