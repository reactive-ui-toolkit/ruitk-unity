#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// The POC's searchable context menu (spec 6.4 chrome): title header,
    /// search field, filtered list, optional freeform fallback item rendered in
    /// warn-orange, "(no matches)" dim row, Enter activates the first item,
    /// Escape closes. Replaces GenericMenu wherever the POC menu is searchable.
    /// </summary>
    internal sealed class BuilderSearchMenu : EditorWindow
    {
        public sealed class Item
        {
            public string Label;
            public string Detail;
            public Action OnPick;

            /// <summary>POC ".ctx .sep" divider row (no label, not pickable).</summary>
            public bool IsSeparator;

            /// <summary>POC ".ctx .mh" inline section header inside the list.</summary>
            public string Header;
        }

        private static Vector2 s_pointer;
        private static bool s_pointerValid;
        private static EditorWindow s_pointerWindow;

        /// <summary>The canvas records the panel-space point of the gesture that
        /// is about to open a menu, so the popup lands under the cursor instead
        /// of at the host window's centre.</summary>
        public static void RememberPointer(Vector2 panelPosition, EditorWindow window)
        {
            s_pointer = panelPosition;
            s_pointerWindow = window;
            s_pointerValid = window != null;
        }

        public static Item Separator => new Item { IsSeparator = true };

        public static Item SectionHeader(string text) => new Item { Header = text };

        private List<Item> _items;
        private Func<string, Item> _freeform;
        private string _title;
        private string _placeholder;
        private bool _searchable = true;
        private TextField _search;
        private ScrollView _list;

        /// <summary>POC plain context menu (row / card / create): title header,
        /// no search field, separators and inline section headers, sized to its
        /// content like the in-page ".ctx" menu.</summary>
        public static void ShowSimple(string title, List<Item> items)
        {
            int rows = 0;
            int widest = title?.Length ?? 0;
            foreach (var item in items)
            {
                rows++;
                int length = item.IsSeparator ? 0
                    : item.Header != null ? item.Header.Length
                    : (item.Label?.Length ?? 0);
                if (length > widest)
                    widest = length;
            }
            Open(
                title, null, items, null, searchable: false,
                width: Mathf.Clamp(widest * 7.2f + 30f, 195f, 420f),
                height: 24f + rows * 21f + 10f);
        }

        public static void Show(
            string title,
            string placeholder,
            List<Item> items,
            Func<string, Item> freeform = null)
            => Open(title, placeholder, items, freeform, true, 260f, 320f);

        private static void Open(
            string title,
            string placeholder,
            List<Item> items,
            Func<string, Item> freeform,
            bool searchable,
            float width,
            float height)
        {
            var window = CreateInstance<BuilderSearchMenu>();
            window._title = title;
            window._placeholder = placeholder;
            window._items = items;
            window._freeform = freeform;
            window._searchable = searchable;
            // POC placeMenu(): every menu (and every submenu chained from one)
            // opens AT the click. Event.current is null outside OnGUI — UITK
            // pointer callbacks hand us the panel-space point instead, which the
            // hosting window's screen rect turns into a screen point.
            Vector2 anchor;
            if (s_pointerValid && s_pointerWindow != null)
                anchor = s_pointerWindow.position.position + s_pointer;
            else if (Event.current != null)
                anchor = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
            else if (focusedWindow != null)
                anchor = focusedWindow.position.center - new Vector2(130f, 160f);
            else
                anchor = new Vector2(400f, 300f);
            anchor.x = Mathf.Max(4f, anchor.x);
            anchor.y = Mathf.Max(4f, anchor.y);
            window.position = new Rect(anchor.x, anchor.y + 8f, width, height);
            window.ShowPopup();
            window.Focus();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.backgroundColor = new Color(0.165f, 0.165f, 0.19f);
            root.style.borderTopWidth = 1f;
            root.style.borderBottomWidth = 1f;
            root.style.borderLeftWidth = 1f;
            root.style.borderRightWidth = 1f;
            root.style.borderTopColor = new Color(0.23f, 0.23f, 0.27f);
            root.style.borderBottomColor = new Color(0.23f, 0.23f, 0.27f);
            root.style.borderLeftColor = new Color(0.23f, 0.23f, 0.27f);
            root.style.borderRightColor = new Color(0.23f, 0.23f, 0.27f);

            if (!string.IsNullOrEmpty(_title))
                root.Add(new Label(_title.ToUpperInvariant())
                {
                    style =
                    {
                        fontSize = 10f,
                        color = new Color(0.545f, 0.545f, 0.588f),
                        paddingLeft = 10f, paddingTop = 4f, paddingBottom = 2f,
                    },
                });

            if (_searchable)
            {
                _search = new TextField
                {
                    style = { marginLeft = 6f, marginRight = 6f, marginBottom = 4f },
                };
                _search.textEdition.placeholder = _placeholder ?? "search…";
                _search.RegisterValueChangedCallback(_ => Rebuild());
                _search.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode == KeyCode.Escape)
                    {
                        Close();
                        evt.StopPropagation();
                    }
                    else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                    {
                        PickFirst();
                        evt.StopPropagation();
                    }
                }, TrickleDown.TrickleDown);
                root.Add(_search);
            }

            _list = new ScrollView { style = { flexGrow = 1f, maxHeight = 300f } };
            root.Add(_list);
            Rebuild();
            if (_searchable)
                _search.schedule.Execute(() => _search.Focus());
            else
                root.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode == KeyCode.Escape)
                        Close();
                });
        }

        private void OnLostFocus() => Close();

        private void PickFirst()
        {
            foreach (var child in _list.contentContainer.Children())
            {
                if (child.userData is Item item)
                {
                    Close();
                    item.OnPick?.Invoke();
                    return;
                }
            }
        }

        private void Rebuild()
        {
            _list.contentContainer.Clear();
            string filter = _search?.value ?? "";
            int shown = 0;
            foreach (var item in _items)
            {
                if (item.IsSeparator)
                {
                    if (filter.Length == 0)
                        _list.contentContainer.Add(new VisualElement
                        {
                            style =
                            {
                                height = 1f,
                                marginTop = 4f, marginBottom = 4f,
                                marginLeft = 2f, marginRight = 2f,
                                backgroundColor = new Color(0.227f, 0.227f, 0.267f),
                            },
                        });
                    continue;
                }
                if (item.Header != null)
                {
                    if (filter.Length == 0)
                        _list.contentContainer.Add(new Label(item.Header.ToUpperInvariant())
                        {
                            style =
                            {
                                fontSize = 10f,
                                color = new Color(0.545f, 0.545f, 0.588f),
                                paddingLeft = 10f, paddingTop = 4f, paddingBottom = 2f,
                            },
                        });
                    continue;
                }
                if (filter.Length > 0
                    && item.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                AddRow(item, new Color(0.839f, 0.839f, 0.863f));
                shown++;
            }
            if (_freeform != null && filter.Trim().Length > 0)
            {
                var free = _freeform(filter.Trim());
                if (free != null)
                    AddRow(free, new Color(1.00f, 0.72f, 0.30f));
            }
            if (shown == 0 && (_freeform == null || filter.Trim().Length == 0))
            {
                _list.contentContainer.Add(new Label("(no matches)")
                {
                    style =
                    {
                        color = new Color(0.45f, 0.45f, 0.50f),
                        paddingLeft = 10f, paddingTop = 4f, paddingBottom = 4f,
                    },
                });
            }
        }

        private void AddRow(Item item, Color color)
        {
            var row = new VisualElement
            {
                userData = item,
                style =
                {
                    flexDirection = FlexDirection.Row,
                    paddingLeft = 10f, paddingRight = 10f,
                    paddingTop = 3f, paddingBottom = 3f,
                },
            };
            row.Add(new Label(item.Label) { style = { color = color, flexGrow = 1f } });
            if (!string.IsNullOrEmpty(item.Detail))
                row.Add(new Label(item.Detail)
                {
                    style = { color = new Color(0.55f, 0.55f, 0.59f), fontSize = 10f },
                });
            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                evt.StopPropagation();
                Close();
                item.OnPick?.Invoke();
            });
            row.RegisterCallback<MouseEnterEvent>(_ =>
                row.style.backgroundColor = new Color(0.31f, 0.76f, 0.97f, 0.14f));
            row.RegisterCallback<MouseLeaveEvent>(_ =>
                row.style.backgroundColor = StyleKeyword.Null);
            _list.contentContainer.Add(row);
        }
    }
}
#endif
