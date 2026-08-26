using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace UitkxVsix;

[Export(typeof(IClassifierProvider))]
[ContentType("uitkx")]
internal sealed class UitkxClassifierProvider : IClassifierProvider
{
    private static readonly string DiagLogPath = Path.Combine(
        Path.GetTempPath(),
        "uitkx-vsix-diag.log"
    );
    private static bool _metadataDumped;
    private static bool _lspLoaded;

    static UitkxClassifierProvider()
    {
        try
        {
            File.AppendAllText(
                DiagLogPath,
                $"[{DateTime.UtcNow:O}] UitkxClassifierProvider type loaded (DLL active).{Environment.NewLine}"
            );
        }
        catch { }
    }

    [Import]
    internal IClassificationTypeRegistryService ClassificationTypeRegistryService { get; set; } =
        null!;

    [Import]
    internal ILanguageClientBroker LanguageClientBroker { get; set; } = null!;

    [ImportMany]
    internal IEnumerable<
        Lazy<ILanguageClient, IDictionary<string, object>>
    > LanguageClients { get; set; } = null!;

    public IClassifier? GetClassifier(ITextBuffer textBuffer)
    {
        if (!_metadataDumped)
        {
            _metadataDumped = true;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[{DateTime.UtcNow:O}] ILanguageClient MEF exports:");
                foreach (
                    var lc in LanguageClients
                        ?? Enumerable.Empty<Lazy<ILanguageClient, IDictionary<string, object>>>()
                )
                {
                    sb.Append($"  Metadata=[");
                    foreach (var kvp in lc.Metadata)
                    {
                        if (
                            kvp.Value is System.Collections.IEnumerable en
                            && !(kvp.Value is string)
                        )
                        {
                            var items = string.Join(
                                ",",
                                en.Cast<object>().Select(o => o?.ToString() ?? "null")
                            );
                            sb.Append($" {kvp.Key}=[{items}]");
                        }
                        else
                            sb.Append($" {kvp.Key}={kvp.Value}");
                    }
                    sb.AppendLine(" ]");
                }
                File.AppendAllText(DiagLogPath, sb.ToString());
            }
            catch (Exception ex)
            {
                File.AppendAllText(
                    DiagLogPath,
                    $"[{DateTime.UtcNow:O}] MetaDump error: {ex.Message}{Environment.NewLine}"
                );
            }
        }

        if (!_lspLoaded)
        {
            _lspLoaded = true;
            var uitkxEntry = LanguageClients?.FirstOrDefault(lc =>
                lc.Metadata.TryGetValue("ContentTypes", out var ct)
                && ct is string[] cts
                && cts.Contains("uitkx", StringComparer.OrdinalIgnoreCase)
            );

            if (uitkxEntry != null && LanguageClientBroker != null)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        File.AppendAllText(
                            DiagLogPath,
                            $"[{DateTime.UtcNow:O}] Calling broker.LoadAsync for uitkx client...{Environment.NewLine}"
                        );
                        await LanguageClientBroker.LoadAsync(
                            new UitkxLspMetadata(),
                            uitkxEntry.Value
                        );
                        File.AppendAllText(
                            DiagLogPath,
                            $"[{DateTime.UtcNow:O}] broker.LoadAsync completed successfully.{Environment.NewLine}"
                        );
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(
                            DiagLogPath,
                            $"[{DateTime.UtcNow:O}] broker.LoadAsync error: {ex}{Environment.NewLine}"
                        );
                    }
                });
            }
            else
            {
                File.AppendAllText(
                    DiagLogPath,
                    $"[{DateTime.UtcNow:O}] LoadAsync skipped: uitkxEntry={uitkxEntry != null}, broker={LanguageClientBroker != null}{Environment.NewLine}"
                );
            }
        }

        if (!ShouldClassifyBuffer(textBuffer))
        {
            return null;
        }

        return textBuffer.Properties.GetOrCreateSingletonProperty(() =>
            new UitkxClassifier(ClassificationTypeRegistryService)
        );
    }

    private static bool ShouldClassifyBuffer(ITextBuffer textBuffer)
    {
        if (
            string.Equals(
                textBuffer.ContentType.TypeName,
                "uitkx",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return true;
        }

        if (textBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document))
        {
            var filePath = document.FilePath;
            return filePath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

/// <summary>Metadata passed to ILanguageClientBroker.LoadAsync for explicit uitkx client activation.</summary>
internal sealed class UitkxLspMetadata : ILanguageClientMetadata
{
    public string? ClientName => null;
    public IEnumerable<string> ContentTypes => new[] { "uitkx" };
}

internal sealed class UitkxClassifier : IClassifier
{
    private readonly IClassificationType _keyword;
    private readonly IClassificationType _string;
    private readonly IClassificationType _number;
    private readonly IClassificationType _identifier;
    private readonly IClassificationType _operator;
    private readonly IClassificationType _method;
    private readonly IClassificationType _type;
    private readonly IClassificationType _comment;
    private readonly IClassificationType _tagName;
    private readonly IClassificationType _attributeName;
    private readonly IClassificationType _directiveName;
    private readonly IClassificationType _controlDirectiveName;
    private readonly IClassificationType _tagDelimiter;
    private readonly IClassificationType _directivePunctuation;

    private readonly HashSet<string> _controlDirectives = new(StringComparer.Ordinal)
    {
        "if",
        "else",
        "for",
        "foreach",
        "while",
        "switch",
        "case",
        "default",
        "break",
        "continue",
    };

    private readonly HashSet<string> _typedDirectives = new(StringComparer.Ordinal)
    {
        "namespace",
        "component",
        "using",
        "props",
    };

    private readonly HashSet<string> _keywords = new(StringComparer.Ordinal)
    {
        "true",
        "false",
        "null",
        "new",
        "typeof",
        "nameof",
        "this",
        "var",
        "int",
        "string",
        "bool",
        "float",
        "double",
        "decimal",
        "long",
        "uint",
        "ulong",
        "byte",
        "char",
        "object",
        "void",
        "return",
        "await",
        "async",
        "if",
        "else",
        "for",
        "foreach",
        "while",
        "do",
        "break",
        "continue",
        "throw",
        "catch",
        "finally",
        "using",
        "static",
        "readonly",
        "const",
        "private",
        "public",
        "protected",
        "internal",
        "class",
        "interface",
        "enum",
        "struct",
        "record",
        "is",
        "as",
        "in",
        "out",
        "ref",
        "hook",
        "module",
        // ES-modules campaign (M6): plain-declaration + full-import-surface keywords.
        // `export`/`from` were previously colored only inside the wrapper-keyword regex
        // paths; `default` already doubles as the switch label. `component`/`hook`/`module`
        // stay for the deprecation window.
        "export",
        "from",
        "import",
    };

    private ITextSnapshot? _cachedSnapshot;
    private List<ClassificationSpan> _cachedSpans = new();
    private IClassificationType? _unreachableCode;
    private ITextBuffer? _ownerBuffer;

#pragma warning disable CS0067
    public event EventHandler<ClassificationChangedEventArgs>? ClassificationChanged;
#pragma warning restore CS0067

    public UitkxClassifier(IClassificationTypeRegistryService classificationTypeRegistryService)
    {
        _unreachableCode =
            classificationTypeRegistryService.GetClassificationType(UitkxClassificationNames.Unreachable)
            ?? classificationTypeRegistryService.GetClassificationType("excluded code")
            ?? classificationTypeRegistryService.GetClassificationType("text")!;
        _keyword =
            classificationTypeRegistryService.GetClassificationType("keyword")
            ?? classificationTypeRegistryService.GetClassificationType("text")!;
        _string =
            classificationTypeRegistryService.GetClassificationType(UitkxClassificationNames.String)
            ?? classificationTypeRegistryService.GetClassificationType("string")
            ?? _keyword;
        _number =
            classificationTypeRegistryService.GetClassificationType(UitkxClassificationNames.Number)
            ?? classificationTypeRegistryService.GetClassificationType("number")
            ?? _keyword;
        _identifier =
            classificationTypeRegistryService.GetClassificationType(
                UitkxClassificationNames.Identifier
            )
            ?? classificationTypeRegistryService.GetClassificationType("identifier")
            ?? classificationTypeRegistryService.GetClassificationType("text")!;
        _operator =
            classificationTypeRegistryService.GetClassificationType(
                UitkxClassificationNames.Operator
            )
            ?? classificationTypeRegistryService.GetClassificationType("operator")
            ?? _keyword;
        _method =
            classificationTypeRegistryService.GetClassificationType(
                UitkxClassificationNames.Function
            )
            ?? classificationTypeRegistryService.GetClassificationType("method name")
            ?? _identifier;
        _type =
            classificationTypeRegistryService.GetClassificationType(
                UitkxClassificationNames.TypeName
            )
            ?? classificationTypeRegistryService.GetClassificationType("class name")
            ?? _method;
        _comment =
            classificationTypeRegistryService.GetClassificationType(
                UitkxClassificationNames.Comment
            )
            ?? classificationTypeRegistryService.GetClassificationType("comment")
            ?? _identifier;
        _tagName =
            classificationTypeRegistryService.GetClassificationType(
                UitkxClassificationNames.TagName
            )
            ?? classificationTypeRegistryService.GetClassificationType("xml literal name")
            ?? _type;
        _attributeName =
            classificationTypeRegistryService.GetClassificationType(
                UitkxClassificationNames.AttributeName
            )
            ?? classificationTypeRegistryService.GetClassificationType("xml literal attribute name")
            ?? _keyword;
        _directiveName =
            classificationTypeRegistryService.GetClassificationType(
                UitkxClassificationNames.DirectiveKeyword
            )
            ?? classificationTypeRegistryService.GetClassificationType("preprocessor keyword")
            ?? _keyword;
        _controlDirectiveName =
            classificationTypeRegistryService.GetClassificationType(
                UitkxClassificationNames.ControlKeyword
            )
            ?? classificationTypeRegistryService.GetClassificationType("preprocessor keyword")
            ?? _directiveName;
        _tagDelimiter =
            classificationTypeRegistryService.GetClassificationType(
                UitkxClassificationNames.TagDelimiter
            )
            ?? classificationTypeRegistryService.GetClassificationType("xml literal delimiter")
            ?? _operator;
        _directivePunctuation =
            classificationTypeRegistryService.GetClassificationType(
                UitkxClassificationNames.DirectivePunctuation
            ) ?? _operator;

        // Log which classification types resolved
        try
        {
            var logPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "uitkx-vsix-diag.log"
            );
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(
                $"[{DateTime.UtcNow:O}] UitkxClassifier ctor — classification type resolution:"
            );
            sb.AppendLine($"  _keyword      = {_keyword.Classification}");
            sb.AppendLine($"  _string       = {_string.Classification}");
            sb.AppendLine($"  _number       = {_number.Classification}");
            sb.AppendLine($"  _identifier   = {_identifier.Classification}");
            sb.AppendLine($"  _operator     = {_operator.Classification}");
            sb.AppendLine($"  _method       = {_method.Classification}");
            sb.AppendLine($"  _type         = {_type.Classification}");
            sb.AppendLine($"  _comment      = {_comment.Classification}");
            sb.AppendLine($"  _tagName      = {_tagName.Classification}");
            sb.AppendLine($"  _attributeName= {_attributeName.Classification}");
            sb.AppendLine($"  _directiveName= {_directiveName.Classification}");
            sb.AppendLine($"  _ctrlDirName  = {_controlDirectiveName.Classification}");
            sb.AppendLine($"  _tagDelimiter = {_tagDelimiter.Classification}");
            sb.AppendLine($"  _dirPunct     = {_directivePunctuation.Classification}");
            System.IO.File.AppendAllText(logPath, sb.ToString());
        }
        catch { }
    }

    private static bool _classifyLogged;
    private static int _classifyCallCount;
    private static readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    private static void ClassifierLog(string msg)
    {
        try
        {
            var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uitkx-unreachable-trace.log");
            System.IO.File.AppendAllText(logPath, $"[{_sw.ElapsedMilliseconds,8}ms] [Classifier] {msg}\n");
        }
        catch { }
    }

    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
    {
        EnsureSnapshotClassified(span.Snapshot);

        // Get unreachable ranges from the diagnostic tagger.
        var unreachableRanges = GetUnreachableRanges(span.Snapshot);

        // Build a position-keyed lookup for classification overrides so the
        // inner loop is O(1) per span.  Key = buffer position, Value = override.
        var overrides = _latestOverrides;
        HashSet<int>? overridePositions = null;
        if (overrides != null && overrides.Count > 0)
        {
            overridePositions = new HashSet<int>(overrides.Count);
            var snapshot = span.Snapshot;
            foreach (var ov in overrides)
            {
                if (ov.Line < 0 || ov.Line >= snapshot.LineCount) continue;
                var line = snapshot.GetLineFromLineNumber(ov.Line);
                int pos = line.Start.Position + ov.Character;
                if (pos >= 0 && pos < snapshot.Length)
                    overridePositions.Add(pos);
            }
        }

        var results = new List<ClassificationSpan>();
        int replacedCount = 0;
        int overrideCount = 0;
        foreach (var classificationSpan in _cachedSpans)
        {
            if (classificationSpan.Span.IntersectsWith(span))
            {
                // Replace tokens inside unreachable regions with our custom
                // high-priority gray classification.  We use per-token
                // replacement (not a broad span) because VS2022's classification
                // aggregator resolves overlapping spans by priority — our
                // uitkx.unreachable format has Order(After = Priority.High) so
                // it will override any competing keyword/type/tag colors from
                // VS2022's built-in LSP classifier.
                if (
                    unreachableRanges != null
                    && IsInUnreachableRange(classificationSpan.Span, unreachableRanges)
                )
                {
                    results.Add(new ClassificationSpan(classificationSpan.Span, _unreachableCode!));
                    replacedCount++;
                }
                else if (
                    overridePositions != null
                    && overridePositions.Contains(classificationSpan.Span.Start.Position)
                )
                {
                    // Reclassify: the server identified this as a delegate-typed
                    // identifier that should be colored as a method/function.
                    results.Add(new ClassificationSpan(classificationSpan.Span, _method));
                    overrideCount++;
                }
                else
                    results.Add(classificationSpan);
            }
        }

        // Also add broad unreachable spans to cover unclassified text
        // (brackets, semicolons, whitespace, etc.) that the lexer skips.
        if (unreachableRanges != null)
        {
            foreach (var (start, end) in unreachableRanges)
            {
                var clippedStart = Math.Max(start, span.Start.Position);
                var clippedEnd = Math.Min(end, span.End.Position);
                if (clippedEnd > clippedStart)
                {
                    results.Add(new ClassificationSpan(
                        new SnapshotSpan(span.Snapshot, clippedStart, clippedEnd - clippedStart),
                        _unreachableCode!));
                }
            }
        }

        // Log every Nth call to avoid flooding (log first 5, then every 50th)
        _classifyCallCount++;
        if (_classifyCallCount <= 5 || _classifyCallCount % 50 == 0)
        {
            ClassifierLog($"GetClassificationSpans #{_classifyCallCount}: {results.Count} spans, {replacedCount} replaced with excluded-code, {overrideCount} reclassified by overrides, {unreachableRanges?.Count ?? 0} unreachable ranges");
        }

        if (!_classifyLogged && results.Count > 0)
        {
            _classifyLogged = true;
            try
            {
                var logPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "uitkx-vsix-diag.log"
                );
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(
                    $"[{DateTime.UtcNow:O}] GetClassificationSpans first call — {results.Count} spans for [{span.Start.Position}..{span.End.Position}]"
                );
                foreach (var s in results.Take(10))
                    sb.AppendLine(
                        $"  [{s.Span.Start.Position}..{s.Span.End.Position}] {s.ClassificationType.Classification}"
                    );
                if (results.Count > 10)
                    sb.AppendLine($"  ... and {results.Count - 10} more");
                System.IO.File.AppendAllText(logPath, sb.ToString());
            }
            catch { }
        }

        return results;
    }

    private void EnsureSnapshotClassified(ITextSnapshot snapshot)
    {
        if (_ownerBuffer == null)
        {
            _ownerBuffer = snapshot.TextBuffer;
            // Listen for diagnostic changes to invalidate cached classifications.
            UitkxDiagnosticStore.DiagnosticsChanged += OnDiagnosticsChanged;
            // Listen for Roslyn-driven classification overrides (delegate → method).
            ClassificationOverrideStore.OverridesChanged += OnOverridesChanged;
        }

        if (ReferenceEquals(snapshot, _cachedSnapshot))
        {
            return;
        }

        _cachedSnapshot = snapshot;
        _cachedSpans = ClassifyAll(snapshot);
    }

    private void OnDiagnosticsChanged(string uri, List<LspDiagnostic> diagnostics)
    {
        // Guard: verify this classifier is still the active singleton for its buffer.
        // When a file is closed and reopened, VS2022 creates a new buffer and a new
        // classifier, but old classifiers remain subscribed to the static event
        // (IClassifier has no Dispose lifecycle).  Without this check, stale instances
        // accumulate and fire ClassificationChanged on dead buffers.
        var buffer = _ownerBuffer;
        if (buffer == null)
        {
            UitkxDiagnosticStore.DiagnosticsChanged -= OnDiagnosticsChanged;
            return;
        }

        try
        {
            if (!buffer.Properties.TryGetProperty(typeof(UitkxClassifier), out UitkxClassifier current)
                || !ReferenceEquals(current, this))
            {
                UitkxDiagnosticStore.DiagnosticsChanged -= OnDiagnosticsChanged;
                _ownerBuffer = null;
                ClassifierLog("Stale classifier detected — unsubscribed from DiagnosticsChanged");
                return;
            }
        }
        catch
        {
            // Buffer may have been disposed.
            UitkxDiagnosticStore.DiagnosticsChanged -= OnDiagnosticsChanged;
            _ownerBuffer = null;
            return;
        }

        // Filter by file URI so we only react to diagnostics for our buffer.
        if (
            buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc)
        )
        {
            var myUri = new Uri(doc.FilePath).AbsoluteUri;
            if (!string.Equals(myUri, uri, StringComparison.OrdinalIgnoreCase))
                return;
        }

        // Store diagnostics directly (bypass tagger's 200ms debounce).
        _latestDiagnostics = diagnostics;

        var unreachableCount = diagnostics.Count(d => d.Tags != null && d.Tags.Contains(1));
        ClassifierLog($"OnDiagnosticsChanged: {diagnostics.Count} total, {unreachableCount} with Unnecessary tag → invalidating cache + firing ClassificationChanged");

        // When diagnostics change, invalidate cache so GetClassificationSpans re-evaluates.
        _cachedSnapshot = null;
        try
        {
            var snapshot = buffer.CurrentSnapshot;
            ClassificationChanged?.Invoke(
                this,
                new ClassificationChangedEventArgs(
                    new SnapshotSpan(snapshot, 0, snapshot.Length)
                )
            );
        }
        catch { }
    }

    private void OnOverridesChanged(string uri, List<ClassificationOverrideEntry> overrides)
    {
        var buffer = _ownerBuffer;
        if (buffer == null)
        {
            ClassificationOverrideStore.OverridesChanged -= OnOverridesChanged;
            return;
        }

        try
        {
            if (!buffer.Properties.TryGetProperty(typeof(UitkxClassifier), out UitkxClassifier current)
                || !ReferenceEquals(current, this))
            {
                ClassificationOverrideStore.OverridesChanged -= OnOverridesChanged;
                return;
            }
        }
        catch
        {
            ClassificationOverrideStore.OverridesChanged -= OnOverridesChanged;
            return;
        }

        // Filter by file URI.
        if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
        {
            var myUri = new Uri(doc.FilePath).AbsoluteUri;
            if (!string.Equals(myUri, uri, StringComparison.OrdinalIgnoreCase))
                return;
        }

        _latestOverrides = overrides;
        ClassifierLog($"OnOverridesChanged: {overrides.Count} overrides → invalidating cache");

        _cachedSnapshot = null;
        try
        {
            var snapshot = buffer.CurrentSnapshot;
            ClassificationChanged?.Invoke(
                this,
                new ClassificationChangedEventArgs(
                    new SnapshotSpan(snapshot, 0, snapshot.Length)
                )
            );
        }
        catch { }
    }

    private List<LspDiagnostic>? _latestDiagnostics;
    private List<ClassificationOverrideEntry>? _latestOverrides;

    private List<(int start, int end)>? GetUnreachableRanges(ITextSnapshot snapshot)
    {
        // Use directly-stored diagnostics (no debounce lag).
        var diags = _latestDiagnostics;
        if (diags == null || diags.Count == 0)
            return null;

        var ranges = new List<(int start, int end)>();
        foreach (var d in diags)
        {
            if (d.Tags == null || !d.Tags.Contains(1))
                continue;

            // Extend to full line boundaries so the entire unreachable line
            // is dimmed, not just the keyword span (e.g. Roslyn CS0162).
            if (d.StartLine < 0 || d.StartLine >= snapshot.LineCount)
                continue;
            if (d.EndLine < 0 || d.EndLine >= snapshot.LineCount)
                continue;

            var startPos = snapshot.GetLineFromLineNumber(d.StartLine).Start.Position;
            var endPos = snapshot.GetLineFromLineNumber(d.EndLine).End.Position;

            if (startPos >= 0 && endPos > startPos && endPos <= snapshot.Length)
                ranges.Add((startPos, endPos));
        }
        return ranges.Count > 0 ? ranges : null;
    }

    private static bool IsInUnreachableRange(SnapshotSpan span, List<(int start, int end)> ranges)
    {
        foreach (var (start, end) in ranges)
        {
            if (span.Start.Position >= start && span.End.Position <= end)
                return true;
        }
        return false;
    }

    private List<ClassificationSpan> ClassifyAll(ITextSnapshot snapshot)
    {
        var text = snapshot.GetText();
        var spans = new List<ClassificationSpan>();

        var index = 0;
        while (index < text.Length)
        {
            if (TryClassifyLineComment(snapshot, text, ref index, spans))
            {
                continue;
            }

            if (TryClassifyBlockComment(snapshot, text, ref index, spans))
            {
                continue;
            }

            if (TryClassifyJsxBlockComment(snapshot, text, ref index, spans))
            {
                continue;
            }

            if (TryClassifyDirective(snapshot, text, ref index, spans))
            {
                continue;
            }

            if (TryClassifyMarkupTag(snapshot, text, ref index, spans))
            {
                continue;
            }

            if (TryClassifyBraceExpression(snapshot, text, ref index, spans))
            {
                continue;
            }

            if (TryClassifyInterpolatedString(snapshot, text, ref index, spans))
            {
                continue;
            }

            if (TryClassifyNormalString(snapshot, text, ref index, spans))
            {
                continue;
            }

            if (char.IsDigit(text[index]))
            {
                var start = index;
                index = ParseNumber(text, index);
                AddSpan(snapshot, spans, start, index - start, _number);
                continue;
            }

            if (IsIdentifierStart(text[index]))
            {
                var start = index;
                index = ParseIdentifier(text, index);
                var token = text.Substring(start, index - start);

                if (_keywords.Contains(token))
                {
                    AddSpan(snapshot, spans, start, token.Length, _keyword);
                }
                else
                {
                    var probe = index;
                    while (probe < text.Length && char.IsWhiteSpace(text[probe]))
                    {
                        probe++;
                    }

                    if (probe < text.Length && text[probe] == '(')
                    {
                        AddSpan(snapshot, spans, start, token.Length, _method);
                    }
                    else if (probe < text.Length && text[probe] == '<')
                    {
                        AddSpan(snapshot, spans, start, token.Length,
                            IsGenericCallSite(text, probe, text.Length) ? _method : _identifier);
                    }
                    else
                    {
                        AddSpan(snapshot, spans, start, token.Length, _identifier);
                    }
                }

                continue;
            }

            if (IsOperatorChar(text[index]))
            {
                AddSpan(snapshot, spans, index, 1, _operator);
            }

            index++;
        }

        return spans;
    }

    private bool TryClassifyLineComment(
        ITextSnapshot snapshot,
        string text,
        ref int index,
        List<ClassificationSpan> spans
    )
    {
        if (index + 1 >= text.Length || text[index] != '/' || text[index + 1] != '/')
        {
            return false;
        }

        var start = index;
        index += 2;
        while (index < text.Length && text[index] != '\r' && text[index] != '\n')
        {
            index++;
        }

        AddSpan(snapshot, spans, start, index - start, _comment);
        return true;
    }

    /// <summary>Classifies C# block comments: /* ... */</summary>
    private bool TryClassifyBlockComment(
        ITextSnapshot snapshot,
        string text,
        ref int index,
        List<ClassificationSpan> spans
    )
    {
        if (index + 1 >= text.Length || text[index] != '/' || text[index + 1] != '*')
            return false;

        var start = index;
        index += 2;
        while (index + 1 < text.Length)
        {
            if (text[index] == '*' && text[index + 1] == '/')
            {
                index += 2;
                break;
            }
            index++;
        }

        AddSpan(snapshot, spans, start, index - start, _comment);
        return true;
    }

    /// <summary>Classifies JSX block comments: {/* ... */}</summary>
    private bool TryClassifyJsxBlockComment(
        ITextSnapshot snapshot,
        string text,
        ref int index,
        List<ClassificationSpan> spans
    )
    {
        if (
            index + 3 >= text.Length
            || text[index] != '{'
            || text[index + 1] != '/'
            || text[index + 2] != '*'
        )
            return false;

        var start = index;
        index += 3; // skip {/*
        while (index + 2 < text.Length)
        {
            if (text[index] == '*' && text[index + 1] == '/' && text[index + 2] == '}')
            {
                index += 3; // skip */}
                AddSpan(snapshot, spans, start, index - start, _comment);
                return true;
            }
            index++;
        }

        // Unterminated — classify what we have
        index = text.Length;
        AddSpan(snapshot, spans, start, index - start, _comment);
        return true;
    }

    private bool TryClassifyDirective(
        ITextSnapshot snapshot,
        string text,
        ref int index,
        List<ClassificationSpan> spans
    )
    {
        if (text[index] != '@')
        {
            return false;
        }

        if (index + 1 >= text.Length || !IsIdentifierStart(text[index + 1]))
        {
            return false;
        }

        AddSpan(snapshot, spans, index, 1, _directivePunctuation);
        var directiveStart = index + 1;
        var directiveEnd = ParseIdentifier(text, directiveStart);
        var directiveToken = text.Substring(directiveStart, directiveEnd - directiveStart);
        var directiveType = _controlDirectives.Contains(directiveToken)
            ? _controlDirectiveName
            : _directiveName;
        AddSpan(snapshot, spans, directiveStart, directiveEnd - directiveStart, directiveType);
        index = directiveEnd;

        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] == '.')
            {
                AddSpan(snapshot, spans, index, 1, _operator);
                index++;
                continue;
            }

            if (text[index] == '(')
            {
                ClassifyParenthesizedExpression(snapshot, text, ref index, spans);
                continue;
            }

            if (
                text[index] == '\r'
                || text[index] == '\n'
                || text[index] == '{'
                || text[index] == '<'
            )
            {
                break;
            }

            if (
                TryClassifyInterpolatedString(snapshot, text, ref index, spans)
                || TryClassifyNormalString(snapshot, text, ref index, spans)
                || TryClassifyBraceExpression(snapshot, text, ref index, spans)
            )
            {
                continue;
            }

            if (IsIdentifierStart(text[index]))
            {
                var tokenStart = index;
                index = ParseIdentifier(text, index);
                var tokenType = _typedDirectives.Contains(directiveToken) ? _type : _identifier;
                AddSpan(snapshot, spans, tokenStart, index - tokenStart, tokenType);
                continue;
            }

            break;
        }

        return true;
    }

    private bool TryClassifyMarkupTag(
        ITextSnapshot snapshot,
        string text,
        ref int index,
        List<ClassificationSpan> spans
    )
    {
        if (text[index] != '<')
        {
            return false;
        }

        if (index + 1 >= text.Length)
        {
            return false;
        }

        var probe = index + 1;
        if (probe < text.Length && text[probe] == '/')
        {
            probe++;
        }

        if (probe >= text.Length || !IsMarkupNameStart(text[probe]))
        {
            return false;
        }

        AddSpan(snapshot, spans, index, 1, _tagDelimiter);
        index++;

        if (index < text.Length && text[index] == '/')
        {
            AddSpan(snapshot, spans, index, 1, _tagDelimiter);
            index++;
        }

        var tagNameStart = index;
        index = ParseMarkupName(text, index);
        AddSpan(snapshot, spans, tagNameStart, index - tagNameStart, _tagName);

        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (
                TryClassifyLineComment(snapshot, text, ref index, spans)
                || TryClassifyBlockComment(snapshot, text, ref index, spans)
                || TryClassifyJsxBlockComment(snapshot, text, ref index, spans)
                || TryClassifyInterpolatedString(snapshot, text, ref index, spans)
                || TryClassifyNormalString(snapshot, text, ref index, spans)
                || TryClassifyBraceExpression(snapshot, text, ref index, spans)
            )
            {
                continue;
            }

            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '>')
            {
                AddSpan(snapshot, spans, index, 1, _tagDelimiter);
                AddSpan(snapshot, spans, index + 1, 1, _tagDelimiter);
                index += 2;
                return true;
            }

            if (text[index] == '>')
            {
                AddSpan(snapshot, spans, index, 1, _tagDelimiter);
                index++;
                return true;
            }

            if (text[index] == '=')
            {
                AddSpan(snapshot, spans, index, 1, _operator);
                index++;
                continue;
            }

            if (IsMarkupNameStart(text[index]))
            {
                var attrStart = index;
                index = ParseMarkupName(text, index);
                AddSpan(snapshot, spans, attrStart, index - attrStart, _attributeName);
                continue;
            }

            if (IsOperatorChar(text[index]))
            {
                AddSpan(snapshot, spans, index, 1, _operator);
            }

            index++;
        }

        return true;
    }

    private bool TryClassifyBraceExpression(
        ITextSnapshot snapshot,
        string text,
        ref int index,
        List<ClassificationSpan> spans
    )
    {
        if (text[index] != '{')
        {
            return false;
        }

        if (!ShouldTreatBraceAsExpression(text, index))
        {
            return false;
        }

        AddSpan(snapshot, spans, index, 1, _operator);
        index++;

        var exprStart = index;
        var depth = 1;

        while (index < text.Length && depth > 0)
        {
            if (
                TryClassifyLineComment(snapshot, text, ref index, spans)
                || TryClassifyBlockComment(snapshot, text, ref index, spans)
                || TryClassifyInterpolatedString(snapshot, text, ref index, spans)
                || TryClassifyNormalString(snapshot, text, ref index, spans)
            )
            {
                continue;
            }

            if (text[index] == '{')
            {
                depth++;
                index++;
                continue;
            }

            if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    if (index > exprStart)
                    {
                        ClassifyExpressionSegment(
                            snapshot,
                            text,
                            exprStart,
                            index - exprStart,
                            spans
                        );
                    }

                    AddSpan(snapshot, spans, index, 1, _operator);
                    index++;
                    return true;
                }

                index++;
                continue;
            }

            index++;
        }

        if (exprStart < text.Length)
        {
            ClassifyExpressionSegment(snapshot, text, exprStart, text.Length - exprStart, spans);
        }

        return true;
    }

    private void ClassifyParenthesizedExpression(
        ITextSnapshot snapshot,
        string text,
        ref int index,
        List<ClassificationSpan> spans
    )
    {
        var parenStart = index;
        AddSpan(snapshot, spans, parenStart, 1, _operator);
        index++;

        var exprStart = index;
        var depth = 1;
        while (index < text.Length && depth > 0)
        {
            if (
                TryClassifyInterpolatedString(snapshot, text, ref index, spans)
                || TryClassifyNormalString(snapshot, text, ref index, spans)
            )
            {
                continue;
            }

            if (text[index] == '(')
            {
                depth++;
                index++;
                continue;
            }

            if (text[index] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    if (index > exprStart)
                    {
                        ClassifyExpressionSegment(
                            snapshot,
                            text,
                            exprStart,
                            index - exprStart,
                            spans
                        );
                    }

                    AddSpan(snapshot, spans, index, 1, _operator);
                    index++;
                    return;
                }

                index++;
                continue;
            }

            index++;
        }

        if (exprStart < text.Length)
        {
            ClassifyExpressionSegment(snapshot, text, exprStart, text.Length - exprStart, spans);
        }
    }

    private bool TryClassifyInterpolatedString(
        ITextSnapshot snapshot,
        string text,
        ref int index,
        List<ClassificationSpan> spans
    )
    {
        if (index + 1 >= text.Length || text[index] != '$' || text[index + 1] != '"')
        {
            return false;
        }

        AddSpan(snapshot, spans, index, 2, _string);
        index += 2;
        var segmentStart = index;

        while (index < text.Length)
        {
            if (text[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (text[index] == '"')
            {
                if (index > segmentStart)
                {
                    AddSpan(snapshot, spans, segmentStart, index - segmentStart, _string);
                }

                AddSpan(snapshot, spans, index, 1, _string);
                index++;
                return true;
            }

            if (text[index] == '{')
            {
                if (index > segmentStart)
                {
                    AddSpan(snapshot, spans, segmentStart, index - segmentStart, _string);
                }

                AddSpan(snapshot, spans, index, 1, _operator);
                index++;

                var holeStart = index;
                var depth = 1;

                while (index < text.Length && depth > 0)
                {
                    if (text[index] == '\\')
                    {
                        index += 2;
                        continue;
                    }

                    if (text[index] == '{')
                    {
                        depth++;
                        index++;
                        continue;
                    }

                    if (text[index] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            if (index > holeStart)
                            {
                                ClassifyExpressionSegment(
                                    snapshot,
                                    text,
                                    holeStart,
                                    index - holeStart,
                                    spans
                                );
                            }

                            AddSpan(snapshot, spans, index, 1, _operator);
                            index++;
                            segmentStart = index;
                            break;
                        }
                    }

                    index++;
                }

                continue;
            }

            index++;
        }

        if (segmentStart < text.Length)
        {
            AddSpan(snapshot, spans, segmentStart, text.Length - segmentStart, _string);
        }

        return true;
    }

    private bool TryClassifyNormalString(
        ITextSnapshot snapshot,
        string text,
        ref int index,
        List<ClassificationSpan> spans
    )
    {
        if (text[index] != '"')
        {
            return false;
        }

        var start = index;
        index++;
        while (index < text.Length)
        {
            if (text[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (text[index] == '"')
            {
                index++;
                break;
            }

            index++;
        }

        AddSpan(snapshot, spans, start, index - start, _string);
        return true;
    }

    private void ClassifyExpressionSegment(
        ITextSnapshot snapshot,
        string text,
        int start,
        int length,
        List<ClassificationSpan> spans
    )
    {
        var end = start + length;
        var i = start;

        while (i < end)
        {
            if (i < text.Length && char.IsDigit(text[i]))
            {
                var numStart = i;
                i = ParseNumber(text, i);
                AddSpan(snapshot, spans, numStart, i - numStart, _number);
                continue;
            }

            if (i < text.Length && IsIdentifierStart(text[i]))
            {
                var identStart = i;
                i = ParseIdentifier(text, i);
                var token = text.Substring(identStart, i - identStart);

                if (_keywords.Contains(token))
                {
                    AddSpan(snapshot, spans, identStart, token.Length, _keyword);
                }
                else
                {
                    var probe = i;
                    while (probe < end && char.IsWhiteSpace(text[probe]))
                    {
                        probe++;
                    }

                    if (probe < end && text[probe] == '(')
                    {
                        AddSpan(snapshot, spans, identStart, token.Length, _method);
                    }
                    else if (probe < end && text[probe] == '<')
                    {
                        AddSpan(snapshot, spans, identStart, token.Length,
                            IsGenericCallSite(text, probe, end) ? _method : _identifier);
                    }
                    else
                    {
                        AddSpan(snapshot, spans, identStart, token.Length, _identifier);
                    }
                }

                continue;
            }

            if (i < text.Length && IsOperatorChar(text[i]))
            {
                AddSpan(snapshot, spans, i, 1, _operator);
            }

            i++;
        }
    }

    /// <summary>
    /// Checks whether the '<' at <paramref name="ltPos"/> starts a generic type
    /// argument list followed by '(', e.g. <c>useState&lt;string&gt;(</c>.
    /// Returns true if the pattern <c>&lt;...&gt;(</c> is found with balanced
    /// angle brackets.  Bails out on characters that cannot appear inside
    /// generic type arguments to avoid false positives on comparison operators.
    /// </summary>
    private static bool IsGenericCallSite(string text, int ltPos, int end)
    {
        if (ltPos >= end || text[ltPos] != '<')
            return false;

        int depth = 1;
        int i = ltPos + 1;
        while (i < end && depth > 0)
        {
            char c = text[i];
            if (c == '<') { depth++; }
            else if (c == '>') { depth--; }
            // Characters that can't appear in a generic type argument list —
            // bail out to avoid misclassifying comparison chains.
            else if (c == ';' || c == '{' || c == '}')
                return false;
            // '=' is allowed only as part of '=>' (lambda) — bare '=' is not.
            else if (c == '=' && (i + 1 >= end || text[i + 1] != '>'))
                return false;
            i++;
        }

        if (depth != 0)
            return false;

        // Skip optional whitespace after '>'
        while (i < end && char.IsWhiteSpace(text[i]))
            i++;

        return i < end && text[i] == '(';
    }

    private static int ParseNumber(string text, int index)
    {
        var i = index;

        while (i < text.Length && char.IsDigit(text[i]))
        {
            i++;
        }

        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && char.IsDigit(text[i]))
            {
                i++;
            }
        }

        while (i < text.Length && char.IsLetter(text[i]))
        {
            i++;
        }

        return i;
    }

    private static int ParseIdentifier(string text, int index)
    {
        var i = index;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
        {
            i++;
        }

        return i;
    }

    private static int ParseMarkupName(string text, int index)
    {
        var i = index;
        while (
            i < text.Length
            && (
                char.IsLetterOrDigit(text[i])
                || text[i] == '_'
                || text[i] == '-'
                || text[i] == ':'
                || text[i] == '.'
            )
        )
        {
            i++;
        }

        return i;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsMarkupNameStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsOperatorChar(char c) =>
        c
            is '+'
                or '-'
                or '*'
                or '/'
                or '%'
                or '&'
                or '|'
                or '^'
                or '~'
                or '!'
                or '<'
                or '>'
                or '='
                or '?'
                or ':'
                or '{'
                or '}'
                or '('
                or ')';

    private static bool ShouldTreatBraceAsExpression(string text, int braceIndex)
    {
        var prev = braceIndex - 1;
        while (prev >= 0 && char.IsWhiteSpace(text[prev]))
        {
            prev--;
        }

        if (prev < 0)
        {
            return false;
        }

        return text[prev] is '=' or '(' or ',' or '?' or ':';
    }

    private static bool IsAtLineContentStart(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && text[i] != '\r' && text[i] != '\n')
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }

            i--;
        }

        return true;
    }

    private static void AddSpan(
        ITextSnapshot snapshot,
        ICollection<ClassificationSpan> spans,
        int start,
        int length,
        IClassificationType classificationType
    )
    {
        if (length <= 0 || start < 0 || start + length > snapshot.Length)
        {
            return;
        }

        spans.Add(
            new ClassificationSpan(new SnapshotSpan(snapshot, start, length), classificationType)
        );
    }
}
