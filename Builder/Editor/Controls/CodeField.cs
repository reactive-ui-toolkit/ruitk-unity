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
    /// Colored editable .uitkx text (the VE-00 overlay technique): a multiline
    /// TextField with transparent ink carries editing, caret, selection and IME;
    /// a picking-ignored rich-text Label behind it carries the colors. Tokens
    /// come from the local semantic-tokens provider per edit; the label text is
    /// rebuilt with markup-escaped segments so offsets survive escaping.
    /// </summary>
    internal sealed class CodeField : VisualElement
    {
        private readonly TextField _input;
        private readonly Label _overlay;
        private readonly Label _diagnosticsLabel;
        private string _filePath = "";
        private HashSet<string> _knownElements;
        private bool _suppressChange;
        private bool _userCaretActive;
        private VisualElement _completionPopup;

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
            var color = error ? new Color(0.94f, 0.38f, 0.38f) : new Color(0f, 0f, 0f, 0f);
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

        /// <summary>POC "#srcpane { font: 12px Consolas, monospace }". The OS font
        /// is resolved once; a machine without it falls back to the editor font.
        /// It must be applied as a FontDefinition — UI Toolkit's text engine
        /// ignores the legacy dynamic-Font `unityFont` slot.</summary>
        private static readonly Color Ground = new Color(0.090f, 0.090f, 0.106f);

        /// <summary>Strips Unity's field chrome off one element of the TextField's
        /// inner hierarchy. The input stack sits ON TOP of the colored overlay, so
        /// every layer of it must be fully transparent — both its background (which
        /// otherwise paints Unity's lighter field box over the whole pane) and its
        /// ink (which otherwise draws neutral-grey glyphs over the colored ones).
        /// The #17171b ground is painted once by the host behind everything.</summary>
        private static void FlattenInput(VisualElement element)
        {
            if (element == null)
                return;
            element.style.backgroundColor = new Color(0f, 0f, 0f, 0f);
            element.style.color = new Color(1f, 1f, 1f, 0f);
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
            element.style.unityParagraphSpacing = LineLead;
            element.style.unityFontDefinition = BuilderCanvasDrawing.MonoFontDefinition;
        }

        /// <summary>POC "#srcpane .srcline" inherits the body's 13px/1.45 metric at
        /// font-size 12, so the line pitch is 17.4px (measured on
        /// poc-l1-cards.png: import rows at y=501, 518, 536, 553, 570, 588). Unity's
        /// mono face packs the same 12px glyphs at a 12px pitch (measured on
        /// unity-showcase-l0.png: 527, 539, 551, 563, …), so the leading is added
        /// back as paragraph spacing — on the overlay AND on the hidden input, or
        /// the caret stops tracking the glyphs it is meant to sit between.</summary>
        private const float LineLead = 5.4f;

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
            style.backgroundColor = Ground;

            var host = new VisualElement
            {
                style =
                {
                    flexGrow = 1f, position = Position.Relative, overflow = Overflow.Hidden,
                    backgroundColor = Ground,
                    paddingTop = 8f, paddingBottom = 8f,
                },
            };
            Add(host);

            _overlay = new Label
            {
                enableRichText = true,
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position = Position.Absolute,
                    top = 8f, left = 14f, right = 0f, bottom = 0f,
                    color = new Color(0.84f, 0.84f, 0.86f),
                    whiteSpace = WhiteSpace.Pre,
                    unityTextAlign = TextAnchor.UpperLeft,
                    fontSize = 12f,
                },
            };
            _overlay.style.unityParagraphSpacing = LineLead;
            _overlay.style.unityFontDefinition = BuilderCanvasDrawing.MonoFontDefinition;
            host.Add(_overlay);

            _input = new TextField { multiline = true };
            _input.style.position = Position.Absolute;
            _input.style.top = 8f;
            _input.style.left = 14f;
            _input.style.right = 0f;
            _input.style.bottom = 0f;
            _input.style.fontSize = 12f;
            _input.style.unityParagraphSpacing = LineLead;
            _input.style.unityFontDefinition = BuilderCanvasDrawing.MonoFontDefinition;
            _input.style.marginTop = 0f;
            _input.style.marginBottom = 0f;
            _input.style.marginLeft = 0f;
            _input.style.marginRight = 0f;
            _input.style.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _input.style.color = new Color(1f, 1f, 1f, 0f);
            // Unity's own USS paints the inner input element with its field
            // background AND its own text color, so the ground read as a lighter
            // inset rectangle and the colored overlay behind it was occluded
            // entirely. The ink must go transparent on the element that actually
            // draws the glyphs, and its box must be flattened onto #17171b with
            // zero padding so the two layers register glyph-for-glyph.
            FlattenInputTree();
            _input.RegisterCallback<AttachToPanelEvent>(_ => FlattenInputTree());
            _input.RegisterValueChangedCallback(OnInputChanged);
            _input.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _input.RegisterCallback<PointerDownEvent>(evt =>
            {
                _userCaretActive = true;
                if (evt.clickCount >= 2)
                    EditRequested?.Invoke();
            });
            host.Add(_input);

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

        /// <summary>POC row→source sync: place the caret at the given 1-based
        /// line, select that line, and focus the field.</summary>
        public void FocusLine(int line1)
        {
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
            // POC ".srcline.sel": the focused line gets a gold band, not Unity's
            // selection blue.
            _selectedLine1 = line1;
            Recolor(TextLf);
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
            _input.Focus();
        }

        public void SetContent(string textLf, string filePath, HashSet<string> knownElements)
        {
            _filePath = filePath ?? "";
            _knownElements = knownElements;
            _suppressChange = true;
            _input.value = textLf ?? "";
            _suppressChange = false;
            _userCaretActive = false;
            Recolor(textLf ?? "");
        }

        /// <summary>Location-aware palette insertion. With a live user caret the
        /// snippet lands there; otherwise markup snippets go INSIDE the last
        /// return block (before its closing) and body snippets go before the
        /// last <c>return (</c> — a blind caret at index 0 used to prepend
        /// markup above the imports and break the whole buffer.</summary>
        public void InsertSnippet(string snippet, bool isMarkup)
        {
            if (string.IsNullOrEmpty(snippet))
                return;
            if (_userCaretActive)
            {
                InsertAtCaret(snippet);
                return;
            }

            string text = TextLf;
            if (isMarkup)
            {
                int close = text.LastIndexOf("\n  );", StringComparison.Ordinal);
                if (close >= 0)
                {
                    ReplaceAll(text.Substring(0, close + 1)
                        + Indent(snippet.TrimEnd('\n'), "    ") + "\n"
                        + text.Substring(close + 1));
                    return;
                }
            }
            else
            {
                int ret = text.LastIndexOf("\n  return (", StringComparison.Ordinal);
                if (ret >= 0)
                {
                    ReplaceAll(text.Substring(0, ret + 1)
                        + Indent(snippet.TrimEnd('\n'), "  ") + "\n"
                        + text.Substring(ret + 1));
                    return;
                }
            }
            InsertAtCaret(snippet);
        }

        private void ReplaceAll(string newText)
        {
            _input.value = newText;
        }

        private static string Indent(string snippet, string pad)
        {
            var lines = snippet.Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    sb.Append('\n');
                sb.Append(pad).Append(lines[i]);
            }
            return sb.ToString();
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

        private void Recolor(string textLf)
        {
            try
            {
                var parsed = BuilderLanguage.Parse(textLf, _filePath);
                var tokens = BuilderLanguage.Tokens(parsed, textLf, _knownElements, _filePath);
                _overlay.text = BuildRichText(textLf, tokens, _selectedLine1, _traceNames);

                var diags = BuilderLanguage.Diagnose(parsed, _filePath, _knownElements);
                if (diags.Count == 0)
                {
                    _diagnosticsLabel.text = "";
                }
                else
                {
                    var sb = new StringBuilder();
                    int shown = 0;
                    foreach (var d in diags)
                    {
                        if (shown++ == 4)
                        {
                            sb.Append("… +").Append(diags.Count - 4).Append(" more");
                            break;
                        }
                        sb.Append(d.Code).Append(" L").Append(d.SourceLine)
                            .Append(": ").Append(d.Message).Append('\n');
                    }
                    _diagnosticsLabel.text = sb.ToString().TrimEnd('\n');
                }
            }
            catch (Exception)
            {
                _overlay.text = Escape(textLf);
                _diagnosticsLabel.text = "";
            }
        }

        private static string Escape(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

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
                case SemanticTokenTypes.Attribute:
                case SemanticTokenTypes.Property:
                    return "#4FC3F7";
                case SemanticTokenTypes.Directive:
                case SemanticTokenTypes.DirectiveName:
                case SemanticTokenTypes.Keyword:
                    return "#C792EA";
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

        private const string StringColor = "#C3E88D";
        private const string ExprColor = "#FFB74D";

        private static bool[] s_inBrace = new bool[512];

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

        private static string EffectiveColor(int column, string color) =>
            color == StringColor || !s_inBrace[column] ? color : ExprColor;

        private static void AppendColored(
            StringBuilder sb, string line, int start, int end, string color)
        {
            int i = start;
            while (i < end)
            {
                string effective = EffectiveColor(i, color);
                int j = i + 1;
                while (j < end && EffectiveColor(j, color) == effective)
                    j++;
                string text = Escape(line.Substring(i, j - i));
                if (effective != null)
                    sb.Append("<color=").Append(effective).Append('>').Append(text).Append("</color>");
                else
                    sb.Append(text);
                i = j;
            }
        }

        private static string BuildRichText(
            string textLf, SemanticTokenData[] tokens, int selectedLine1, List<string> traceNames)
        {
            string[] lines = textLf.Split('\n');
            var byLine = new Dictionary<int, List<SemanticTokenData>>();
            foreach (var token in tokens)
            {
                if (!byLine.TryGetValue(token.Line, out var list))
                    byLine[token.Line] = list = new List<SemanticTokenData>();
                list.Add(token);
            }

            var sb = new StringBuilder(textLf.Length + tokens.Length * 24);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                MarkBraces(line);
                if (i > 0)
                    sb.Append('\n');
                bool selected = selectedLine1 == i + 1;
                if (!selected && LineNamesAny(line, traceNames))
                {
                    selected = true;
                    sb.Append("<mark=#FFB74D26>");
                }
                else if (selected)
                {
                    sb.Append("<mark=#FFD54F2E>");
                }
                if (!byLine.TryGetValue(i, out var list))
                {
                    AppendColored(sb, line, 0, line.Length, null);
                    if (selected)
                        sb.Append("</mark>");
                    continue;
                }
                list.Sort((a, b) => a.Column.CompareTo(b.Column));
                int cursor = 0;
                foreach (var token in list)
                {
                    int start = Mathf.Clamp(token.Column, 0, line.Length);
                    int end = Mathf.Clamp(token.Column + token.Length, start, line.Length);
                    if (start < cursor)
                        continue;
                    AppendColored(sb, line, cursor, start, null);
                    AppendColored(sb, line, start, end, ColorFor(token.TokenType));
                    cursor = end;
                }
                if (cursor < line.Length)
                    AppendColored(sb, line, cursor, line.Length, null);
                if (selected)
                    sb.Append("</mark>");
            }
            return sb.ToString();
        }
    }
}
#endif
