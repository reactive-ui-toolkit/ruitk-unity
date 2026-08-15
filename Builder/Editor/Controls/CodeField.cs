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

        /// <summary>Ctrl+Space asks this for completions at (line0, char0);
        /// the window wires it to the shared LSP client.</summary>
        public Func<int, int, System.Threading.Tasks.Task<List<(string Label, string Insert)>>>
            CompletionProvider { get; set; }

        public CodeField()
        {
            style.flexGrow = 1f;

            var host = new VisualElement
            {
                style = { flexGrow = 1f, position = Position.Relative, overflow = Overflow.Hidden },
            };
            Add(host);

            _overlay = new Label
            {
                enableRichText = true,
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position = Position.Absolute,
                    top = 0f, left = 0f, right = 0f, bottom = 0f,
                    color = new Color(0.84f, 0.84f, 0.86f),
                    whiteSpace = WhiteSpace.Pre,
                    unityTextAlign = TextAnchor.UpperLeft,
                },
            };
            host.Add(_overlay);

            _input = new TextField { multiline = true };
            _input.style.position = Position.Absolute;
            _input.style.top = 0f;
            _input.style.left = 0f;
            _input.style.right = 0f;
            _input.style.bottom = 0f;
            _input.style.color = new Color(1f, 1f, 1f, 0f);
            _input.RegisterValueChangedCallback(OnInputChanged);
            _input.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _input.RegisterCallback<PointerDownEvent>(_ => _userCaretActive = true);
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
        }

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
                _overlay.text = BuildRichText(textLf, tokens);

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

        private static string ColorFor(string tokenType)
        {
            switch (tokenType)
            {
                case SemanticTokenTypes.Element:
                    return "#4FC3F7";
                case SemanticTokenTypes.Attribute:
                case SemanticTokenTypes.Property:
                    return "#9CDCFE";
                case SemanticTokenTypes.Directive:
                case SemanticTokenTypes.DirectiveName:
                    return "#C586C0";
                case SemanticTokenTypes.Keyword:
                    return "#569CD6";
                case SemanticTokenTypes.String:
                    return "#CE9178";
                case SemanticTokenTypes.Number:
                    return "#B5CEA8";
                case SemanticTokenTypes.Comment:
                    return "#6A9955";
                case SemanticTokenTypes.Type:
                    return "#4EC9B0";
                case SemanticTokenTypes.Function:
                    return "#DCDCAA";
                case SemanticTokenTypes.Variable:
                    return "#D4D4D4";
                default:
                    return null;
            }
        }

        /// <summary>Tokens are 0-based line/column over the LF buffer; segments
        /// between tokens escape verbatim, token text escapes inside its color
        /// tag, so rich-text markup never shifts what the user is editing.</summary>
        private static string BuildRichText(string textLf, SemanticTokenData[] tokens)
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
                if (i > 0)
                    sb.Append('\n');
                if (!byLine.TryGetValue(i, out var list))
                {
                    sb.Append(Escape(line));
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
                    sb.Append(Escape(line.Substring(cursor, start - cursor)));
                    string color = ColorFor(token.TokenType);
                    string text = Escape(line.Substring(start, end - start));
                    if (color != null)
                        sb.Append("<color=").Append(color).Append('>').Append(text).Append("</color>");
                    else
                        sb.Append(text);
                    cursor = end;
                }
                if (cursor < line.Length)
                    sb.Append(Escape(line.Substring(cursor)));
            }
            return sb.ToString();
        }
    }
}
#endif
