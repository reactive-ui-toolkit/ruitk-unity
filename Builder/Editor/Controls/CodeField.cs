#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using Ruitk.Language;
using Ruitk.Language.SemanticTokens;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// The POC source pane, shape for shape: in READ mode "#srcpane" is a
    /// scrolling column of ".srcline" divs (one per line, white-space:pre, the
    /// .hl/.sel bands painted on the row), and "edit" swaps the whole pane for the
    /// plain "#src-edit" textarea. The single-Label overlay this replaced could
    /// not carry the POC's 17.4px line pitch (UI Toolkit applies
    /// unityParagraphSpacing between PARAGRAPHS, and a whiteSpace:Pre label is one
    /// paragraph) and could not scroll on either axis without the transparent
    /// input drifting off the glyphs it sits on.
    /// </summary>
    internal sealed class CodeField : VisualElement
    {
        private readonly TextField _input;
        private readonly ScrollView _scroll;
        private readonly VisualElement _linesHost;
        private readonly Label _probe;
        private readonly Label _diagnosticsLabel;
        private string _filePath = "";
        private HashSet<string> _knownElements;
        private bool _suppressChange;
        private bool _userCaretActive;
        private bool _editing;
        private bool _coloredEdit;
        private bool _innerScrollWired;
        private float _editPitch;
        private readonly Label _pitchProbe;
        private VisualElement _completionPopup;
        private List<(string Code, int Line1, string Message)> _overlayDiagnostics;
        private string _localDiagnosticsText = "";

        public event Action<string> TextEdited;

        /// <summary>POC source pane: double-click enters edit mode, Ctrl+Enter
        /// applies (re-parse), Esc cancels and restores the snapshot.</summary>
        public event Action EditRequested;

        public event Action ApplyRequested;

        public event Action CancelRequested;

        private readonly List<string> _traceNames = new List<string>();
        private string _traceSource = "";

        /// <summary>POC ".srcline.hl": while a hook chip is hovered, every source
        /// line naming one of its states gets the warn-tinted band.</summary>
        public void SetTraceNames(string spaceSeparated)
        {
            string next = spaceSeparated ?? "";
            if (next == _traceSource)
                return;
            _traceSource = next;
            _traceNames.Clear();
            if (!string.IsNullOrEmpty(spaceSeparated))
            {
                foreach (string raw in spaceSeparated.Split(' '))
                {
                    string name = raw.Trim();
                    if (name.Length > 0)
                        _traceNames.Add(name);
                }
            }
            Recolor(TextLf);
        }

        /// <summary>POC "textarea.err": a failed apply turns the field's border
        /// red until the next successful parse.</summary>
        public void SetError(bool error)
        {
            var color = error ? new Color(0.94f, 0.38f, 0.38f) : BuilderPalette.Transparent;
            float width = error ? 1f : 0f;
            style.borderTopWidth = width;
            style.borderBottomWidth = width;
            style.borderLeftWidth = width;
            style.borderRightWidth = width;
            style.borderTopColor = color;
            style.borderBottomColor = color;
            style.borderLeftColor = color;
            style.borderRightColor = color;
        }

        public void FocusEditor()
        {
            _input.Focus();
            _userCaretActive = true;
        }

        /// <summary>Ctrl+Space asks this for completions at (line0, char0);
        /// the window wires it to the shared LSP client.</summary>
        public Func<int, int, System.Threading.Tasks.Task<List<(string Label, string Insert)>>>
            CompletionProvider { get; set; }

        /// <summary>POC "#srcpane .srcline" inherits the body's 13px/1.45 metric at
        /// font-size 12, so the line pitch is 17.4px (measured on poc-l2-edit.png:
        /// line tops at 502, 519, 537, 554, 571, 589 at zoom 1).</summary>
        private const float LineHeight = 17.4f;

        /// <summary>POC ".srcline { padding: 0 14px }".</summary>
        private const float Gutter = 14f;

        /// <summary>Strips Unity's field chrome off one element of the TextField's
        /// inner hierarchy so the edit box reads as the POC's "#src-edit": a bare
        /// #17171b textarea with 12px mono ink and no inset field box.</summary>
        private static void FlattenInput(VisualElement element)
        {
            if (element == null)
                return;
            element.style.backgroundColor = BuilderPalette.Transparent;
            element.style.color = BuilderPalette.Text;
            element.style.borderTopWidth = 0f;
            element.style.borderBottomWidth = 0f;
            element.style.borderLeftWidth = 0f;
            element.style.borderRightWidth = 0f;
            element.style.borderTopLeftRadius = 0f;
            element.style.borderTopRightRadius = 0f;
            element.style.borderBottomLeftRadius = 0f;
            element.style.borderBottomRightRadius = 0f;
            element.style.paddingTop = 0f;
            element.style.paddingBottom = 0f;
            element.style.paddingLeft = 0f;
            element.style.paddingRight = 0f;
            element.style.marginTop = 0f;
            element.style.marginBottom = 0f;
            element.style.marginLeft = 0f;
            element.style.marginRight = 0f;
            element.style.fontSize = 12f;
            element.style.unityFontDefinition = BuilderCanvasDrawing.MonoFontDefinition;
        }

        private void FlattenInputTree()
        {
            FlattenInput(_input.Q(TextField.textInputUssName));
            foreach (var text in _input.Query<TextElement>().ToList())
                FlattenInput(text);
        }

        public CodeField()
        {
            style.flexGrow = 1f;
            // POC "#srcpane": #17171b ground, 12px monospace, 8px 0 padding.
            style.backgroundColor = BuilderPalette.Ground;

            var host = new VisualElement
            {
                style =
                {
                    flexGrow = 1f, position = Position.Relative, overflow = Overflow.Hidden,
                    backgroundColor = BuilderPalette.Ground,
                },
            };
            Add(host);

            // POC "#srcpane { overflow: auto }" — BOTH axes, so a long line and a
            // long file are always reachable.
            _scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal)
            {
                style =
                {
                    position = Position.Absolute,
                    top = 0f, left = 0f, right = 0f, bottom = 0f,
                    paddingTop = 8f, paddingBottom = 8f,
                },
            };
            BuilderWindow.StyleScrollers(_scroll);
            _linesHost = new VisualElement
            {
                style = { flexDirection = FlexDirection.Column, flexShrink = 0f },
            };
            _scroll.contentContainer.Add(_linesHost);
            host.Add(_scroll);
            // POC: srcpane.addEventListener("dblclick", enterSrcEdit).
            _scroll.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.clickCount >= 2)
                    EditRequested?.Invoke();
            });
            _scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => ApplyLinesWidth());

            _probe = new Label("MMMMMMMMMM")
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position = Position.Absolute,
                    top = 0f, left = 0f,
                    visibility = Visibility.Hidden,
                    fontSize = 12f,
                    whiteSpace = WhiteSpace.Pre,
                },
            };
            _probe.style.unityFontDefinition = BuilderCanvasDrawing.MonoFontDefinition;
            _probe.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float width = _probe.layout.width;
                if (float.IsNaN(width) || width <= 0f)
                    return;
                float advance = width / 10f;
                if (Mathf.Approximately(advance, _charAdvance))
                    return;
                _charAdvance = advance;
                ApplyLinesWidth();
            });
            host.Add(_probe);

            // UB-25: two stacked M's measure the text engine's NATURAL line
            // pitch — in coloured edit mode the listing rows adopt it so the
            // glyphs sit exactly under the transparent input's own lines.
            _pitchProbe = new Label("M\nM")
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position = Position.Absolute,
                    top = 0f, left = 0f,
                    visibility = Visibility.Hidden,
                    fontSize = 12f,
                    whiteSpace = WhiteSpace.Pre,
                },
            };
            _pitchProbe.style.unityFontDefinition = BuilderCanvasDrawing.MonoFontDefinition;
            _pitchProbe.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float height = _pitchProbe.layout.height;
                if (!float.IsNaN(height) && height > 0f)
                    _editPitch = height / 2f;
            });
            host.Add(_pitchProbe);

            _input = new TextField { multiline = true };
            _input.verticalScrollerVisibility = ScrollerVisibility.Auto;
            _input.style.position = Position.Absolute;
            _input.style.top = 0f;
            _input.style.left = 0f;
            _input.style.right = 0f;
            _input.style.bottom = 0f;
            _input.style.fontSize = 12f;
            _input.style.unityFontDefinition = BuilderCanvasDrawing.MonoFontDefinition;
            _input.style.marginTop = 0f;
            _input.style.marginBottom = 0f;
            _input.style.marginLeft = 0f;
            _input.style.marginRight = 0f;
            // POC "#src-edit { padding: 8px 14px }" on the #17171b ground.
            _input.style.paddingTop = 8f;
            _input.style.paddingBottom = 8f;
            _input.style.paddingLeft = Gutter;
            _input.style.paddingRight = Gutter;
            _input.style.backgroundColor = BuilderPalette.Ground;
            _input.style.color = BuilderPalette.Text;
            _input.style.display = DisplayStyle.None;
            FlattenInputTree();
            _input.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                FlattenInputTree();
                if (_coloredEdit)
                    ApplyColoredEditInk();
            });
            _input.RegisterValueChangedCallback(OnInputChanged);
            _input.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _input.RegisterCallback<PointerDownEvent>(_ => _userCaretActive = true);
            host.Add(_input);

            RegisterCallback<AttachToPanelEvent>(_ => ApplyLinesWidth());

            _diagnosticsLabel = new Label
            {
                style =
                {
                    flexShrink = 0f,
                    maxHeight = 90f,
                    color = new Color(0.94f, 0.55f, 0.45f),
                    fontSize = 10f,
                    marginLeft = 4f,
                    whiteSpace = WhiteSpace.Normal,
                },
            };
            Add(_diagnosticsLabel);
        }

        public string TextLf => (_input.value ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

        /// <summary>UB-25 (deliberate POC divergence — the POC drops to a plain
        /// textarea): edit mode keeps the COLOURED listing visible under a
        /// transparent-ink input whose caret and selection stay visible. The
        /// registration contract: rows adopt the text engine's measured natural
        /// pitch and the input's internal scroller drives the listing's offset.
        /// If either the pitch or the internal scroller is unavailable the pane
        /// degrades to the opaque textarea — never a misregistered overlay.</summary>
        public void SetEditing(bool editing)
        {
            _editing = editing;
            _input.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            if (!editing)
            {
                // Restore BEFORE clearing the flag — the old order made
                // EndColoredEdit an unconditional no-op and left the input's
                // ink transparent for every read-mode session after the first
                // coloured edit (review finding, 2026-08-16).
                EndColoredEdit();
                _scroll.style.display = DisplayStyle.Flex;
                _userCaretActive = false;
                Recolor(TextLf);
                return;
            }
            _coloredEdit = TryBeginColoredEdit();
            _scroll.style.display = _coloredEdit ? DisplayStyle.Flex : DisplayStyle.None;
            if (_coloredEdit)
                Recolor(TextLf);
        }

        private bool TryBeginColoredEdit()
        {
            if (_editPitch <= 0f)
                return false;
            var inner = _input.Q<ScrollView>();
            if (inner == null)
                return false;
            if (!_innerScrollWired)
            {
                _innerScrollWired = true;
                if (inner.verticalScroller != null)
                    inner.verticalScroller.valueChanged += _ => SyncListingScroll(inner);
                if (inner.horizontalScroller != null)
                    inner.horizontalScroller.valueChanged += _ => SyncListingScroll(inner);
            }
            ApplyColoredEditInk();
            SyncListingScroll(inner);
            return true;
        }

        private void ApplyColoredEditInk()
        {
            _input.style.backgroundColor = BuilderPalette.Transparent;
            foreach (var text in _input.Query<TextElement>().ToList())
                text.style.color = BuilderPalette.Transparent;
            var selection = _input.textSelection;
            selection.cursorColor = BuilderPalette.Text;
            var band = BuilderPalette.Accent;
            band.a = 0.3f;
            selection.selectionColor = band;
        }

        private void EndColoredEdit()
        {
            _coloredEdit = false;
            _input.style.backgroundColor = BuilderPalette.Ground;
            foreach (var text in _input.Query<TextElement>().ToList())
                text.style.color = BuilderPalette.Text;
        }

        private void SyncListingScroll(ScrollView inner)
        {
            if (!_coloredEdit || inner == null)
                return;
            _scroll.scrollOffset = new Vector2(
                inner.horizontalScroller?.value ?? 0f,
                inner.verticalScroller?.value ?? 0f);
        }

        /// <summary>POC row→source sync: band the line, scroll it into view, and —
        /// in edit mode — select it.</summary>
        public void FocusLine(int line1)
        {
            // POC ".srcline.sel": the focused line gets a gold band, not Unity's
            // selection blue.
            _selectedLine1 = line1;
            Recolor(TextLf);
            // In coloured edit mode the input's internal scroller OWNS the
            // listing offset — scrolling the listing directly would shear it
            // off the transparent glyphs; the caret move below drives both.
            if (!_coloredEdit && line1 >= 1 && line1 <= _linesHost.childCount)
                _scroll.ScrollTo(_linesHost[line1 - 1]);
            if (!_editing)
                return;
            string text = _input.value ?? "";
            int line = 1, start = 0;
            for (int i = 0; i < text.Length && line < line1; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    start = i + 1;
                }
            }
            int end = text.IndexOf('\n', start);
            if (end < 0)
                end = text.Length;
            _input.cursorIndex = start;
            _input.selectIndex = end;
            _input.Focus();
            _userCaretActive = true;
        }

        private int _selectedLine1;

        /// <summary>Inserts at the caret (or replaces the selection) and fires
        /// the normal edited path — palette clicks author through the same
        /// session/undo/recompile pipeline as typing.</summary>
        public void InsertAtCaret(string snippet)
        {
            if (_input.isReadOnly || string.IsNullOrEmpty(snippet))
                return;
            string text = _input.value ?? "";
            int start = Mathf.Clamp(Mathf.Min(_input.cursorIndex, _input.selectIndex), 0, text.Length);
            int end = Mathf.Clamp(Mathf.Max(_input.cursorIndex, _input.selectIndex), 0, text.Length);
            _input.value = text.Substring(0, start) + snippet + text.Substring(end);
            _input.cursorIndex = start + snippet.Length;
            _input.selectIndex = _input.cursorIndex;
            if (_editing)
                _input.Focus();
        }

        public void SetContent(string textLf, string filePath, HashSet<string> knownElements)
        {
            string nextPath = filePath ?? "";
            if (!string.Equals(_filePath, nextPath, StringComparison.OrdinalIgnoreCase))
                _overlayDiagnostics = null;
            _filePath = nextPath;
            _knownElements = knownElements;
            _suppressChange = true;
            _input.value = textLf ?? "";
            _suppressChange = false;
            _userCaretActive = false;
            Recolor(textLf ?? "");
        }

        /// <summary>UB-07: refresh the element set without touching the buffer
        /// or the caret — the graph loads after the first SetContent, and custom
        /// tags stay unclassified until the set arrives.</summary>
        public void SetKnownElements(HashSet<string> knownElements)
        {
            if (ReferenceEquals(_knownElements, knownElements))
                return;
            _knownElements = knownElements;
            Recolor(_input.value ?? "");
        }

        public void SetEditable(bool editable)
        {
            _input.isReadOnly = !editable;
        }

        private void OnInputChanged(ChangeEvent<string> evt)
        {
            if (_suppressChange)
                return;
            CloseCompletionPopup();
            string lf = (evt.newValue ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            Recolor(lf);
            TextEdited?.Invoke(lf);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                CloseCompletionPopup();
                CancelRequested?.Invoke();
                return;
            }
            if (evt.ctrlKey
                && (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter))
            {
                evt.StopPropagation();
                ApplyRequested?.Invoke();
                return;
            }
            if (!(evt.ctrlKey && evt.keyCode == KeyCode.Space))
                return;
            evt.StopPropagation();
            ShowCompletions();
        }

        private (int Line0, int Char0) CaretPosition()
        {
            string text = TextLf;
            int index = Mathf.Clamp(_input.cursorIndex, 0, text.Length);
            int line = 0, lineStart = 0;
            for (int i = 0; i < index; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }
            return (line, index - lineStart);
        }

        private async void ShowCompletions()
        {
            if (CompletionProvider == null)
                return;
            var (line0, char0) = CaretPosition();
            List<(string Label, string Insert)> items;
            try
            {
                items = await CompletionProvider(line0, char0);
            }
            catch (Exception)
            {
                return;
            }
            if (items == null || items.Count == 0 || panel == null)
                return;

            CloseCompletionPopup();
            var popup = new ScrollView
            {
                style =
                {
                    position = Position.Absolute,
                    top = 22f, right = 8f,
                    maxHeight = 200f, minWidth = 180f,
                    backgroundColor = new Color(0.13f, 0.13f, 0.15f),
                    borderTopWidth = 1f, borderBottomWidth = 1f,
                    borderLeftWidth = 1f, borderRightWidth = 1f,
                    borderTopColor = new Color(0.3f, 0.3f, 0.35f),
                    borderBottomColor = new Color(0.3f, 0.3f, 0.35f),
                    borderLeftColor = new Color(0.3f, 0.3f, 0.35f),
                    borderRightColor = new Color(0.3f, 0.3f, 0.35f),
                },
            };
            int shown = 0;
            foreach (var item in items)
            {
                if (shown++ == 25)
                    break;
                var row = new Label(item.Label)
                {
                    style = { paddingLeft = 6f, paddingTop = 1f, paddingBottom = 1f },
                };
                var captured = item;
                row.RegisterCallback<PointerDownEvent>(e =>
                {
                    e.StopPropagation();
                    CloseCompletionPopup();
                    InsertAtCaret(captured.Insert ?? captured.Label);
                });
                row.RegisterCallback<MouseEnterEvent>(_ =>
                    row.style.backgroundColor = new Color(0.2f, 0.3f, 0.4f));
                row.RegisterCallback<MouseLeaveEvent>(_ =>
                    row.style.backgroundColor = StyleKeyword.Null);
                popup.Add(row);
            }
            _completionPopup = popup;
            Add(popup);
        }

        private void CloseCompletionPopup()
        {
            if (_completionPopup != null)
            {
                _completionPopup.RemoveFromHierarchy();
                _completionPopup = null;
            }
        }

        /// <summary>POC ".srcline.sel" rgba(255,213,79,.18) and ".srcline.hl"
        /// rgba(255,183,77,.15) — bands on the ROW, which is why the listing is a
        /// column of rows and not one text block.</summary>
        private static readonly Color SelBand = new Color(1f, 0.835f, 0.310f, 0.18f);

        private static readonly Color TraceBand = new Color(1f, 0.718f, 0.302f, 0.15f);


        private int _longestLine;

        private float _charAdvance;

        /// <summary>The mono advance is read off a hidden ten-glyph probe once the
        /// panel has laid it out (Consolas 12px ≈ 6.6px, but the fallback face on a
        /// machine without it is not the same width).</summary>
        private float CharAdvance() => _charAdvance > 0f ? _charAdvance : 6.6f;

        /// <summary>The rows carry the .hl/.sel bands, so they must all be as wide
        /// as the widest line (and never narrower than the viewport) — that width
        /// is also what gives the ScrollView something to scroll horizontally.</summary>
        private void ApplyLinesWidth()
        {
            float content = Gutter * 2f + _longestLine * CharAdvance();
            float viewport = _scroll.contentViewport.layout.width;
            if (float.IsNaN(viewport) || viewport <= 0f)
                viewport = 0f;
            _linesHost.style.width = Mathf.Max(content, viewport);
        }

        private Label NewLine()
        {
            var label = new Label
            {
                enableRichText = true,
                style =
                {
                    height = LineHeight,
                    minHeight = 0f,
                    flexShrink = 0f,
                    paddingLeft = Gutter,
                    paddingRight = Gutter,
                    color = BuilderPalette.Text,
                    fontSize = 12f,
                    whiteSpace = WhiteSpace.Pre,
                    unityTextAlign = TextAnchor.MiddleLeft,
                },
            };
            label.style.unityFontDefinition = BuilderCanvasDrawing.MonoFontDefinition;
            return label;
        }

        private void Recolor(string textLf)
        {
            string[] lines = (textLf ?? "").Split('\n');
            SemanticTokenData[] tokens;
            try
            {
                var parsed = BuilderLanguage.Parse(textLf, _filePath);
                tokens = BuilderLanguage.Tokens(parsed, textLf, _knownElements, _filePath);

                var diags = BuilderLanguage.Diagnose(parsed, _filePath, _knownElements);
                var sb = new StringBuilder();
                foreach (var d in diags)
                    sb.Append(d.Code).Append(" L").Append(d.SourceLine)
                        .Append(": ").Append(d.Message).Append('\n');
                _localDiagnosticsText = sb.ToString();
                RenderDiagnosticsLabel();
            }
            catch (Exception)
            {
                tokens = Array.Empty<SemanticTokenData>();
                _localDiagnosticsText = "";
                RenderDiagnosticsLabel();
            }

            var byLine = new Dictionary<int, List<SemanticTokenData>>();
            foreach (var token in tokens)
            {
                if (!byLine.TryGetValue(token.Line, out var list))
                    byLine[token.Line] = list = new List<SemanticTokenData>();
                list.Add(token);
            }

            while (_linesHost.childCount > lines.Length)
                _linesHost.RemoveAt(_linesHost.childCount - 1);
            while (_linesHost.childCount < lines.Length)
                _linesHost.Add(NewLine());

            _longestLine = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length > _longestLine)
                    _longestLine = line.Length;
                var row = (Label)_linesHost[i];
                byLine.TryGetValue(i, out var list);
                row.text = BuildLineRichText(line, list);
                // UB-25: coloured edit mode re-pitches every row to the text
                // engine's natural line height so the listing registers with the
                // transparent input's glyphs.
                row.style.height = _coloredEdit && _editPitch > 0f ? _editPitch : LineHeight;
                row.style.backgroundColor = _selectedLine1 == i + 1
                    ? SelBand
                    : LineNamesAny(line, _traceNames) ? TraceBand : BuilderPalette.Transparent;
            }
            ApplyLinesWidth();
        }

        /// <summary>UB-06: the label merges the local T1+T2 list with the LSP's
        /// published diagnostics — only the Roslyn tier (CS####) is taken from
        /// the server, the UITKX tiers are already computed locally. Cap 4.</summary>
        public void SetOverlayDiagnostics(List<(string Code, int Line1, string Message)> overlay)
        {
            _overlayDiagnostics = overlay;
            RenderDiagnosticsLabel();
        }

        private void RenderDiagnosticsLabel()
        {
            var lines = new List<string>();
            foreach (string line in _localDiagnosticsText.Split('\n'))
                if (line.Length > 0)
                    lines.Add(line);
            if (_overlayDiagnostics != null)
                foreach (var (code, line1, message) in _overlayDiagnostics)
                    if (code != null && code.StartsWith("CS", StringComparison.Ordinal))
                        lines.Add(code + " L" + line1 + ": " + message);
            if (lines.Count == 0)
            {
                _diagnosticsLabel.text = "";
                return;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i == 4)
                {
                    sb.Append("… +").Append(lines.Count - 4).Append(" more");
                    break;
                }
                sb.Append(lines[i]).Append('\n');
            }
            _diagnosticsLabel.text = sb.ToString().TrimEnd('\n');
        }

        /// <summary>Tokens are 0-based line/column over the LF buffer; segments
        /// between tokens escape verbatim, token text escapes inside its color
        /// tag, so rich-text markup never shifts what the user is editing.</summary>
        private static bool LineNamesAny(string line, List<string> names)
        {
            if (names == null || names.Count == 0 || line.Length == 0)
                return false;
            foreach (string name in names)
            {
                int at = line.IndexOf(name, StringComparison.Ordinal);
                while (at >= 0)
                {
                    bool leftOk = at == 0 || !char.IsLetterOrDigit(line[at - 1]);
                    int end = at + name.Length;
                    bool rightOk = end >= line.Length || !char.IsLetterOrDigit(line[end]);
                    if (leftOk && rightOk)
                        return true;
                    at = line.IndexOf(name, at + 1, StringComparison.Ordinal);
                }
            }
            return false;
        }

        /// <summary>The POC source palette (index.html .k/.t/.s/.e/.cm/.cu):
        /// keywords #c792ea, tags #4fc3f7, custom tags #7fdbca, strings #c3e88d,
        /// {expr} #ffb74d, comments #616e7a.</summary>
        private static string ColorFor(string tokenType)
        {
            switch (tokenType)
            {
                case SemanticTokenTypes.Element:
                    return "#4FC3F7";
                case SemanticTokenTypes.Type:
                    return "#7FDBCA";
                // POC tokenize() (index.html:1266-1275) has FOUR passes — strings,
                // {…}, <Tag and the nine keywords. Attribute names are classified
                // by none of them, so they keep ".srcline" ink #d6d6dc; painting
                // them accent blue made `text=` read as loud as `<Label`.
                case SemanticTokenTypes.Attribute:
                case SemanticTokenTypes.Property:
                    return null;
                case SemanticTokenTypes.Directive:
                case SemanticTokenTypes.DirectiveName:
                case SemanticTokenTypes.Keyword:
                    return KeywordColor;
                case SemanticTokenTypes.String:
                    return StringColor;
                case SemanticTokenTypes.Number:
                case SemanticTokenTypes.Expression:
                    return ExprColor;
                case SemanticTokenTypes.Comment:
                    return "#616E7A";
                case SemanticTokenTypes.Function:
                case SemanticTokenTypes.Variable:
                    return "#CFCFDA";
                default:
                    return null;
            }
        }

        private const string StringColor = "#C3E88D";
        private const string ExprColor = "#FFB74D";
        private const string KeywordColor = "#C792EA";

        /// <summary>POC tokenize() line 1273: the keyword pass runs LAST, over the
        /// HTML the earlier passes already emitted, and its nested span WINS — so
        /// `new` is purple even inside an orange `{…}` run. The LSP only classifies
        /// markup-side words as Keyword, so `VirtualNode`, `var`, `return`, `new`,
        /// `Style` and `as` in a C# body reached the pane as plain ink.</summary>
        private static readonly System.Text.RegularExpressions.Regex s_keywords =
            new System.Text.RegularExpressions.Regex(
                @"\b(export|VirtualNode|Style|var|return|import|from|as|new)\b"
                + @"|(@if|@else if|@else|@foreach|@for|@while|@switch|@case|@default)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>POC tokenize() pass 1:
        /// <c>h.replace(/(&amp;quot;|")([^"]*)(")/g, …)</c>.</summary>
        private static readonly System.Text.RegularExpressions.Regex s_strings =
            new System.Text.RegularExpressions.Regex(
                "\"[^\"\n]*\"",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static bool[] s_inBrace = new bool[512];

        private static string[] s_colors = new string[512];

        /// <summary>POC tokenize(): after strings are painted, EVERY <c>{…}</c> run
        /// on the line becomes class .e (var(--warn) #ffb74d) — which is why
        /// <c>import { Header }</c> and <c>key={item.Id}</c> are orange in the POC
        /// captures while the LSP classifies those same names as Element/Property
        /// and paints them blue. Strings keep their green because the POC wraps
        /// them first and the brace span nests around them.</summary>
        private static void MarkBraces(string line)
        {
            if (s_inBrace.Length < line.Length)
                s_inBrace = new bool[Mathf.NextPowerOfTwo(line.Length + 1)];
            for (int i = 0; i < line.Length; i++)
                s_inBrace[i] = false;
            int open = -1;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '{')
                    open = i;
                else if (line[i] == '}' && open >= 0)
                {
                    for (int j = open; j <= i; j++)
                        s_inBrace[j] = true;
                    open = -1;
                }
            }
        }

        /// <summary>UI Toolkit's rich-text parser strips tags but does NOT decode
        /// character entities, so "&amp;lt;" reached the glyph run verbatim and
        /// every markup line in the pane read "&amp;lt;VisualElement&amp;gt;". A run
        /// containing '&lt;' is wrapped in &lt;noparse&gt; instead — the glyphs
        /// round-trip exactly, which they must, because the edit box above holds
        /// the same characters.</summary>
        private static void AppendSegment(StringBuilder sb, string text, string color)
        {
            if (text.Length == 0)
                return;
            bool needsNoParse = text.IndexOf('<') >= 0;
            if (color != null)
                sb.Append("<color=").Append(color).Append('>');
            if (needsNoParse)
                sb.Append("<noparse>").Append(text).Append("</noparse>");
            else
                sb.Append(text);
            if (color != null)
                sb.Append("</color>");
        }

        private static string BuildLineRichText(string line, List<SemanticTokenData> tokens)
        {
            if (line.Length == 0)
                return "";
            if (s_colors.Length < line.Length)
                s_colors = new string[Mathf.NextPowerOfTwo(line.Length + 1)];
            for (int i = 0; i < line.Length; i++)
                s_colors[i] = null;

            if (tokens != null)
            {
                tokens.Sort((a, b) => a.Column.CompareTo(b.Column));
                foreach (var token in tokens)
                {
                    int start = Mathf.Clamp(token.Column, 0, line.Length);
                    int end = Mathf.Clamp(token.Column + token.Length, start, line.Length);
                    string color = ColorFor(token.TokenType);
                    // POC ".cu": a tag that is NOT a schema element renders in
                    // the custom-component teal. The semantic provider emits one
                    // Element type for every tag, so the split happens here,
                    // where the tag text and the schema are both in hand.
                    if (token.TokenType == SemanticTokenTypes.Element
                        && BuilderSchemaCache.HasSchema && end > start)
                    {
                        string tag = line.Substring(start, end - start);
                        if (tag.Length > 0 && char.IsUpper(tag[0])
                            && !BuilderSchemaCache.HasElement(tag))
                            color = "#7FDBCA";
                    }
                    for (int i = start; i < end; i++)
                        s_colors[i] = color;
                }
            }

            // POC tokenize() pass 1, and it runs BEFORE everything else: every
            // quoted run on the line becomes ".s" green. The LSP only emits String
            // tokens for C#-side literals, so a markup attribute value
            // (text="SOLD OUT") arrived at the pane as plain ink.
            foreach (System.Text.RegularExpressions.Match m in s_strings.Matches(line))
                for (int i = m.Index; i < m.Index + m.Length; i++)
                    s_colors[i] = StringColor;

            MarkBraces(line);
            for (int i = 0; i < line.Length; i++)
                if (s_inBrace[i] && s_colors[i] != StringColor)
                    s_colors[i] = ExprColor;

            foreach (System.Text.RegularExpressions.Match m in s_keywords.Matches(line))
                for (int i = m.Index; i < m.Index + m.Length; i++)
                    s_colors[i] = KeywordColor;

            var sb = new StringBuilder(line.Length + 32);
            int cursor = 0;
            while (cursor < line.Length)
            {
                string color = s_colors[cursor];
                int run = cursor + 1;
                while (run < line.Length && s_colors[run] == color)
                    run++;
                AppendSegment(sb, line.Substring(cursor, run - cursor), color);
                cursor = run;
            }
            return sb.ToString();
        }
    }
}
#endif
