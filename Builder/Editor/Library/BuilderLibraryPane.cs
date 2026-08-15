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

        /// <summary>POC HOOK_TEMPLATES — the curated four that lead the HOOKS
        /// section, followed by the tree's own hook modules.</summary>
        public static readonly string[] HookTemplates =
        {
            "useState", "useEffect", "useMemo", "useRef",
        };

        private const string LibraryHint =
            "Drag onto a JSX row: top edge inserts before, bottom edge after, middle nests "
            + "inside. Drag hooks onto BODY; style/util modules onto a card (adds the import). "
            + "Drag existing rows to reorder.";

        private readonly List<Entry> _entries = new List<Entry>();
        private VisualElement _listHost;
        private string _filter = "";

        public async void Attach(VisualElement container, Action onNewFile = null)
        {
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
                    flexShrink = 0f,
                    // POC ".pane-title { padding: 7px 12px }" measures a 30px band
                    // plus its 1px rule; pinned because Unity's font metrics run
                    // taller than Segoe UI at the same padding.
                    height = 31f,
                    paddingLeft = 12f, paddingRight = 12f,
                    paddingTop = 0f, paddingBottom = 0f,
                    borderBottomWidth = 1f, borderBottomColor = Line,
                },
            };
            titleRow.Add(new Label("LIBRARY")
            {
                // POC ".pane-title { letter-spacing: .08em }".
                style = { fontSize = 11f, color = Dim, letterSpacing = 1f },
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
                BuilderCursor.Set(newBtn, UnityEditor.MouseCursor.Link);
                newBtn.RegisterCallback<PointerDownEvent>(evt =>
                {
                    BuilderSearchMenu.RememberPointer(
                        new Vector2(evt.position.x, evt.position.y),
                        UnityEditor.EditorWindow.focusedWindow);
                    onNewFile();
                });
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
                style = { marginTop = 8f, marginBottom = 8f, flexShrink = 0f },
            };
            search.textEdition.placeholder = "search library…";
            BuilderPreviewPane.StyleInput(search);
            // POC "#lib-search { padding: 4px 8px; font-size: 12px }" — the knobs
            // ground is shared, the box metrics are not.
            search.style.paddingTop = 4f;
            search.style.paddingBottom = 4f;
            search.style.paddingLeft = 8f;
            search.style.paddingRight = 8f;
            search.style.fontSize = 12f;
            search.RegisterValueChangedCallback(e =>
            {
                _filter = e.newValue ?? "";
                Rebuild();
            });
            // POC buildLibrary writes "#lib-search" as the FIRST CHILD of "#lib-body",
            // and "#lib-body { overflow-y: auto }" is the scroller — so the search box
            // scrolls away with the list instead of being pinned above it. The rows go
            // in a sibling host so a rebuild never removes (and unfocuses) the field.
            var scroll = new ScrollView { style = { flexGrow = 1f, paddingLeft = 8f } };
            // The POC pane shows no editor chrome and its pills keep the full 8px
            // gutter; Unity's default scroller is a light control laid out INSIDE
            // the pane, which stole 13px from every row.
            BuilderWindow.StyleScrollers(scroll);
            // Yoga resolves an absolutely positioned child's "right" against the
            // parent's CONTENT box, so the 8px right padding pulled the overlay
            // thumb 8px INSIDE the list and it painted over every pill's right
            // border (the POC pane never shows scrollbar chrome over a pill). The
            // gutter moves onto the content container instead, which leaves the
            // thumb in the empty 8px lane the POC keeps between pill and pane edge.
            scroll.contentContainer.style.paddingRight = 8f;
            scroll.contentContainer.Add(search);
            var rowsHost = new VisualElement();
            scroll.contentContainer.Add(rowsHost);
            _listHost = rowsHost;
            container.Add(scroll);

            // POC buildLibrary() seeds HOOK_TEMPLATES before it ever looks at the
            // tree, so the HOOKS section exists whatever the language server says.
            // Sourcing them from RequestHooks() alone made the whole section vanish
            // whenever the LSP answered with nothing.
            foreach (string hook in HookTemplates)
                _entries.Add(new Entry
                {
                    Name = hook,
                    Description = "hook template",
                    Snippet = hook + "()",
                    Section = "Hooks",
                    Tint = HookTint,
                });
            SortSections();
            Rebuild();

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
                    foreach (var entry in _entries)
                        if (entry.Section == "Hooks")
                            seen.Add(entry.Name);
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
            "Style modules", "Util modules",
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
            {
                if (entry.Name.EndsWith(" (module)", StringComparison.Ordinal))
                    return 100;
                int template = Array.IndexOf(HookTemplates, entry.Name);
                return template < 0 ? 50 : template;
            }
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

        private static Label SectionHeader(string section) =>
            new Label(section.ToUpperInvariant())
            {
                style =
                {
                    marginTop = 10f, marginBottom = 5f,
                    color = Dim,
                    fontSize = 10f,
                    letterSpacing = 1f,
                },
            };

        private void Rebuild()
        {
            if (_listHost == null)
                return;
            _listHost.Clear();
            string section = null;
            // POC buildLibrary() emits all five headers unconditionally, in order,
            // whether or not the tree contributes a row to any of them — an empty
            // section still tells you the drawer exists. Filtering hides headers.
            if (_filter.Length == 0)
                section = "";
            foreach (var entry in _entries)
            {
                if (_filter.Length > 0
                    && entry.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                // POC NATIVE ELEMENTS is a curated seven and HOOK_TEMPLATES a
                // curated four (plus the tree's own "(module)" hooks); the rest of
                // the live schema/registry stays behind the search field.
                if (_filter.Length == 0
                    && entry.Section == "Native elements"
                    && Array.IndexOf(NativeTagOrder, entry.Name.Trim('<', '>')) < 0)
                    continue;
                if (_filter.Length == 0
                    && entry.Section == "Hooks"
                    && !entry.Name.EndsWith(" (module)", StringComparison.Ordinal)
                    && Array.IndexOf(HookTemplates, entry.Name) < 0)
                    continue;
                if (entry.Section != section)
                {
                    if (_filter.Length == 0)
                    {
                        int from = Array.IndexOf(s_sectionOrder, section) + 1;
                        int upto = Array.IndexOf(s_sectionOrder, entry.Section);
                        for (int s = Math.Max(from, 0); s < upto; s++)
                            _listHost.Add(SectionHeader(s_sectionOrder[s]));
                    }
                    section = entry.Section;
                    _listHost.Add(SectionHeader(section));
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
                        // POC ".lib-item" inherits body's 1.45 line box: 12px text
                        // becomes a 17.4px line, so the box is 28px tall, not the
                        // 25px UI Toolkit's line-height-less Label produces.
                        height = 28f,
                        unityTextAlign = TextAnchor.MiddleLeft,
                        flexShrink = 0f,
                        // The legacy dynamic-Font `unityFont` slot is ignored by
                        // UI Toolkit's text engine — only a FontDefinition takes.
                        unityFontDefinition = BuilderCanvasDrawing.MonoFontDefinition,
                    },
                };
                var captured = entry;
                // POC ".lib-item" is draggable="true" ONLY — there is no click
                // handler on the library body, so a misjudged click never mutates
                // the buffer at a position the user did not choose.
                row.RegisterCallback<PointerDownEvent>(_ =>
                    BuilderDragService.Arm(PayloadFor(captured)));
                row.RegisterCallback<PointerUpEvent>(_ => BuilderDragService.Cancel());
                // POC ".lib-item { cursor: grab }".
                BuilderCursor.Set(row, UnityEditor.MouseCursor.Pan);
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

            if (_filter.Length == 0)
            {
                for (int s = Array.IndexOf(s_sectionOrder, section) + 1;
                    s < s_sectionOrder.Length;
                    s++)
                    _listHost.Add(SectionHeader(s_sectionOrder[s]));
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
