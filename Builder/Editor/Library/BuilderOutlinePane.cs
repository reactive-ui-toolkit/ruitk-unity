#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Ruitk.Language.Nodes;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// Structure outline of the focused buffer (VE-13 reorder leg): the element
    /// tree with per-row up/down reorder. Reordering swaps the two siblings'
    /// LINE RANGES in the buffer (elements are line-delimited in canonical
    /// formatting) and routes the result through the same edited-text pipeline
    /// as typing — session, undo, debounced recompile.
    /// </summary>
    internal sealed class BuilderOutlinePane
    {
        private sealed class Row
        {
            public ElementNode Node;
            public ElementNode Parent;
            public int Depth;
            public int SiblingIndex;
            public List<ElementNode> Siblings;
        }

        private VisualElement _listHost;
        private Func<string> _getBuffer;
        private Action<string> _applyBuffer;
        private string _filePath = "";

        public void Attach(VisualElement container, Func<string> getBuffer, Action<string> applyBuffer)
        {
            _getBuffer = getBuffer;
            _applyBuffer = applyBuffer;
            container.Clear();
            container.Add(new Label("Outline")
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    marginTop = 6f, marginLeft = 6f,
                    color = new Color(0.55f, 0.55f, 0.6f),
                    fontSize = 10f,
                },
            });
            var scroll = new ScrollView { style = { flexGrow = 1f } };
            _listHost = scroll.contentContainer;
            container.Add(scroll);
        }

        public void ShowFile(string filePath)
        {
            _filePath = filePath ?? "";
            Rebuild();
        }

        public void Rebuild()
        {
            if (_listHost == null || _getBuffer == null)
                return;
            _listHost.Clear();
            string buffer = _getBuffer();
            if (string.IsNullOrEmpty(buffer))
                return;

            var rows = new List<Row>();
            try
            {
                var parsed = BuilderLanguage.Parse(buffer, _filePath);
                foreach (var node in parsed.RootNodes)
                    Collect(node, null, 0, rows);
            }
            catch
            {
                return;
            }

            foreach (var row in rows)
            {
                var line = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.Center },
                };
                line.Add(new Label(row.Node.TagName)
                {
                    style = { marginLeft = 8f + row.Depth * 12f, flexGrow = 1f },
                });
                if (row.Siblings != null && row.Siblings.Count > 1)
                {
                    var captured = row;
                    var up = MakeMini("▲", () => Swap(captured, -1));
                    var down = MakeMini("▼", () => Swap(captured, 1));
                    up.SetEnabled(row.SiblingIndex > 0);
                    down.SetEnabled(row.SiblingIndex < row.Siblings.Count - 1);
                    line.Add(up);
                    line.Add(down);
                }
                _listHost.Add(line);
            }
        }

        private static Button MakeMini(string glyph, Action onClick)
        {
            return new Button(onClick)
            {
                text = glyph,
                style =
                {
                    fontSize = 8f,
                    width = 18f,
                    height = 14f,
                    marginLeft = 1f,
                    marginRight = 1f,
                    paddingLeft = 0f,
                    paddingRight = 0f,
                    paddingTop = 0f,
                    paddingBottom = 0f,
                },
            };
        }

        private static void Collect(AstNode node, ElementNode parent, int depth, List<Row> rows)
        {
            if (node is ElementNode element)
            {
                var siblings = parent == null
                    ? null
                    : ElementChildren(parent);
                rows.Add(new Row
                {
                    Node = element,
                    Parent = parent,
                    Depth = depth,
                    Siblings = siblings,
                    SiblingIndex = siblings?.FindIndex(e => ReferenceEquals(e, element)) ?? 0,
                });
                foreach (var child in element.Children)
                    Collect(child, element, depth + 1, rows);
                return;
            }

            foreach (var child in ChildrenOf(node))
                Collect(child, parent, depth, rows);
        }

        private static List<ElementNode> ElementChildren(ElementNode parent)
        {
            var list = new List<ElementNode>();
            foreach (var child in parent.Children)
                if (child is ElementNode e)
                    list.Add(e);
            return list;
        }

        private static IEnumerable<AstNode> ChildrenOf(AstNode node)
        {
            switch (node)
            {
                case ElementNode e:
                    return e.Children;
                default:
                    return Array.Empty<AstNode>();
            }
        }

        private static int EndLineOf(ElementNode element)
        {
            int end = Math.Max(element.SourceLine, element.CloseTagLine);
            if (element.EndLine > end)
                end = element.EndLine;
            return end;
        }

        private void Swap(Row row, int direction)
        {
            if (row.Siblings == null)
                return;
            int otherIndex = row.SiblingIndex + direction;
            if (otherIndex < 0 || otherIndex >= row.Siblings.Count)
                return;

            var a = direction < 0 ? row.Siblings[otherIndex] : row.Node;
            var b = direction < 0 ? row.Node : row.Siblings[otherIndex];

            string buffer = _getBuffer();
            string[] lines = buffer.Split('\n');
            int a1 = a.SourceLine - 1, a2 = EndLineOf(a) - 1;
            int b1 = b.SourceLine - 1, b2 = EndLineOf(b) - 1;
            if (a1 < 0 || b2 >= lines.Length || a2 >= b1)
                return;

            var result = new List<string>(lines.Length);
            for (int i = 0; i < a1; i++)
                result.Add(lines[i]);
            for (int i = b1; i <= b2; i++)
                result.Add(lines[i]);
            for (int i = a2 + 1; i < b1; i++)
                result.Add(lines[i]);
            for (int i = a1; i <= a2; i++)
                result.Add(lines[i]);
            for (int i = b2 + 1; i < lines.Length; i++)
                result.Add(lines[i]);

            _applyBuffer(string.Join("\n", result));
            Rebuild();
        }
    }
}
#endif
