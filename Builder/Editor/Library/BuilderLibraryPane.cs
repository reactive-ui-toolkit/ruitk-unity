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
            public Color Tint = new Color(0.80f, 0.80f, 0.84f);
        }

        private static readonly Color ElementTint = new Color(0.42f, 0.68f, 0.90f);
        private static readonly Color ComponentTint = new Color(0.31f, 0.86f, 0.77f);
        private static readonly Color HookTint = new Color(0.90f, 0.78f, 0.40f);
        private static readonly Color StyleTint = new Color(1.00f, 0.72f, 0.30f);
        private static readonly Color UtilTint = new Color(0.51f, 0.78f, 0.52f);
        private static readonly Color DirectiveTint = new Color(0.77f, 0.53f, 0.75f);

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
                            Name = "<" + prop.Name + ">",
                            Description = body?.Value<string>("description") ?? "",
                            Snippet = "<" + prop.Name + " />",
                            Section = "Native elements",
                            Tint = ElementTint,
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
                            Tint = HookTint,
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
            SortSections();
            Rebuild();
        }

        /// <summary>The open tree's own modules (POC "Custom components" /
        /// "Style modules" / "Util modules" sections) — refreshed whenever the
        /// canvas graph loads.</summary>
        public void SetWorkspaceEntries(BuilderGraph graph)
        {
            _entries.RemoveAll(e =>
                e.Section == "Custom components"
                || e.Section == "Style modules"
                || e.Section == "Util modules"
                || e.Section == "Hook modules");
            if (graph == null)
            {
                Rebuild();
                return;
            }
            foreach (var node in graph.Nodes)
            {
                switch (node.Kind)
                {
                    case BuilderNodeKind.Component:
                        foreach (string export in node.Exports)
                            _entries.Add(new Entry
                            {
                                Name = "<" + export + ">",
                                Description = node.FilePath,
                                Snippet = "<" + export + " />",
                                Section = "Custom components",
                                Tint = ComponentTint,
                            });
                        break;
                    case BuilderNodeKind.Style:
                        _entries.Add(new Entry
                        {
                            Name = node.Title,
                            Description = node.FilePath,
                            Snippet = node.Title + ".",
                            Section = "Style modules",
                            Tint = StyleTint,
                        });
                        break;
                    case BuilderNodeKind.Hook:
                        foreach (string export in node.Exports)
                            _entries.Add(new Entry
                            {
                                Name = export,
                                Description = node.FilePath,
                                Snippet = export + "()",
                                Section = "Hook modules",
                                Tint = HookTint,
                            });
                        break;
                    case BuilderNodeKind.Util:
                        _entries.Add(new Entry
                        {
                            Name = node.Title,
                            Description = node.FilePath,
                            Snippet = node.Title,
                            Section = "Util modules",
                            Tint = UtilTint,
                        });
                        break;
                }
            }
            SortSections();
            Rebuild();
        }

        private static readonly string[] s_sectionOrder =
        {
            "Native elements", "Custom components", "Hooks", "Hook modules",
            "Style modules", "Util modules", "Directives", "Style keys",
        };

        private void SortSections()
        {
            _entries.Sort((a, b) =>
            {
                int sa = Array.IndexOf(s_sectionOrder, a.Section);
                int sb = Array.IndexOf(s_sectionOrder, b.Section);
                if (sa != sb)
                    return sa.CompareTo(sb);
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
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
                    Tint = DirectiveTint,
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
                    style =
                    {
                        marginLeft = 10f,
                        paddingTop = 1f,
                        paddingBottom = 1f,
                        color = entry.Tint,
                    },
                };
                var captured = entry;
                row.RegisterCallback<PointerDownEvent>(_ => _insert?.Invoke(captured.Snippet));
                row.RegisterCallback<MouseEnterEvent>(_ =>
                    row.style.backgroundColor = new Color(0.18f, 0.24f, 0.30f));
                row.RegisterCallback<MouseLeaveEvent>(_ =>
                    row.style.backgroundColor = StyleKeyword.Null);
                _listHost.Add(row);
            }
        }
    }
}
#endif
