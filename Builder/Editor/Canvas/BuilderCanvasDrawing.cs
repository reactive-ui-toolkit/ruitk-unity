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
        public const float CardWidth = 220f;

        public const float EdgeAnchorY = 19f;

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

        public static void DrawEdges(MeshGenerationContext ctx, BuilderGraph graph)
        {
            if (ctx == null || graph == null)
                return;
            var p = ctx.painter2D;
            foreach (var edge in graph.Edges)
            {
                if (edge.FromIndex < 0 || edge.FromIndex >= graph.Nodes.Count)
                    continue;
                var from = graph.Nodes[edge.FromIndex];
                var a = new Vector2(from.X + CardWidth, from.Y + EdgeAnchorY);

                p.lineWidth = 2f;
                p.BeginPath();
                p.MoveTo(a);
                if (edge.ToIndex >= 0 && edge.ToIndex < graph.Nodes.Count)
                {
                    var to = graph.Nodes[edge.ToIndex];
                    var b = new Vector2(to.X, to.Y + EdgeAnchorY);
                    float dx = Mathf.Max(40f, Mathf.Abs(b.x - a.x) * 0.5f);
                    p.strokeColor = KindColor(edge.TargetKind);
                    p.BezierCurveTo(new Vector2(a.x + dx, a.y), new Vector2(b.x - dx, b.y), b);
                }
                else
                {
                    p.strokeColor = new Color(0.90f, 0.30f, 0.30f);
                    p.LineTo(new Vector2(a.x + 48f, a.y));
                }
                p.Stroke();
            }
        }
    }
}
#endif
