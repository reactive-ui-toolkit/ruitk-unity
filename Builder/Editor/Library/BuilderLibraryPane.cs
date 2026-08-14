#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// The searchable palette (plan VE-13 leg 1): elements from the LSP's
    /// embedded schema (name, description, attribute tooltips) and ambient
    /// hooks from <c>ruitk/hooks</c>. A click inserts a snippet at the code
    /// caret — palette authoring rides the same session/undo/recompile path
    /// as typing, never a parallel one.
    /// </summary>
    internal sealed class BuilderLibraryPane
    {
        private sealed class Entry
        {
            public string Name;
            public string Description;
            public string Snippet;
            public string Section;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private VisualElement _listHost;
        private string _filter = "";
        private Action<string> _insert;

        public async void Attach(VisualElement container, Action<string> insertAtCaret)
        {
            _insert = insertAtCaret;
            container.Clear();

            var search = new TextField { style = { marginTop = 4f, marginLeft = 4f, marginRight = 4f } };
            search.textEdition.placeholder = "Search elements & hooks";
            search.RegisterValueChangedCallback(e =>
            {
                _filter = e.newValue ?? "";
                Rebuild();
            });
            container.Add(search);

            var scroll = new ScrollView { style = { flexGrow = 1f } };
            _listHost = scroll.contentContainer;
            container.Add(scroll);

            try
            {
                var client = await BuilderLspService.GetOrStartAsync();

                var schema = await client.RequestSchema();
                string json = schema?.Value<string>("json") ?? schema?.Value<string>("Json");
                if (!string.IsNullOrEmpty(json) && JObject.Parse(json)["elements"] is JObject elements)
                {
                    foreach (var prop in elements.Properties())
                    {
                        var body = prop.Value as JObject;
                        _entries.Add(new Entry
                        {
                            Name = prop.Name,
                            Description = body?.Value<string>("description") ?? "",
                            Snippet = "<" + prop.Name + " />",
                            Section = "Elements",
                        });
                    }
                }

                var hooks = await client.RequestHooks();
                if ((hooks?["hooks"] ?? hooks?["Hooks"]) is JArray hookArr)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in hookArr)
                    {
                        string name = h.Value<string>("name") ?? h.Value<string>("Name") ?? h.ToString();
                        if (string.IsNullOrEmpty(name) || !seen.Add(name))
                            continue;
                        _entries.Add(new Entry
                        {
                            Name = name,
                            Description = h.Value<string>("doc") ?? h.Value<string>("Doc") ?? "",
                            Snippet = name + "()",
                            Section = "Hooks",
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _listHost.Add(new Label("Palette unavailable: " + ex.Message)
                {
                    style = { color = new Color(0.7f, 0.5f, 0.4f), marginLeft = 6f, whiteSpace = WhiteSpace.Normal },
                });
                return;
            }

            AddDirectiveEntries();
            AddStyleKeyEntries();
            Rebuild();
        }

        private void AddDirectiveEntries()
        {
            var directives = new (string Name, string Snippet)[]
            {
                ("@if", "@if (condition) {\n  return (\n    <VisualElement />\n  );\n}\n"),
                ("@if/else", "@if (condition) {\n  return (\n    <VisualElement />\n  );\n} else {\n  return (\n    <VisualElement />\n  );\n}\n"),
                ("@for", "@for (int i = 0; i < count; i++) {\n  return (\n    <VisualElement key={$\"item-{i}\"} />\n  );\n}\n"),
                ("@foreach", "@foreach (var item in items) {\n  return (\n    <VisualElement key={item.ToString()} />\n  );\n}\n"),
                ("@switch", "@switch (value) {\n  @case (0) {\n    return (\n      <VisualElement />\n    );\n  }\n  @default {\n    return (\n      <VisualElement />\n    );\n  }\n}\n"),
            };
            foreach (var (name, snippet) in directives)
            {
                _entries.Add(new Entry
                {
                    Name = name,
                    Description = "Directive block — the body wraps markup in return (...).",
                    Snippet = snippet,
                    Section = "Directives",
                });
            }
        }

        private void AddStyleKeyEntries()
        {
            foreach (var prop in typeof(Ruitk.Props.Typed.Style).GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!prop.CanWrite)
                    continue;
                _entries.Add(new Entry
                {
                    Name = prop.Name,
                    Description = prop.PropertyType.Name,
                    Snippet = prop.Name + " = ",
                    Section = "Style keys",
                });
            }
        }

        private void Rebuild()
        {
            if (_listHost == null)
                return;
            _listHost.Clear();
            string section = null;
            foreach (var entry in _entries)
            {
                if (_filter.Length > 0
                    && entry.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (entry.Section != section)
                {
                    section = entry.Section;
                    _listHost.Add(new Label(section)
                    {
                        style =
                        {
                            unityFontStyleAndWeight = FontStyle.Bold,
                            marginTop = 6f, marginLeft = 6f,
                            color = new Color(0.55f, 0.55f, 0.6f),
                            fontSize = 10f,
                        },
                    });
                }
                var row = new Label(entry.Name)
                {
                    tooltip = entry.Description,
                    style = { marginLeft = 10f, paddingTop = 1f, paddingBottom = 1f },
                };
                var captured = entry;
                row.RegisterCallback<PointerDownEvent>(_ => _insert?.Invoke(captured.Snippet));
                row.RegisterCallback<MouseEnterEvent>(_ => row.style.color = new Color(0.31f, 0.76f, 0.97f));
                row.RegisterCallback<MouseLeaveEvent>(_ => row.style.color = StyleKeyword.Null);
                _listHost.Add(row);
            }
        }
    }
}
#endif
