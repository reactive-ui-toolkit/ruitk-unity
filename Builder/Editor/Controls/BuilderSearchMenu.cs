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

            /// <summary>A SUBMENU. The row shows an arrow and opens these in a
            /// second column when the pointer rests on it.
            ///
            /// The column lives in the SAME window rather than in a popup of its
            /// own, and that is the whole trick: every menu here is an EditorWindow
            /// that closes on lost focus, so a child popup taking focus would close
            /// its own parent. One window cannot fight itself.</summary>
            public List<Item> Children;
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

        /// <summary>Makes the invoking window the ACTIVE one before a popup opens.
        ///
        /// A right-click reaches its element whatever Unity considers focused, but
        /// a popup cannot open from a window that is not - so after any action that
        /// left focus elsewhere (closing the previous menu, an inline editor), the
        /// gesture landed and no menu appeared. It was chased with a timer that
        /// re-asserted focus after the fact; taking it HERE, synchronously, is the
        /// point of decision and needs no timing to be right (UB-209).</summary>
        private static void FocusInvoker()
        {
            if (s_pointerWindow != null)
                s_pointerWindow.Focus();
        }

        public static Item Separator => new Item { IsSeparator = true };

        public static Item SectionHeader(string text) => new Item { Header = text };

        private EditorWindow _invoker;
        private List<Item> _items;
        private Func<string, Item> _freeform;
        private string _title;
        private string _placeholder;
        private bool _searchable = true;
        private TextField _search;
        private ScrollView _list;
        private ScrollView _submenu;

        /// <summary>The row whose children the second column is showing.</summary>
        private Item _openParent;

        /// <summary>Which column the keyboard is walking. Right arrow steps into a
        /// submenu, Left steps back out.</summary>
        private bool _inSubmenu;
        private Func<string, string> _nameValidate;
        private Action<string> _nameSubmit;

        /// <summary>UB-129: the starting TEXT of a name prompt, as opposed to
        /// the placeholder. A rename has to open with the current name IN the
        /// field and selected, so it can be edited or replaced; a placeholder
        /// only draws grey hint text over an EMPTY field, which is why the
        /// first keystroke wiped the name and nothing could be selected.</summary>
        private string _initialValue;
        private Label _errorLabel;

        /// <summary>POC plain context menu (row / card / create): title header,
        /// no search field, separators and inline section headers, sized to its
        /// content like the in-page ".ctx" menu.</summary>
        public static void ShowSimple(string title, List<Item> items)
        {
            FocusInvoker();
            int rows = 0;
            int widest = title?.Length ?? 0;
            int widestChild = 0;
            int deepestChild = 0;
            foreach (var item in items)
            {
                rows++;
                int length = item.IsSeparator ? 0
                    : item.Header != null ? item.Header.Length
                    : (item.Label?.Length ?? 0);
                if (item.Children != null)
                {
                    // A submenu opens BESIDE the list, so the window is sized for
                    // both columns up front. Growing it on hover would move the
                    // rows out from under the pointer that opened them.
                    length += 2;
                    int deep = 0;
                    foreach (var child in item.Children)
                    {
                        if (child.IsSeparator || child.Header != null)
                            continue;
                        deep++;
                        if ((child.Label?.Length ?? 0) > widestChild)
                            widestChild = child.Label.Length;
                    }
                    if (deep > deepestChild)
                        deepestChild = deep;
                }
                if (length > widest)
                    widest = length;
            }
            float listWidth = Mathf.Clamp(widest * 7.2f + 30f, 195f, 420f);
            float childWidth = widestChild == 0
                ? 0f
                : Mathf.Clamp(widestChild * 7.2f + 34f, 150f, 360f);
            Open(
                title, null, items, null, searchable: false,
                width: listWidth + childWidth,
                height: 24f + Mathf.Max(rows, deepestChild) * 21f + 10f);
        }

        public static void Show(
            string title,
            string placeholder,
            List<Item> items,
            Func<string, Item> freeform = null)
            => Open(title, placeholder, items, freeform, true, 260f, 320f);

        /// <summary>POC openNameMenu(): a compact at-cursor popup with a title, a
        /// placeholder-only input, an inline ".ctx-err" line and a persistent
        /// "Create" row. Enter submits, Esc closes, and a failed validation writes
        /// into the error line WITHOUT closing so the name can be corrected.</summary>
        public static void ShowNamePrompt(
            string title, string placeholder, Func<string, string> validate,
            Action<string> onSubmit, string initialValue = null)
        {
            var window = CreateInstance<BuilderSearchMenu>();
            window._title = title;
            window._placeholder = placeholder;
            window._items = new List<Item>();
            window._searchable = true;
            window._nameValidate = validate;
            window._nameSubmit = onSubmit;
            window._initialValue = initialValue;
            Place(window, 240f, 104f);
        }

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
            Place(window, width, height);
        }

        private static void Place(BuilderSearchMenu window, float width, float height)
        {
            // Captured BEFORE ShowPopup steals focus. s_pointerWindow is only set
            // by the gestures that need at-cursor placement, so it cannot be the
            // only source for UB-92's focus restore — a menu opened from a
            // toolbar or a chained submenu would have nothing to go back to.
            window._invoker = s_pointerWindow != null && s_pointerValid
                ? s_pointerWindow
                : focusedWindow;
            if (window._invoker == window)
                window._invoker = null;
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
            // POC placeMenu clamps on BOTH axes against the viewport, so a menu
            // opened near the right or bottom edge slides back on-screen.
            var bounds = ScreenBoundsAround(anchor);
            anchor.x = Mathf.Clamp(anchor.x, bounds.xMin + 4f, Mathf.Max(bounds.xMin + 4f, bounds.xMax - width - 8f));
            anchor.y = Mathf.Clamp(anchor.y + 8f, bounds.yMin + 4f, Mathf.Max(bounds.yMin + 4f, bounds.yMax - height - 8f));
            window.position = new Rect(anchor.x, anchor.y, width, height);
            window.ShowPopup();
            window.Focus();
        }

        private static Rect ScreenBoundsAround(Vector2 point)
        {
            try
            {
                var main = UnityEditor.EditorGUIUtility.GetMainWindowPosition();
                if (main.width > 0f && main.height > 0f && main.Contains(point))
                    return main;
                if (main.width > 0f && main.height > 0f)
                    return new Rect(
                        Mathf.Min(main.xMin, point.x - 200f),
                        Mathf.Min(main.yMin, point.y - 200f),
                        Mathf.Max(main.width, 1600f),
                        Mathf.Max(main.height, 1000f));
            }
            catch (Exception)
            {
            }
            return new Rect(0f, 0f, Screen.currentResolution.width, Screen.currentResolution.height);
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
                        color = BuilderPalette.Dim,
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
                BuilderPreviewPane.StyleInput(_search);
                _search.RegisterValueChangedCallback(_ =>
                {
                    if (_nameValidate == null)
                        Rebuild();
                    else if (_errorLabel != null)
                        _errorLabel.text = "";
                });
                _search.RegisterCallback<KeyDownEvent>(evt =>
                {
                    // UB-130: this popup is its own EditorWindow, so an undo
                    // chord typed while editing a name fell straight through to
                    // UNITY's global undo and mutated the scene. The field owns
                    // these keys for the duration of the prompt.
                    if ((evt.ctrlKey || evt.commandKey)
                        && (evt.keyCode == KeyCode.Z || evt.keyCode == KeyCode.Y))
                    {
                        evt.StopImmediatePropagation();
                        evt.imguiEvent?.Use();
                        return;
                    }
                    if (evt.keyCode == KeyCode.Escape)
                    {
                        Close();
                        evt.StopPropagation();
                    }
                    else if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                    {
                        if (_nameValidate != null)
                            SubmitName();
                        else
                            PickHighlighted();
                        evt.StopPropagation();
                    }
                    // UB-122: the list could only ever be driven by the mouse or
                    // by Enter-on-the-first-match. Arrows move a highlight and
                    // Enter takes it, so a menu is usable without leaving the
                    // keyboard the search field already owns.
                    else if (evt.keyCode == KeyCode.DownArrow)
                    {
                        MoveHighlight(1);
                        evt.StopPropagation();
                    }
                    else if (evt.keyCode == KeyCode.UpArrow)
                    {
                        MoveHighlight(-1);
                        evt.StopPropagation();
                    }
                }, TrickleDown.TrickleDown);
                root.Add(_search);
            }

            if (_nameValidate != null)
            {
                _errorLabel = new Label
                {
                    style =
                    {
                        color = new Color(0.94f, 0.38f, 0.38f),
                        fontSize = 10f,
                        paddingLeft = 10f,
                        whiteSpace = WhiteSpace.Normal,
                    },
                };
                root.Add(_errorLabel);
            }

            var columns = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, flexGrow = 1f, minHeight = 0f },
            };
            _list = new ScrollView { style = { flexGrow = 1f, maxHeight = 300f } };
            columns.Add(_list);
            _submenu = new ScrollView
            {
                style =
                {
                    flexGrow = 1f, maxHeight = 300f, display = DisplayStyle.None,
                    borderLeftWidth = 1f, borderLeftColor = BuilderPalette.Line,
                },
            };
            columns.Add(_submenu);
            root.Add(columns);
            Rebuild();
            if (_searchable)
            {
                if (!string.IsNullOrEmpty(_initialValue))
                    _search.SetValueWithoutNotify(_initialValue);
                _search.schedule.Execute(() =>
                {
                    _search.Focus();
                    if (!string.IsNullOrEmpty(_initialValue))
                        _search.textSelection.SelectAll();
                });
            }
            else
            {
                // UB-126: the arrow/Enter handling lived on the SEARCH FIELD, so
                // a menu built without one — the create menu, and every simple
                // pick list — was mouse-only. The same keys are bound to the
                // root, and the root takes focus so they arrive at all.
                root.focusable = true;
                root.RegisterCallback<KeyDownEvent>(OnListKeyDown);
                root.schedule.Execute(() => root.Focus());
            }
        }

        private void OnLostFocus() => Close();

        /// <summary>UB-92: every menu here is its own EditorWindow, so closing
        /// one hands Unity's focus back to whatever it was on BEFORE the menu
        /// opened — usually the Project window. The action a pick triggers often
        /// opens the inline editor, and that editor was then focusing a field
        /// inside a window Unity did not consider focused, so keystrokes went
        /// somewhere else entirely: Enter reached the Project window, which runs
        /// OpenAsset, which opens VS2022. The invoking window is already
        /// remembered for positioning; focusing it back is what was missing.
        /// The pick runs AFTER the focus is restored, so anything it opens
        /// inherits a focused window.</summary>
        private void CloseAndRestoreFocus()
        {
            var invoker = _invoker != null ? _invoker : s_pointerWindow;
            Close();
            if (invoker != null)
                invoker.Focus();
        }

        /// <summary>The list keyboard, shared by the searchable and the
        /// non-searchable menu so neither can drift from the other.</summary>
        private void OnListKeyDown(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.Escape:
                    Close();
                    evt.StopPropagation();
                    break;
                case KeyCode.DownArrow:
                    MoveHighlight(1);
                    evt.StopPropagation();
                    break;
                case KeyCode.UpArrow:
                    MoveHighlight(-1);
                    evt.StopPropagation();
                    break;
                case KeyCode.RightArrow:
                    EnterSubmenu();
                    evt.StopPropagation();
                    break;
                case KeyCode.LeftArrow:
                    if (_inSubmenu)
                    {
                        _inSubmenu = false;
                        _highlight = -1;
                        MoveHighlight(1);
                    }
                    evt.StopPropagation();
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (!_inSubmenu && HighlightedItem()?.Children != null)
                    {
                        EnterSubmenu();
                        evt.StopPropagation();
                        break;
                    }
                    PickHighlighted();
                    evt.StopPropagation();
                    break;
            }
        }

        private void PickFirst() => PickHighlighted();

        /// <summary>Index into the PICKABLE rows (headers and separators are not
        /// pickable and are skipped). -1 means "the first pickable row", which
        /// keeps Enter-without-arrows behaving as it always did.</summary>
        private int _highlight = -1;

        private List<VisualElement> PickableRows()
        {
            var rows = new List<VisualElement>();
            var column = _inSubmenu && _openParent != null ? _submenu : _list;
            if (column == null)
                return rows;
            foreach (var child in column.contentContainer.Children())
                if (child.userData is Item)
                    rows.Add(child);
            return rows;
        }

        private void MoveHighlight(int delta)
        {
            var rows = PickableRows();
            if (rows.Count == 0)
                return;
            int next = _highlight < 0 ? (delta > 0 ? 0 : rows.Count - 1) : _highlight + delta;
            _highlight = Mathf.Clamp(next, 0, rows.Count - 1);
            for (int i = 0; i < rows.Count; i++)
                rows[i].style.backgroundColor = i == _highlight
                    ? new Color(0.31f, 0.76f, 0.97f, 0.14f)
                    : new Color(0f, 0f, 0f, 0f);
            (_inSubmenu && _openParent != null ? _submenu : _list).ScrollTo(rows[_highlight]);
        }

        /// <summary>The item the keyboard is on, or null.</summary>
        private Item HighlightedItem()
        {
            var rows = PickableRows();
            if (rows.Count == 0 || _highlight < 0 || _highlight >= rows.Count)
                return null;
            return rows[_highlight].userData as Item;
        }

        /// <summary>Steps the keyboard into the highlighted row's submenu.</summary>
        private void EnterSubmenu()
        {
            if (_inSubmenu)
                return;
            var item = HighlightedItem();
            if (item?.Children == null || item.Children.Count == 0)
                return;
            OpenSubmenu(item);
            _inSubmenu = true;
            _highlight = -1;
            MoveHighlight(1);
        }

        private void PickHighlighted()
        {
            var rows = PickableRows();
            if (rows.Count == 0)
                return;
            int index = _highlight < 0 ? 0 : Mathf.Clamp(_highlight, 0, rows.Count - 1);
            if (!(rows[index].userData is Item item))
                return;
            CloseAndRestoreFocus();
            item.OnPick?.Invoke();
        }

        private void SubmitName()
        {
            string value = (_search?.value ?? "").Trim();
            string error = _nameValidate(value);
            if (!string.IsNullOrEmpty(error))
            {
                if (_errorLabel != null)
                    _errorLabel.text = error;
                return;
            }
            CloseAndRestoreFocus();
            _nameSubmit?.Invoke(value);
        }

        private void Rebuild()
        {
            // Typing refilters, so the previous highlight index means nothing
            // against the new row set — back to "the first match", which is what
            // Enter has always taken.
            _highlight = -1;
            _list.contentContainer.Clear();
            if (_nameValidate != null)
            {
                AddRow(
                    new Item { Label = "Create", OnPick = null },
                    BuilderPalette.Text,
                    submitsName: true);
                return;
            }
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
                                backgroundColor = BuilderPalette.Line,
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
                                color = BuilderPalette.Dim,
                                paddingLeft = 10f, paddingTop = 4f, paddingBottom = 2f,
                            },
                        });
                    continue;
                }
                if (filter.Length > 0
                    && item.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                AddRow(item, BuilderPalette.Text);
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

        private void AddRow(Item item, Color color, bool submitsName = false) =>
            AddRow(item, color, submitsName, _list);

        private void AddRow(Item item, Color color, bool submitsName, ScrollView into)
        {
            bool hasChildren = item.Children != null && item.Children.Count > 0;
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
            if (hasChildren)
                row.Add(new Label("›")
                {
                    style = { color = new Color(0.55f, 0.55f, 0.59f), marginLeft = 6f },
                });
            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                evt.StopPropagation();
                if (hasChildren)
                {
                    // A click on a parent row opens its column too, so the menu
                    // works for someone who clicks rather than hovers.
                    OpenSubmenu(item);
                    return;
                }
                if (submitsName)
                {
                    SubmitName();
                    return;
                }
                CloseAndRestoreFocus();
                item.OnPick?.Invoke();
            });
            BuilderCursor.Set(row, MouseCursor.Link);
            row.RegisterCallback<MouseEnterEvent>(_ =>
            {
                row.style.backgroundColor = new Color(0.31f, 0.76f, 0.97f, 0.14f);
                // Resting on a parent opens its column; resting on any OTHER row
                // of the same column closes it. Moving into the column itself is
                // neither, so the submenu survives the trip across.
                if (hasChildren)
                    OpenSubmenu(item);
                else if (into == _list)
                    CloseSubmenu();
            });
            row.RegisterCallback<MouseLeaveEvent>(_ =>
                row.style.backgroundColor = StyleKeyword.Null);
            into.contentContainer.Add(row);
        }

        /// <summary>Fills the second column with one row's children.</summary>
        private void OpenSubmenu(Item parent)
        {
            if (_submenu == null || parent?.Children == null)
                return;
            if (ReferenceEquals(_openParent, parent))
                return;
            _openParent = parent;
            _submenu.contentContainer.Clear();
            foreach (var child in parent.Children)
            {
                if (child.IsSeparator || child.Header != null)
                    continue;
                AddRow(child, BuilderPalette.Text, submitsName: false, into: _submenu);
            }
            _submenu.style.display = DisplayStyle.Flex;
        }

        private void CloseSubmenu()
        {
            if (_submenu == null || _openParent == null)
                return;
            _openParent = null;
            _inSubmenu = false;
            _submenu.contentContainer.Clear();
            _submenu.style.display = DisplayStyle.None;
        }
    }
}
#endif
