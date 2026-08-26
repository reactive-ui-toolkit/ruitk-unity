#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// The plain context menu - card, row, canvas, import - drawn as a LAYER IN
    /// THE BUILDER'S OWN PANEL rather than as a window.
    ///
    /// Three shapes were tried before this one and each failed on the same
    /// structural point. A custom EditorWindow (the original) cannot have a
    /// submenu, because the submenu is a second EditorWindow and each closes on
    /// lost focus, so the child kills its parent. Sizing one window for two
    /// columns up front (UB-214) avoided the fight and left a permanently
    /// oversized menu with a hidden half. IMGUI's GenericMenu (UB-215) has real
    /// submenus and correct focus but cannot be styled AT ALL - no GUIStyle hook,
    /// no skin override - so it could not match the rest of the builder.
    ///
    /// UI Toolkit's own GenericDropdownMenu pointed at the answer without being
    /// it: its DropDown() adds a full-panel container to the panel's root and
    /// positions the menu inside it. No window, so nothing can lose focus to
    /// anything. But its rows come from AddItem, which offers no hook to open a
    /// submenu and no public way to dismiss the menu from a row of one's own -
    /// so a submenu could not be built on it either.
    ///
    /// The lifecycle it owns is thirty lines: cover the panel, close on a click
    /// outside or Escape. Owning those thirty lines instead costs nothing and
    /// removes every limit - the styling is the builder's, the submenu is a real
    /// flyout beside its parent, and there is still exactly one window (UB-216).
    /// </summary>
    internal static class BuilderContextMenu
    {
        private static VisualElement s_scrim;
        private static VisualElement s_flyout;
        private static object s_openParent;

        /// <summary>The panel root the menu is living in, kept so the key handler
        /// can be taken off it again. Keys are caught THERE rather than on the menu
        /// itself: a KeyDownEvent goes to the FOCUSED element and bubbles through
        /// its ancestors, and the menu is not one of them - which is why Escape did
        /// nothing at all (UB-217).</summary>
        private static VisualElement s_keyHost;

        /// <summary>Rows of the column the keyboard is walking, and where it is.
        /// Rebuilt when a flyout opens or closes, so the arrows always move within
        /// whichever column the eye is on.</summary>
        private static readonly List<(VisualElement Row, BuilderSearchMenu.Item Item)> s_walk =
            new List<(VisualElement, BuilderSearchMenu.Item)>();
        private static int s_highlight = -1;
        private static readonly List<(VisualElement Row, BuilderSearchMenu.Item Item)> s_mainRows =
            new List<(VisualElement, BuilderSearchMenu.Item)>();
        private static readonly List<(VisualElement Row, BuilderSearchMenu.Item Item)> s_flyoutRows =
            new List<(VisualElement, BuilderSearchMenu.Item)>();
        private static bool s_inFlyout;

        /// <summary>Opens a menu at a panel-space point. Closes any menu already
        /// open, so a second right-click replaces rather than stacks.</summary>
        public static void Show(
            VisualElement panelRoot, Vector2 at, string title,
            List<BuilderSearchMenu.Item> items)
        {
            Close();
            if (panelRoot == null || items == null)
                return;

            s_scrim = new VisualElement
            {
                // Covers the whole panel so a click ANYWHERE outside the menu
                // dismisses it - which is the entire lifecycle a dropdown needs.
                focusable = true,
                style =
                {
                    position = Position.Absolute,
                    left = 0f, top = 0f, right = 0f, bottom = 0f,
                },
            };
            s_scrim.RegisterCallback<PointerDownEvent>(_ => Close());
            panelRoot.Add(s_scrim);
            s_keyHost = panelRoot;
            // TrickleDown, so the menu gets first refusal on the key before
            // whatever happens to hold focus does.
            panelRoot.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);

            s_mainRows.Clear();
            s_flyoutRows.Clear();
            var menu = Panel();
            if (!string.IsNullOrEmpty(title))
                menu.Add(TitleRow(title));
            foreach (var item in items)
                AddRow(menu, item, isFlyout: false);
            s_scrim.Add(menu);
            PlaceAt(menu, at, panelRoot);
            UseColumn(s_mainRows, inFlyout: false);
        }

        public static void Close()
        {
            if (s_keyHost != null)
            {
                s_keyHost.UnregisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
                s_keyHost = null;
            }
            s_flyout = null;
            s_openParent = null;
            s_mainRows.Clear();
            s_flyoutRows.Clear();
            s_walk.Clear();
            s_highlight = -1;
            s_inFlyout = false;
            s_scrim?.RemoveFromHierarchy();
            s_scrim = null;
        }

        public static bool IsOpen => s_scrim != null;

        // ── Keyboard ─────────────────────────────────────────────────────────

        private static void OnKey(KeyDownEvent evt)
        {
            if (s_scrim == null)
                return;
            switch (evt.keyCode)
            {
                case KeyCode.Escape:
                    // Out of the flyout first, then out of the menu - Escape backs
                    // out one level at a time, which is what it does everywhere.
                    if (s_inFlyout)
                    {
                        CloseFlyout();
                        UseColumn(s_mainRows, inFlyout: false);
                    }
                    else
                    {
                        Close();
                    }
                    evt.StopPropagation();
                    break;
                case KeyCode.DownArrow:
                    Move(1);
                    evt.StopPropagation();
                    break;
                case KeyCode.UpArrow:
                    Move(-1);
                    evt.StopPropagation();
                    break;
                case KeyCode.RightArrow:
                    StepIn();
                    evt.StopPropagation();
                    break;
                case KeyCode.LeftArrow:
                    if (s_inFlyout)
                    {
                        CloseFlyout();
                        UseColumn(s_mainRows, inFlyout: false);
                    }
                    evt.StopPropagation();
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Activate();
                    evt.StopPropagation();
                    break;
            }
        }

        private static void UseColumn(
            List<(VisualElement Row, BuilderSearchMenu.Item Item)> rows, bool inFlyout)
        {
            s_walk.Clear();
            s_walk.AddRange(rows);
            s_inFlyout = inFlyout;
            s_highlight = -1;
            Paint();
        }

        private static void Move(int delta)
        {
            if (s_walk.Count == 0)
                return;
            int next = s_highlight < 0 ? (delta > 0 ? 0 : s_walk.Count - 1) : s_highlight + delta;
            s_highlight = Mathf.Clamp(next, 0, s_walk.Count - 1);
            Paint();
        }

        private static void Paint()
        {
            for (int i = 0; i < s_walk.Count; i++)
                s_walk[i].Row.style.backgroundColor = i == s_highlight
                    ? new Color(0.31f, 0.76f, 0.97f, 0.14f)
                    : new Color(0f, 0f, 0f, 0f);
        }

        private static void StepIn()
        {
            if (s_inFlyout || s_highlight < 0 || s_highlight >= s_walk.Count)
                return;
            var (row, item) = s_walk[s_highlight];
            if (item?.Children == null || item.Children.Count == 0)
                return;
            OpenFlyout(row, item);
            UseColumn(s_flyoutRows, inFlyout: true);
            Move(1);
        }

        private static void Activate()
        {
            if (s_highlight < 0 || s_highlight >= s_walk.Count)
                return;
            var item = s_walk[s_highlight].Item;
            if (item == null)
                return;
            if (item.Children != null && item.Children.Count > 0)
            {
                StepIn();
                return;
            }
            var pick = item.OnPick;
            Close();
            pick?.Invoke();
        }

        // ── Rows ─────────────────────────────────────────────────────────────

        private static void AddRow(
            VisualElement into, BuilderSearchMenu.Item item, bool isFlyout)
        {
            if (item == null)
                return;
            if (item.IsSeparator)
            {
                into.Add(new VisualElement
                {
                    style =
                    {
                        height = 1f, marginTop = 4f, marginBottom = 4f,
                        marginLeft = 2f, marginRight = 2f,
                        backgroundColor = BuilderPalette.Line,
                    },
                });
                return;
            }
            if (item.Header != null)
            {
                into.Add(new Label(item.Header.ToUpperInvariant())
                {
                    style =
                    {
                        fontSize = 10f, color = BuilderPalette.Dim,
                        paddingLeft = 10f, paddingTop = 4f, paddingBottom = 2f,
                    },
                });
                return;
            }

            bool nests = item.Children != null && item.Children.Count > 0;
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    paddingLeft = 10f, paddingRight = 10f,
                    paddingTop = 3f, paddingBottom = 3f,
                },
            };
            row.Add(new Label(item.Label)
            {
                style = { color = BuilderPalette.Text, flexGrow = 1f },
            });
            if (!string.IsNullOrEmpty(item.Detail))
                row.Add(new Label(item.Detail)
                {
                    style = { color = BuilderPalette.Dim, fontSize = 10f, marginLeft = 8f },
                });
            if (nests)
                row.Add(new Label("›")
                {
                    style = { color = BuilderPalette.Dim, marginLeft = 8f },
                });

            BuilderCursor.Set(row, MouseCursor.Link);
            (isFlyout ? s_flyoutRows : s_mainRows).Add((row, item));
            row.RegisterCallback<MouseEnterEvent>(_ =>
            {
                row.style.backgroundColor = new Color(0.31f, 0.76f, 0.97f, 0.14f);
                // Resting on a nesting row opens its flyout; resting on any other
                // row of the SAME menu closes it. A row of the flyout itself is
                // neither, so the flyout survives the trip across to it.
                if (nests)
                    OpenFlyout(row, item);
                else if (!isFlyout)
                    CloseFlyout();
            });
            row.RegisterCallback<MouseLeaveEvent>(_ =>
                row.style.backgroundColor = StyleKeyword.Null);
            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                evt.StopPropagation();
                if (nests)
                {
                    OpenFlyout(row, item);
                    return;
                }
                var pick = item.OnPick;
                Close();
                pick?.Invoke();
            });
            into.Add(row);
        }

        private static VisualElement TitleRow(string title) => new Label(title.ToUpperInvariant())
        {
            style =
            {
                fontSize = 10f, color = BuilderPalette.Dim, letterSpacing = 1f,
                paddingLeft = 10f, paddingRight = 10f,
                paddingTop = 5f, paddingBottom = 4f,
                borderBottomWidth = 1f, borderBottomColor = BuilderPalette.Line,
                marginBottom = 3f,
            },
        };

        // ── The flyout ───────────────────────────────────────────────────────

        /// <summary>Opens a submenu BESIDE its row. It is parented to the scrim,
        /// which spans the whole panel - so unlike a child of the menu box it is
        /// not clipped by it, and can sit anywhere in the window.</summary>
        private static void OpenFlyout(VisualElement row, BuilderSearchMenu.Item parent)
        {
            if (s_scrim == null || ReferenceEquals(s_openParent, parent))
                return;
            CloseFlyout();
            s_openParent = parent;

            var flyout = Panel();
            // Its own heading, because the menu's title names the SUBJECT of the
            // menu - a card - and read over the flyout it looked like a heading
            // for these rows, which it is not (UB-218).
            flyout.Add(TitleRow(parent.Label));
            s_flyoutRows.Clear();
            foreach (var child in parent.Children)
                AddRow(flyout, child, isFlyout: true);
            s_scrim.Add(flyout);
            s_flyout = flyout;

            // The row already has a layout - it has to, the pointer is on it - so
            // its bounds are read straight away rather than waited for. PlaceAt
            // does the clamping once the flyout itself measures.
            var bounds = row.worldBound;
            var scrim = s_scrim.worldBound;
            PlaceAt(
                flyout,
                new Vector2(bounds.xMax - 4f - scrim.x, bounds.yMin - 4f - scrim.y),
                s_scrim);
        }

        private static void CloseFlyout()
        {
            s_flyout?.RemoveFromHierarchy();
            s_flyout = null;
            s_openParent = null;
            s_flyoutRows.Clear();
        }

        // ── Chrome ───────────────────────────────────────────────────────────

        private static VisualElement Panel() => new VisualElement
        {
            style =
            {
                position = Position.Absolute,
                minWidth = 180f,
                paddingTop = 3f, paddingBottom = 3f,
                backgroundColor = BuilderPalette.Panel,
                borderTopWidth = 1f, borderBottomWidth = 1f,
                borderLeftWidth = 1f, borderRightWidth = 1f,
                borderTopColor = BuilderPalette.Line,
                borderBottomColor = BuilderPalette.Line,
                borderLeftColor = BuilderPalette.Line,
                borderRightColor = BuilderPalette.Line,
                borderTopLeftRadius = 6f, borderTopRightRadius = 6f,
                borderBottomLeftRadius = 6f, borderBottomRightRadius = 6f,
            },
        };

        /// <summary>Puts a panel at a point and keeps it inside the window. A menu
        /// opened near the right or bottom edge would otherwise run off it, which
        /// is where a right-click most often lands.</summary>
        private static void PlaceAt(VisualElement panel, Vector2 at, VisualElement within)
        {
            panel.style.left = at.x;
            panel.style.top = at.y;
            panel.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float roomX = within.resolvedStyle.width;
                float roomY = within.resolvedStyle.height;
                float w = panel.resolvedStyle.width;
                float h = panel.resolvedStyle.height;
                if (roomX <= 0f || w <= 0f)
                    return;
                float x = Mathf.Clamp(at.x, 4f, Mathf.Max(4f, roomX - w - 4f));
                float y = Mathf.Clamp(at.y, 4f, Mathf.Max(4f, roomY - h - 4f));
                if (!Mathf.Approximately(x, panel.resolvedStyle.left))
                    panel.style.left = x;
                if (!Mathf.Approximately(y, panel.resolvedStyle.top))
                    panel.style.top = y;
            });
        }
    }
}
#endif
