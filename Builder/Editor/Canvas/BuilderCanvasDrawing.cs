#if UNITY_EDITOR
using System.Collections.Generic;
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

        /// <summary>Import-row marker for a namespace import ("@UnityEngine.UIElements"):
        /// the row is on the card like every other import line, but it resolves to
        /// no graph node, so it carries no anchor dot and no edge.</summary>
        public const int NamespaceImportBadge = 8;

        /// <summary>Card geometry the edge painter estimates anchors from — kept
        /// in lockstep with canvasStyles (POC .card-title / .card-section
        /// paddings and the per-row font sizes).</summary>
        private const float HeaderH = 38f;

        /// <summary>POC "body.lod0 .pill": 1.5px frame + 16px padding + the 26px
        /// title in the body's 1.45 line box (37.7) + 16 + 1.5 = 72.7.</summary>
        private const float PillH = 72.7f;

        private const float SignatureSectionH = 33f;

        private const float SectionOverheadH = 34f;

        private const float ImportRowH = 19.7f;

        private const float MarkupRowH = 22.4f;

        private const float ChipRowH = 34.7f;

        /// <summary>Set by CanvasView each render; the edge painter shares it so
        /// anchors track the LOD-dependent card width (POC: 300 / 340 / 430).</summary>
        public static int CurrentLod = 1;

        /// <summary>UB-79: the wheel handler, the ctrl-wheel handler and the
        /// layout loader each carried their own copy of this range — three
        /// literals that had to agree. The floor was the POC's L0 preset (0.30);
        /// the owner asked for a wider view, so it drops to 0.10 (a ~9x larger
        /// visible area) with the card LOD already collapsing to pills below
        /// 0.45. Zoom is a VIEW property only: nothing about the graph, the
        /// layout or the buffers changes with it.</summary>
        public const float ZoomMin = 0.10f;

        public const float ZoomMax = 2.2f;

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

        /// <summary>POC 'font: 13px/1.45 "Segoe UI", system-ui, sans-serif' — the
        /// PROPORTIONAL face behind every non-code surface (toolbar, library,
        /// legend, section labels, footer hint). Unity left it at the editor
        /// default, whose advances run ~8px wider over a 15-character button
        /// label, so the chrome silhouette drifted from the POC's even though
        /// size, weight and colour matched. Resolved from the OS by NAME (never a
        /// font path — see the machine-local-paths rule); a machine without any
        /// of them keeps the editor font.</summary>
        public static Font UiFont()
        {
            if (s_uiResolved)
                return s_ui;
            s_uiResolved = true;
            try
            {
                s_ui = Font.CreateDynamicFontFromOSFont(
                    new[] { "Segoe UI", "Helvetica Neue", "Helvetica", "DejaVu Sans", "Arial" }, 12);
            }
            catch (System.Exception)
            {
                s_ui = null;
            }
            return s_ui;
        }

        private static Font s_ui;
        private static bool s_uiResolved;
        private static bool s_uiDefResolved;
        private static FontDefinition s_uiDef;

        /// <summary>The proportional face as a typed-Style value. Stays default
        /// when the OS has none of the candidates, which leaves the inheriting
        /// element on Unity's own editor font instead of blanking its text.</summary>
        public static FontDefinition UiFontDefinition
        {
            get
            {
                if (s_uiDefResolved)
                    return s_uiDef;
                s_uiDefResolved = true;
                var font = UiFont();
                s_uiDef = font != null ? FontDefinition.FromFont(font) : default;
                return s_uiDef;
            }
        }

        /// <summary>POC ".card" widths (300 / 340 / 430) are OUTER boxes: index.html
        /// carries a global "* { box-sizing: border-box }" reset, so the 1.5px frame
        /// is inside the declared width. UI Toolkit Width is border-box too (Yoga
        /// counts border + padding), so the authored numbers transfer unchanged.</summary>
        public static float CardWidthFor(int lod) =>
            lod == 0 ? 300f : lod == 1 ? 340f : 430f;

        public static Color KindColor(BuilderNodeKind kind)
        {
            switch (kind)
            {
                case BuilderNodeKind.Component:
                    return BuilderPalette.Accent;
                case BuilderNodeKind.Hook:
                    return BuilderPalette.HookTint;
                case BuilderNodeKind.Style:
                    return BuilderPalette.StyleTint;
                case BuilderNodeKind.Util:
                    return BuilderPalette.UtilTint;
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

        /// <summary>POC props-row: the export name bold, the parameter list after
        /// it. UB-78 — the parameter list used to be emitted RAW, so every type
        /// and parameter name in it inherited the section's flat grey while the
        /// islands beside it were fully coloured. It now runs through the same
        /// CodeField passes the islands use (name gold from its own '(' , types
        /// teal, parameters blue, null/false purple, numbers green) with the
        /// name kept bold via the boldPrefix run break.</summary>
        public static string SignatureRichText(string signature)
        {
            if (string.IsNullOrEmpty(signature))
                return "";
            int paren = signature.IndexOf('(');
            return CodeField.BuildLineRichText(signature, null, paren < 0 ? signature.Length : paren);
        }

        /// <summary>Chip line "useState  →  gold, setGold" → POC coloring:
        /// hook name green, state names warn-gold. The POC joins the .st spans with
        /// a bare ", " that sits OUTSIDE any span, so the separator inherits the
        /// chip's own --text #d6d6dc — it is not a state name.</summary>
        public static string ChipRichText(string bodyLineText)
        {
            int arrow = bodyLineText.IndexOf("  →  ", System.StringComparison.Ordinal);
            if (arrow < 0)
                return "<color=#81C784>" + bodyLineText + "</color>";
            var sb = new System.Text.StringBuilder();
            sb.Append("<color=#81C784>").Append(bodyLineText, 0, arrow).Append("</color> → ");
            string names = bodyLineText.Substring(arrow + 5);
            int at = 0;
            while (at < names.Length)
            {
                int sep = names.IndexOf(", ", at, System.StringComparison.Ordinal);
                int end = sep < 0 ? names.Length : sep;
                sb.Append("<color=#FFB74D>").Append(names, at, end - at).Append("</color>");
                if (sep < 0)
                    break;
                sb.Append(", ");
                at = sep + 2;
            }
            return sb.ToString();
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

        /// <summary>POC "body.lod0 .pill { cursor: move }" / ".card-title
        /// { cursor: move }" — the card's drag handle says so on hover, distinct
        /// from "#canvasWrap { cursor: grab }" that every card would otherwise
        /// inherit from the canvas pane.</summary>
        public static void SetMoveCursor(Ruitk.Core.ReactivePanelEvent evt)
        {
            if (evt?.Target == null)
                return;
            BuilderCursor.Set(evt.Target, UnityEditor.MouseCursor.MoveArrow);
        }

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

        /// <summary>Index of the first element row in a card's markup — the
        /// RETURN root. With §8.1 head rows a directive wrapping the root
        /// occupies index 0, so every "root row" guard keys on this instead of
        /// a literal 0. -1 when the card has no element rows.</summary>
        public static int FirstElementRow(BuilderCanvasNode node)
        {
            if (node?.Markup == null)
                return -1;
            for (int i = 0; i < node.Markup.Count; i++)
                if (node.Markup[i].Kind != BuilderCardLineKind.Directive)
                    return i;
            return -1;
        }

        /// <summary>Anchor-dot color per import kind marker (5 usage / 6 hook /
        /// 7 style).</summary>
        public static Color DotColor(int dotKind) => dotKind switch
        {
            6 => BuilderPalette.HookEdge,
            7 => BuilderPalette.StyleEdge,
            _ => BuilderPalette.UsageEdge,
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
            14 => new Color(0.31f, 0.76f, 0.97f),
            15 => new Color(0.55f, 0.73f, 0.97f),
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
                    return BuilderPalette.Dim;
                case BuilderCardLineKind.Hook:
                    return BuilderPalette.HookTint;
                case BuilderCardLineKind.Element:
                    return BuilderPalette.Accent;
                case BuilderCardLineKind.Component:
                    return BuilderPalette.ComponentTint;
                case BuilderCardLineKind.Directive:
                    return BuilderPalette.StyleTint;
                case BuilderCardLineKind.Export:
                    return BuilderPalette.StyleTint;
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
            // The CSS gradient is "circle at 1px 1px, #2c2c33 1px, transparent 1px"
            // on a 26px tile — a HARD stop, so the covered pixels are 0 and 1 of the
            // tile and nothing bleeds onto pixel 2. Painter2D antialiases its fills
            // and put a 1/255 halo around every dot (a 4x4 footprint against the
            // POC's 2x2), so the lattice is written as raw quads instead: a mesh
            // vertex run has no coverage falloff at all.
            int cols = Mathf.FloorToInt((rect.width - 1f) / 26f) + 1;
            int rows = Mathf.FloorToInt((rect.height - 1f) / 26f) + 1;
            if (cols <= 0 || rows <= 0)
                return;
            // ushort indices cap one mesh at 65535 vertices; 16000 dots is well past
            // any window the builder opens in and keeps the allocation legal.
            int dots = Mathf.Min(cols * rows, 16000);
            var mesh = ctx.Allocate(dots * 4, dots * 6);
            var color = new Color(0.173f, 0.173f, 0.200f);
            int drawn = 0;
            for (int row = 0; row < rows && drawn < dots; row++)
            {
                float y0 = row * 26f;
                for (int col = 0; col < cols && drawn < dots; col++)
                {
                    float x0 = col * 26f;
                    ushort v = (ushort)(drawn * 4);
                    mesh.SetNextVertex(new Vertex { position = new Vector3(x0, y0, Vertex.nearZ), tint = color });
                    mesh.SetNextVertex(new Vertex { position = new Vector3(x0 + 2f, y0, Vertex.nearZ), tint = color });
                    mesh.SetNextVertex(new Vertex { position = new Vector3(x0 + 2f, y0 + 2f, Vertex.nearZ), tint = color });
                    mesh.SetNextVertex(new Vertex { position = new Vector3(x0, y0 + 2f, Vertex.nearZ), tint = color });
                    mesh.SetNextIndex(v);
                    mesh.SetNextIndex((ushort)(v + 1));
                    mesh.SetNextIndex((ushort)(v + 2));
                    mesh.SetNextIndex(v);
                    mesh.SetNextIndex((ushort)(v + 2));
                    mesh.SetNextIndex((ushort)(v + 3));
                    drawn++;
                }
            }
        }

        /// <summary>POC ".card { border: 1px solid var(--line) }" / ".card.sel
        /// { border-color: var(--warn) }". The browser renders that 1px at the live
        /// zoom, so at the L0 zoom (~0.29) the outline all but disappears into the
        /// #232329 fill (probed #24242a on poc-l0-architecture.png). UI Toolkit
        /// alpha-modulates a sub-pixel border far less aggressively and painted a
        /// hard #303038 hairline, which made the L0 pills read as outlined chips
        /// instead of flat plates — the alpha is folded down at L0 to match.
        /// The SELECTED gold is dimmed but NOT erased: probed on
        /// poc-l0-architecture.png the selected edge lays gold on TWO device rows
        /// per side (#494025 over #4C442C on top, peak #695A2F at the bottom),
        /// ~0.39 coverage per side — the 1.5px #ffd54f frame plus the .25 spread
        /// ring, both scaled by the 0.30 zoom. The ring is emitted at lod 0 too
        /// (it carries the outer row); this frame carries the inner one.
        /// The selected gold takes NO alpha fold-down at any lod: the sub-pixel
        /// frame width at the 0.30 zoom already performs the browser's own
        /// coverage dimming. Probed gold coverage over the two edge rows —
        /// POC 0.225 + 0.186 = 0.411; Unity with the 0.4 fold-down measured
        /// 0.136 + 0.118 = 0.254, of which the 2px .25 spread ring contributes a
        /// fixed 0.151, so scaling the frame share by 1/0.4 lands on 0.409.</summary>
        public static Color CardFrameBorderColor(bool selected, int lod)
        {
            if (selected)
                return new Color(1f, 0.835f, 0.310f, 1f);
            var line = BuilderPalette.Line;
            if (lod == 0)
                line.a = 0.12f;
            return line;
        }

        /// <summary>POC ".card { box-shadow: 0 6px 18px rgba(0,0,0,.35) }". Four
        /// stacked opaque rects gave four flat plateaus with visible steps; a real
        /// Gaussian falloff is painted here instead, as N concentric rounded rects
        /// whose per-ring alphas composite to 0.35*PHI(d/sigma) at every depth d
        /// inside the shadow rect (the card box offset down by 6, radius 10).
        /// The host element is inset -18/-18/-24/+(-12) around the card, so its own
        /// content rect is exactly the shadow rect grown by the 18px blur reach.</summary>
        public static void DrawCardShadow(MeshGenerationContext ctx)
        {
            if (ctx?.visualElement == null)
                return;
            var rect = ctx.visualElement.contentRect;
            if (rect.width <= ShadowBlur * 2f || rect.height <= ShadowBlur * 2f)
                return;

            var shadow = new Rect(
                ShadowBlur, ShadowBlur,
                rect.width - ShadowBlur * 2f, rect.height - ShadowBlur * 2f);
            var p = ctx.painter2D;
            const int steps = 16;
            const float innerReach = 6f;
            float span = ShadowBlur + innerReach;
            float previous = 0f;
            for (int i = 0; i < steps; i++)
            {
                float d = -ShadowBlur + span * (i + 1) / steps;
                float target = ShadowAlpha * NormalCdf(d / ShadowSigma);
                float ring = 1f - (1f - target) / (1f - previous);
                previous = target;
                if (ring <= 0f)
                    continue;
                float inset = d;
                var box = new Rect(
                    shadow.x + inset, shadow.y + inset,
                    shadow.width - inset * 2f, shadow.height - inset * 2f);
                if (box.width <= 0f || box.height <= 0f)
                    continue;
                p.fillColor = new Color(0f, 0f, 0f, ring);
                p.BeginPath();
                TraceRoundedRect(p, box, Mathf.Max(0f, 10f - inset));
                p.ClosePath();
                p.Fill();
            }
        }

        private const float ShadowBlur = 18f;

        private const float ShadowSigma = 9f;

        private const float ShadowAlpha = 0.35f;

        /// <summary>Logistic approximation of the standard normal CDF (max error
        /// ~0.01) — the shape of a Gaussian blur's edge falloff.</summary>
        private static float NormalCdf(float x) => 1f / (1f + Mathf.Exp(-1.702f * x));

        private static void TraceRoundedRect(Painter2D p, Rect box, float radius)
        {
            radius = Mathf.Min(radius, Mathf.Min(box.width, box.height) * 0.5f);
            if (radius <= 0.01f)
            {
                p.MoveTo(new Vector2(box.xMin, box.yMin));
                p.LineTo(new Vector2(box.xMax, box.yMin));
                p.LineTo(new Vector2(box.xMax, box.yMax));
                p.LineTo(new Vector2(box.xMin, box.yMax));
                return;
            }
            void Corner(Vector2 center, float from, float to)
            {
                for (int i = 0; i <= 5; i++)
                {
                    float angle = Mathf.Deg2Rad * Mathf.Lerp(from, to, i / 5f);
                    p.LineTo(new Vector2(
                        center.x + Mathf.Cos(angle) * radius,
                        center.y + Mathf.Sin(angle) * radius));
                }
            }
            p.MoveTo(new Vector2(box.xMin + radius, box.yMin));
            p.LineTo(new Vector2(box.xMax - radius, box.yMin));
            Corner(new Vector2(box.xMax - radius, box.yMin + radius), -90f, 0f);
            p.LineTo(new Vector2(box.xMax, box.yMax - radius));
            Corner(new Vector2(box.xMax - radius, box.yMax - radius), 0f, 90f);
            p.LineTo(new Vector2(box.xMin + radius, box.yMax));
            Corner(new Vector2(box.xMin + radius, box.yMax - radius), 90f, 180f);
            p.LineTo(new Vector2(box.xMin, box.yMin + radius));
            Corner(new Vector2(box.xMin + radius, box.yMin + radius), 180f, 270f);
        }

        /// <summary>POC ".knobs { border-top: 1px dashed var(--line) }" — UI Toolkit
        /// has no dashed border, so the rule is painted as a 3-on / 3-off run on a
        /// 1px-high strip.</summary>
        public static void DrawDashedRule(MeshGenerationContext ctx)
        {
            if (ctx?.visualElement == null)
                return;
            var rect = ctx.visualElement.contentRect;
            if (rect.width <= 0f)
                return;
            var p = ctx.painter2D;
            p.fillColor = BuilderPalette.Line;
            p.BeginPath();
            for (float x = 0f; x < rect.width; x += 6f)
            {
                float w = Mathf.Min(3f, rect.width - x);
                p.MoveTo(new Vector2(x, 0f));
                p.LineTo(new Vector2(x + w, 0f));
                p.LineTo(new Vector2(x + w, 1f));
                p.LineTo(new Vector2(x, 1f));
                p.ClosePath();
            }
            p.Fill();
        }

        /// <summary>POC ".jsx-attrs .expr:hover { text-decoration: underline dotted }"
        /// — hovering an attribute value rules a dotted line under it and leaves the
        /// row ground untouched. UI Toolkit has no text-decoration, so the rule is
        /// painted as 1-on / 1-off dots the width of the value, in the value's own
        /// colour, just below its baseline.</summary>
        public static void DrawDottedUnderline(MeshGenerationContext ctx, Color color, bool on)
        {
            if (!on || ctx?.visualElement == null)
                return;
            var rect = ctx.visualElement.contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
                return;
            float y = rect.height - 2.5f;
            var p = ctx.painter2D;
            p.fillColor = color;
            p.BeginPath();
            for (float x = 0f; x < rect.width; x += 2f)
            {
                float w = Mathf.Min(1f, rect.width - x);
                p.MoveTo(new Vector2(x, y));
                p.LineTo(new Vector2(x + w, y));
                p.LineTo(new Vector2(x + w, y + 1f));
                p.LineTo(new Vector2(x, y + 1f));
                p.ClosePath();
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
            // marker elements are measured here; the section-stack estimate is only
            // the pre-layout fallback. §8.2: a marker scrolled out of its capped
            // section clamps to the section viewport's edge — the curve terminates
            // visibly instead of diving under the card chrome.
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
                Vector2 point = bound.center;
                var scroll = el.GetFirstAncestorOfType<ScrollView>();
                if (scroll != null && scroll.name == "ruitk-section-scroll"
                    && scroll.contentViewport != null)
                {
                    var viewport = scroll.contentViewport.worldBound;
                    if (viewport.height > 0f)
                        point.y = Mathf.Clamp(point.y, viewport.yMin, viewport.yMax);
                }
                return world.WorldToLocal(point);
            }

            // Cards are placed with Translate, not Left/Top, so layout.x/y are
            // always 0 — only the MODEL coordinates carry position. The measured
            // element still supplies the true size.
            Rect CardRect(int index, BuilderCanvasNode node)
            {
                var card = world?.Q("card-" + index);
                float w = card != null && card.layout.width > 0f ? card.layout.width : width;
                float h = card != null && card.layout.height > 0f ? card.layout.height : PillH;
                if (card == null || card.layout.width <= 0f)
                    estimated = true;
                return new Rect(node.X, node.Y, w, h);
            }

            Vector2 TargetOf(int index, BuilderCanvasNode to)
            {
                var rect = CardRect(index, to);
                // POC: "y2 = r2.top - wr.top + 18" is measured off getBoundingClientRect,
                // so the 18 is 18 SCREEN px at every zoom. Our anchors are world-space,
                // so the offset is divided by the live zoom. The lod0 branch is a
                // fraction of the card height and is already zoom-invariant.
                return CurrentLod == 0
                    ? new Vector2(rect.xMin, rect.yMin + rect.height * 0.5f)
                    : new Vector2(rect.xMin, rect.yMin + EdgeAnchorY / CurrentZoom);
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
                    // §8.3 (deliberate POC divergence): the L0 source is the
                    // card's RIGHT edge — the POC's card-centre start drew the
                    // curve out through the card body.
                    var pillRect = CardRect(edge.FromIndex, from);
                    a = new Vector2(pillRect.xMax, pillRect.center.y);
                }
                else
                {
                    int importRow = ImportRowIndex(from, edge.Specifier);
                    a = AnchorOf(
                        "a-imp-" + edge.FromIndex + "-" + importRow,
                        new Vector2(
                            from.X + width - 16f, from.Y + ImportRowY(from, edge.Specifier)));
                }

                // POC drawEdges() line 1458: "if (!a || !target) return;" — an
                // import whose target is not a card on the canvas draws NOTHING at
                // any lod. The red stub this used to paint had no counterpart in
                // the POC and, at lod 0, started from the card centre.
                if (edge.ToIndex < 0 || edge.ToIndex >= graph.Nodes.Count)
                    continue;
                var to = graph.Nodes[edge.ToIndex];
                Color color = edge.TargetKind == BuilderNodeKind.Hook ? BuilderPalette.HookEdge
                    : edge.TargetKind == BuilderNodeKind.Style ? BuilderPalette.StyleEdge
                    : BuilderPalette.UsageEdge;
                bool dashed = edge.TargetKind == BuilderNodeKind.Style
                    || edge.TargetKind == BuilderNodeKind.Hook;
                StrokeEdge(p, a, TargetOf(edge.ToIndex, to), color, dashed);
            }

            // POC computeEdges() emits the markup-row edges at EVERY lod — only
            // drawEdges() branches, swapping the SOURCE end to the card centre at
            // lod0. Returning early here left L0 with import edges alone: a usage
            // with no matching import drew nothing, and where the two coincide the
            // POC's two stacked 0.85 strokes read brighter than our single one.
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
                    Vector2 a;
                    if (CurrentLod == 0)
                    {
                        var pillRect = CardRect(i, from);
                        a = new Vector2(pillRect.xMax, pillRect.center.y);
                    }
                    else
                    {
                        a = AnchorOf(
                            "a-row-" + i + "-" + r,
                            new Vector2(from.X + width - 16f, from.Y + MarkupRowY(from, r)));
                    }
                    StrokeEdge(p, a, TargetOf(target, to), BuilderPalette.UsageEdge, false);
                }
            }

            // §8.3: anchor-dot glyphs paint HERE, in the overlay, so no card can
            // occlude them (UB-22). The row elements are invisible measurement
            // markers; a clamped anchor paints its dot at the section edge.
            if (CurrentLod > 0)
            {
                for (int i = 0; i < graph.Nodes.Count; i++)
                {
                    var node = graph.Nodes[i];
                    for (int r = 0; r < node.Imports.Count; r++)
                    {
                        var imp = node.Imports[r];
                        if (imp.BadgeKind == NamespaceImportBadge)
                            continue;
                        PaintAnchorDot(p, AnchorOf(
                            "a-imp-" + i + "-" + r,
                            new Vector2(node.X + width - 16f, node.Y + ImportRowY(node, imp.AttrsText))),
                            imp.BadgeKind);
                    }
                    for (int r = 0; r < node.Markup.Count; r++)
                    {
                        if (!ResolvesToNode(graph, node.Markup[r].Text))
                            continue;
                        PaintAnchorDot(p, AnchorOf(
                            "a-row-" + i + "-" + r,
                            new Vector2(node.X + width - 16f, node.Y + MarkupRowY(node, r))), 5);
                    }
                }
            }
            // UB-30/31: the drop band hint paints from the drag machine's live
            // hit-tested target — no render state, no stale closure, and it
            // works identically for library and canvas drags.
            if (BuilderDragService.Dragging && BuilderDragService.Target.Valid)
                PaintDropHint(p, world);
            MaybeRetry(layer, estimated);
        }

        private static void PaintDropHint(Painter2D p, VisualElement world)
        {
            var target = BuilderDragService.Target;
            var el = target.RowElementName != null
                ? world?.Q(target.RowElementName)
                : world?.Q("card-" + target.CardIndex);
            if (el == null || world == null)
                return;
            var bound = el.worldBound;
            if (bound.width <= 0f || bound.height <= 0f)
                return;
            // A target row scrolled partly out of its capped section paints
            // only the visible slice — the hint must never bleed over the rows
            // that occlude it (same clamp AnchorOf applies to the dots).
            var scroll = el.GetFirstAncestorOfType<ScrollView>();
            if (scroll != null && scroll.name == "ruitk-section-scroll"
                && scroll.contentViewport != null)
            {
                var viewport = scroll.contentViewport.worldBound;
                bound.yMin = Mathf.Max(bound.yMin, viewport.yMin);
                bound.yMax = Mathf.Min(bound.yMax, viewport.yMax);
                if (bound.height <= 0f)
                    return;
            }
            Vector2 min = world.WorldToLocal(new Vector2(bound.xMin, bound.yMin));
            Vector2 max = world.WorldToLocal(new Vector2(bound.xMax, bound.yMax));
            var accent = BuilderPalette.Accent;
            p.lineWidth = 2f;
            if (target.RowIdx < 0 || target.Band == 1)
            {
                var fill = accent;
                fill.a = 30f / 255f;
                p.fillColor = fill;
                p.BeginPath();
                p.MoveTo(min);
                p.LineTo(new Vector2(max.x, min.y));
                p.LineTo(max);
                p.LineTo(new Vector2(min.x, max.y));
                p.ClosePath();
                p.Fill();
                p.strokeColor = accent;
                p.BeginPath();
                p.MoveTo(min);
                p.LineTo(new Vector2(max.x, min.y));
                p.LineTo(max);
                p.LineTo(new Vector2(min.x, max.y));
                p.ClosePath();
                p.Stroke();
            }
            else
            {
                float y = target.Band == 0 ? min.y : max.y;
                p.strokeColor = accent;
                p.BeginPath();
                p.MoveTo(new Vector2(min.x, y));
                p.LineTo(new Vector2(max.x, y));
                p.Stroke();
            }
        }

        /// <summary>The 8px anchor dot with its 25% halo ring, painted in the
        /// overlay in world units so it matches the element dots it replaced.</summary>
        private static void PaintAnchorDot(Painter2D p, Vector2 center, int dotKind)
        {
            var halo = DotHalo(dotKind);
            p.fillColor = halo;
            p.BeginPath();
            p.Arc(center, 6f, 0f, 360f);
            p.Fill();
            p.fillColor = DotColor(dotKind);
            p.BeginPath();
            p.Arc(center, 4f, 0f, 360f);
            p.Fill();
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
            // POC drawEdges: "Math.max(40, Math.abs(x2 - x1) * 0.45)" over SCREEN
            // coordinates (getBoundingClientRect), so the floor is 40 SCREEN px —
            // ~133 world px at the L0 zoom, which is what gives the back-edges their
            // wide S-bow. The proportional term is scale-invariant; only the
            // constant needs the world conversion, like lineWidth and the dot radius.
            float dx = Mathf.Max(40f / CurrentZoom, Mathf.Abs(b.x - a.x) * 0.45f);
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
            // POC drawEdges(): the PATH carries opacity="0.85", the terminal
            // <circle r="4"> carries no opacity attribute at all — probed on
            // poc-l0-architecture.png the Header dot is exactly #5C8BB0, full
            // saturation. Reusing the stroke colour washed every endpoint out.
            color.a = 1f;
            p.fillColor = color;
            p.BeginPath();
            p.Arc(b, 4f / CurrentZoom, 0f, 360f);
            p.Fill();
        }

        /// <summary>POC ".chip-add { border-style: dashed }" — UI Toolkit has no
        /// dashed border, so the add-chip's rounded outline is painted as a
        /// 3-on / 3-off walk around the element's own box. Local units are CSS px
        /// (the canvas layer carries the zoom transform), so the dash period is
        /// fixed exactly like the POC's scaled 1px border.</summary>
        public static void DrawDashedChipBorder(MeshGenerationContext ctx, Color color)
        {
            if (ctx?.visualElement == null)
                return;
            var ve = ctx.visualElement;
            float w = ve.layout.width;
            float h = ve.layout.height;
            if (float.IsNaN(w) || float.IsNaN(h) || w <= 4f || h <= 4f)
                return;

            const float lineWidth = 1.5f;
            float inset = lineWidth * 0.5f;
            float x0 = inset;
            float y0 = inset;
            float x1 = w - inset;
            float y1 = h - inset;
            float radius = Mathf.Min(10f, Mathf.Min(x1 - x0, y1 - y0) * 0.5f);

            var path = new List<Vector2>(64);
            void Corner(Vector2 center, float from, float to)
            {
                for (int i = 0; i <= 6; i++)
                {
                    float angle = Mathf.Deg2Rad * Mathf.Lerp(from, to, i / 6f);
                    path.Add(new Vector2(
                        center.x + Mathf.Cos(angle) * radius,
                        center.y + Mathf.Sin(angle) * radius));
                }
            }

            path.Add(new Vector2(x0 + radius, y0));
            path.Add(new Vector2(x1 - radius, y0));
            Corner(new Vector2(x1 - radius, y0 + radius), -90f, 0f);
            path.Add(new Vector2(x1, y1 - radius));
            Corner(new Vector2(x1 - radius, y1 - radius), 0f, 90f);
            path.Add(new Vector2(x0 + radius, y1));
            Corner(new Vector2(x0 + radius, y1 - radius), 90f, 180f);
            path.Add(new Vector2(x0, y0 + radius));
            Corner(new Vector2(x0 + radius, y0 + radius), 180f, 270f);
            path.Add(new Vector2(x0 + radius, y0));

            var p = ctx.painter2D;
            p.strokeColor = color;
            p.lineWidth = lineWidth;
            const float on = 3f;
            const float off = 3f;
            bool pen = true;
            float left = on;
            var cursor = path[0];
            p.BeginPath();
            p.MoveTo(cursor);
            for (int i = 1; i < path.Count; i++)
            {
                var next = path[i];
                float remaining = Vector2.Distance(cursor, next);
                while (remaining > 0f)
                {
                    if (remaining < left)
                    {
                        left -= remaining;
                        if (pen)
                            p.LineTo(next);
                        cursor = next;
                        break;
                    }
                    var split = Vector2.Lerp(cursor, next, left / remaining);
                    if (pen)
                    {
                        p.LineTo(split);
                        p.Stroke();
                    }
                    remaining -= left;
                    cursor = split;
                    pen = !pen;
                    left = pen ? on : off;
                    if (pen)
                    {
                        p.BeginPath();
                        p.MoveTo(cursor);
                    }
                }
            }
            if (pen)
                p.Stroke();
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

        private static readonly Vector2[] s_dashSamples = new Vector2[401];

        private static void StrokeDashedBezier(
            Painter2D p, Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, Color color)
        {
            // SVG stroke-dasharray="6 4" is measured along ARC LENGTH, so the 6-on
            // / 4-off period is uniform however tight the curve gets. Splitting in
            // parameter t instead makes dash length track curve speed — long dashes
            // on the straight run, a dotted crumble through the bend. The curve is
            // flattened into a polyline and the pattern walked by distance; the
            // period is divided by the zoom so it stays 6px/4px on screen.
            Vector2 Point(float t)
            {
                float u = 1f - t;
                return u * u * u * a
                    + 3f * u * u * t * c1
                    + 3f * u * t * t * c2
                    + t * t * t * b;
            }

            int samples = Mathf.Clamp(
                Mathf.RoundToInt(Vector2.Distance(a, b) * CurrentZoom / 4f), 32, 400);
            for (int i = 0; i <= samples; i++)
                s_dashSamples[i] = Point((float)i / samples);

            p.strokeColor = color;
            p.lineWidth = 2f / CurrentZoom;
            float on = 6f / CurrentZoom;
            float off = 4f / CurrentZoom;
            bool pen = true;
            float left = on;
            var cursor = s_dashSamples[0];
            p.BeginPath();
            p.MoveTo(cursor);
            for (int i = 1; i <= samples; i++)
            {
                var next = s_dashSamples[i];
                float remaining = Vector2.Distance(cursor, next);
                while (remaining > 0f)
                {
                    if (remaining < left)
                    {
                        left -= remaining;
                        if (pen)
                            p.LineTo(next);
                        cursor = next;
                        break;
                    }
                    var split = Vector2.Lerp(cursor, next, left / remaining);
                    if (pen)
                    {
                        p.LineTo(split);
                        p.Stroke();
                    }
                    remaining -= left;
                    cursor = split;
                    pen = !pen;
                    left = pen ? on : off;
                    if (pen)
                    {
                        p.BeginPath();
                        p.MoveTo(cursor);
                    }
                }
            }
            if (pen)
                p.Stroke();
        }
    }
}
#endif
