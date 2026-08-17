#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// UB-76: THE inline editor. One floating panel hosting a fragment-mode
    /// <see cref="CodeField"/> replaces all six bare in-card TextFields, so
    /// every editable text surface gets the same colouring, Ctrl+Space
    /// completion (at a position the OWNER maps into the real file) and the
    /// coloured-edit overlay — implemented once. The panel anchors over the
    /// element being edited, owns keyboard focus from the frame it opens
    /// (UB-70's stray-Enter race cannot happen), commits on Enter
    /// (Ctrl+Enter when multiline) or focus loss, and cancels on Esc.
    /// </summary>
    internal sealed class BuilderInlineEditorOverlay
    {
        private VisualElement _root;
        private VisualElement _panel;
        private CodeField _field;
        private string _seed;
        private Action<string> _commit;
        private Action _closed;
        private bool _done;

        public bool IsOpen => _panel != null;

        /// <summary>The field's current LF text — completion providers read it
        /// to synthesize the in-progress buffer.</summary>
        public string CurrentText => _field?.TextLf ?? "";

        /// <summary>0-based caret within the fragment.</summary>
        public (int Line0, int Char0) Caret => _field?.CaretLocal ?? (0, 0);

        public void Attach(VisualElement windowRoot) => _root = windowRoot;

        /// <summary>Opens the editor over <paramref name="anchor"/> (panel
        /// coordinates come from its worldBound). A commit fires
        /// <paramref name="commit"/> only when the text changed;
        /// <paramref name="closed"/> always fires exactly once afterwards —
        /// the owner re-syncs the LSP buffer there because completion requests
        /// push synthesized text.</summary>
        public void Show(
            VisualElement anchor,
            string seed,
            bool multiline,
            Func<int, int, System.Threading.Tasks.Task<List<(string Label, string Insert)>>> completionProvider,
            Action<string> commit,
            Action closed = null)
        {
            if (_root == null)
                return;
            Close(commitIfChanged: true);

            _seed = seed ?? "";
            _commit = commit;
            _closed = closed;
            _done = false;

            _field = new CodeField
            {
                FragmentMode = true,
                SingleLine = !multiline,
                CompletionProvider = completionProvider,
            };
            _field.SetContent(_seed, "fragment.uitkx", null);
            _field.SetEditable(true);
            _field.ApplyRequested += () => Close(commitIfChanged: true);
            _field.CancelRequested += () => Close(commitIfChanged: false);

            _panel = new VisualElement
            {
                style =
                {
                    position = Position.Absolute,
                    backgroundColor = BuilderPalette.Ground,
                    borderTopWidth = 1f, borderBottomWidth = 1f,
                    borderLeftWidth = 1f, borderRightWidth = 1f,
                    borderTopColor = BuilderPalette.Accent,
                    borderBottomColor = BuilderPalette.Accent,
                    borderLeftColor = BuilderPalette.Accent,
                    borderRightColor = BuilderPalette.Accent,
                    borderTopLeftRadius = 4f, borderTopRightRadius = 4f,
                    borderBottomLeftRadius = 4f, borderBottomRightRadius = 4f,
                },
            };
            _panel.Add(_field);

            // The editor spans the whole ROW it belongs to, not just the badge or
            // label that was clicked: a field narrower than the highlighted item
            // it is editing reads as misaligned chrome (owner report
            // 2026-08-17). Falls back to the anchor when the row cannot be found,
            // which is what every non-row surface uses anyway.
            Rect bound = anchor != null ? anchor.worldBound : new Rect(60f, 60f, 320f, 24f);
            var rowHost = RowAncestorOf(anchor);
            if (rowHost != null && rowHost.worldBound.width > bound.width)
            {
                var rowBound = rowHost.worldBound;
                bound = new Rect(rowBound.xMin, bound.yMin, rowBound.width, bound.height);
            }
            Vector2 local = _root.WorldToLocal(new Vector2(bound.xMin, bound.yMin));
            float rootWidth = _root.layout.width;
            float rootHeight = _root.layout.height;
            float width = Mathf.Clamp(Mathf.Max(bound.width, 260f), 200f, 640f);
            float height = multiline
                ? Mathf.Clamp(Mathf.Max(bound.height + 24f, 140f), 140f, 380f)
                : 30f;
            _panel.style.left = Mathf.Clamp(local.x, 4f, Mathf.Max(4f, rootWidth - width - 4f));
            _panel.style.top = Mathf.Clamp(local.y - 3f, 4f, Mathf.Max(4f, rootHeight - height - 4f));
            _panel.style.width = width;
            _panel.style.height = height;

            _root.Add(_panel);
            _panel.BringToFront();
            var field = _field;
            _panel.schedule.Execute(() =>
            {
                if (_field != field)
                    return;
                field.SetEditing(true);
                // The edit box is display:none until the line above, so it has
                // no layout this tick and Focus() would be a silent no-op.
                // Focusing is retried on later ticks until it actually takes —
                // a canvas re-render landing between the open and the focus
                // (which is exactly what a freshly seeded directive header does)
                // can also drop it once.
                TryFocusRepeatedly(field, 8);
            }).ExecuteLater(16);
            // Commit-on-blur, the semantics every in-card editor had: when
            // focus leaves the panel entirely, the edit lands unless Esc
            // already finished it.
            _panel.RegisterCallback<FocusOutEvent>(_ =>
            {
                var panel = _panel;
                panel?.schedule.Execute(() =>
                {
                    if (_panel != panel || _done)
                        return;
                    var focused = panel.panel?.focusController?.focusedElement as VisualElement;
                    for (var walk = focused; walk != null; walk = walk.parent)
                        if (walk == panel)
                            return;
                    Close(commitIfChanged: true);
                }).ExecuteLater(1);
            });
        }

        private void TryFocusRepeatedly(CodeField field, int attempts)
        {
            if (_field != field || _done || attempts <= 0)
                return;
            field.FocusEditor();
            if (field.EditorHasFocus)
                return;
            _panel?.schedule.Execute(() => TryFocusRepeatedly(field, attempts - 1))
                .ExecuteLater(24);
        }

        /// <summary>The canvas row an inline-edit anchor belongs to. Rows are the
        /// only elements named "row-{card}-{row}", which is the same handle the
        /// drag hit-test walks for.</summary>
        private static VisualElement RowAncestorOf(VisualElement anchor)
        {
            for (var walk = anchor; walk != null; walk = walk.parent)
                if (!string.IsNullOrEmpty(walk.name)
                    && walk.name.StartsWith("row-", System.StringComparison.Ordinal))
                    return walk;
            return null;
        }

        public void Close(bool commitIfChanged)
        {
            if (_panel == null || _done)
            {
                RemovePanel();
                return;
            }
            _done = true;
            string text = _field?.TextLf ?? "";
            var commit = _commit;
            var closed = _closed;
            bool changed = !string.Equals(text, _seed, StringComparison.Ordinal);
            RemovePanel();
            if (commitIfChanged && changed)
                commit?.Invoke(text);
            closed?.Invoke();
        }

        private void RemovePanel()
        {
            _panel?.RemoveFromHierarchy();
            _panel = null;
            _field = null;
            _commit = null;
            _closed = null;
        }
    }
}
#endif
