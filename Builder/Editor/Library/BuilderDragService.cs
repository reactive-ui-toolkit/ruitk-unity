#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>Where a drag currently points, resolved by hit-test from the
    /// captured pointer stream — never from render-state closures (UB-31).</summary>
    internal struct BuilderDropTarget
    {
        public bool Valid;
        public string Path;
        public int RowIdx;
        public int Band;
        public string RowElementName;
        public int CardIndex;
    }

    /// <summary>
    /// Cross-pane drag state machine (UB-30/31/32). A source (library pill or
    /// canvas markup row) arms a payload on pointer-down and CAPTURES the
    /// pointer; past a small travel threshold the drag becomes visible — a
    /// window-level ghost chip follows the cursor and the canvas paints band
    /// hints from the live hit-tested target. The drop resolves on pointer-up
    /// from a FRESH hit-test, so the first drop is as accurate as the last, and
    /// a release over nothing cancels loudly instead of silently.
    /// Payloads: "element:Tag", "component:Tag", "hook:name", "stylemod:name",
    /// "utilmod:name", "move:&lt;path&gt;:&lt;rowIdx&gt;".
    /// </summary>
    internal static class BuilderDragService
    {
        private const float DragThresholdPx = 5f;

        public static string Payload;

        /// <summary>Window root that hosts the ghost chip (set by BuilderWindow).</summary>
        public static VisualElement GhostRoot;

        /// <summary>Panel point → drop target (installed by BuilderCanvasHost).</summary>
        public static System.Func<Vector2, BuilderDropTarget> HitTester;

        /// <summary>(path, rowIdx, band, payload) — the drop sink.</summary>
        public static System.Action<string, int, int, string> DropHandler;

        /// <summary>Repaints the hint overlay (edge layer).</summary>
        public static System.Action RepaintHints;

        /// <summary>Loud-cancel surface (toast).</summary>
        public static System.Action<string> OnBlockedDrop;

        public static BuilderDropTarget Target;

        private static VisualElement s_source;
        private static int s_pointerId = -1;
        private static Vector2 s_origin;
        private static bool s_dragging;
        private static string s_label;
        private static Label s_ghost;

        public static bool Active => !string.IsNullOrEmpty(Payload);

        /// <summary>True once the pointer travelled past the click threshold —
        /// hints and the ghost render only in this state.</summary>
        public static bool Dragging => s_dragging;

        public static void Begin(string payload, string label, VisualElement source, int pointerId, Vector2 panelPos)
        {
            CancelInternal();
            Payload = payload;
            s_label = string.IsNullOrEmpty(label) ? payload : label;
            s_source = source;
            s_pointerId = pointerId;
            s_origin = panelPos;
            s_dragging = false;
            Target = default;
            if (source != null && source.panel != null)
                source.CapturePointer(pointerId);
        }

        public static void Begin(string payload, string label, Ruitk.Core.ReactivePointerEvent evt)
        {
            if (evt == null)
                return;
            Begin(payload, label, evt.CurrentTarget, evt.PointerId, evt.Position);
        }

        public static void PointerMove(Vector2 panelPos)
        {
            if (!Active)
                return;
            if (!s_dragging)
            {
                if ((panelPos - s_origin).magnitude < DragThresholdPx)
                    return;
                s_dragging = true;
                ShowGhost();
            }
            MoveGhost(panelPos);
            Target = HitTester?.Invoke(panelPos) ?? default;
            RepaintHints?.Invoke();
        }

        public static void PointerMove(Ruitk.Core.ReactivePointerEvent evt)
        {
            if (evt != null)
                PointerMove(evt.Position);
        }

        public static void PointerUp(Vector2 panelPos)
        {
            if (!Active)
            {
                CancelInternal();
                return;
            }
            string payload = Payload;
            bool wasDrag = s_dragging;
            var target = wasDrag ? (HitTester?.Invoke(panelPos) ?? default) : default;
            CancelInternal();
            if (!wasDrag)
                return;
            if (target.Valid)
                DropHandler?.Invoke(target.Path, target.RowIdx, target.Band, payload);
            else
                OnBlockedDrop?.Invoke("No drop target — release over a card or row.");
        }

        public static void PointerUp(Ruitk.Core.ReactivePointerEvent evt)
        {
            if (evt != null)
                PointerUp(evt.Position);
        }

        public static string Take()
        {
            string payload = Payload;
            CancelInternal();
            return payload;
        }

        public static void Cancel() => CancelInternal();

        private static void CancelInternal()
        {
            if (s_source != null && s_pointerId >= 0 && s_source.panel != null
                && s_source.HasPointerCapture(s_pointerId))
                s_source.ReleasePointer(s_pointerId);
            s_source = null;
            s_pointerId = -1;
            Payload = null;
            s_label = null;
            bool hadVisuals = s_dragging || Target.Valid;
            s_dragging = false;
            Target = default;
            HideGhost();
            if (hadVisuals)
                RepaintHints?.Invoke();
        }

        private static void ShowGhost()
        {
            if (GhostRoot == null)
                return;
            if (s_ghost == null)
            {
                s_ghost = new Label
                {
                    pickingMode = PickingMode.Ignore,
                    style =
                    {
                        position = Position.Absolute,
                        backgroundColor = BuilderPalette.Panel2,
                        color = BuilderPalette.Text,
                        borderTopWidth = 1f, borderBottomWidth = 1f,
                        borderLeftWidth = 1f, borderRightWidth = 1f,
                        borderTopColor = BuilderPalette.Accent,
                        borderBottomColor = BuilderPalette.Accent,
                        borderLeftColor = BuilderPalette.Accent,
                        borderRightColor = BuilderPalette.Accent,
                        borderTopLeftRadius = 5f, borderTopRightRadius = 5f,
                        borderBottomLeftRadius = 5f, borderBottomRightRadius = 5f,
                        paddingLeft = 8f, paddingRight = 8f,
                        paddingTop = 3f, paddingBottom = 3f,
                        fontSize = 11f,
                        unityTextAlign = TextAnchor.MiddleLeft,
                    },
                };
            }
            s_ghost.text = s_label ?? "";
            if (s_ghost.parent != GhostRoot)
                GhostRoot.Add(s_ghost);
            s_ghost.BringToFront();
        }

        private static void MoveGhost(Vector2 panelPos)
        {
            if (s_ghost == null || GhostRoot == null)
                return;
            var local = GhostRoot.WorldToLocal(panelPos);
            s_ghost.style.left = local.x + 14f;
            s_ghost.style.top = local.y + 10f;
        }

        private static void HideGhost()
        {
            s_ghost?.RemoveFromHierarchy();
        }
    }
}
#endif
