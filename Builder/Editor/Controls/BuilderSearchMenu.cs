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
        }

        private List<Item> _items;
        private Func<string, Item> _freeform;
        private string _title;
        private string _placeholder;
        private TextField _search;
        private ScrollView _list;

        public static void Show(
            string title,
            string placeholder,
            List<Item> items,
            Func<string, Item> freeform = null)
        {
            var window = CreateInstance<BuilderSearchMenu>();
            window._title = title;
            window._placeholder = placeholder;
            window._items = items;
            window._freeform = freeform;
            var mouse = GUIUtility.GUIToScreenPoint(Event.current?.mousePosition ?? Vector2.zero);
            window.position = new Rect(mouse.x, mouse.y + 8f, 260f, 320f);
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

            root.Add(new Label(_title)
            {
                style =
                {
                    fontSize = 10f,
                    color = new Color(0.55f, 0.55f, 0.59f),
                    paddingLeft = 10f, paddingTop = 4f, paddingBottom = 2f,
                },
            });

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

            _list = new ScrollView { style = { flexGrow = 1f, maxHeight = 300f } };
            root.Add(_list);
            Rebuild();
            _search.schedule.Execute(() => _search.Focus());
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
                if (filter.Length > 0
                    && item.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                AddRow(item, new Color(0.84f, 0.84f, 0.86f));
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
