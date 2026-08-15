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

        /// <summary>Card geometry the edge painter estimates anchors from — kept
        /// in lockstep with canvasStyles (POC .card-title / .card-section
        /// paddings and the per-row font sizes).</summary>
        private const float HeaderH = 35f;

        private const float PillH = 66f;

        private const float SignatureSectionH = 30f;

        private const float SectionOverheadH = 33f;

        private const float ImportRowH = 17f;

        private const float MarkupRowH = 21f;

        private const float ChipRowH = 26f;

        /// <summary>Set by CanvasView each render; the edge painter shares it so
        /// anchors track the LOD-dependent card width (POC: 300 / 340 / 430).</summary>
        public static int CurrentLod = 1;

        private static float s_currentZoom = 1f;

        private static int s_anchorRetries;

        /// <summary>POC "#edges" is a SCREEN-space overlay over the transformed
        /// world: stroke width, the terminal dot and the dash period are constant
        /// pixels at every LOD. Our layer paints inside the scaled container, so
        /// the painter divides by the live zoom to get the same result.</summary>
        public static float CurrentZoom
        {
            get => s_currentZoom <= 0f ? 1f : s_currentZoom;
            set
            {
                s_currentZoom = value;
                s_anchorRetries = 0;
            }
        }

        /// <summary>POC "font: 12px Consolas, monospace" — every code-bearing
        /// surface (card rows, chips, imports, islands, palette) is monospace.
        /// Resolved once from the OS; a machine without it keeps the editor font.</summary>
        public static Font MonoFont()
        {
            if (s_monoResolved)
                return s_mono;
            s_monoResolved = true;
            try
            {
                s_mono = Font.CreateDynamicFontFromOSFont(
                    new[] { "Consolas", "Menlo", "DejaVu Sans Mono", "Courier New" }, 12);
            }
            catch (System.Exception)
            {
                s_mono = null;
            }
            return s_mono;
        }

        private static Font s_mono;
        private static bool s_monoResolved;
        private static bool s_monoDefResolved;
        private static FontDefinition s_monoDef;

        /// <summary>The same font as a typed-Style value. Falls back to Unity's
        /// legacy runtime font so a missing Consolas never blanks the card text.</summary>
        public static FontDefinition MonoFontDefinition
        {
            get
            {
                if (s_monoDefResolved)
                    return s_monoDef;
                s_monoDefResolved = true;
                var font = MonoFont();
                if (font == null)
                {
                    try
                    {
                        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    }
                    catch (System.Exception)
                    {
                        font = null;
                    }
                }
                s_monoDef = font != null ? FontDefinition.FromFont(font) : default;
                return s_monoDef;
            }
        }

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
                    return new Color(0.310f, 0.765f, 0.969f);
                case BuilderNodeKind.Hook:
                    return new Color(0.506f, 0.780f, 0.518f);
                case BuilderNodeKind.Style:
                    return new Color(0.808f, 0.576f, 0.847f);
                case BuilderNodeKind.Util:
                    return new Color(1.000f, 0.718f, 0.302f);
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

        /// <summary>POC props-row: the export name in bold text-color, the
        /// parameter signature dimmed ("<b>Header</b>(string title, int gold)").</summary>
        public static string SignatureRichText(string signature)
        {
            if (string.IsNullOrEmpty(signature))
                return "";
            int paren = signature.IndexOf('(');
            if (paren < 0)
                return "<b><color=#D6D6DC>" + signature + "</color></b>";
            return "<b><color=#D6D6DC>" + signature.Substring(0, paren) + "</color></b>"
                + signature.Substring(paren);
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
        /// after (2), else inside (1) — measured against the ROW element the
        /// handler is registered on (currentTarget), never the inner label the
        /// event happens to bubble from.</summary>
        public static int DropBand(Ruitk.Core.ReactivePointerEvent evt)
        {
            var row = evt?.CurrentTarget ?? evt?.Target;
            var pointer = evt?.NativeEvent as IPointerEvent;
            if (row == null || pointer == null)
                return 1;
            float height = row.resolvedStyle.height;
            if (height <= 0f)
                return 1;
            float y = row.WorldToLocal(pointer.position).y;
            float rel = y / height;
            return rel < 0.3f ? 0 : rel > 0.7f ? 2 : 1;
        }

        /// <summary>POC ".style-entry b": only the export NAME is bold and
        /// --style purple; " = new Style {" stays #cfcfda.</summary>
        public static string ExportRichText(string line)
        {
            if (string.IsNullOrEmpty(line))
                return "";
            int split = line.IndexOf(" = ", System.StringComparison.Ordinal);
            if (split < 0)
                return line;
            return "<b><color=#CE93D8>" + line.Substring(0, split) + "</color></b>"
                + line.Substring(split);
        }

        /// <summary>POC ".card-section:last-child { border-bottom: none }" —
        /// which section index is the bottom-most one this card renders
        /// (0 signature, 1 imports, 2 body, 3 exports, 4 markup).</summary>
        public static int LastSectionOf(BuilderCanvasNode node)
        {
            if (node == null)
                return 0;
            if (node.Markup.Count > 0)
                return 4;
            if (node.ExportDetail.Count > 0)
                return 3;
            if (node.Kind == BuilderNodeKind.Component || node.Kind == BuilderNodeKind.Hook
                || node.Body.Count > 0)
                return 2;
            if (node.Imports.Count > 0)
                return 1;
            return 0;
        }

        public static bool DragActive => BuilderDragService.Active;

        /// <summary>POC placeMenu(): menus open AT the click. Records the
        /// gesture's panel-space point for the next BuilderSearchMenu.</summary>
        public static void RememberMenuPointer(Ruitk.Core.ReactivePointerEvent evt)
        {
            if (evt == null)
                return;
            BuilderSearchMenu.RememberPointer(
                new Vector2(evt.Position.x, evt.Position.y),
                UnityEditor.EditorWindow.focusedWindow);
        }

        /// <summary>Anchor-dot color per import kind marker (5 usage / 6 hook /
        /// 7 style).</summary>
        public static Color DotColor(int dotKind) => dotKind switch
        {
            6 => new Color(0.427f, 0.659f, 0.435f),
            7 => new Color(0.647f, 0.467f, 0.702f),
            _ => new Color(0.361f, 0.545f, 0.690f),
        };

        /// <summary>POC ".anchor-dot { box-shadow: 0 0 0 2px rgba(...,.25) }" —
        /// the halo ring around an anchor dot, per import kind.</summary>
        public static Color DotHalo(int dotKind)
        {
            var c = DotColor(dotKind);
            c.a = 0.25f;
            return c;
        }

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
                    return "hook";
                case BuilderNodeKind.Style:
                    return "styles";
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
                    return new Color(0.545f, 0.545f, 0.588f);
                case BuilderCardLineKind.Hook:
                    return new Color(0.506f, 0.780f, 0.518f);
                case BuilderCardLineKind.Element:
                    return new Color(0.310f, 0.765f, 0.969f);
                case BuilderCardLineKind.Component:
                    return new Color(0.498f, 0.859f, 0.792f);
                case BuilderCardLineKind.Directive:
                    return new Color(0.808f, 0.576f, 0.847f);
                case BuilderCardLineKind.Export:
                    return new Color(0.808f, 0.576f, 0.847f);
                default:
                    return new Color(0.812f, 0.812f, 0.855f);
            }
        }

        /// <summary>POC canvas ground: a 26px dot lattice (#2c2c33, 1px) painted
        /// in SCREEN space behind the camera-transformed world, matching the
        /// radial-gradient background of #canvasWrap.</summary>
        public static void DrawDotGrid(MeshGenerationContext ctx)
        {
            if (ctx?.visualElement == null)
                return;
            var rect = ctx.visualElement.contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
                return;
            var p = ctx.painter2D;
            p.fillColor = new Color(0.173f, 0.173f, 0.200f);
            p.BeginPath();
            for (float y = 1f; y < rect.height; y += 26f)
            {
                for (float x = 1f; x < rect.width; x += 26f)
                {
                    p.MoveTo(new Vector2(x, y));
                    p.LineTo(new Vector2(x + 1.5f, y));
                    p.LineTo(new Vector2(x + 1.5f, y + 1.5f));
                    p.LineTo(new Vector2(x, y + 1.5f));
                    p.ClosePath();
                }
            }
            p.Fill();
        }

        public static void DrawEdges(MeshGenerationContext ctx, BuilderGraph graph)
        {
            if (ctx == null || graph == null)
                return;
            var p = ctx.painter2D;
            float width = CardWidthFor(CurrentLod);
            var layer = ctx.visualElement;
            var world = layer?.parent;
            bool estimated = false;

            // POC drawEdges reads getBoundingClientRect() off the anchor DOT, so a
            // curve leaves exactly from the dot on its import/markup row. The named
            // dot elements are measured here; the section-stack estimate is only the
            // pre-layout fallback.
            Vector2 AnchorOf(string name, Vector2 fallback)
            {
                var el = world?.Q(name);
                if (el == null)
                {
                    estimated = true;
                    return fallback;
                }
                var bound = el.worldBound;
                if (bound.width <= 0f || bound.height <= 0f)
                {
                    estimated = true;
                    return fallback;
                }
                return world.WorldToLocal(new Vector2(bound.xMax - 4f, bound.center.y));
            }

            Vector2 TargetOf(int index, BuilderCanvasNode to)
            {
                var card = world?.Q("card-" + index);
                var rect = card != null && card.layout.width > 0f
                    ? card.layout
                    : new Rect(to.X, to.Y, width, PillH);
                return CurrentLod == 0
                    ? new Vector2(rect.xMin, rect.yMin + rect.height * 0.5f)
                    : new Vector2(rect.xMin, rect.yMin + EdgeAnchorY);
            }

            // POC computeEdges: ONE edge per import row, PLUS one per markup row
            // that instantiates a graph node — ShopScreen draws 6 + 3 curves, not 6.
            foreach (var edge in graph.Edges)
            {
                if (edge.FromIndex < 0 || edge.FromIndex >= graph.Nodes.Count)
                    continue;
                var from = graph.Nodes[edge.FromIndex];
                Vector2 a;
                if (CurrentLod == 0)
                {
                    // POC lod0 branch: the source is the whole card and
                    // x1 = r1.right - r1.width / 2 — the card's CENTRE.
                    var card = world?.Q("card-" + edge.FromIndex);
                    var rect = card != null && card.layout.width > 0f
                        ? card.layout
                        : new Rect(from.X, from.Y, width, PillH);
                    a = new Vector2(rect.center.x, rect.center.y);
                }
                else
                {
                    int importRow = ImportRowIndex(from, edge.Specifier);
                    a = AnchorOf(
                        "a-imp-" + edge.FromIndex + "-" + importRow,
                        new Vector2(
                            from.X + width - 16f, from.Y + ImportRowY(from, edge.Specifier)));
                }

                if (edge.ToIndex >= 0 && edge.ToIndex < graph.Nodes.Count)
                {
                    var to = graph.Nodes[edge.ToIndex];
                    Color color = edge.TargetKind == BuilderNodeKind.Hook ? HookEdge
                        : edge.TargetKind == BuilderNodeKind.Style ? StyleEdge
                        : UsageEdge;
                    bool dashed = edge.TargetKind == BuilderNodeKind.Style
                        || edge.TargetKind == BuilderNodeKind.Hook;
                    StrokeEdge(p, a, TargetOf(edge.ToIndex, to), color, dashed);
                }
                else
                {
                    p.strokeColor = new Color(0.90f, 0.30f, 0.30f);
                    p.lineWidth = 2f / CurrentZoom;
                    p.BeginPath();
                    p.MoveTo(a);
                    p.LineTo(new Vector2(a.x + 48f, a.y));
                    p.Stroke();
                }
            }

            if (CurrentLod == 0)
            {
                MaybeRetry(layer, estimated);
                return;
            }
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                var from = graph.Nodes[i];
                for (int r = 0; r < from.Markup.Count; r++)
                {
                    string tag = from.Markup[r].Text.Trim('<', '>');
                    int target = IndexOfTitle(graph, tag);
                    if (target < 0 || target == i)
                        continue;
                    var to = graph.Nodes[target];
                    var a = AnchorOf(
                        "a-row-" + i + "-" + r,
                        new Vector2(from.X + width - 16f, from.Y + MarkupRowY(from, r)));
                    StrokeEdge(p, a, TargetOf(target, to), UsageEdge, false);
                }
            }
            MaybeRetry(layer, estimated);
        }

        /// <summary>An anchor dot that has not laid out yet paints from the
        /// estimate; ask for one more paint so the measured position lands.</summary>
        private static void MaybeRetry(VisualElement layer, bool estimated)
        {
            if (!estimated)
            {
                s_anchorRetries = 0;
                return;
            }
            if (layer == null || s_anchorRetries >= 12)
                return;
            s_anchorRetries++;
            layer.schedule.Execute(layer.MarkDirtyRepaint).ExecuteLater(16);
        }

        /// <summary>POC: a markup row carries an anchor dot only when its tag
        /// RESOLVES to a card in the graph (j.ref) — dots and edges are 1:1.</summary>
        public static bool ResolvesToNode(BuilderGraph graph, string tagText)
        {
            if (graph == null || string.IsNullOrEmpty(tagText))
                return false;
            return IndexOfTitle(graph, tagText.Trim('<', '>')) >= 0;
        }

        private static int IndexOfTitle(BuilderGraph graph, string title)
        {
            if (string.IsNullOrEmpty(title))
                return -1;
            for (int i = 0; i < graph.Nodes.Count; i++)
                if (graph.Nodes[i].Kind == BuilderNodeKind.Component
                    && string.Equals(graph.Nodes[i].Title, title, System.StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private static void StrokeEdge(Painter2D p, Vector2 a, Vector2 b, Color color, bool dashed)
        {
            color.a = 0.85f;
            float dx = Mathf.Max(40f, Mathf.Abs(b.x - a.x) * 0.45f);
            var c1 = new Vector2(a.x + dx, a.y);
            var c2 = new Vector2(b.x - dx, b.y);
            if (dashed)
                StrokeDashedBezier(p, a, c1, c2, b, color);
            else
            {
                p.strokeColor = color;
                p.lineWidth = 2f / CurrentZoom;
                p.BeginPath();
                p.MoveTo(a);
                p.BezierCurveTo(c1, c2, b);
                p.Stroke();
            }
            p.fillColor = color;
            p.BeginPath();
            p.Arc(b, 4f / CurrentZoom, 0f, 360f);
            p.Fill();
        }

        /// <summary>Which import row (by specifier) an edge leaves from, or -1.</summary>
        private static int ImportRowIndex(BuilderCanvasNode node, string specifier)
        {
            if (node.Imports.Count == 0 || string.IsNullOrEmpty(specifier))
                return -1;
            for (int i = 0; i < node.Imports.Count; i++)
                if (string.Equals(node.Imports[i].AttrsText, specifier, System.StringComparison.Ordinal))
                    return i;
            return -1;
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
                if (string.Equals(node.Imports[i].AttrsText, specifier, System.StringComparison.Ordinal))
                {
                    row = i;
                    break;
                }
            }
            if (row < 0)
                return EdgeAnchorY;
            float headerBlock = HeaderH
                + (string.IsNullOrEmpty(node.Signature) ? 0f : SignatureSectionH)
                + SectionOverheadH;
            return headerBlock + row * ImportRowH + ImportRowH * 0.5f;
        }

        /// <summary>POC usage edges leave from the markup row that instantiates
        /// the target component. The Y is estimated from the card's section
        /// stack (header, signature, imports, chips, island, markup rows) —
        /// approximate because chip wrapping varies, but row-true in shape.</summary>
        private static float MarkupRowY(BuilderCanvasNode from, int rowIdx)
        {
            float y = HeaderH;
            if (!string.IsNullOrEmpty(from.Signature))
                y += SignatureSectionH;
            if (from.Imports.Count > 0)
                y += SectionOverheadH + from.Imports.Count * ImportRowH;
            if (from.Kind == BuilderNodeKind.Component || from.Kind == BuilderNodeKind.Hook)
            {
                int chipRows = (from.Body.Count + 2) / 2;
                y += SectionOverheadH + chipRows * ChipRowH;
                if (CurrentLod == 2 && from.IslandLines.Count > 0)
                    y += 18f + from.IslandLines.Count * 16f;
            }
            y += SectionOverheadH;
            y += rowIdx * MarkupRowH + MarkupRowH * 0.5f;
            return y;
        }

        private static void StrokeDashedBezier(
            Painter2D p, Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, Color color)
        {
            // POC stroke-dasharray="6 4" is SCREEN pixels: the dash period must not
            // change with zoom, so the segment count tracks the curve's on-screen
            // length rather than a fixed 28.
            int segments = Mathf.Clamp(
                Mathf.RoundToInt(Vector2.Distance(a, b) * CurrentZoom / 5f) * 2, 8, 240);
            p.strokeColor = color;
            p.lineWidth = 2f / CurrentZoom;
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
