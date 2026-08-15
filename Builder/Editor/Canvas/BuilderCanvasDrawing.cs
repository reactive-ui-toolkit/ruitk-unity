#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// Painter2D edge rendering plus the card metrics shared between the edge
    /// painter and the CanvasView markup. The edge layer draws in WORLD
    /// coordinates: it sits inside the camera-scaled world container as a 1x1
    /// overflow-visible element at the world origin, so pan/zoom transforms the
    /// strokes for free and no repaint is needed on camera moves — only on
    /// node-position changes (redrawKey = layout version).
    /// </summary>
    public static class BuilderCanvasDrawing
    {
        public const float CardWidth = 340f;

        public const float EdgeAnchorY = 18f;

        /// <summary>Set by CanvasView each render; the edge painter shares it so
        /// anchors track the LOD-dependent card width (POC: 300 / 340 / 430).</summary>
        public static int CurrentLod = 1;

        public static float CardWidthFor(int lod) =>
            lod == 0 ? 300f : lod == 1 ? 340f : 430f;

        private static readonly Color UsageEdge = new Color(0.361f, 0.545f, 0.690f);
        private static readonly Color HookEdge = new Color(0.427f, 0.659f, 0.435f);
        private static readonly Color StyleEdge = new Color(0.647f, 0.467f, 0.702f);

        public static Color KindColor(BuilderNodeKind kind)
        {
            switch (kind)
            {
                case BuilderNodeKind.Component:
                    return new Color(0.31f, 0.76f, 0.97f);
                case BuilderNodeKind.Hook:
                    return new Color(0.70f, 0.62f, 0.86f);
                case BuilderNodeKind.Style:
                    return new Color(1.00f, 0.72f, 0.30f);
                case BuilderNodeKind.Util:
                    return new Color(0.51f, 0.78f, 0.52f);
                default:
                    return new Color(0.55f, 0.55f, 0.60f);
            }
        }

        public static Color KindBadgeBg(BuilderNodeKind kind)
        {
            var c = KindColor(kind);
            c.a = 0.16f;
            return c;
        }

        /// <summary>Chip line "useState  →  gold, setGold" → POC coloring:
        /// hook name green, state names warn-gold.</summary>
        public static string ChipRichText(string bodyLineText)
        {
            int arrow = bodyLineText.IndexOf("  →  ", System.StringComparison.Ordinal);
            if (arrow < 0)
                return "<color=#81C784>" + bodyLineText + "</color>";
            return "<color=#81C784>" + bodyLineText.Substring(0, arrow) + "</color> → <color=#FFB74D>"
                + bodyLineText.Substring(arrow + 5) + "</color>";
        }

        /// <summary>POC 6.6 band math: rel &lt; 0.3 → before (0), rel &gt; 0.7 →
        /// after (2), else inside (1) — computed from the pointer's local Y in
        /// the hovered row.</summary>
        public static int DropBand(Ruitk.Core.ReactivePointerEvent evt)
        {
            var target = evt?.NativeEvent?.target as VisualElement;
            var pointer = evt?.NativeEvent as IPointerEvent;
            if (target == null || pointer == null)
                return 1;
            float height = target.resolvedStyle.height;
            if (height <= 0f)
                return 1;
            float rel = pointer.localPosition.y / height;
            return rel < 0.3f ? 0 : rel > 0.7f ? 2 : 1;
        }

        public static bool DragActive => BuilderDragService.Active;

        public static Color BadgeColor(int badgeKind) => badgeKind switch
        {
            1 => new Color(1.00f, 0.72f, 0.30f),
            2 => new Color(0.51f, 0.78f, 0.52f),
            3 => new Color(0.94f, 0.38f, 0.57f),
            _ => new Color(0.77f, 0.53f, 0.75f),
        };

        public static Color BadgeBg(int badgeKind)
        {
            var c = BadgeColor(badgeKind);
            c.a = 0.2f;
            return c;
        }

        /// <summary>State names carried by a hook chip line ("useState  →  gold, setGold"
        /// → ["gold","setGold"]), null when the chip has no states.</summary>
        public static string ChipStates(string bodyLineText)
        {
            int arrow = bodyLineText.IndexOf("  →  ", System.StringComparison.Ordinal);
            if (arrow < 0)
                return null;
            return bodyLineText.Substring(arrow + 5).Replace(",", " ");
        }

        /// <summary>POC hover-trace: does this row's attribute text reference any
        /// of the hovered chip's state names (word boundaries)?</summary>
        public static bool RowMatchesTrace(string attrsText, string traceStates)
        {
            if (string.IsNullOrEmpty(traceStates) || string.IsNullOrEmpty(attrsText))
                return false;
            foreach (string name in traceStates.Split(' '))
            {
                string n = name.Trim();
                if (n.Length == 0)
                    continue;
                int at = attrsText.IndexOf(n, System.StringComparison.Ordinal);
                while (at >= 0)
                {
                    bool leftOk = at == 0 || !char.IsLetterOrDigit(attrsText[at - 1]);
                    int end = at + n.Length;
                    bool rightOk = end >= attrsText.Length || !char.IsLetterOrDigit(attrsText[end]);
                    if (leftOk && rightOk)
                        return true;
                    at = attrsText.IndexOf(n, at + 1, System.StringComparison.Ordinal);
                }
            }
            return false;
        }

        public static string KindLabel(BuilderNodeKind kind)
        {
            switch (kind)
            {
                case BuilderNodeKind.Component:
                    return "component";
                case BuilderNodeKind.Hook:
                    return "hooks";
                case BuilderNodeKind.Style:
                    return "style";
                case BuilderNodeKind.Util:
                    return "utils";
                default:
                    return "file";
            }
        }

        public static Color LineColor(BuilderCardLineKind kind)
        {
            switch (kind)
            {
                case BuilderCardLineKind.Import:
                    return new Color(0.50f, 0.70f, 0.84f);
                case BuilderCardLineKind.Hook:
                    return new Color(0.90f, 0.78f, 0.40f);
                case BuilderCardLineKind.Element:
                    return new Color(0.42f, 0.68f, 0.90f);
                case BuilderCardLineKind.Component:
                    return new Color(0.31f, 0.86f, 0.77f);
                case BuilderCardLineKind.Directive:
                    return new Color(0.77f, 0.53f, 0.75f);
                case BuilderCardLineKind.Export:
                    return new Color(0.62f, 0.78f, 0.55f);
                default:
                    return new Color(0.60f, 0.60f, 0.66f);
            }
        }

        public static void DrawEdges(MeshGenerationContext ctx, BuilderGraph graph)
        {
            if (ctx == null || graph == null)
                return;
            var p = ctx.painter2D;
            float width = CardWidthFor(CurrentLod);
            foreach (var edge in graph.Edges)
            {
                if (edge.FromIndex < 0 || edge.FromIndex >= graph.Nodes.Count)
                    continue;
                var from = graph.Nodes[edge.FromIndex];
                var a = CurrentLod == 0
                    ? new Vector2(from.X + width, from.Y + 24f)
                    : new Vector2(from.X + width, from.Y + ImportRowY(from, edge.Specifier));

                if (edge.ToIndex >= 0 && edge.ToIndex < graph.Nodes.Count)
                {
                    var to = graph.Nodes[edge.ToIndex];
                    var b = new Vector2(to.X, to.Y + EdgeAnchorY);
                    Color color = edge.TargetKind == BuilderNodeKind.Hook ? HookEdge
                        : edge.TargetKind == BuilderNodeKind.Style ? StyleEdge
                        : UsageEdge;
                    color.a = 0.85f;
                    bool dashed = edge.TargetKind == BuilderNodeKind.Style
                        || edge.TargetKind == BuilderNodeKind.Hook;
                    float dx = Mathf.Max(40f, Mathf.Abs(b.x - a.x) * 0.45f);
                    var c1 = new Vector2(a.x + dx, a.y);
                    var c2 = new Vector2(b.x - dx, b.y);
                    if (dashed)
                        StrokeDashedBezier(p, a, c1, c2, b, color);
                    else
                    {
                        p.strokeColor = color;
                        p.lineWidth = 2f;
                        p.BeginPath();
                        p.MoveTo(a);
                        p.BezierCurveTo(c1, c2, b);
                        p.Stroke();
                    }
                    p.fillColor = color;
                    p.BeginPath();
                    p.Arc(b, 4f, 0f, 360f);
                    p.Fill();
                }
                else
                {
                    p.strokeColor = new Color(0.90f, 0.30f, 0.30f);
                    p.lineWidth = 2f;
                    p.BeginPath();
                    p.MoveTo(a);
                    p.LineTo(new Vector2(a.x + 48f, a.y));
                    p.Stroke();
                }
            }
        }

        /// <summary>Edges leave the importer at ITS import row (matched by
        /// specifier), like the POC — not a shared card-level anchor.</summary>
        private static float ImportRowY(BuilderCanvasNode node, string specifier)
        {
            if (node.Imports.Count == 0 || string.IsNullOrEmpty(specifier))
                return EdgeAnchorY;
            int row = -1;
            for (int i = 0; i < node.Imports.Count; i++)
            {
                if (node.Imports[i].Text.EndsWith("  " + specifier, System.StringComparison.Ordinal)
                    || node.Imports[i].Text.EndsWith("←  " + specifier, System.StringComparison.Ordinal))
                {
                    row = i;
                    break;
                }
            }
            if (row < 0)
                return EdgeAnchorY;
            float headerBlock = 24f
                + (string.IsNullOrEmpty(node.Signature) ? 0f : 14f)
                + 16f;
            return headerBlock + row * 13f + 6f;
        }

        private static void StrokeDashedBezier(
            Painter2D p, Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, Color color)
        {
            const int segments = 28;
            p.strokeColor = color;
            p.lineWidth = 2f;
            Vector2 Point(float t)
            {
                float u = 1f - t;
                return u * u * u * a
                    + 3f * u * u * t * c1
                    + 3f * u * t * t * c2
                    + t * t * t * b;
            }
            for (int i = 0; i < segments; i += 2)
            {
                p.BeginPath();
                p.MoveTo(Point((float)i / segments));
                p.LineTo(Point((float)(i + 1) / segments));
                p.Stroke();
            }
        }
    }
}
#endif
