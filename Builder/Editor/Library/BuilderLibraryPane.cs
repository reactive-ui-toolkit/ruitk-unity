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
            public string Payload;
            public Color Tint = new Color(0.80f, 0.80f, 0.84f);
        }

        private static readonly Color ElementTint = new Color(0.310f, 0.765f, 0.969f);
        private static readonly Color ComponentTint = new Color(0.498f, 0.859f, 0.792f);
        private static readonly Color HookTint = new Color(0.506f, 0.780f, 0.518f);
        private static readonly Color StyleTint = new Color(0.808f, 0.576f, 0.847f);
        private static readonly Color UtilTint = new Color(1.000f, 0.718f, 0.302f);
        private static readonly Color DirectiveTint = new Color(0.808f, 0.576f, 0.847f);

        private static readonly Color Panel2 = new Color(0.165f, 0.165f, 0.192f);
        private static readonly Color Line = new Color(0.227f, 0.227f, 0.267f);
        private static readonly Color Dim = new Color(0.545f, 0.545f, 0.588f);
        private static readonly Color Accent = new Color(0.310f, 0.765f, 0.969f);

        /// <summary>The POC's curated native-tag order — these lead the section,
        /// the rest of the live schema follows alphabetically.</summary>
        public static readonly string[] NativeTagOrder =
        {
            "VisualElement", "Label", "Button", "ScrollView", "TextField", "Toggle", "Slider",
        };

        private const string LibraryHint =
            "Drag onto a JSX row: top edge inserts before, bottom edge after, middle nests "
            + "inside. Drag hooks onto BODY; style/util modules onto a card (adds the import). "
            + "Drag existing rows to reorder.";

        private readonly List<Entry> _entries = new List<Entry>();
        private VisualElement _listHost;
        private string _filter = "";
        private Action<string, string> _insert;

        public async void Attach(
            VisualElement container, Action<string, string> insertSnippet, Action onNewFile = null)
        {
            _insert = insertSnippet;
            container.Clear();

            container.style.backgroundColor = new Color(0.137f, 0.137f, 0.161f);

            var titleRow = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    justifyContent = Justify.SpaceBetween,
                    backgroundColor = Panel2,
                    paddingLeft = 12f, paddingRight = 12f,
                    paddingTop = 7f, paddingBottom = 7f,
                    borderBottomWidth = 1f, borderBottomColor = Line,
                },
            };
            titleRow.Add(new Label("LIBRARY")
            {
                style = { fontSize = 11f, color = Dim },
            });
            if (onNewFile != null)
            {
                var newBtn = new Label("+ new")
                {
                    tooltip = "create a component / style module / hook module",
                    style =
                    {
                        fontSize = 10f,
                        color = new Color(0.839f, 0.839f, 0.863f),
                        backgroundColor = Panel2,
                        borderTopWidth = 1f, borderBottomWidth = 1f,
                        borderLeftWidth = 1f, borderRightWidth = 1f,
                        borderTopColor = Line, borderBottomColor = Line,
                        borderLeftColor = Line, borderRightColor = Line,
                        borderTopLeftRadius = 3f, borderTopRightRadius = 3f,
                        borderBottomLeftRadius = 3f, borderBottomRightRadius = 3f,
                        paddingLeft = 7f, paddingRight = 7f,
                        paddingTop = 1f, paddingBottom = 1f,
                    },
                };
                newBtn.RegisterCallback<PointerDownEvent>(_ => onNewFile());
                newBtn.RegisterCallback<MouseEnterEvent>(_ =>
                {
                    newBtn.style.borderTopColor = Accent;
                    newBtn.style.borderBottomColor = Accent;
                    newBtn.style.borderLeftColor = Accent;
                    newBtn.style.borderRightColor = Accent;
                });
                newBtn.RegisterCallback<MouseLeaveEvent>(_ =>
                {
                    newBtn.style.borderTopColor = Line;
                    newBtn.style.borderBottomColor = Line;
                    newBtn.style.borderLeftColor = Line;
                    newBtn.style.borderRightColor = Line;
                });
                titleRow.Add(newBtn);
            }
            container.Add(titleRow);

            var search = new TextField
            {
                style = { marginTop = 8f, marginLeft = 8f, marginRight = 8f, marginBottom = 8f },
            };
            search.textEdition.placeholder = "search library…";
            search.RegisterValueChangedCallback(e =>
            {
                _filter = e.newValue ?? "";
                Rebuild();
            });
            container.Add(search);

            var scroll = new ScrollView { style = { flexGrow = 1f, paddingLeft = 8f, paddingRight = 8f } };
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
                        if (body?["attributes"] is JArray schemaAttrs)
                        {
                            var infos = new List<BuilderSchemaCache.AttrInfo>();
                            foreach (var a in schemaAttrs)
                                infos.Add(new BuilderSchemaCache.AttrInfo(
                                    a.Value<string>("name") ?? "",
                                    a.Value<string>("type") ?? ""));
                            BuilderSchemaCache.Register(prop.Name, infos);
                        }
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
                || (e.Section == "Hooks" && e.Name.EndsWith(" (module)", StringComparison.Ordinal)));
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
                                Name = export + " (module)",
                                Description = node.FilePath,
                                Snippet = export + "()",
                                Section = "Hooks",
                                Payload = "hook:" + export,
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
            "Native elements", "Custom components", "Hooks",
            "Style modules", "Util modules", "Directives",
        };

        /// <summary>POC ordering: sections in POC order, the seven POC native
        /// tags first inside their section, hook modules after the ambient
        /// hooks, everything else alphabetical.</summary>
        private static int RankWithin(Entry entry)
        {
            if (entry.Section == "Native elements")
            {
                int index = Array.IndexOf(NativeTagOrder, entry.Name.Trim('<', '>'));
                return index < 0 ? NativeTagOrder.Length : index;
            }
            if (entry.Section == "Hooks")
                return entry.Name.EndsWith(" (module)", StringComparison.Ordinal) ? 1 : 0;
            return 0;
        }

        private void SortSections()
        {
            _entries.Sort((a, b) =>
            {
                int sa = Array.IndexOf(s_sectionOrder, a.Section);
                int sb = Array.IndexOf(s_sectionOrder, b.Section);
                if (sa != sb)
                    return sa.CompareTo(sb);
                int ra = RankWithin(a);
                int rb = RankWithin(b);
                if (ra != rb)
                    return ra.CompareTo(rb);
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

        private static string PayloadFor(Entry entry)
        {
            if (!string.IsNullOrEmpty(entry.Payload))
                return entry.Payload;
            string bare = entry.Name.Trim('<', '>');
            switch (entry.Section)
            {
                case "Native elements":
                    return "element:" + bare;
                case "Custom components":
                    return "component:" + bare;
                case "Hooks":
                case "Hook modules":
                    return "hook:" + bare;
                case "Style modules":
                    return "stylemod:" + bare;
                case "Util modules":
                    return "utilmod:" + bare;
                default:
                    return "snippet:" + entry.Snippet;
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
                    _listHost.Add(new Label(section.ToUpperInvariant())
                    {
                        style =
                        {
                            marginTop = 10f, marginBottom = 5f,
                            color = Dim,
                            fontSize = 10f,
                            letterSpacing = 1f,
                        },
                    });
                }
                var row = new Label(entry.Name)
                {
                    tooltip = entry.Description,
                    style =
                    {
                        fontSize = 12f,
                        color = entry.Tint,
                        backgroundColor = Panel2,
                        borderTopWidth = 1f, borderBottomWidth = 1f,
                        borderLeftWidth = 1f, borderRightWidth = 1f,
                        borderTopColor = Line, borderBottomColor = Line,
                        borderLeftColor = Line, borderRightColor = Line,
                        borderTopLeftRadius = 5f, borderTopRightRadius = 5f,
                        borderBottomLeftRadius = 5f, borderBottomRightRadius = 5f,
                        paddingLeft = 9f, paddingRight = 9f,
                        paddingTop = 4f, paddingBottom = 4f,
                        marginBottom = 4f,
                    },
                };
                var captured = entry;
                row.RegisterCallback<PointerDownEvent>(_ =>
                    BuilderDragService.Arm(PayloadFor(captured)));
                row.RegisterCallback<PointerUpEvent>(_ =>
                {
                    if (BuilderDragService.Active && BuilderDragService.IsQuickClick)
                        _insert?.Invoke(captured.Snippet, captured.Section);
                    BuilderDragService.Cancel();
                });
                row.RegisterCallback<MouseEnterEvent>(_ =>
                {
                    row.style.borderTopColor = Accent;
                    row.style.borderBottomColor = Accent;
                    row.style.borderLeftColor = Accent;
                    row.style.borderRightColor = Accent;
                });
                row.RegisterCallback<MouseLeaveEvent>(_ =>
                {
                    row.style.borderTopColor = Line;
                    row.style.borderBottomColor = Line;
                    row.style.borderLeftColor = Line;
                    row.style.borderRightColor = Line;
                });
                _listHost.Add(row);
            }

            _listHost.Add(new Label(LibraryHint)
            {
                style =
                {
                    fontSize = 10.5f,
                    color = Dim,
                    marginTop = 10f,
                    marginBottom = 8f,
                    whiteSpace = WhiteSpace.Normal,
                },
            });
        }
    }
}
#endif
