#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// The RUITK Builder window shell: hosts the workspace (sessions + save/abort),
    /// intercepts the keys Unity routes globally (Ctrl+S never reaches
    /// <see cref="SaveChanges"/> on its own; Ctrl+Z must stay off Unity's global
    /// undo — the builder's undo is session-scoped), and carries the
    /// serialized state that survives external domain reloads.
    ///
    /// The panes (library / canvas / preview / inspector) mount into the named
    /// containers as their features land; the shell owns lifecycle only.
    /// </summary>
    public sealed class BuilderWindow : EditorWindow
    {
        [SerializeField] private BuilderWorkspace _workspace = new BuilderWorkspace();
        [SerializeField] private string _focusFile = string.Empty;


        private Label _statusLabel;
        private Label _previewName;
        private Label _sourceName;

        // UB-40: one mapping table for the layer select — labels, zoom presets
        // and the active index can never drift apart.
        private static readonly string[] s_layerLabels =
            { "Layer 1 — Architecture", "Layer 2 — Cards", "Layer 3 — Edit" };
        private static readonly float[] s_layerZooms = { 0.30f, 0.75f, 1.25f };
        [System.NonSerialized] private DropdownField _layerSelect;
        [System.NonSerialized] private BuilderCanvasHost _canvasHost;
        [System.NonSerialized] private BuilderLibraryPane _libraryPane;
        [System.NonSerialized] private CodeField _codeField;
        [System.NonSerialized] private BuilderPreviewPane _previewPane;
        [System.NonSerialized] private BuilderPreviewCompiler _previewCompiler;
        [System.NonSerialized] private double _recompileDue;
        [System.NonSerialized] private bool _recompileScheduled;

        public BuilderWorkspace Workspace => _workspace;

        /// <summary>Brings an ALREADY-OPEN builder window forward without
        /// creating one. UB-92: an inline editor needs its host window focused
        /// for the keyboard to reach it, and a menu pick leaves Unity focused on
        /// whatever window was in front before the popup.</summary>
        internal static void FocusExisting(bool focusRoot = false)
        {
            var windows = Resources.FindObjectsOfTypeAll<BuilderWindow>();
            if (windows == null || windows.Length == 0)
                return;
            var window = windows[0];
            window.Focus();
            // Focusing the WINDOW is not enough for shortcuts: a KeyDownEvent is
            // dispatched to the focused ELEMENT, and closing an inline editor
            // leaves none. The root takes it back so Ctrl+Z reaches OnKeyDown
            // rather than falling through to Unity's global undo.
            if (focusRoot)
                window.rootVisualElement?.Focus();
        }

        public static BuilderWindow OpenEmpty()
        {
            var window = GetWindow<BuilderWindow>();
            window.titleContent = new GUIContent("RUITK Builder");
            window.minSize = new Vector2(900f, 560f);
            window.Show();
            return window;
        }

        public static BuilderWindow OpenFor(string uitkxFilePath)
        {
            var window = OpenEmpty();
            window._focusFile = uitkxFilePath;
            window._workspace.Open(uitkxFilePath);
            window.MountCanvas();
            window.RefreshChrome();
            return window;
        }

        private void OnEnable()
        {
            _workspace.Changed -= OnWorkspaceChanged;
            _workspace.Changed += OnWorkspaceChanged;
            BuilderLspService.DiagnosticsPublished -= OnLspDiagnosticsPublished;
            BuilderLspService.DiagnosticsPublished += OnLspDiagnosticsPublished;
            BuilderAssetEvents.UitkxImported -= OnUitkxImported;
            BuilderAssetEvents.UitkxImported += OnUitkxImported;
            // Sessions just deserialized across the domain reload; any file
            // that changed externally WHILE the old domain was alive missed its
            // import event, so sweep once — the panes mount after this and
            // read the adopted buffers.
            var openPaths = new System.Collections.Generic.List<string>();
            foreach (var session in _workspace.Sessions)
                openPaths.Add(session.FilePath);
            if (openPaths.Count > 0)
                _workspace.ReloadCleanFromDisk(openPaths);
            saveChangesMessage = "The RUITK Builder has unsaved component edits.";
        }

        private void OnDisable()
        {
            _workspace.Changed -= OnWorkspaceChanged;
            BuilderLspService.DiagnosticsPublished -= OnLspDiagnosticsPublished;
            BuilderAssetEvents.UitkxImported -= OnUitkxImported;
            _canvasHost?.Unmount();
            _canvasHost = null;
            _previewPane?.Dispose();
            _previewPane = null;
            _previewCompiler?.Dispose();
            _previewCompiler = null;
        }

        /// <summary>External .uitkx changes (a sync, a git pull, an IDE edit):
        /// clean sessions adopt the new disk text and every touched card, the
        /// source pane and the preview refresh. Dirty sessions keep the user's
        /// unsaved buffer.</summary>
        private void OnUitkxImported(System.Collections.Generic.List<string> fullPaths)
        {
            if (_workspace == null || fullPaths == null)
                return;
            var changed = _workspace.ReloadCleanFromDisk(fullPaths);
            if (changed.Count == 0)
                return;
            bool focusChanged = false;
            string focusFull = string.IsNullOrEmpty(_focusFile) ? "" : Path.GetFullPath(_focusFile);
            foreach (string path in changed)
            {
                _canvasHost?.RefreshGraph(path, ReadBufferOrDisk);
                if (string.Equals(path, focusFull, System.StringComparison.OrdinalIgnoreCase))
                    focusChanged = true;
            }
            if (focusChanged)
            {
                var session = _workspace.TryGet(focusFull);
                if (session != null && _sourceSnapshot == null)
                    _codeField?.SetContent(session.BufferText, _focusFile, KnownElementsOrNull());
                ScheduleServerTokens();
            }
            RefreshChrome();
        }

        /// <summary>UB-06: the server's published diagnostics reach the source
        /// pane. Only the focus file's list is shown; the CodeField keeps the
        /// Roslyn (CS####) entries and drops the UITKX tiers it already
        /// computes locally.</summary>
        private void OnLspDiagnosticsPublished(string path, Newtonsoft.Json.Linq.JToken diagnostics)
        {
            if (_codeField == null || string.IsNullOrEmpty(_focusFile)
                || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(_focusFile),
                    System.StringComparison.OrdinalIgnoreCase))
                return;
            var overlay = new System.Collections.Generic.List<(string, int, string)>();
            if (diagnostics is Newtonsoft.Json.Linq.JArray items)
            {
                foreach (var item in items)
                {
                    string code = item.Value<string>("code") ?? "";
                    string message = item.Value<string>("message") ?? "";
                    int line0 = item["range"]?["start"]?.Value<int>("line") ?? 0;
                    overlay.Add((code, line0 + 1, message));
                }
            }
            _codeField.SetOverlayDiagnostics(overlay);
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexGrow = 1f;
            // UB-30: the drag ghost chip lives on the window root so it can
            // travel from the library across every pane.
            BuilderDragService.GhostRoot = root;
            // UB-76: the floating inline editor anchors on canvas elements but
            // lives at the window root, above every pane.
            _inlineEditor.Attach(root);
            // "-unity-font-definition" is an inherited property, so the POC's
            // proportional face is pinned ONCE here and cascades to the toolbar,
            // the library, the preview strip, the legend and the footer hint.
            // Code-bearing surfaces re-pin the mono face over it as they already do.
            var uiFont = BuilderCanvasDrawing.UiFontDefinition;
            if (uiFont.font != null)
                root.style.unityFontDefinition = uiFont;

            var toolbar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    flexShrink = 0f,
                    // POC "#toolbar { padding: 8px 12px }" measures 40px of fill
                    // plus its 1px rule; Unity's font metrics add ~3px over Segoe
                    // at the same padding, so the band is pinned instead.
                    height = 41f,
                    paddingTop = 0f,
                    paddingBottom = 0f,
                    paddingLeft = 12f,
                    paddingRight = 12f,
                    backgroundColor = BuilderPalette.Panel,
                    borderBottomWidth = 1f,
                    borderBottomColor = BuilderPalette.Line,
                },
            };
            toolbar.Add(new Label("RUITK Visual Editor")
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    color = BuilderPalette.Accent,
                    marginRight = 8f,
                },
            });
            _statusLabel = new Label
            {
                // POC "#toolbar { gap: 8px }" + ".sep { margin: 0 4px }" put 12px
                // between a separator and each neighbour; the label carries the 4
                // the separator's own margin no longer supplies on this side.
                style =
                {
                    unityTextAlign = TextAnchor.MiddleLeft, fontSize = 11f, color = BuilderPalette.Dim,
                    marginRight = 4f,
                },
            };
            toolbar.Add(_statusLabel);
            toolbar.Add(Separator());
            _layerSelect = new DropdownField(
                new System.Collections.Generic.List<string>(s_layerLabels), 1);
            _layerSelect.RegisterValueChangedCallback(evt =>
            {
                int index = System.Array.IndexOf(s_layerLabels, evt.newValue);
                if (index >= 0)
                    _canvasHost?.SetViewPreset(s_layerZooms[index]);
            });
            _layerSelect.style.minWidth = 190f;
            _layerSelect.style.marginLeft = 2f;
            _layerSelect.style.marginRight = 2f;
            _layerSelect.style.alignSelf = Align.Center;
            toolbar.Add(_layerSelect);
            toolbar.Add(Separator());
            toolbar.Add(ToolbarButton("Import .uxml…", ImportUxml));
            toolbar.Add(ToolbarButton("History", ToggleHistory));
            toolbar.Add(ToolbarButton("? How to drive it", ToggleHelp));
            // DOCUMENTED DEVIATION (owner-decided, do not re-flag): the POC has no
            // Save/Abort because it never writes a file — every "commit" is mock.
            // Ours are real disk writes, so the two buttons stay, behind their own
            // separator at the end of the run so the POC's button silhouette up to
            // "? How to drive it" is unchanged. Ctrl+S mirrors Save; Abort has no
            // keyboard route, which is why it cannot simply move off the bar.
            toolbar.Add(Separator());
            toolbar.Add(ToolbarButton("Save", SaveAll));
            toolbar.Add(ToolbarButton("Abort", AbortAll));
            toolbar.Add(BuildLegend());
            root.Add(toolbar);
            SetActiveMode(1);

            // POC "#library { flex: 0 0 205px; border-right: 1px solid var(--line) }"
            // and initSplitters(), which wires exactly TWO handles: canvas|right and
            // preview|source. A third draggable handle at the library boundary is a
            // pane the POC does not have, so the library is a fixed column.
            var body = new VisualElement
            {
                name = "builder-body",
                style = { flexGrow = 1f, flexDirection = FlexDirection.Row, minHeight = 0f },
            };
            body.Add(new VisualElement
            {
                name = "builder-library",
                style =
                {
                    flexGrow = 0f, flexShrink = 0f, width = 205f, minHeight = 0f,
                    borderRightWidth = 1f, borderRightColor = BuilderPalette.Line,
                },
            });
            var innerSplit = new TwoPaneSplitView(1, 440f, TwoPaneSplitViewOrientation.Horizontal)
            {
                style = { flexGrow = 1f },
            };
            var canvasPane = new VisualElement { name = "builder-canvas", style = { minWidth = 300f } };
            // POC "#canvasWrap { cursor: grab }".
            BuilderCursor.Set(canvasPane, MouseCursor.Pan);
            innerSplit.Add(canvasPane);
            innerSplit.Add(new VisualElement { name = "builder-side", style = { minWidth = 280f } });
            body.Add(innerSplit);
            root.Add(body);
            StyleSplitter(innerSplit, vertical: false);

            // POC "#hint" writes "&nbsp;•&nbsp;" between clauses; UI Toolkit's
            // whiteSpace:Normal collapses a plain double space back to one, so the
            // separator carries NO-BREAK SPACE (U+00A0) on each side exactly like
            // the POC's entity.
            const string Bullet = " \u00a0•\u00a0 ";
            var footer = new Label(
                "Wheel: zoom (Ctrl+wheel over a scrolling section)" + Bullet
                + "Drag Library items onto rows (top=before, bottom=after, "
                + "middle=inside) or BODY (hooks); drag rows to reorder" + Bullet + "Right-click rows / "
                + "cards / canvas for typed attributes, directives, delete, create" + Bullet + "L2: click "
                + "attrs / badges / style entries to edit" + Bullet + "Source pane: edit → apply "
                + "re-parses" + Bullet + "Drag splitters to resize")
            {
                style =
                {
                    flexShrink = 0f,
                    fontSize = 11f,
                    color = BuilderPalette.Dim,
                    backgroundColor = BuilderPalette.Panel,
                    paddingLeft = 12f,
                    paddingRight = 12f,
                    paddingTop = 5f,
                    paddingBottom = 5f,
                    // POC "#hint { padding: 5px 12px; font-size: 11px }" inside the
                    // body's 1.45 line box: 11 * 1.45 = 15.95 line box + the 5px
                    // padding pair + the 1px rule = 26.95. UITK sizes border-box, so
                    // the whole 26.95 is the declared height; without it the bar laid
                    // out at UITK's natural ~12px line and came up ~5px short.
                    minHeight = 26.95f,
                    unityTextAlign = TextAnchor.MiddleLeft,
                    whiteSpace = WhiteSpace.Normal,
                    borderTopWidth = 1f,
                    borderTopColor = BuilderPalette.Line,
                },
            };
            root.Add(footer);

            // TrickleDown: the keys must be consumed before Unity's global routes
            // see them (Ctrl+S -> Save Project, Ctrl+Z -> global Undo).
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            // A KeyDownEvent is dispatched to the FOCUSED element, and nothing in
            // the canvas is focusable — so every window shortcut only ever fired
            // while a TextField happened to hold focus, which is exactly when the
            // field wants the key for itself. Owner report 2026-08-17: Ctrl+Z/Y
            // and Delete did nothing at all. The root is now focusable and takes
            // focus whenever a click lands on something that does not want it, so
            // the shortcuts have a route; a text surface that DID take focus
            // keeps it and OnKeyDown steps aside for it.
            root.focusable = true;
            // TrickleDown here too, and the decision is made from the EVENT
            // TARGET rather than from who holds focus now: canvas rows
            // StopPropagation on pointer-down, so a bubble-phase handler would
            // never see the very clicks that select the thing Delete acts on.
            root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target is VisualElement target && IsTypingSurface(target))
                    return;
                root.Focus();
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<AttachToPanelEvent>(
                _ => root.schedule.Execute(() =>
                {
                    if (!TypingTargetFocused())
                        root.Focus();
                }).ExecuteLater(60));

            MountCanvas();
            RefreshChrome();
        }

        private void MountCanvas()
        {
            if (string.IsNullOrEmpty(_focusFile))
            {
                // UB-113: opening the builder from the menu used to mount
                // nothing at all — an empty window whose only hint pointed back
                // at the Project view. The empty state is now the way IN.
                ShowEmptyState();
                return;
            }
            var container = rootVisualElement?.Q("builder-canvas");
            if (container == null)
                return;
            _canvasHost?.Unmount();
            _canvasHost = new BuilderCanvasHost();
            _canvasHost.ZoomChanged = zoom =>
            {
                SetActiveMode(BuilderCanvasHost.LodOf(zoom));
                _canvasHost?.RestyleScrollers();
            };
            _canvasHost.OnRowClick = OnCanvasRowClicked;
            _canvasHost.OnRowContext = OnCanvasRowContext;
            _canvasHost.OnRowDrop = OnCanvasRowDrop;
            _canvasHost.OnStyleAddEntry = OnStyleAddEntry;
            _canvasHost.OnToast = Toast;
            _canvasHost.OnSelect = index =>
            {
                var nodes = _canvasHost?.Nodes;
                if (nodes != null && index >= 0 && index < nodes.Count)
                    OpenFileFromCanvas(nodes[index].FilePath);
            };
            _canvasHost.OnAddHook = path =>
            {
                string full = Path.GetFullPath(path);
                OpenSession(full);
                var node = _canvasHost?.FindNode(full);
                int chipIndex = node?.Body.Count ?? 0;
                int cardIndex = _canvasHost?.NodeIndexOf(full) ?? -1;
                InsertBeforeLastReturn(full, "  var (value, setValue) = useState(0);", "new hook");
                if (cardIndex >= 0)
                    _canvasHost?.WithCanvasElement(
                        $"chip-{cardIndex}-{chipIndex}",
                        anchor => ShowLineEditor(
                            full, LineOfNewHook(full, chipIndex),
                            "var (value, setValue) = useState(0);", "", anchor));
            };
            // UB-117: "+ code" seeds a plain statement in the body and opens it
            // for editing, so custom logic no longer requires the source pane.
            // It rides the exact path "+ hook" uses — the body is one list of
            // statement lines and a hook call is just one of them.
            _canvasHost.OnAddCode = path =>
            {
                string full = Path.GetFullPath(path);
                EditSession(full);
                var node = _canvasHost?.FindNode(full);
                int chipIndex = node?.Body.Count ?? 0;
                int cardIndex = _canvasHost?.NodeIndexOf(full) ?? -1;
                const string seed = "var someThing = \"\";";
                InsertBeforeLastReturn(full, "  " + seed, "new code");
                if (cardIndex >= 0)
                    _canvasHost?.WithCanvasElement(
                        $"chip-{cardIndex}-{chipIndex}",
                        anchor => ShowLineEditor(
                            full, LineOfNewHook(full, chipIndex), seed, "", anchor));
            };
            _canvasHost.OnEditAttrValue = ShowAttrValueEditor;
            // Editing an EXISTING badge cancels only the edit; there is no
            // seeding gesture behind it to undo.
            _canvasHost.OnEditDirective =
                (path, line, seed, anchor) => ShowDirectiveEditor(path, line, seed, anchor);
            _canvasHost.OnEditLine = ShowLineEditor;
            _canvasHost.OnEditIsland = ShowIslandEditor;
            // POC openNameMenu: a name entry is a title + placeholder-only input +
            // an inline error LINE + a persistent "Create" row — an invalid name
            // writes into the error line and the menu STAYS OPEN.
            _canvasHost.OnAddStyleExport = path =>
                BuilderSearchMenu.ShowNamePrompt(
                    "new style export", "styleName",
                    name => !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][A-Za-z0-9]*$")
                        ? "camelCase identifier required"
                        : StyleExportExists(path, name) ? name + " already exists" : null,
                    name => AppendToFile(path,
                        "\nexport Style " + name + " = new Style {\n  FlexGrow = 1,\n};",
                        "style " + name));
            _canvasHost.OnAddUtilExport = path =>
                BuilderSearchMenu.ShowSimple("add export", new System.Collections.Generic.List<BuilderSearchMenu.Item>
                {
                    new BuilderSearchMenu.Item
                    {
                        Label = "New function…",
                        OnPick = () => BuilderSearchMenu.ShowNamePrompt(
                            "new exported function", "FunctionName",
                            name => System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Z][A-Za-z0-9]*$")
                                ? null : "PascalCase function name required",
                            name => AppendToFile(path,
                                "\nexport int " + name + "(int value) {\n  return value;\n}",
                                "util " + name)),
                    },
                    new BuilderSearchMenu.Item
                    {
                        Label = "New value…",
                        OnPick = () => BuilderSearchMenu.ShowNamePrompt(
                            "new exported value", "ValueName",
                            name => System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Z][A-Za-z0-9]*$")
                                ? null : "PascalCase value name required",
                            name => AppendToFile(
                                path, "\nexport int " + name + " = 0;", "value " + name)),
                    },
                });
            // UB-88: a delete is an EDIT, and edits do not reach disk until Save
            // (VE-D2). This used to call AssetDatabase straight away, which is
            // how a keypress destroyed sample files the user never saved. It now
            // only marks the intent, so Abort forgets it and Ctrl+Z un-marks it —
            // the asset is never re-created, so no GUID ever churns.
            _canvasHost.OnDeleteFile = path =>
            {
                if (!_workspace.MarkForDeletion(path))
                {
                    Toast("Can't delete " + Path.GetFileName(path) + " (read-only)");
                    return;
                }
                _ledger.Begin("delete " + Path.GetFileName(path));
                _ledger.RecordDeletion(path);
                _ledger.End();
                RefreshHistoryPanel();
                Toast("Deleted " + Path.GetFileName(path) + " - applies on Save");
                RefreshChrome();
                MountCanvas();
            };
            _canvasHost.IsFileHidden = path => _workspace.IsPendingDelete(path);
            _canvasHost.PendingNewFiles = () => _workspace.PendingNewFiles;
            _canvasHost.OnCreateRequested = ShowCreatePrompt;
            _canvasHost.OnTraceStates = states => _codeField?.SetTraceNames(states);
            // The side panes are built BEFORE the canvas mounts. Mount() is an
            // async method that only suspends at its first real await, so a warm
            // LSP client plus a cached tree ran the whole body — including the
            // onGraphLoaded callback — synchronously, while _previewPane and
            // _libraryPane were still null. The null-conditional calls below then
            // silently no-opped and the module note kept the "no component imports
            // it yet" fallback for the rest of the session even though the graph
            // had the signature and the consumer edge.
            MountPreview();
            MountLibrary();
            _canvasHost.Mount(
                container, _focusFile, OpenFileFromCanvas, ReadBufferOrDisk,
                graph =>
                {
                    _libraryPane?.SetWorkspaceEntries(graph);
                    _previewPane?.RefreshModuleNotes();
                    _codeField?.SetKnownElements(KnownElementsOrNull());
                });
        }

        /// <summary>UB-07: the element set for custom-tag colouring and the
        /// tier-2 unknown-element check — schema natives plus every export in
        /// the live graph, mirroring the LSP's BuildProjectElements. Null until
        /// BOTH sources are live, which suppresses UITKX0105/0109 instead of
        /// storming false errors during startup (same discipline as the LSP's
        /// initial-scan gate).</summary>
        private System.Collections.Generic.HashSet<string> KnownElementsOrNull()
        {
            var nodes = _canvasHost?.Nodes;
            if (!BuilderSchemaCache.HasSchema || nodes == null || nodes.Count == 0)
                return null;
            var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (string element in BuilderSchemaCache.ElementNames)
                set.Add(element);
            // UB-75: the schema is the tooling's view, the registry is what
            // actually renders. Any element the runtime can mount is legal
            // markup, so schema drift may cost completion but must never
            // manufacture a UITKX0105 for a tag that works.
            foreach (string element in Ruitk.Elements.ElementRegistryProvider
                .GetDefaultRegistry().RegisteredNames)
                set.Add(element);
            // Only COMPONENT exports are legal tags — feeding style/util export
            // names in would make <BgDeep /> pass the unknown-element check.
            foreach (var node in nodes)
            {
                if (node.Kind != BuilderNodeKind.Component)
                    continue;
                foreach (string export in node.Exports)
                    set.Add(export);
                if (!string.IsNullOrEmpty(node.Title))
                    set.Add(node.Title);
            }
            return set;
        }

        /// <summary>UB-113: the start screen. A builder opened with no tree can
        /// begin one here; the modules live in memory until Save, which is when
        /// it asks where they belong (there is no folder to infer one from).</summary>
        private void ShowEmptyState()
        {
            var container = rootVisualElement?.Q("builder-canvas");
            if (container == null)
                return;
            _canvasHost?.Unmount();
            _canvasHost = null;
            container.Clear();
            var centre = new VisualElement
            {
                style =
                {
                    position = Position.Absolute,
                    top = 0f, left = 0f, right = 0f, bottom = 0f,
                    alignItems = Align.Center,
                    justifyContent = Justify.Center,
                },
            };
            centre.Add(new Label("Start a UI")
            {
                style =
                {
                    fontSize = 22f, color = BuilderPalette.Text, marginBottom = 6f,
                    unityFontStyleAndWeight = FontStyle.Bold,
                },
            });
            centre.Add(new Label("Nothing is written to disk until you press Save.")
            {
                style = { fontSize = 12f, color = BuilderPalette.Dim, marginBottom = 18f },
            });
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, marginBottom = 18f },
            };
            foreach (var (kind, label) in s_startKinds)
            {
                string captured = kind;
                var button = ToolbarButton(label, () => CreateModule(captured, 60f, 40f));
                button.style.marginRight = 8f;
                button.style.paddingTop = 5f;
                button.style.paddingBottom = 5f;
                row.Add(button);
            }
            centre.Add(row);
            centre.Add(new Label(
                "…or right-click a .uitkx asset in the Project window and choose "
                + "\"Open in RUITK UI Builder\" to edit an existing tree.")
            {
                style =
                {
                    fontSize = 11f, color = BuilderPalette.Dim,
                    whiteSpace = WhiteSpace.Normal, maxWidth = 420f,
                    unityTextAlign = TextAnchor.MiddleCenter,
                },
            });
            container.Add(centre);
        }

        private static readonly (string Kind, string Label)[] s_startKinds =
        {
            ("Component", "New component"),
            ("Style", "New style module"),
            ("Hooks", "New hook module"),
            ("Utils", "New util module"),
        };

        private void MountLibrary()
        {
            var container = rootVisualElement?.Q("builder-library");
            if (container == null)
                return;
            if (_libraryPane != null)
                return;
            container.Clear();
            _libraryPane = new BuilderLibraryPane();
            _libraryPane.SchemaLoaded =
                () => _codeField?.SetKnownElements(KnownElementsOrNull());
            _libraryPane.FocusComponent = path =>
            {
                if (_canvasHost != null && !_canvasHost.FocusNode(path))
                    Toast("That module has no card on this canvas.");
            };
            // POC btnLibNew → openCreateMenu at the button: "+ new" opens the same
            // four-item create menu the empty-canvas right-click does.
            _libraryPane.Attach(container, NewFile);
        }

        private void MountPreview()
        {
            var container = rootVisualElement?.Q("builder-side");
            if (container == null || string.IsNullOrEmpty(_focusFile))
                return;
            if (_previewPane == null)
            {
                container.Clear();
                container.style.backgroundColor = BuilderPalette.Panel;
                // POC "#preview { overflow: auto; padding: 12px }" — a tall render
                // or a long knobs list scrolls INSIDE the pane; a plain container
                // clipped both against the bottom of the frame with no way out.
                var previewSection = new ScrollView(ScrollViewMode.Vertical)
                {
                    style =
                    {
                        flexGrow = 1f, minHeight = 0f,
                        paddingTop = 12f, paddingBottom = 12f,
                        paddingLeft = 12f, paddingRight = 12f,
                    },
                };
                StyleScrollers(previewSection);
                var codeSection = new VisualElement { style = { flexGrow = 1f } };
                var previewPane = new VisualElement { style = { minHeight = 120f, minWidth = 0f } };
                previewPane.Add(PaneTitle("LIVE PREVIEW", out _previewName));
                previewPane.Add(previewSection);
                var sourcePane = new VisualElement { style = { minHeight = 120f } };
                _editButton = MiniButton(
                    "edit", "edit the text; apply re-parses it back into the model", BeginSourceEdit);
                _applyButton = MiniButton("apply (Ctrl+Enter)", "re-parse the edited text", ApplySourceEdit);
                _cancelButton = MiniButton("cancel (Esc)", "discard the edit", CancelSourceEdit);
                _applyButton.style.display = DisplayStyle.None;
                _cancelButton.style.display = DisplayStyle.None;
                sourcePane.Add(PaneTitle(
                    "SOURCE — .UITKX", out _sourceName, _editButton, _applyButton, _cancelButton));
                sourcePane.Add(codeSection);
                // POC "#preview { flex: 0 0 380px }" sizes the BODY, not the pane:
                // on poc-l1-cards.png the preview title is 41..71 and its body
                // 72..451. The fixed dimension here covers the whole pane, so the
                // 31px title band is added on top of the POC's 380.
                var sideSplit = new TwoPaneSplitView(0, 411f, TwoPaneSplitViewOrientation.Vertical)
                {
                    style = { flexGrow = 1f },
                };
                sideSplit.Add(previewPane);
                sideSplit.Add(sourcePane);
                container.Add(sideSplit);
                StyleSplitter(sideSplit, vertical: true);

                _previewPane = new BuilderPreviewPane();
                _previewPane.UsageProvider = UsageFor;
                _previewPane.ModuleInfoProvider = ModuleInfoFor;
                _previewPane.ComponentPicked += OnPreviewComponentPicked;
                _previewPane.Attach(previewSection);
                _codeField = new CodeField();
                _codeField.TextEdited += OnCodeEdited;
                _codeField.CompletionProvider = RequestCompletions;
                _codeField.EditRequested += BeginSourceEdit;
                _codeField.ApplyRequested += ApplySourceEdit;
                _codeField.CancelRequested += CancelSourceEdit;
                codeSection.Add(_codeField);
            }
            var session = _workspace.TryGet(_focusFile);
            if (_previewName != null)
                _previewName.text = "<" + Path.GetFileNameWithoutExtension(_focusFile)
                    .Replace(".style", "").Replace(".hooks", "").ToUpperInvariant() + ">";
            if (_sourceName != null)
                _sourceName.text = Path.GetFileName(_focusFile).ToUpperInvariant();
            _previewPane.ShowFile(_focusFile, session?.BufferText, null);
            _codeField.SetContent(session?.BufferText ?? "", _focusFile, KnownElementsOrNull());
            _codeField.SetEditable(session != null && !session.IsReadOnly);
            // POC selectNode(): opening another file leaves source-edit mode.
            _codeField.SetEditing(_sourceSnapshot != null);
            SyncLspBuffer(_focusFile, session?.BufferText, open: true);
            ScheduleServerTokens();
        }

        [System.NonSerialized] private Label _editButton;
        [System.NonSerialized] private Label _applyButton;
        [System.NonSerialized] private Label _cancelButton;
        [System.NonSerialized] private string _sourceSnapshot;

        /// <summary>POC source-pane edit mode: "edit" snapshots the buffer so
        /// "cancel (Esc)" restores it, and "apply (Ctrl+Enter)" runs the parser —
        /// a failure toasts "Parse failed: …" and turns the field's border red.
        /// The live re-parse stays on (that is our real-behaviour divergence from
        /// the POC's read-only render), so edit/apply is the commit gesture.</summary>
        private void BeginSourceEdit()
        {
            var session = _workspace.TryGet(_focusFile);
            if (session == null || session.IsReadOnly)
            {
                Toast("Read-only file");
                return;
            }
            _sourceSnapshot = session.BufferText;
            SetSourceEditing(true);
            _codeField?.FocusEditor();
        }

        private void ApplySourceEdit()
        {
            if (_sourceSnapshot == null)
                return;
            string text = _codeField?.TextLf ?? "";
            var parsed = BuilderLanguage.Parse(text, _focusFile);
            string failure = null;
            foreach (var diagnostic in parsed.Diagnostics)
            {
                if (diagnostic.Severity == Ruitk.Language.ParseSeverity.Error)
                {
                    failure = diagnostic.Message;
                    break;
                }
            }
            if (failure != null)
            {
                _codeField?.SetError(true);
                Toast("Parse failed: " + failure);
                return;
            }
            _codeField?.SetError(false);
            _sourceSnapshot = null;
            SetSourceEditing(false);
            ScheduleCanvasRefresh(_focusFile);
            NotifyBufferChanged();
        }

        private void CancelSourceEdit()
        {
            if (_sourceSnapshot == null)
                return;
            string restore = _sourceSnapshot;
            _sourceSnapshot = null;
            _codeField?.SetError(false);
            SetSourceEditing(false);
            var session = _workspace.TryGet(_focusFile);
            if (session != null && !session.IsReadOnly)
            {
                string before = session.BufferText;
                session.ApplyEdit(restore);
                _ledger.Record(_focusFile, before, restore);
                _codeField?.SetContent(restore, _focusFile, KnownElementsOrNull());
                RefreshChrome();
                ScheduleCanvasRefresh(_focusFile);
                NotifyBufferChanged();
            }
        }

        private void SetSourceEditing(bool editing)
        {
            if (_editButton != null)
                _editButton.style.display = editing ? DisplayStyle.None : DisplayStyle.Flex;
            if (_applyButton != null)
                _applyButton.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            if (_cancelButton != null)
                _cancelButton.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
            // POC enterSrcEdit/cancelSrcEdit swap the rendered listing for the
            // plain textarea and back; the pane is read-only until "edit".
            _codeField?.SetEditing(editing);
        }

        [System.NonSerialized]
        private readonly System.Collections.Generic.HashSet<string> _lspOpened =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        private async void SyncLspBuffer(string path, string textLf, bool open)
        {
            if (string.IsNullOrEmpty(path) || textLf == null)
                return;
            try
            {
                var client = await BuilderLspService.GetOrStartAsync();
                if (open && _lspOpened.Add(Path.GetFullPath(path)))
                    client.DidOpen(path, textLf);
                else
                    client.DidChangeDebounced(path, textLf);
            }
            catch (System.Exception)
            {
                // LSP-less sessions still edit and save; completions just stay empty.
            }
        }

        private async System.Threading.Tasks.Task<System.Collections.Generic.List<(string, string)>>
            RequestCompletions(int line0, int char0)
        {
            var results = new System.Collections.Generic.List<(string, string)>();
            var session = _workspace.TryGet(_focusFile);
            if (session == null)
                return results;
            var client = await BuilderLspService.GetOrStartAsync();
            client.SendDidChangeNow(_focusFile, session.BufferText);
            var response = await client.RequestCompletion(_focusFile, line0, char0);
            ParseCompletionItems(response, results);
            return results;
        }

        [System.NonSerialized] private double _serverTokensDue;
        [System.NonSerialized] private bool _serverTokensScheduled;

        /// <summary>The source pane's C# body colouring comes from the LSP's
        /// semanticTokens/full (UITKX structural + Roslyn), requested quietly
        /// ~400 ms after the buffer settles. The legend is SemanticTokenTypes
        /// .All — the same table this process links, so indices decode locally.</summary>
        private void ScheduleServerTokens()
        {
            _serverTokensDue = EditorApplication.timeSinceStartup + 0.4;
            if (_serverTokensScheduled)
                return;
            _serverTokensScheduled = true;
            EditorApplication.update += RequestServerTokensWhenQuiet;
        }

        private async void RequestServerTokensWhenQuiet()
        {
            if (EditorApplication.timeSinceStartup < _serverTokensDue)
                return;
            EditorApplication.update -= RequestServerTokensWhenQuiet;
            _serverTokensScheduled = false;
            var session = _workspace.TryGet(_focusFile);
            if (session == null || _codeField == null)
                return;
            string text = session.BufferText;
            try
            {
                var client = await BuilderLspService.GetOrStartAsync();
                client.SendDidChangeNow(_focusFile, text);
                var response = await client.RequestSemanticTokens(_focusFile);
                var data = response?["data"] as Newtonsoft.Json.Linq.JArray
                    ?? response?["Data"] as Newtonsoft.Json.Linq.JArray;
                if (data == null)
                    return;
                var legend = Ruitk.Language.SemanticTokens.SemanticTokenTypes.All;
                var decoded = new System.Collections.Generic.List<
                    Ruitk.Language.SemanticTokens.SemanticTokenData>(data.Count / 5);
                int line = 0, column = 0;
                for (int i = 0; i + 4 < data.Count; i += 5)
                {
                    int deltaLine = (int)data[i];
                    int deltaStart = (int)data[i + 1];
                    int length = (int)data[i + 2];
                    int typeIndex = (int)data[i + 3];
                    line += deltaLine;
                    column = deltaLine > 0 ? deltaStart : column + deltaStart;
                    if (typeIndex < 0 || typeIndex >= legend.Length)
                        continue;
                    decoded.Add(new Ruitk.Language.SemanticTokens.SemanticTokenData
                    {
                        Line = line,
                        Column = column,
                        Length = length,
                        TokenType = legend[typeIndex],
                        Modifiers = System.Array.Empty<string>(),
                    });
                }
                _codeField.SetServerTokens(decoded.ToArray(), text);
            }
            catch (System.Exception)
            {
                // LSP-less sessions keep the local structural colouring.
            }
        }

        [System.NonSerialized] private readonly BuilderActionLedger _ledger = new BuilderActionLedger();

        private void UndoAction()
        {
            var entry = _ledger.Undo();
            if (entry == null)
            {
                Toast("Nothing to undo");
                return;
            }
            // Reverse order: a gesture that inserted then re-indented the same
            // region unwinds in the order it was written.
            var writes = new System.Collections.Generic.List<(string, string)>();
            bool remounted = false;
            for (int i = entry.Changes.Count - 1; i >= 0; i--)
            {
                var change = entry.Changes[i];
                if (change.IsDeletion)
                {
                    // The file never left the disk, so undoing a delete is just
                    // dropping the intent — the card comes straight back.
                    remounted |= _workspace.UnmarkForDeletion(change.FilePath);
                    continue;
                }
                if (change.IsCreation)
                {
                    // Nothing was written, so undoing a create is closing the
                    // never-saved session. Its text is kept on the entry so redo
                    // can re-open it unchanged.
                    var pending = _workspace.TryGet(change.FilePath);
                    if (pending != null)
                        change.After = pending.BufferText;
                    remounted |= _workspace.DiscardNew(change.FilePath);
                    continue;
                }
                writes.Add((change.FilePath, change.Before));
            }
            ApplyLedgerWrites(writes, "Undo " + entry.Description, remounted);
        }

        private void RedoAction()
        {
            var entry = _ledger.Redo();
            if (entry == null)
            {
                Toast("Nothing to redo");
                return;
            }
            var writes = new System.Collections.Generic.List<(string, string)>();
            bool remounted = false;
            foreach (var change in entry.Changes)
            {
                if (change.IsDeletion)
                {
                    remounted |= _workspace.MarkForDeletion(change.FilePath);
                    continue;
                }
                if (change.IsCreation)
                {
                    remounted |= _workspace.CreateNew(change.FilePath, change.After ?? "") != null;
                    continue;
                }
                writes.Add((change.FilePath, change.After));
            }
            ApplyLedgerWrites(writes, "Redo " + entry.Description, remounted);
        }

        /// <summary>Writes a ledger step's buffers back with recording OFF, then
        /// re-syncs every surface the edit path normally touches. Read-only
        /// sessions are skipped rather than throwing — a package file cannot be
        /// in the ledger, but the guard is the same last line of defense the
        /// edit path uses.</summary>
        private void ApplyLedgerWrites(
            System.Collections.Generic.List<(string FilePath, string Text)> writes, string label,
            bool remountCanvas = false)
        {
            if (writes.Count == 0 && !remountCanvas)
                return;
            if (remountCanvas)
            {
                RefreshChrome();
                RefreshHistoryPanel();
                MountCanvas();
                Toast(label);
                if (writes.Count == 0)
                    return;
            }
            using (_ledger.Suppress())
            {
                foreach (var (filePath, text) in writes)
                {
                    var session = _workspace.TryGet(filePath);
                    if (session == null || session.IsReadOnly)
                        continue;
                    session.ApplyEdit(text);
                    SyncLspBuffer(filePath, text, open: false);
                    _canvasHost?.RefreshGraph(filePath, ReadBufferOrDisk);
                    if (string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(_focusFile),
                            System.StringComparison.OrdinalIgnoreCase))
                        _codeField?.SetContent(text, _focusFile, KnownElementsOrNull());
                }
            }
            RefreshChrome();
            NotifyBufferChanged();
            RefreshHistoryPanel();
            Toast(label);
        }

        private void RefreshEditedBuffer(BuilderDocumentSession session)
        {
            _codeField?.SetContent(session.BufferText, session.FilePath, KnownElementsOrNull());
            RefreshChrome();
            NotifyBufferChanged();
        }

        private void OnCodeEdited(string bufferLf)
        {
            var session = _workspace.TryGet(_focusFile);
            if (session == null || session.IsReadOnly)
                return;
            string before = session.BufferText;
            session.ApplyEdit(bufferLf);
            _ledger.Record(_focusFile, before, bufferLf);
            SyncLspBuffer(_focusFile, bufferLf, open: false);
            RefreshChrome();
            NotifyBufferChanged();
            ScheduleCanvasRefresh(_focusFile);
        }

        [System.NonSerialized] private double _canvasRefreshDue;
        [System.NonSerialized] private bool _canvasRefreshScheduled;
        [System.NonSerialized] private string _canvasRefreshFile;

        /// <summary>POC step 12 (source pane is bidirectional): typing in the
        /// source re-parses into the model and the CARD updates. Debounced on the
        /// same 300 ms quiet window as the preview recompile.</summary>
        private void ScheduleCanvasRefresh(string filePath)
        {
            _canvasRefreshFile = filePath;
            _canvasRefreshDue = EditorApplication.timeSinceStartup + 0.3;
            if (_canvasRefreshScheduled)
                return;
            _canvasRefreshScheduled = true;
            EditorApplication.update += RefreshCanvasWhenQuiet;
        }

        private void RefreshCanvasWhenQuiet()
        {
            if (EditorApplication.timeSinceStartup < _canvasRefreshDue)
                return;
            EditorApplication.update -= RefreshCanvasWhenQuiet;
            _canvasRefreshScheduled = false;
            if (!string.IsNullOrEmpty(_canvasRefreshFile))
                _canvasHost?.RefreshGraph(_canvasRefreshFile, ReadBufferOrDisk);
        }

        /// <summary>POC 6.2: clicking a JSX row focuses its file and scrolls the
        /// source pane to that line (selected).</summary>
        private void OnCanvasRowClicked(string filePath, int sourceLine)
        {
            string full = Path.GetFullPath(filePath);
            if (!string.Equals(full, Path.GetFullPath(_focusFile), System.StringComparison.OrdinalIgnoreCase))
                OpenFileFromCanvas(full);
            _codeField?.FocusLine(sourceLine);
        }

        /// <summary>POC 6.4A: the row context menu — typed attributes from the
        /// schema, directive wraps, add child, delete — all landing as text
        /// edits on the row's source lines through the session pipeline.</summary>
        private void OnCanvasRowContext(string filePath, int sourceLine, int rowIdx)
        {
            string full = Path.GetFullPath(filePath);
            var node = _canvasHost?.FindNode(full);
            if (node == null || rowIdx < 0 || rowIdx >= node.Markup.Count)
                return;
            var row = node.Markup[rowIdx];
            int cardIndex = _canvasHost?.NodeIndexOf(full) ?? -1;

            if (row.Kind == BuilderCardLineKind.Directive)
            {
                var headItems = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
                AddDirectiveHeadItems(headItems, full, node, row, cardIndex, rowIdx);
                BuilderSearchMenu.ShowSimple(
                    (row.BadgeText + " " + row.Text).TrimEnd(), headItems);
                return;
            }

            string tag = row.Text.Trim('<', '>');
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>
            {
                new BuilderSearchMenu.Item
                {
                    Label = "Add attribute (typed)…",
                    OnPick = () => ShowAttributeMenu(full, sourceLine, tag, row, rowIdx),
                },
                new BuilderSearchMenu.Item
                {
                    Label = "Add child element…",
                    OnPick = () => ShowAddChildMenu(full, row, tag),
                },
            };
            if (!string.IsNullOrEmpty(row.AttrsText))
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "Remove attribute…",
                    OnPick = () => ShowRemoveAttributeMenu(full, sourceLine, row.AttrsText),
                });
            items.Add(BuilderSearchMenu.Separator);
            // UB-95: one entry, not five siblings crowding the row menu.
            items.Add(new BuilderSearchMenu.Item
            {
                Label = "Wrap in…",
                OnPick = () =>
                {
                    var wraps = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
                    AddWrapItems(wraps, full, node, row, cardIndex, rowIdx);
                    BuilderSearchMenu.ShowSimple("wrap <" + tag + "> in", wraps);
                },
            });
            if (rowIdx != BuilderCanvasDrawing.FirstElementRow(node))
            {
                items.Add(BuilderSearchMenu.Separator);
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "Delete element",
                    OnPick = () => DeleteElementRow(full, row),
                });
            }
            BuilderSearchMenu.ShowSimple("<" + tag + ">", items);
        }

        /// <summary>§8.1 UI semantics per schema controlFlow name: true = offered
        /// as a wrap on element rows; false = a clause name that rides its
        /// construct head's menu. A schema name missing here trips the drift
        /// warning — the builder must never silently trail the language again.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, bool> s_directiveSupport =
            new System.Collections.Generic.Dictionary<string, bool>(System.StringComparer.Ordinal)
            {
                ["if"] = true, ["foreach"] = true, ["for"] = true,
                ["while"] = true, ["switch"] = true,
                ["else"] = false, ["case"] = false, ["default"] = false,
            };
        private static bool s_directiveDriftChecked;

        private static void WarnOnDirectiveDrift()
        {
            if (s_directiveDriftChecked || BuilderSchemaCache.ControlFlow == null)
                return;
            s_directiveDriftChecked = true;
            foreach (string name in BuilderSchemaCache.ControlFlow)
                if (!s_directiveSupport.ContainsKey(name))
                    UnityEngine.Debug.LogWarning(
                        "[RUITK Builder] schema controlFlow directive '" + name
                        + "' has no builder support — wrap/clause menus will not offer it.");
        }

        /// <summary>Wrap offers on an element row. Loops are array-valued
        /// (Func&lt;VirtualNode[]&gt;) and illegal where a single node is required
        /// (UITKX0025), so the return ROOT row only offers the node-valued
        /// constructs.
        /// <para>UB-72: every header is seeded with a COMPILABLE literal rather
        /// than a placeholder identifier ("condition", "count"), which the
        /// compiler could only ever reject — the loud preview then reported
        /// CS1525 on a wrap the user had not finished typing. @while seeds
        /// FALSE deliberately: a true-seeded render loop would not
        /// terminate.</para></summary>
        private void AddWrapItems(
            System.Collections.Generic.List<BuilderSearchMenu.Item> items,
            string full, BuilderCanvasNode node, BuilderCardLine row, int cardIndex, int rowIdx)
        {
            WarnOnDirectiveDrift();
            bool isRoot = rowIdx == BuilderCanvasDrawing.FirstElementRow(node);
            items.Add(new BuilderSearchMenu.Item
            {
                Label = "@if",
                OnPick = () => WrapRowInDirective(full, row, "@if (true)", cardIndex, rowIdx),
            });
            items.Add(new BuilderSearchMenu.Item
            {
                Label = "@switch",
                OnPick = () => WrapRowInSwitch(full, row, cardIndex),
            });
            if (isRoot)
            {
                items.Add(BuilderSearchMenu.SectionHeader(
                    "loops yield arrays — illegal on the root (UITKX0025)"));
                return;
            }
            items.Add(new BuilderSearchMenu.Item
            {
                Label = "@foreach…",
                OnPick = () => ShowForeachWrapMenu(full, node, row, cardIndex, rowIdx),
            });
            items.Add(new BuilderSearchMenu.Item
            {
                Label = "@for",
                OnPick = () => WrapRowInDirective(
                    full, row, "@for (int i = 0; i < 1; i++)", cardIndex, rowIdx),
            });
            items.Add(new BuilderSearchMenu.Item
            {
                Label = "@while",
                OnPick = () => WrapRowInDirective(full, row, "@while (false)", cardIndex, rowIdx),
            });
        }

        /// <summary>Context menu of a directive head row: header edit, clause
        /// adds on the construct head, unwrap only where unwrap is well-defined
        /// (single clause), block delete, and clause delete on continuations.</summary>
        private void AddDirectiveHeadItems(
            System.Collections.Generic.List<BuilderSearchMenu.Item> items,
            string full, BuilderCanvasNode node, BuilderCardLine row, int cardIndex, int rowIdx)
        {
            bool hasHeader = row.BadgeText != "@else" && row.BadgeText != "@default";
            if (hasHeader)
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "Edit " + row.BadgeText.TrimStart('@') + " header",
                    OnPick = () =>
                    {
                        if (cardIndex >= 0)
                            _canvasHost?.WithCanvasElement(
                                $"row-{cardIndex}-{rowIdx}",
                                anchor => ShowDirectiveEditor(
                                    full, row.DirectiveLine, row.DirectiveText, anchor));
                    },
                });

            if (row.ClauseIndex > 0)
            {
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "Delete clause",
                    OnPick = () => DeleteClause(full, row),
                });
                return;
            }

            if (row.BadgeText == "@if")
            {
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "Add @else if",
                    OnPick = () => AddIfClause(full, row, cardIndex, withCondition: true),
                });
                if (!ConstructHasClause(node, rowIdx, "@else"))
                    items.Add(new BuilderSearchMenu.Item
                    {
                        Label = "Add @else",
                        OnPick = () => AddIfClause(full, row, cardIndex, withCondition: false),
                    });
            }
            if (row.BadgeText == "@switch")
            {
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "Add @case…",
                    OnPick = () => AddSwitchClause(full, node, rowIdx, cardIndex, isDefault: false),
                });
                if (!ConstructHasClause(node, rowIdx, "@default"))
                    items.Add(new BuilderSearchMenu.Item
                    {
                        Label = "Add @default",
                        OnPick = () => AddSwitchClause(full, node, rowIdx, cardIndex, isDefault: true),
                    });
            }
            items.Add(BuilderSearchMenu.Separator);
            if (row.BadgeKind != 14 && IsSingleClauseConstruct(node, rowIdx))
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "Remove directive (unwrap)",
                    OnPick = () => RemoveDirectiveBlock(full, row.DirectiveLine),
                });
            items.Add(new BuilderSearchMenu.Item
            {
                Label = "Delete block",
                OnPick = () => DeleteLinesInFile(
                    full, row.DirectiveLine, System.Math.Max(row.DirectiveLine, row.CloseLine),
                    "delete " + row.BadgeText + " block"),
            });
        }

        /// <summary>True when a continuation clause with the given keyword
        /// follows the construct head at <paramref name="headIdx"/>. Clause rows
        /// of an @if sit at the head's depth; @switch cases sit one deeper.
        /// Nested constructs live deeper still and are skipped.</summary>
        private static bool ConstructHasClause(BuilderCanvasNode node, int headIdx, string keyword) =>
            ConstructClause(node, headIdx, keyword) != null;

        /// <summary>The construct's continuation clause carrying the given
        /// keyword, or null. UB-71 needs the clause ROW (for its source line),
        /// not just its existence, so the walk lives here and the boolean is a
        /// thin wrapper over it.</summary>
        private static BuilderCardLine ConstructClause(
            BuilderCanvasNode node, int headIdx, string keyword)
        {
            var head = node.Markup[headIdx];
            int clauseDepth = head.BadgeKind == 14 ? head.Depth + 1 : head.Depth;
            for (int i = headIdx + 1; i < node.Markup.Count; i++)
            {
                var r = node.Markup[i];
                if (r.Depth > clauseDepth)
                    continue;
                if (r.Depth == clauseDepth
                    && r.Kind == BuilderCardLineKind.Directive && r.ClauseIndex > 0)
                {
                    if (r.BadgeText == keyword)
                        return r;
                    continue;
                }
                break;
            }
            return null;
        }

        /// <summary>The lowest non-negative integer no @case arm of this switch
        /// already uses, so a seeded arm is always a legal distinct label. Arms
        /// the user has retyped to non-integer labels simply do not constrain
        /// it — a duplicate is impossible either way.</summary>
        private static int NextCaseLabel(BuilderCanvasNode node, int headIdx)
        {
            var head = node.Markup[headIdx];
            int clauseDepth = head.BadgeKind == 14 ? head.Depth + 1 : head.Depth;
            var used = new System.Collections.Generic.HashSet<int>();
            for (int i = headIdx + 1; i < node.Markup.Count; i++)
            {
                var r = node.Markup[i];
                if (r.Depth > clauseDepth)
                    continue;
                if (r.Depth == clauseDepth
                    && r.Kind == BuilderCardLineKind.Directive && r.ClauseIndex > 0)
                {
                    // A @case row carries its keyword in BadgeText and its label
                    // (with the trailing colon) in Text — "@case 0:" arrives as
                    // BadgeText "@case" + Text "0:".
                    if (r.BadgeText == "@case"
                        && int.TryParse((r.Text ?? "").Trim().TrimEnd(':').Trim(), out int n))
                        used.Add(n);
                    continue;
                }
                break;
            }
            int next = 0;
            while (used.Contains(next))
                next++;
            return next;
        }

        private static bool IsSingleClauseConstruct(BuilderCanvasNode node, int headIdx)
        {
            var head = node.Markup[headIdx];
            int clauseDepth = head.BadgeKind == 14 ? head.Depth + 1 : head.Depth;
            for (int i = headIdx + 1; i < node.Markup.Count; i++)
            {
                var r = node.Markup[i];
                if (r.Depth > clauseDepth)
                    continue;
                if (r.Depth == clauseDepth
                    && r.Kind == BuilderCardLineKind.Directive && r.ClauseIndex > 0)
                    return false;
                break;
            }
            return true;
        }

        /// <summary>Adds an @else / @else if clause at the construct's final
        /// close: "}" becomes "} @else … {" with a fresh "}" after it. The new
        /// clause is empty and renders as its own head row. The close line must
        /// actually BE a lone closer — a mis-tracked CloseLine must bail, never
        /// overwrite user markup.</summary>
        private void AddIfClause(string filePath, BuilderCardLine head, int cardIndex, bool withCondition)
        {
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly || head.CloseLine <= 0)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int closeIdx = head.CloseLine - 1;
            if (closeIdx < 0 || closeIdx >= lines.Count)
                return;
            if (lines[closeIdx].Trim() != "}")
            {
                Toast("Couldn't locate the block's closing brace — nothing changed.");
                return;
            }
            string indent = BuilderText.LeadingIndent(lines[closeIdx]);
            string header = withCondition ? "@else if (condition)" : "@else";
            lines[closeIdx] = indent + "} " + header + " {";
            lines.Insert(closeIdx + 1, indent + "}");
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines), header);
            if (withCondition)
                BeginEditOnDirectiveLine(filePath, cardIndex, head.CloseLine);
        }

        /// <summary>Adds a @case/@default arm, indented one level in. UB-71: a
        /// new @case lands ABOVE an existing @default, never after it —
        /// appending at the closing brace put every case the user added below
        /// the catch-all, which reads wrong and leaves no way to author a case
        /// above it. @default itself still goes last, which is where it
        /// belongs.</summary>
        private void AddSwitchClause(
            string filePath, BuilderCanvasNode node, int rowIdx, int cardIndex, bool isDefault)
        {
            var head = node.Markup[rowIdx];
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly || head.CloseLine <= 0)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int closeIdx = head.CloseLine - 1;
            if (closeIdx < 0 || closeIdx >= lines.Count)
                return;
            int insertIdx = closeIdx;
            if (!isDefault)
            {
                var fallback = ConstructClause(node, rowIdx, "@default");
                if (fallback != null && fallback.DirectiveLine > 0
                    && fallback.DirectiveLine - 1 > head.DirectiveLine - 1
                    && fallback.DirectiveLine - 1 <= closeIdx)
                    insertIdx = fallback.DirectiveLine - 1;
            }
            string indent = BuilderText.LeadingIndent(lines[closeIdx]) + "  ";
            // UB-72: "@case value:" never compiled — an undefined identifier in
            // a case label. The seed is the next unused integer constant, which
            // compiles against the seeded "@switch (0)" subject and cannot
            // collide with an arm already in the construct (CS0152).
            lines.Insert(insertIdx, indent
                + (isDefault ? "@default:" : "@case " + NextCaseLabel(node, rowIdx) + ":"));
            ApplyProgrammaticEdit(
                filePath, string.Join("\n", lines), isDefault ? "@default" : "@case");
            if (!isDefault)
                BeginEditOnDirectiveLine(filePath, cardIndex, insertIdx + 1);
        }

        /// <summary>Deletes a continuation clause. Colon arms (@case/@default)
        /// are a plain line range. Brace clauses keep the balance: a middle
        /// clause's close line carries the NEXT head (its leading '}' takes over
        /// closing the previous clause); the last clause is replaced by a lone
        /// '}' that closes the previous one.</summary>
        private void DeleteClause(string filePath, BuilderCardLine row)
        {
            if (row.BadgeKind == 15)
            {
                DeleteLinesInFile(
                    filePath, row.DirectiveLine,
                    System.Math.Max(row.DirectiveLine, row.CloseLine),
                    "delete " + row.BadgeText + " clause");
                return;
            }
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int head = row.DirectiveLine - 1;
            int close = row.CloseLine - 1;
            if (head < 0 || close < head || close >= lines.Count)
                return;
            // Brace bookkeeping depends on the head form. Shared head
            // ("} @else {") means this clause's head line carried the PREVIOUS
            // clause's closer; the parser-legal separate-line form ("@else {"
            // on its own line) means the previous clause already closed itself.
            // Inserting the compensating '}' for the separate form — or leaving
            // the next head's leading '}' after deleting a separate middle
            // clause before a shared next head — unbalances the file (review
            // findings, 2026-08-16).
            bool sharedHead = lines[head].TrimStart().StartsWith("}", System.StringComparison.Ordinal);
            if (lines[close].Contains("@"))
            {
                lines.RemoveRange(head, close - head);
                bool nextShared = lines[head].TrimStart()
                    .StartsWith("}", System.StringComparison.Ordinal);
                if (!sharedHead && nextShared)
                    lines[head] = BuilderText.LeadingIndent(lines[head])
                        + lines[head].TrimStart().Substring(1).TrimStart();
            }
            else
            {
                string closeIndent = BuilderText.LeadingIndent(lines[close]);
                lines.RemoveRange(head, close - head + 1);
                if (sharedHead)
                    lines.Insert(head, closeIndent + "}");
            }
            ApplyProgrammaticEdit(
                filePath, string.Join("\n", lines), "delete " + row.BadgeText + " clause");
        }

        /// <summary>Opens the header editor on whichever refreshed row now owns
        /// the given directive-header line — clause adds and wraps cannot know
        /// their row index ahead of the graph rebuild.</summary>
        private void BeginEditOnDirectiveLine(string filePath, int cardIndex, int line1)
        {
            if (cardIndex < 0)
                return;
            string full = Path.GetFullPath(filePath);
            var node = _canvasHost?.FindNode(full);
            if (node == null)
                return;
            for (int i = 0; i < node.Markup.Count; i++)
            {
                var r = node.Markup[i];
                if (r.Kind == BuilderCardLineKind.Directive && r.DirectiveLine == line1)
                {
                    // UB-93: this editor only exists because a wrap or a clause
                    // add just seeded the line, so Escape undoes that seeding —
                    // the ledger's last entry IS the gesture that opened it.
                    _canvasHost?.WithCanvasElement(
                        $"row-{cardIndex}-{i}",
                        anchor => ShowDirectiveEditor(
                            full, r.DirectiveLine, r.DirectiveText, anchor, UndoAction));
                    return;
                }
            }
        }

        /// <summary>UB-03: a loop wrap binds a REAL collection — in-scope
        /// enumerable props (typed, via ruitk/componentProps) and hook-state
        /// locals, with the warn-orange freeform as the escape hatch. The loop
        /// variable is singularised from the pick and collision-checked.</summary>
        private async void ShowForeachWrapMenu(
            string filePath, BuilderCanvasNode node, BuilderCardLine row, int cardIndex, int rowIdx)
        {
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            var taken = LocalNamesOf(node);

            void AddCollection(string expr, string origin)
            {
                if (string.IsNullOrEmpty(expr) || !seen.Add(expr))
                    return;
                string captured = expr;
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = expr + "  —  " + origin,
                    OnPick = () => WrapRowInDirective(
                        filePath, row,
                        "@foreach (var " + LoopVarFor(captured, taken) + " in " + captured + ")",
                        cardIndex, rowIdx),
                });
            }

            if (node.Kind == BuilderNodeKind.Component || node.Kind == BuilderNodeKind.Hook)
            {
                var props = await FetchComponentPropsAsync(node.Title) ?? PropsOf(node);
                foreach (var (name, type) in props)
                    if (LooksEnumerable(type))
                        AddCollection(name, type);
            }
            foreach (var body in node.Body)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    body.SourceText ?? "", @"^\s*var\s+(?:\(([^)]+)\)|(\w+))\s*=");
                if (!m.Success)
                    continue;
                string lhs = m.Groups[2].Success
                    ? m.Groups[2].Value
                    : m.Groups[1].Value.Split(',')[0].Trim();
                AddCollection(lhs, "hook state");
            }
            BuilderSearchMenu.Show(
                "@foreach — collection", "in-scope enumerable…", items,
                free => new BuilderSearchMenu.Item
                {
                    Label = "loop over \"" + free + "\"",
                    OnPick = () => WrapRowInDirective(
                        filePath, row,
                        "@foreach (var " + LoopVarFor(free, taken) + " in " + free + ")",
                        cardIndex, rowIdx),
                });
        }

        private static bool LooksEnumerable(string type)
        {
            if (string.IsNullOrEmpty(type))
                return false;
            return type.Contains("List") || type.Contains("[]") || type.Contains("IEnumerable")
                || type.Contains("Collection") || type.Contains("Dictionary") || type.Contains("Set");
        }

        private static System.Collections.Generic.HashSet<string> LocalNamesOf(BuilderCanvasNode node)
        {
            var names = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (var body in node.Body)
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(body.SourceText ?? "", @"\b\w+\b"))
                    names.Add(m.Value);
            return names;
        }

        private static string LoopVarFor(
            string collection, System.Collections.Generic.HashSet<string> taken)
        {
            string tail = collection;
            int dot = tail.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < tail.Length)
                tail = tail.Substring(dot + 1);
            var sb = new System.Text.StringBuilder();
            foreach (char c in tail)
                if (char.IsLetterOrDigit(c) || c == '_')
                    sb.Append(c);
            tail = sb.ToString();
            string candidate = tail.Length > 1 && tail.EndsWith("s", System.StringComparison.Ordinal)
                ? tail.Substring(0, tail.Length - 1)
                : "item";
            if (candidate.Length > 0 && char.IsUpper(candidate[0]))
                candidate = char.ToLowerInvariant(candidate[0]) + candidate.Substring(1);
            if (candidate.Length == 0 || taken.Contains(candidate))
                candidate = candidate.Length == 0 ? "item" : candidate + "Item";
            return candidate;
        }

        /// <summary>Wrap in @switch seeds the colon form the parser accepts. The
        /// row lands in a @case arm, NOT a @default one (UB-71: seeding @default
        /// first put every case the user went on to add below the catch-all, and
        /// left no way to author one above it) — "Add @default" stays on the menu
        /// and appends last, where C# wants it. The subject and the case label
        /// are seeded as the constant 0 rather than the identifier "value", so
        /// the wrap compiles the instant it is written (UB-72) and the arm always
        /// matches, which keeps the wrapped row on screen while the user edits
        /// the header.</summary>
        private void WrapRowInSwitch(string filePath, BuilderCardLine row, int cardIndex)
        {
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int from = Mathf.Clamp(row.SourceLine - 1, 0, lines.Count - 1);
            int to = Mathf.Clamp(
                (row.EndLine > 0 ? row.EndLine : row.SourceLine) - 1, from, lines.Count - 1);
            string indent = IndentOf(filePath, row.SourceLine);
            for (int i = from; i <= to; i++)
                lines[i] = "      " + lines[i];
            lines.Insert(to + 1, indent + "    );");
            lines.Insert(to + 2, indent + "}");
            lines.Insert(from, indent + "    return (");
            lines.Insert(from, indent + "  @case 0:");
            lines.Insert(from, indent + "@switch (0) {");
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines), "@switch");
            BeginEditOnDirectiveLine(filePath, cardIndex, row.SourceLine);
        }

        /// <summary>Resolves where an "inside" drop on a directive head lands:
        /// the clause's FIRST element row (drops nest into the clause's real
        /// markup — a clause return has ONE root, so siblinging the root would
        /// not compile). False with a user-facing reason when the clause cannot
        /// take the drop safely: a body sitting on its label line (single-line
        /// arms need expanding first) or a @switch head whose arms must be
        /// targeted individually.</summary>
        private static bool TryClauseNestTarget(
            BuilderCanvasNode node, int headIdx,
            out BuilderCardLine target, out string blockReason)
        {
            target = null;
            blockReason = null;
            var head = node.Markup[headIdx];
            for (int i = headIdx + 1; i < node.Markup.Count; i++)
            {
                var r = node.Markup[i];
                if (r.Depth <= head.Depth)
                    break;
                if (r.Kind == BuilderCardLineKind.Directive)
                    continue;
                if (r.SourceLine == head.DirectiveLine)
                {
                    blockReason = "This arm's body sits on its label line — expand it to a block first.";
                    return false;
                }
                target = r;
                return true;
            }
            if (head.BadgeKind == 14)
            {
                blockReason = "Drop into a specific @case arm, not the @switch head.";
                return false;
            }
            return false;
        }

        /// <summary>An element dropped INSIDE a directive clause must land in
        /// the clause's return markup, never at C# statement level: it nests
        /// into the clause's root element, and an EMPTY clause gets its return
        /// scaffold in the same commit.</summary>
        private void InsertIntoClause(string filePath, BuilderCanvasNode node, int headIdx, string tag)
        {
            var head = node.Markup[headIdx];
            if (TryClauseNestTarget(node, headIdx, out var target, out string blockReason))
            {
                InsertChildTag(filePath, target, tag);
                return;
            }
            if (blockReason != null)
            {
                Toast(blockReason);
                return;
            }
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int headLineIdx = head.DirectiveLine - 1;
            if (headLineIdx < 0 || headLineIdx >= lines.Count)
                return;
            string indent = BuilderText.LeadingIndent(lines[headLineIdx]);
            lines.Insert(headLineIdx + 1, indent + "  return (");
            lines.Insert(headLineIdx + 2, indent + "    " + SeededTag(tag));
            lines.Insert(headLineIdx + 3, indent + "  );");
            AddUsageImport(lines, filePath, tag);
            ApplyProgrammaticEdit(
                filePath, string.Join("\n", lines), "<" + tag + "> into " + head.BadgeText);
        }

        /// <summary>POC "Add child element…": a searchable menu of native
        /// elements then the tree's custom components; the pick lands as a
        /// nested tag one indent inside the target row.</summary>
        private void ShowAddChildMenu(string filePath, BuilderCardLine row, string tag)
        {
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>
            {
                BuilderSearchMenu.SectionHeader("native elements"),
            };
            void AddChild(string childTag) => InsertChildTag(filePath, row, childTag);
            // UB-10: the full schema element set, curated order first — the
            // 7-tag NativeTagOrder is a display curation, not a source list.
            var natives = new System.Collections.Generic.List<string>();
            foreach (string element in BuilderLibraryPane.NativeTagOrder)
                if (!BuilderSchemaCache.HasSchema || BuilderSchemaCache.HasElement(element))
                    natives.Add(element);
            if (BuilderSchemaCache.HasSchema)
            {
                var rest = new System.Collections.Generic.List<string>();
                foreach (string element in BuilderSchemaCache.ElementNames)
                    if (!natives.Contains(element))
                        rest.Add(element);
                rest.Sort(System.StringComparer.Ordinal);
                natives.AddRange(rest);
            }
            foreach (string element in natives)
            {
                string captured = element;
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "<" + captured + ">",
                    OnPick = () => AddChild(captured),
                });
            }
            items.Add(BuilderSearchMenu.Separator);
            items.Add(BuilderSearchMenu.SectionHeader("custom components"));
            var graphNode = _canvasHost?.FindNode(filePath);
            foreach (var candidate in _canvasHost?.Nodes
                ?? new System.Collections.Generic.List<BuilderCanvasNode>())
            {
                if (candidate.Kind != BuilderNodeKind.Component || candidate == graphNode)
                    continue;
                foreach (string export in candidate.Exports)
                {
                    string captured = export;
                    items.Add(new BuilderSearchMenu.Item
                    {
                        Label = "<" + captured + ">",
                        OnPick = () => AddChild(captured),
                    });
                }
            }
            BuilderSearchMenu.Show("add child to <" + tag + ">", "search elements…", items);
        }

        private static string SeededTag(string tag) =>
            tag == "Label" ? "<Label text=\"New label\" />"
            : tag == "Button" ? "<Button text=\"Click\" />"
            : "<" + tag + " />";

        /// <summary>POC addChildAt(index = j.children.length): the new tag lands as
        /// the target's LAST child — just before its closing tag — and, when the
        /// tag resolves to a component in the graph, the import is pushed in the
        /// SAME commit ("&lt;Tag&gt; child (import auto-added where needed)").
        /// A self-closing target is re-opened first, because the POC re-serialises
        /// the AST and a childless node simply gains a body.</summary>
        /// <summary>Inserts <paramref name="tag"/> as the target's FIRST child,
        /// directly under its open tag.
        /// <para>UB-110: the canvas lists rows flattened, so the gap under a
        /// container row is visually the gap before its first CHILD — but an
        /// "after" drop there inserted past the container's whole block, which
        /// on a deep tree is hundreds of lines away. The caret said one thing
        /// and the edit did another ("i see the dotted line and it drops it on
        /// the first visualElement"). The owner's model is the one the layout
        /// already implies: hovering a row appends inside it, and the gap
        /// between a row and its first child means "become that first
        /// child".</para></summary>
        private void InsertFirstChildTag(string filePath, BuilderCardLine row, string tag)
        {
            string full = Path.GetFullPath(filePath);
            var session = EditSession(full);
            if (session == null || session.IsReadOnly)
            {
                Toast("Read-only file");
                return;
            }
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int start = Mathf.Clamp(row.SourceLine - 1, 0, Mathf.Max(0, lines.Count - 1));
            // A self-closing target has no inside to be first in; appending is
            // the only meaning available, and it also rewrites "/>" to "> … </>".
            if (row.SelfClosing)
            {
                InsertChildTag(filePath, row, tag);
                return;
            }
            int openEnd = OpenTagEndLine(lines, start);
            int at = Mathf.Clamp(openEnd + 1, 0, lines.Count);
            lines.Insert(at, IndentOf(full, row.SourceLine) + "  " + SeededTag(tag));
            AddUsageImport(lines, full, tag);
            ApplyProgrammaticEdit(
                full, string.Join("\n", lines),
                "<" + tag + "> as first child (import auto-added where needed)");
        }

        private void InsertChildTag(string filePath, BuilderCardLine row, string tag)
        {
            string full = Path.GetFullPath(filePath);
            var session = _workspace.TryGet(full) ?? OpenSession(full);
            if (session == null || session.IsReadOnly)
            {
                Toast("Read-only file");
                return;
            }
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            string indent = IndentOf(full, row.SourceLine);
            string seeded = SeededTag(tag);
            int end = row.EndLine > 0 ? row.EndLine : row.SourceLine;
            if (row.SelfClosing)
            {
                int idx = Mathf.Clamp(end - 1, 0, lines.Count - 1);
                int slash = lines[idx].LastIndexOf("/>", System.StringComparison.Ordinal);
                if (slash < 0)
                    return;
                lines[idx] = lines[idx].Remove(slash, 2).TrimEnd() + ">";
                lines.Insert(idx + 1, indent + "  " + seeded);
                lines.Insert(idx + 2, indent + "</" + row.Text.Trim('<', '>') + ">");
            }
            else
            {
                int at = Mathf.Clamp(end > row.SourceLine ? end - 1 : row.SourceLine, 0, lines.Count);
                lines.Insert(at, indent + "  " + seeded);
            }
            AddUsageImport(lines, full, tag);
            ApplyProgrammaticEdit(
                full, string.Join("\n", lines),
                "<" + tag + "> child (import auto-added where needed)");
        }

        /// <summary>POC addChildAt: a custom tag that resolves to a graph node
        /// pushes its import onto the importer in the same commit, so the new tag
        /// compiles and draws its usage edge immediately.</summary>
        private void AddUsageImport(
            System.Collections.Generic.List<string> lines, string importerPath, string tag)
        {
            var importer = _canvasHost?.FindNode(importerPath);
            var target = _canvasHost?.FindNodeByTitle(tag);
            if (importer == null || target == null || target.Kind != BuilderNodeKind.Component)
                return;
            if (string.Equals(target.FilePath, importerPath, System.StringComparison.OrdinalIgnoreCase))
                return;
            if (AlreadyImports(importer, target.FilePath))
                return;
            string import = BuildImportLine(importerPath, target, false, tag);
            if (import != null)
                lines.Insert(0, import);
        }

        /// <summary>POC 6.6 drop resolution: element/component payloads insert a
        /// seeded tag before/after/inside the target row; hooks land before the
        /// last return; style/util modules add the import line; row moves
        /// relocate the source line range (same file only).</summary>
        private void OnCanvasRowDrop(string filePath, int rowIdx, int band, string payload)
        {
            if (string.IsNullOrEmpty(payload))
                return;
            string full = Path.GetFullPath(filePath);
            var node = _canvasHost?.FindNode(full);
            if (node == null)
                return;
            bool hasRow = rowIdx >= 0 && rowIdx < node.Markup.Count;
            var row = hasRow ? node.Markup[rowIdx] : null;
            var session = _workspace.TryGet(full) ?? OpenSession(full);
            if (session == null || session.IsReadOnly)
            {
                Toast("Read-only file");
                return;
            }

            int colon = payload.IndexOf(':');
            string kind = colon < 0 ? payload : payload.Substring(0, colon);
            string name = colon < 0 ? "" : payload.Substring(colon + 1);
            string indent = hasRow ? IndentOf(full, row.SourceLine) : "  ";
            // POC: the root markup row can never take a sibling — a drop on it
            // always nests inside. §8.1: the root is the first ELEMENT row (a
            // directive head can occupy index 0), and a continuation clause
            // (@else/@case) only ever accepts "inside".
            if (rowIdx == BuilderCanvasDrawing.FirstElementRow(node)
                || (hasRow && row.Kind == BuilderCardLineKind.Directive && row.ClauseIndex > 0))
                band = 1;

            switch (kind)
            {
                case "element":
                case "component":
                {
                    if (kind == "component"
                        && string.Equals(name, node.Title, System.StringComparison.Ordinal))
                    {
                        Toast("A component can't contain itself.");
                        return;
                    }
                    if (node.Markup.Count == 0)
                    {
                        Toast("Drop elements onto a component's markup.");
                        return;
                    }
                    // POC: a drop with no row under the cursor, and every "inside"
                    // band, appends to the target's children — never inserts
                    // straight after the open tag.
                    if (!hasRow)
                    {
                        int rootIdx = BuilderCanvasDrawing.FirstElementRow(node);
                        if (rootIdx < 0)
                        {
                            Toast("Drop elements onto a component's markup.");
                            return;
                        }
                        InsertChildTag(full, node.Markup[rootIdx], name);
                        break;
                    }
                    if (band == 1)
                    {
                        if (row.Kind == BuilderCardLineKind.Directive)
                            InsertIntoClause(full, node, rowIdx, name);
                        else
                            InsertChildTag(full, row, name);
                        break;
                    }
                    // UB-110: the caret under a container row is drawn in the gap
                    // before its first CHILD, because the canvas flattens the
                    // tree. Inserting after the container's whole block there
                    // put the element far below where the caret pointed. When
                    // the next listed row is deeper, "after" means "first
                    // child" — which is exactly what the caret shows.
                    if (band == 2 && rowIdx + 1 < node.Markup.Count
                        && node.Markup[rowIdx + 1].Depth > row.Depth)
                    {
                        InsertFirstChildTag(full, row, name);
                        break;
                    }
                    var siblingLines =
                        new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
                    int at = band == 0 ? BeforeAnchor(row) : AfterAnchor(full, row);
                    siblingLines.Insert(
                        Mathf.Clamp(at, 0, siblingLines.Count), indent + SeededTag(name));
                    AddUsageImport(siblingLines, full, name);
                    ApplyProgrammaticEdit(
                        full, string.Join("\n", siblingLines),
                        "<" + name + "> child (import auto-added where needed)");
                    break;
                }
                case "hook":
                {
                    if (node.Kind == BuilderNodeKind.Style)
                    {
                        Toast("Style modules have no hooks.");
                        return;
                    }
                    // UB-11: declarations come from HookRegistry's single
                    // call-site table — 21 real snippets, not 4 hand-written
                    // ones with a wrong-for-most-hooks fallback. Module hooks
                    // (an export dragged off a .hooks card) keep the generic
                    // call form.
                    string decl = Ruitk.Core.HookRegistry.GetInsertionSnippets()
                        .TryGetValue(name, out string snippet)
                        ? snippet
                        : "var value = " + name + "();";
                    InsertBeforeLastReturn(full, "  " + decl, "hook " + name);
                    break;
                }
                case "stylemod":
                case "utilmod":
                {
                    if (kind == "stylemod" && node.Kind != BuilderNodeKind.Component)
                    {
                        Toast("Style imports go on components.");
                        return;
                    }
                    if (kind == "utilmod"
                        && (node.Kind == BuilderNodeKind.Style || node.Kind == BuilderNodeKind.Util))
                    {
                        Toast("Util imports go on components and hook modules.");
                        return;
                    }
                    var module = _canvasHost.FindNodeByTitle(name);
                    if (module != null && AlreadyImports(node, module.FilePath))
                    {
                        Toast(name + " is already imported.");
                        return;
                    }
                    string import = BuildImportLine(full, module, kind == "stylemod", name);
                    if (import != null)
                        InsertLinesInFile(
                            full, 0, import,
                            (kind == "stylemod" ? "style import " : "util import ") + name);
                    break;
                }
                case "snippet":
                    _codeField?.InsertAtCaret(name);
                    break;
                case "move":
                {
                    // POC drop with no .jsx-row under the cursor but inside the
                    // card: index = children.length — the row relocates to the END
                    // of the ROOT element's children.
                    bool appendToRoot = false;
                    int rootRowIdx = BuilderCanvasDrawing.FirstElementRow(node);
                    if (!hasRow && rootRowIdx >= 0)
                    {
                        var rootRow = node.Markup[rootRowIdx];
                        row = rootRow;
                        rowIdx = rootRowIdx;
                        hasRow = true;
                        appendToRoot = true;
                        indent = IndentOf(full, rootRow.SourceLine) + "  ";
                    }
                    if (!hasRow)
                        break;
                    int split = name.LastIndexOf(':');
                    if (split < 0)
                        break;
                    string srcPath = Path.GetFullPath(name.Substring(0, split));
                    if (!int.TryParse(name.Substring(split + 1), out int srcRowIdx))
                        break;
                    if (!string.Equals(srcPath, full, System.StringComparison.OrdinalIgnoreCase))
                    {
                        Toast("Moving across components isn't in the POC — delete and re-add.");
                        break;
                    }
                    var srcNode = _canvasHost?.FindNode(srcPath);
                    if (srcNode == null || srcRowIdx < 0 || srcRowIdx >= srcNode.Markup.Count
                        || srcRowIdx == rowIdx)
                        break;
                    var srcRow = srcNode.Markup[srcRowIdx];
                    // An "inside" move onto a directive head must land in the
                    // clause's return markup exactly like the insert path — the
                    // raw line range after the clause's ');' is C# statement
                    // level and never compiles (review finding, 2026-08-16).
                    if (row.Kind == BuilderCardLineKind.Directive && band == 1)
                    {
                        if (!TryClauseNestTarget(node, rowIdx, out var clauseTarget, out string why))
                        {
                            Toast(why ?? "This clause is empty — drop a new element into it first.");
                            break;
                        }
                        row = clauseTarget;
                    }
                    // §8.1: a construct head's move carries the WHOLE block —
                    // every clause through the final close. Element rows carry
                    // their own tag span; continuation clauses never arm.
                    int srcFrom = srcRow.DirectiveLine > 0 ? srcRow.DirectiveLine : srcRow.SourceLine;
                    int srcTo = srcRow.Kind == BuilderCardLineKind.Directive
                        ? srcRow.CloseLine
                        : (srcRow.EndLine > 0 ? srcRow.EndLine : srcRow.SourceLine);
                    if (srcTo <= 0)
                        srcTo = srcRow.EndLine > 0 ? srcRow.EndLine : srcRow.SourceLine;
                    if (row.SourceLine >= srcFrom && row.SourceLine <= srcTo)
                    {
                        Toast("Can't move an element into its own subtree.");
                        break;
                    }
                    bool intoSelfClosing = band == 1 && row.SelfClosing;
                    // UB-110, move side: the same caret means the same thing for
                    // a relocation as for an insert — the gap under a container
                    // row is the gap before its first child.
                    bool asFirstChild = band == 2 && !appendToRoot && !row.SelfClosing
                        && rowIdx + 1 < node.Markup.Count
                        && node.Markup[rowIdx + 1].Depth > row.Depth;
                    int destination = appendToRoot
                        ? (row.EndLine > row.SourceLine ? row.EndLine - 1 : row.SourceLine)
                        : asFirstChild
                            ? OpenTagEndLine(
                                new System.Collections.Generic.List<string>(
                                    session.BufferText.Split('\n')),
                                Mathf.Max(0, row.SourceLine - 1)) + 1
                        : band == 0 ? BeforeAnchor(row)
                            : band == 2 || intoSelfClosing ? AfterAnchor(full, row)
                            : (row.EndLine > row.SourceLine ? row.EndLine - 1 : row.SourceLine);
                    MoveLineRange(
                        full, srcFrom, srcTo, destination,
                        appendToRoot
                            ? indent
                            : indent + ((asFirstChild || (band == 1 && !intoSelfClosing)) ? "  " : ""),
                        "moved " + srcRow.Text);
                    break;
                }
            }
        }

        /// <summary>1-based "insert after" anchor for a BEFORE drop on a row —
        /// above the row's directive header when it carries one.</summary>
        private static int BeforeAnchor(BuilderCardLine row) =>
            (row.DirectiveLine > 0 ? row.DirectiveLine : row.SourceLine) - 1;

        /// <summary>1-based "insert after" anchor for an AFTER drop on a row —
        /// below the whole construct for a directive head, and below the LAST
        /// line of a wrapped self-closing tag otherwise.</summary>
        private int AfterAnchor(string filePath, BuilderCardLine row)
        {
            if (row.Kind == BuilderCardLineKind.Directive && row.CloseLine > 0)
                return row.CloseLine;
            return row.EndLine > 0 ? row.EndLine : row.SourceLine;
        }

        /// <summary>Relocates a 1-based inclusive line range to sit after
        /// <paramref name="afterLine1"/> (0 = top), guarding against moving a
        /// range into itself and re-indenting to the destination depth.</summary>
        private void MoveLineRange(
            string filePath, int fromLine1, int toLine1, int afterLine1, string destIndent,
            string what = null)
        {
            if (afterLine1 >= fromLine1 - 1 && afterLine1 <= toLine1)
                return;
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int from = Mathf.Clamp(fromLine1 - 1, 0, lines.Count - 1);
            int to = Mathf.Clamp(toLine1 - 1, from, lines.Count - 1);
            var moved = lines.GetRange(from, to - from + 1);

            string srcIndent = BuilderText.LeadingIndent(moved[0]);
            for (int i = 0; i < moved.Count; i++)
            {
                if (moved[i].StartsWith(srcIndent, System.StringComparison.Ordinal))
                    moved[i] = destIndent + moved[i].Substring(srcIndent.Length);
            }

            lines.RemoveRange(from, to - from + 1);
            int insertAt = afterLine1 > to ? afterLine1 - (to - from + 1) : afterLine1;
            insertAt = Mathf.Clamp(insertAt, 0, lines.Count);
            lines.InsertRange(insertAt, moved);
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines), what);
        }


        /// <summary>POC 6.3 per-attr commit: rewrite ONE attribute's value on
        /// the row's open-tag line; an empty value removes the attribute.</summary>
        private void OnAttrValueEdited(string filePath, int sourceLine, int attrIdx, string newValue)
        {
            // POC commitNode labels: "<Tag> attrName", or "removed attrName" when
            // the emptied value takes the attribute with it.
            string tagName = "";
            string attrName = "";
            var owner = _canvasHost?.FindNode(Path.GetFullPath(filePath));
            if (owner != null)
            {
                foreach (var candidate in owner.Markup)
                {
                    if (candidate.SourceLine != sourceLine)
                        continue;
                    tagName = candidate.Text;
                    if (attrIdx >= 0 && attrIdx < candidate.AttrPairs.Count)
                    {
                        string pair = candidate.AttrPairs[attrIdx];
                        int eq = pair.IndexOf('=');
                        attrName = eq < 0 ? pair : pair.Substring(0, eq);
                    }
                    break;
                }
            }
            string what = string.IsNullOrWhiteSpace(newValue)
                ? "removed " + attrName
                : tagName + " " + attrName;
            EditOpenTagInFile(Path.GetFullPath(filePath), sourceLine, tag =>
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    tag, "(\\w+)=(\\{[^}]*\\}|\"[^\"]*\")");
                if (attrIdx < 0 || attrIdx >= matches.Count)
                {
                    Toast("Couldn't locate that attribute in the source.");
                    return null;
                }
                var m = matches[attrIdx];
                if (string.IsNullOrWhiteSpace(newValue))
                    return tag.Remove(m.Index, m.Length).Replace("  ", " ");
                string oldValue = m.Groups[2].Value;
                bool expr = oldValue.StartsWith("{", System.StringComparison.Ordinal);
                // UB-115: the wrapper stays outside the field, so typing an
                // EXPRESSION into a quoted attribute used to nest it —
                // {value} became text="{value}", a literal brace string. A
                // value the user wrapped in braces themselves means "make this
                // an expression"; the same in reverse for a quoted literal
                // typed into an expression slot.
                string typed = newValue.Trim();
                if (typed.Length >= 2 && typed.StartsWith("{", System.StringComparison.Ordinal)
                    && typed.EndsWith("}", System.StringComparison.Ordinal))
                {
                    expr = true;
                    newValue = typed.Substring(1, typed.Length - 2);
                }
                else if (typed.Length >= 2
                    && typed.StartsWith("\"", System.StringComparison.Ordinal)
                    && typed.EndsWith("\"", System.StringComparison.Ordinal))
                {
                    expr = false;
                    newValue = typed.Substring(1, typed.Length - 2);
                }
                string wrapped = expr ? "{" + newValue + "}" : "\"" + newValue + "\"";
                return tag.Substring(0, m.Groups[2].Index) + wrapped
                    + tag.Substring(m.Groups[2].Index + m.Groups[2].Length);
            }, what);
        }

        /// <summary>POC 6.3 directive commit: rewrite the directive header,
        /// preserving indent; empty text removes the directive. Three header
        /// forms round-trip: plain "@if (…) {", the shared "} @else if (…) {"
        /// close-and-open, and the colon arm "@case x:" whose line may carry
        /// the case body after the label — only the label is replaced.</summary>
        private void OnDirectiveEdited(string filePath, int sourceLine, string newText)
        {
            string full = Path.GetFullPath(filePath);
            if (string.IsNullOrWhiteSpace(newText))
            {
                // POC editDirectiveInline: an emptied badge removes the
                // directive and keeps the element. A CONTINUATION clause
                // (@else/@case) must go through clause deletion — the construct
                // unwrap assumes it starts at a construct head and corrupts the
                // brace balance from a clause line.
                var node = _canvasHost?.FindNode(full);
                if (node != null)
                {
                    foreach (var markupRow in node.Markup)
                    {
                        if (markupRow.Kind == BuilderCardLineKind.Directive
                            && markupRow.DirectiveLine == sourceLine
                            && markupRow.ClauseIndex > 0)
                        {
                            DeleteClause(full, markupRow);
                            return;
                        }
                    }
                }
                RemoveDirectiveBlock(full, sourceLine);
                return;
            }
            EditLineInFile(full, sourceLine, line =>
            {
                int w = 0;
                while (w < line.Length && line[w] == ' ')
                    w++;
                string body = line.Substring(w);
                string cleaned = newText.Trim();
                if (body.StartsWith("@case", System.StringComparison.Ordinal)
                    || body.StartsWith("@default", System.StringComparison.Ordinal))
                {
                    int colon = BuilderGraphService.SingleColonIndex(body);
                    string rest = colon >= 0 ? body.Substring(colon) : ":";
                    return line.Substring(0, w) + cleaned.TrimEnd(':', ' ') + rest;
                }
                bool sharedClose = body.StartsWith("}", System.StringComparison.Ordinal);
                return line.Substring(0, w) + (sharedClose ? "} " : "")
                    + cleaned.TrimStart('}', ' ').TrimEnd('{', ' ') + " {";
            }, "directive");
        }

        // ── UB-76: the ONE floating inline editor ────────────────────────────

        [System.NonSerialized]
        private readonly BuilderInlineEditorOverlay _inlineEditor = new BuilderInlineEditorOverlay();

        private void ShowAttrValueEditor(
            string path, int sourceLine, int attrIdx, string seed, VisualElement anchor)
        {
            string full = Path.GetFullPath(path);
            _inlineEditor.Show(anchor, seed, multiline: false,
                FragmentCompletion(full, (text, l0, c0) => MapAttrFragment(full, sourceLine, attrIdx, text, c0)),
                text => OnAttrValueEdited(full, sourceLine, attrIdx, text),
                () => ResyncLspBuffer(full));
        }

        private void ShowDirectiveEditor(
            string path, int directiveLine, string seed, VisualElement anchor,
            System.Action cancelled = null)
        {
            string full = Path.GetFullPath(path);
            _inlineEditor.Show(anchor, seed, multiline: false,
                FragmentCompletion(full, (text, l0, c0) => MapLineFragment(full, directiveLine, text, "", c0)),
                text => OnDirectiveEdited(full, directiveLine, text),
                () => ResyncLspBuffer(full),
                cancelled);
        }

        private void ShowLineEditor(
            string path, int sourceLine, string seed, string suffix, VisualElement anchor)
        {
            string full = Path.GetFullPath(path);
            _inlineEditor.Show(anchor, seed, multiline: false,
                FragmentCompletion(full, (text, l0, c0) => MapLineFragment(full, sourceLine, text, suffix, c0)),
                text => OnLineRewritten(full, sourceLine, text + (suffix ?? "")),
                () => ResyncLspBuffer(full));
        }

        private void ShowIslandEditor(
            string path, int startLine, int endLine, string seed, VisualElement anchor)
        {
            string full = Path.GetFullPath(path);
            _inlineEditor.Show(anchor, seed, multiline: true,
                FragmentCompletion(full, (text, l0, c0) => MapIslandFragment(full, startLine, endLine, text, l0, c0)),
                text => OnIslandEdited(full, startLine, endLine, text),
                () => ResyncLspBuffer(full));
        }

        /// <summary>Ctrl+Space inside a fragment editor: the mapper splices the
        /// IN-PROGRESS fragment into the real buffer and returns the caret's
        /// file position; the request runs against that synthesized text and
        /// the real buffer is re-pushed when the editor closes.</summary>
        private System.Func<int, int, System.Threading.Tasks.Task<System.Collections.Generic.List<(string Label, string Insert)>>>
            FragmentCompletion(
                string fullPath,
                System.Func<string, int, int, (string Synth, int Line0, int Col0)?> map)
        {
            return async (localLine0, localCol0) =>
            {
                var results = new System.Collections.Generic.List<(string, string)>();
                try
                {
                    var mapped = map(_inlineEditor.CurrentText, localLine0, localCol0);
                    if (mapped == null)
                        return results;
                    var client = await BuilderLspService.GetOrStartAsync();
                    client.SendDidChangeNow(fullPath, mapped.Value.Synth);
                    var response = await client.RequestCompletion(
                        fullPath, mapped.Value.Line0, mapped.Value.Col0);
                    ParseCompletionItems(response, results);
                }
                catch (System.Exception)
                {
                }
                return results;
            };
        }

        private static void ParseCompletionItems(
            Newtonsoft.Json.Linq.JToken response,
            System.Collections.Generic.List<(string Label, string Insert)> results)
        {
            var items = response as Newtonsoft.Json.Linq.JArray
                ?? (response?["items"] ?? response?["Items"]) as Newtonsoft.Json.Linq.JArray;
            if (items == null)
                return;
            foreach (var item in items)
            {
                string label = item.Value<string>("label") ?? item.Value<string>("Label");
                if (string.IsNullOrEmpty(label))
                    continue;
                string insert = item.Value<string>("insertText")
                    ?? item.Value<string>("InsertText")
                    ?? label;
                results.Add((label, insert));
            }
        }

        /// <summary>Single-line fragment (directive header, hook chip, style
        /// entry): the line is replaced by indent + fragment (+ suffix) and the
        /// caret sits at indent + local column.</summary>
        private (string Synth, int Line0, int Col0)? MapLineFragment(
            string fullPath, int line1, string fieldText, string suffix, int localCol0)
        {
            var session = EditSession(fullPath);
            if (session == null || line1 <= 0)
                return null;
            var lines = session.BufferText.Split('\n');
            if (line1 - 1 >= lines.Length)
                return null;
            string indent = BuilderText.LeadingIndent(lines[line1 - 1]);
            lines[line1 - 1] = indent + fieldText + (suffix ?? "");
            return (string.Join("\n", lines), line1 - 1, indent.Length + localCol0);
        }

        /// <summary>Attribute-value fragment: the open tag's span is joined,
        /// attr #idx's value run is replaced by the wrapped fragment, and the
        /// caret maps through the joined offset back to a file position.</summary>
        private (string Synth, int Line0, int Col0)? MapAttrFragment(
            string fullPath, int sourceLine, int attrIdx, string fieldText, int localCol0)
        {
            var session = EditSession(fullPath);
            if (session == null || sourceLine <= 0)
                return null;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int start = sourceLine - 1;
            if (start >= lines.Count)
                return null;
            int end = OpenTagEndLine(lines, start);
            string joined = string.Join("\n", lines.GetRange(start, end - start + 1));
            var matches = System.Text.RegularExpressions.Regex.Matches(
                joined, "(\\w+)=(\\{[^}]*\\}|\"[^\"]*\")");
            if (attrIdx < 0 || attrIdx >= matches.Count)
                return null;
            var valueGroup = matches[attrIdx].Groups[2];
            bool expr = valueGroup.Value.StartsWith("{", System.StringComparison.Ordinal);
            string wrapped = expr ? "{" + fieldText + "}" : "\"" + fieldText + "\"";
            string newJoined = joined.Substring(0, valueGroup.Index) + wrapped
                + joined.Substring(valueGroup.Index + valueGroup.Length);
            int caretOffset = valueGroup.Index + 1 + localCol0;
            int caretLine = 0, caretLineStart = 0;
            for (int i = 0; i < caretOffset && i < newJoined.Length; i++)
            {
                if (newJoined[i] == '\n')
                {
                    caretLine++;
                    caretLineStart = i + 1;
                }
            }
            lines.RemoveRange(start, end - start + 1);
            lines.InsertRange(start, newJoined.Split('\n'));
            return (string.Join("\n", lines), start + caretLine, caretOffset - caretLineStart);
        }

        /// <summary>Island fragment: the range is replaced by the re-indented
        /// in-progress lines (the same re-basing the commit performs), caret =
        /// range start + local line, 2-space indent + local column.</summary>
        private (string Synth, int Line0, int Col0)? MapIslandFragment(
            string fullPath, int startLine, int endLine, string fieldText, int localLine0, int localCol0)
        {
            var session = EditSession(fullPath);
            if (session == null || startLine <= 0)
                return null;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int from = Mathf.Clamp(startLine - 1, 0, lines.Count - 1);
            int to = Mathf.Clamp(endLine - 1, from, lines.Count - 1);
            lines.RemoveRange(from, to - from + 1);
            var replacement = new System.Collections.Generic.List<string>(fieldText.Split('\n'));
            for (int r = 0; r < replacement.Count; r++)
                replacement[r] = replacement[r].Length == 0 ? "" : "  " + replacement[r];
            lines.InsertRange(from, replacement);
            return (string.Join("\n", lines), from + localLine0, 2 + localCol0);
        }

        /// <summary>0-based index of the line where the open tag starting at
        /// <paramref name="start"/> closes ('&gt;' outside strings/braces).</summary>
        private static int OpenTagEndLine(System.Collections.Generic.List<string> lines, int start)
        {
            int braces = 0;
            bool inString = false;
            for (int i = start; i < lines.Count && i < start + 24; i++)
            {
                foreach (char c in lines[i])
                {
                    if (inString)
                    {
                        if (c == '"')
                            inString = false;
                        continue;
                    }
                    if (c == '"')
                        inString = true;
                    else if (c == '{')
                        braces++;
                    else if (c == '}')
                        braces--;
                    else if (c == '>' && braces <= 0)
                        return i;
                }
            }
            return start;
        }

        /// <summary>1-based line of the hook decl OnAddHook just inserted — the
        /// line directly above the component's last <c>return (</c>.</summary>
        private int LineOfNewHook(string fullPath, int chipIndex)
        {
            var session = EditSession(fullPath);
            if (session == null)
                return 0;
            var lines = session.BufferText.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].TrimStart().StartsWith("return (", System.StringComparison.Ordinal))
                    return i;
            return 0;
        }

        /// <summary>Completion requests push SYNTHESIZED buffers to the LSP;
        /// when the editor closes the server must see the real one again.</summary>
        private async void ResyncLspBuffer(string fullPath)
        {
            try
            {
                var session = EditSession(fullPath);
                if (session == null)
                    return;
                var client = await BuilderLspService.GetOrStartAsync();
                client.SendDidChangeNow(fullPath, session.BufferText);
            }
            catch (System.Exception)
            {
            }
        }

        /// <summary>Inline single-line commit: rewrite the line keeping its
        /// indent; an export declaration can never lose its keyword.</summary>
        private void OnLineRewritten(string path, int line, string text)
        {
            EditLineInFile(Path.GetFullPath(path), line, old =>
            {
                int w = 0;
                while (w < old.Length && old[w] == ' ')
                    w++;
                string rewritten = old.Substring(0, w) + text.Trim();
                string trimmedOld = old.TrimStart();
                if (trimmedOld.StartsWith("export ", System.StringComparison.Ordinal)
                    && !text.TrimStart().StartsWith("export ", System.StringComparison.Ordinal))
                {
                    Toast("An export declaration keeps its 'export' keyword — edit skipped.");
                    return old;
                }
                return rewritten;
            }, "line edit");
        }

        /// <summary>Island commit: the range is replaced, blank edges trimmed,
        /// the block's common indent re-based onto the body's two spaces.</summary>
        private void OnIslandEdited(string path, int start, int end, string text)
        {
            var session = EditSession(Path.GetFullPath(path));
            if (session == null || session.IsReadOnly || start <= 0)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int from = Mathf.Clamp(start - 1, 0, lines.Count - 1);
            int to = Mathf.Clamp(end - 1, from, lines.Count - 1);
            lines.RemoveRange(from, to - from + 1);
            var replacement = new System.Collections.Generic.List<string>(
                text.Replace("\r\n", "\n").Split('\n'));
            while (replacement.Count > 0 && replacement[replacement.Count - 1].Trim().Length == 0)
                replacement.RemoveAt(replacement.Count - 1);
            while (replacement.Count > 0 && replacement[0].Trim().Length == 0)
                replacement.RemoveAt(0);
            BuilderGraphService.StripCommonIndent(replacement);
            for (int r = 0; r < replacement.Count; r++)
                replacement[r] = replacement[r].Length == 0 ? "" : "  " + replacement[r];
            lines.InsertRange(from, replacement);
            ApplyProgrammaticEdit(Path.GetFullPath(path), string.Join("\n", lines), "body");
        }

        private void AppendToFile(string filePath, string block, string what = null)
        {
            string full = Path.GetFullPath(filePath);
            OpenSession(full);
            var session = EditSession(full);
            if (session == null || session.IsReadOnly)
                return;
            ApplyProgrammaticEdit(full, session.BufferText.TrimEnd('\n') + block + "\n", what);
        }

        /// <summary>POC "Remove attribute…" submenu: lists the row's current
        /// attributes (name = value), removing the picked one from the line.</summary>
        private void ShowRemoveAttributeMenu(string filePath, int sourceLine, string attrsText)
        {
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(
                    attrsText, "(\\w+)=(\\{[^}]*\\}|\"[^\"]*\")"))
            {
                string full = match.Value;
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = match.Groups[1].Value + " = " + match.Groups[2].Value,
                    OnPick = () => EditOpenTagInFile(filePath, sourceLine, tag =>
                    {
                        if (tag.IndexOf(full, System.StringComparison.Ordinal) < 0)
                        {
                            Toast("Couldn't locate that attribute in the source.");
                            return null;
                        }
                        return tag.Replace(" " + full, "").Replace(full, "");
                    }, "removed " + match.Groups[1].Value),
                });
            }
            BuilderSearchMenu.Show("remove attribute", "search…", items);
        }

        /// <summary>POC 6.4 A.1: searchable typed-attribute menu with the
        /// untyped freeform fallback; the picked attribute lands on the row's
        /// open tag with a type-driven default. Custom components resolve their
        /// props through ruitk/componentProps (UB-13) with the signature parse
        /// as the LSP-down fallback.</summary>
        private async void ShowAttributeMenu(
            string filePath, int sourceLine, string tag, BuilderCardLine row, int rowIdx)
        {
            var present = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (string pair in row.AttrPairs)
            {
                int eq = pair.IndexOf('=');
                present.Add(eq < 0 ? pair : pair.Substring(0, eq));
            }
            int cardIndex = _canvasHost?.NodeIndexOf(Path.GetFullPath(filePath)) ?? -1;
            int newAttrIndex = row.AttrPairs.Count;

            void AddAttr(string name, string type)
            {
                string value = BuilderSchemaCache.DefaultValueFor(name, type);
                bool wrote = false;
                EditOpenTagInFile(filePath, sourceLine, tag =>
                {
                    int close = tag.LastIndexOf("/>", System.StringComparison.Ordinal);
                    if (close < 0)
                        close = tag.LastIndexOf('>');
                    if (close < 0)
                    {
                        Toast("Couldn't find the open tag's end — attribute not added.");
                        return null;
                    }
                    wrote = true;
                    return tag.Substring(0, close).TrimEnd() + " " + name + "=" + value
                        + (tag.Substring(close).StartsWith("/") ? " " : "") + tag.Substring(close);
                }, "added " + name);
                if (!wrote)
                    return;
                // POC addAttr: commit, jump to L2 if we are below it, then open the
                // new value's inline editor.
                if (_canvasHost != null && _canvasHost.Zoom < 1.05f)
                    _canvasHost.SetViewPreset(1.25f);
                if (cardIndex >= 0)
                    _canvasHost?.WithCanvasElement(
                        $"row-{cardIndex}-{rowIdx}",
                        anchor => ShowAttrValueEditor(
                            filePath, sourceLine, newAttrIndex,
                            value.Length >= 2 ? value.Substring(1, value.Length - 2) : value,
                            anchor));
            }

            var component = _canvasHost?.FindNodeByTitle(tag);
            bool custom = component != null && component.Kind == BuilderNodeKind.Component;
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
            if (custom)
            {
                var props = await FetchComponentPropsAsync(component.Title) ?? PropsOf(component);
                foreach (var (name, type) in props)
                {
                    if (present.Contains(name))
                        continue;
                    string capturedName = name;
                    string capturedType = type;
                    items.Add(new BuilderSearchMenu.Item
                    {
                        Label = capturedName + "  :  " + capturedType,
                        OnPick = () => AddAttr(capturedName, capturedType),
                    });
                }
                items.Add(BuilderSearchMenu.Separator);
                items.Add(BuilderSearchMenu.SectionHeader(
                    "not declared on " + tag + " — needs a matching prop"));
            }
            if (!BuilderSchemaCache.HasSchema)
                items.Add(BuilderSearchMenu.SectionHeader(
                    "schema offline — common attributes only"));
            foreach (var attr in BuilderSchemaCache.AttributesFor(tag))
            {
                string name = attr.Name;
                string type = attr.Type;
                if (present.Contains(name))
                    continue;
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = name + "  :  " + type,
                    OnPick = () => AddAttr(name, type),
                });
            }
            BuilderSearchMenu.Show(
                custom ? "attributes — typed props of " + tag : "attributes — UI Toolkit schema",
                "search attributes…", items,
                free => new BuilderSearchMenu.Item
                {
                    Label = "add \"" + free + "\" (untyped)",
                    OnPick = () => AddAttr(free, ""),
                });
        }

        /// <summary>UB-13: the REAL prop surface from the LSP's WorkspaceIndex
        /// (ruitk/componentProps). Null on timeout/failure so the caller can fall
        /// back to the signature parse.</summary>
        private async System.Threading.Tasks.Task<System.Collections.Generic.List<(string Name, string Type)>>
            FetchComponentPropsAsync(string componentName)
        {
            try
            {
                var client = await BuilderLspService.GetOrStartAsync();
                var session = _workspace.TryGet(_focusFile);
                if (session != null)
                    client.SendDidChangeNow(_focusFile, session.BufferText);
                var request = client.RequestComponentProps(componentName);
                var done = await System.Threading.Tasks.Task.WhenAny(
                    request, System.Threading.Tasks.Task.Delay(1500));
                if (done != request)
                    return null;
                var response = await request;
                var propArr = (response?["props"] ?? response?["Props"])
                    as Newtonsoft.Json.Linq.JArray;
                if (propArr == null || propArr.Count == 0)
                    return null;
                var props = new System.Collections.Generic.List<(string, string)>();
                foreach (var p in propArr)
                {
                    string name = p.Value<string>("name") ?? p.Value<string>("Name");
                    if (string.IsNullOrEmpty(name))
                        continue;
                    props.Add((name, p.Value<string>("type") ?? p.Value<string>("Type") ?? ""));
                }
                if (props.Count == 0)
                    return null;
                props.Add(("key", "list key"));
                return props;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>Signature-parse fallback for LSP-less sessions (plus the
        /// always-available list "key").</summary>
        private static System.Collections.Generic.List<(string Name, string Type)> PropsOf(
            BuilderCanvasNode component)
        {
            var props = new System.Collections.Generic.List<(string, string)>();
            string signature = component?.Signature ?? "";
            int open = signature.IndexOf('(');
            int close = signature.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                string inner = signature.Substring(open + 1, close - open - 1);
                foreach (string raw in inner.Split(','))
                {
                    string part = raw.Trim();
                    if (part.Length == 0)
                        continue;
                    int eq = part.IndexOf('=');
                    if (eq >= 0)
                        part = part.Substring(0, eq).Trim();
                    int space = part.LastIndexOf(' ');
                    if (space <= 0)
                        continue;
                    props.Add((part.Substring(space + 1), part.Substring(0, space)));
                }
            }
            props.Add(("key", "list key"));
            return props;
        }

        /// <summary>POC 6.5: "+ entry" → searchable key menu → value/helper menu
        /// → the entry lands before the export's closing brace. Keys and
        /// templates come from the reflected Style surface (UB-08), never a
        /// hand-written table.</summary>
        private void OnStyleAddEntry(string filePath, string styleName, int closeLine)
        {
            var used = UsedStyleKeys(filePath, styleName);
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
            foreach (var info in BuilderStyleSurface.Keys)
            {
                if (used.Contains(info.Name))
                    continue;
                var captured = info;
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = captured.Name + "  :  " + captured.TypeLabel,
                    OnPick = () => ShowStyleValueMenu(
                        filePath, styleName, closeLine, captured.Name, captured.Templates),
                });
            }
            BuilderSearchMenu.Show(
                styleName + " — style keys", "search keys…", items,
                free => new BuilderSearchMenu.Item
                {
                    Label = "use key \"" + free + "\"",
                    OnPick = () => ShowStyleValueMenu(
                        filePath, styleName, closeLine, free, BuilderStyleSurface.GenericTemplates),
                });
        }

        /// <summary>POC openStyleKeyMenu: keys already present on that export are
        /// filtered out of the menu.</summary>
        private System.Collections.Generic.HashSet<string> UsedStyleKeys(
            string filePath, string styleName)
        {
            var used = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            var node = _canvasHost?.FindNode(Path.GetFullPath(filePath));
            if (node == null)
                return used;
            bool inExport = false;
            foreach (var line in node.ExportDetail)
            {
                if (line.BadgeKind == 13)
                {
                    inExport = string.Equals(line.AttrsText, styleName, System.StringComparison.Ordinal);
                    continue;
                }
                if (!inExport)
                    continue;
                if (line.Text == "}" || line.BadgeKind == 9)
                {
                    inExport = false;
                    continue;
                }
                int eq = line.Text.IndexOf('=');
                if (eq > 0)
                    used.Add(line.Text.Substring(0, eq).Trim());
            }
            return used;
        }

        private void ShowStyleValueMenu(
            string filePath, string styleName, int closeLine, string key, string[] templates)
        {
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
            foreach (string template in templates)
            {
                string captured = template;
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = captured,
                    OnPick = () => InsertStyleEntry(filePath, styleName, closeLine, key, captured),
                });
            }
            BuilderSearchMenu.Show(
                key + " — values & helpers", "value or helper…", items,
                free => new BuilderSearchMenu.Item
                {
                    Label = "use \"" + free + "\"",
                    OnPick = () => InsertStyleEntry(filePath, styleName, closeLine, key, free),
                });
        }

        private void InsertStyleEntry(
            string filePath, string styleName, int closeLine, string key, string value)
        {
            OpenSession(filePath);
            InsertLinesInFile(
                filePath, closeLine - 1, "  " + key + " = " + value + ",", styleName + "." + key);
        }

        private BuilderDocumentSession OpenSession(string filePath)
        {
            _workspace.Open(filePath);
            return _workspace.TryGet(filePath);
        }

        /// <summary>The session an EDIT should act on, opened on demand. A canvas
        /// card is parsed straight from disk, so its rows, menus and drop targets
        /// all exist for files the user has never opened — and `TryGet` returns
        /// null for those, which made every action on such a card silently do
        /// nothing (owner report 2026-08-17: "clicking on wrap with switch
        /// without selecting the component doesnt do anything"). Read-only
        /// sessions still refuse to mutate one layer down, which is where that
        /// decision belongs.</summary>
        private BuilderDocumentSession EditSession(string filePath) =>
            _workspace.TryGet(filePath) ?? OpenSession(filePath);

        private void InsertBeforeLastReturn(string filePath, string line, string what = null)
        {
            var session = EditSession(filePath);
            if (session == null)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int at = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].TrimStart().StartsWith("return (", System.StringComparison.Ordinal))
                {
                    at = i;
                    break;
                }
            }
            if (at < 0)
                return;
            lines.Insert(at, line);
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines), what);
        }

        /// <summary>POC "&lt;name&gt; is already imported." guard.</summary>
        private static bool AlreadyImports(BuilderCanvasNode importer, string modulePath)
        {
            string stem = Path.GetFileNameWithoutExtension(modulePath);
            foreach (var line in importer.Imports)
            {
                string spec = line.AttrsText ?? "";
                if (spec.EndsWith("/" + stem, System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(spec, "./" + stem, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string BuildImportLine(
            string importerPath, BuilderCanvasNode module, bool styleModule, string name)
        {
            if (module == null)
                return null;
            try
            {
                string importerDir = Path.GetDirectoryName(importerPath) ?? "";
                string rel = Path.GetRelativePath(importerDir, module.FilePath)
                    .Replace('\\', '/');
                if (rel.EndsWith(".uitkx", System.StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(0, rel.Length - ".uitkx".Length);
                if (!rel.StartsWith(".", System.StringComparison.Ordinal))
                    rel = "./" + rel;
                if (styleModule)
                    return "import * as " + char.ToUpperInvariant(name[0]) + name.Substring(1)
                        + " from \"" + rel + "\"";
                string names = module.Exports.Count > 0
                    ? string.Join(", ", module.Exports)
                    : name;
                return "import { " + names + " } from \"" + rel + "\"";
            }
            catch
            {
                return null;
            }
        }

        private string IndentOf(string filePath, int line1)
        {
            var session = EditSession(filePath);
            if (session == null)
                return "";
            string[] lines = session.BufferText.Split('\n');
            if (line1 - 1 < 0 || line1 - 1 >= lines.Length)
                return "";
            return BuilderText.LeadingIndent(lines[line1 - 1]);
        }

        private void EditLineInFile(
            string filePath, int line1, System.Func<string, string> transform, string what = null)
        {
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly)
                return;
            string[] lines = session.BufferText.Split('\n');
            if (line1 - 1 < 0 || line1 - 1 >= lines.Length)
                return;
            lines[line1 - 1] = transform(lines[line1 - 1]);
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines), what);
        }

        /// <summary>The POC edits the AST, so an open tag whose attributes wrap
        /// across lines behaves exactly like a single-line one. Here the open
        /// tag's line SPAN is joined, transformed as one string, and re-split —
        /// otherwise the per-line regexes silently no-op on wrapped tags.</summary>
        private void EditOpenTagInFile(
            string filePath, int line1, System.Func<string, string> transform, string what)
        {
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int start = line1 - 1;
            if (start < 0 || start >= lines.Count)
                return;
            int end = OpenTagEnd(lines, start);
            string joined = string.Join("\n", lines.GetRange(start, end - start + 1));
            string rewritten = transform(joined);
            if (rewritten == null || rewritten == joined)
                return;
            lines.RemoveRange(start, end - start + 1);
            lines.InsertRange(start, rewritten.Split('\n'));
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines), what);
        }

        /// <summary>Index of the last line of the open tag that starts on
        /// <paramref name="start"/> — the first line that closes it outside of a
        /// string or a braced expression.</summary>
        private static int OpenTagEnd(System.Collections.Generic.List<string> lines, int start)
        {
            int braces = 0;
            bool inString = false;
            for (int i = start; i < lines.Count && i < start + 24; i++)
            {
                string line = lines[i];
                for (int c = 0; c < line.Length; c++)
                {
                    char ch = line[c];
                    if (inString)
                    {
                        if (ch == '"')
                            inString = false;
                        continue;
                    }
                    if (ch == '"')
                        inString = true;
                    else if (ch == '{')
                        braces++;
                    else if (ch == '}')
                        braces--;
                    else if (ch == '>' && braces <= 0)
                        return i;
                }
            }
            return start;
        }

        private void InsertLinesInFile(
            string filePath, int afterLine1, string newLine, string what = null)
        {
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int at = Mathf.Clamp(afterLine1, 0, lines.Count);
            lines.Insert(at, newLine);
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines), what);
        }

        private void DeleteLinesInFile(
            string filePath, int fromLine1, int toLine1, string what = null)
        {
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int from = Mathf.Clamp(fromLine1 - 1, 0, lines.Count - 1);
            int to = Mathf.Clamp(toLine1 - 1, from, lines.Count - 1);
            lines.RemoveRange(from, to - from + 1);
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines), what);
        }

        private void WrapRowInDirective(
            string filePath, BuilderCardLine row, string header, int cardIndex, int rowIdx)
        {
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int from = Mathf.Clamp(row.SourceLine - 1, 0, lines.Count - 1);
            int to = Mathf.Clamp((row.EndLine > 0 ? row.EndLine : row.SourceLine) - 1, from, lines.Count - 1);
            string indent = IndentOf(filePath, row.SourceLine);
            for (int i = from; i <= to; i++)
                lines[i] = "    " + lines[i];
            // The closer aligns with its own "return (", not with the body —
            // the house form every sample uses. It was emitted a level deeper.
            lines.Insert(to + 1, indent + "  );");
            lines.Insert(to + 1 + 1, indent + "}");
            lines.Insert(from, indent + "  return (");
            lines.Insert(from, indent + header + " {");
            int space = header.IndexOf(' ');
            ApplyProgrammaticEdit(
                filePath, string.Join("\n", lines),
                space > 0 ? header.Substring(0, space) : header);
            if (cardIndex >= 0)
                BeginEditOnDirectiveLine(filePath, cardIndex, row.SourceLine);
        }

        /// <summary>POC "Remove directive" (j.directive = null): the ELEMENT
        /// survives, the wrapper disappears — header line, its <c>return (</c> /
        /// <c>);</c> scaffolding and the closing brace go, and the enclosed block
        /// de-indents by one level. The inverse of WrapRowInDirective.</summary>
        private void RemoveDirectiveBlock(string filePath, int headerLine1)
        {
            var session = EditSession(filePath);
            if (session == null || session.IsReadOnly || headerLine1 <= 0)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int header = headerLine1 - 1;
            if (header < 0 || header >= lines.Count)
                return;

            int depth = 0;
            int close = -1;
            for (int i = header; i < lines.Count; i++)
            {
                foreach (char c in lines[i])
                {
                    if (c == '{')
                        depth++;
                    else if (c == '}')
                        depth--;
                }
                if (depth <= 0 && i > header)
                {
                    close = i;
                    break;
                }
            }
            if (close < 0)
                return;

            var inner = lines.GetRange(header + 1, close - header - 1);
            int openIdx = inner.FindIndex(l => l.Trim() == "return (");
            if (openIdx >= 0)
            {
                int closeIdx = inner.FindLastIndex(l => l.Trim() == ");");
                if (closeIdx > openIdx)
                    inner.RemoveAt(closeIdx);
                inner.RemoveAt(openIdx);
            }

            string headerIndent = BuilderText.LeadingIndent(lines[header]);
            int minIndent = int.MaxValue;
            foreach (string l in inner)
                if (l.Trim().Length > 0)
                    minIndent = Mathf.Min(minIndent, BuilderText.LeadingIndent(l).Length);
            int shift = minIndent == int.MaxValue ? 0 : minIndent - headerIndent.Length;
            for (int i = 0; i < inner.Count; i++)
            {
                if (shift > 0 && inner[i].Length >= shift && inner[i].Substring(0, shift).Trim().Length == 0)
                    inner[i] = inner[i].Substring(shift);
            }

            lines.RemoveRange(header, close - header + 1);
            lines.InsertRange(header, inner);
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines), "directive removed");
        }

        /// <summary>POC delete: the NODE goes, and with it the directive that was
        /// a property of that node — so a directive-wrapped row takes its whole
        /// block, never leaving an orphan "@if (…) { return ( ); }".</summary>
        private void DeleteElementRow(string filePath, BuilderCardLine row)
        {
            string what = "delete " + row.Text;
            if (row.DirectiveLine > 0)
            {
                int close = MatchingCloseLine(filePath, row.DirectiveLine);
                if (close > 0)
                {
                    DeleteLinesInFile(filePath, row.DirectiveLine, close, what);
                    return;
                }
            }
            DeleteLinesInFile(
                filePath, row.SourceLine, row.EndLine > 0 ? row.EndLine : row.SourceLine, what);
        }

        /// <summary>1-based line of the '}' that closes the block opened on
        /// <paramref name="headerLine1"/>, or 0 when unbalanced.</summary>
        private int MatchingCloseLine(string filePath, int headerLine1)
        {
            var session = EditSession(filePath);
            if (session == null)
                return 0;
            string[] lines = session.BufferText.Split('\n');
            int depth = 0;
            for (int i = headerLine1 - 1; i >= 0 && i < lines.Length; i++)
            {
                foreach (char c in lines[i])
                {
                    if (c == '{')
                        depth++;
                    else if (c == '}')
                        depth--;
                }
                if (depth <= 0 && i > headerLine1 - 1)
                    return i + 1;
            }
            return 0;
        }

        private bool StyleExportExists(string filePath, string name)
        {
            var node = _canvasHost?.FindNode(Path.GetFullPath(filePath));
            if (node == null)
                return false;
            foreach (var line in node.ExportDetail)
                if (line.BadgeKind == 13
                    && string.Equals(line.AttrsText, name, System.StringComparison.Ordinal))
                    return true;
            return false;
        }

        private void ApplyProgrammaticEdit(string filePath, string newBufferLf, string what = null)
        {
            var session = EditSession(filePath);
            if (session == null)
                return;
            string before = session.BufferText;
            session.ApplyEdit(newBufferLf);
            // UB-73: the ledger names the gesture. Nested scopes collapse, so a
            // compound gesture calling this twice still reads as one action.
            _ledger.Begin(string.IsNullOrEmpty(what) ? "edit" : what);
            _ledger.Record(filePath, before, newBufferLf);
            _ledger.End();
            RefreshHistoryPanel();
            if (string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(_focusFile),
                    System.StringComparison.OrdinalIgnoreCase))
                _codeField?.SetContent(newBufferLf, _focusFile, KnownElementsOrNull());
            SyncLspBuffer(filePath, newBufferLf, open: false);
            RefreshChrome();
            NotifyBufferChanged();
            // POC commitNode(): rebuild ONLY the edited card and redraw the edges —
            // zoom, camera, card selection and row selection survive the commit.
            _canvasHost?.RefreshGraph(filePath, ReadBufferOrDisk);
            // POC commitNode(label): the toast names WHAT changed, not just the file.
            Toast(string.IsNullOrEmpty(what)
                ? "Committed edit → " + Path.GetFileName(filePath)
                : "Committed " + what + " → " + Path.GetFileName(filePath));
        }

        private void OnPreviewComponentPicked(string filePath)
        {
            string full = Path.GetFullPath(filePath);
            if (!string.Equals(full, Path.GetFullPath(_focusFile), System.StringComparison.OrdinalIgnoreCase))
                OpenFileFromCanvas(full);
        }

        private string ReadBufferOrDisk(string filePath)
        {
            var session = _workspace.TryGet(filePath);
            if (session != null)
                return session.BufferText;
            return File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }

        private void OpenFileFromCanvas(string filePath)
        {
            _workspace.Open(filePath);
            _focusFile = filePath;
            // POC selectNode(): every route into a file moves the gold ring too.
            _canvasHost?.SelectByPath(filePath);
            MountPreview();
            RefreshChrome();
        }

        /// <summary>POC firstUsageProps + collectExprs, read off the live graph:
        /// the first card that instantiates this component (and that usage's
        /// attribute pairs), plus the component's own expression blob.</summary>
        private (string Owner, string UsageAttrs, string Blob) UsageFor(string uitkxPath)
        {
            var nodes = _canvasHost?.Nodes;
            if (nodes == null)
                return (null, "", "");
            var self = _canvasHost.FindNode(Path.GetFullPath(uitkxPath));
            if (self == null)
                return (null, "", "");
            var blob = new System.Text.StringBuilder();
            foreach (var markupRow in self.Markup)
                blob.Append(markupRow.AttrsText).Append(' ');
            foreach (string island in self.IslandLines)
                blob.Append(island).Append(' ');
            foreach (var bodyRow in self.Body)
                blob.Append(bodyRow.SourceText).Append(' ');
            foreach (var candidate in nodes)
            {
                if (candidate == self)
                    continue;
                foreach (var candidateRow in candidate.Markup)
                {
                    if (!string.Equals(
                            candidateRow.Text.Trim('<', '>'), self.Title, System.StringComparison.Ordinal))
                        continue;
                    return (candidate.Title, candidateRow.AttrsText, blob.ToString());
                }
            }
            return (null, "", blob.ToString());
        }

        /// <summary>POC ".nopreview" for a hook module names the exported signature
        /// and the components that import it; both are already parsed onto the
        /// graph, so the pane never has to fall back to generic phrasing.</summary>
        private (string Signature, string Consumers) ModuleInfoFor(string uitkxPath)
        {
            var host = _canvasHost;
            if (host?.Nodes == null)
                return ("", "");
            int index = host.NodeIndexOf(uitkxPath);
            if (index < 0 || index >= host.Nodes.Count)
                return ("", "");
            var self = host.Nodes[index];
            var consumers = new System.Collections.Generic.List<string>();
            var edges = host.Edges;
            if (edges != null)
            {
                foreach (var edge in edges)
                {
                    if (edge.ToIndex != index || edge.FromIndex < 0 || edge.FromIndex >= host.Nodes.Count)
                        continue;
                    string title = host.Nodes[edge.FromIndex].Title;
                    if (!consumers.Contains(title))
                        consumers.Add(title);
                }
            }
            // A style/util module has no declaration head at all; what the POC's
            // ".nopreview" names for one is its FIRST export ("edit root's
            // BackgroundColor hex"), which the card already parsed onto ExportDetail.
            string signature = self.ExposedSignature;
            if (string.IsNullOrEmpty(signature))
                signature = self.Signature;
            if (string.IsNullOrEmpty(signature))
            {
                foreach (var detail in self.ExportDetail)
                {
                    if (string.IsNullOrEmpty(detail.AttrsText) || detail.BadgeKind != 13)
                        continue;
                    signature = detail.AttrsText;
                    break;
                }
            }
            return (signature ?? "", string.Join(", ", consumers));
        }

        /// <summary>Debounced buffer-edit entry point (CodeField/authoring call
        /// this): dirty buffers recompile in import order after 300 ms of quiet,
        /// then the preview re-resolves its delegate from the swap assembly.</summary>
        public void NotifyBufferChanged()
        {
            ScheduleServerTokens();
            _recompileDue = EditorApplication.timeSinceStartup + 0.3;
            if (_recompileScheduled)
                return;
            _recompileScheduled = true;
            EditorApplication.update += RecompileWhenQuiet;
        }

        private void RecompileWhenQuiet()
        {
            if (EditorApplication.timeSinceStartup < _recompileDue)
                return;
            EditorApplication.update -= RecompileWhenQuiet;
            _recompileScheduled = false;

            _previewCompiler ??= new BuilderPreviewCompiler();
            if (!_previewCompiler.EnsureReady(_workspace))
            {
                _previewPane?.ShowError("Preview compiler unavailable: " + _previewCompiler.InitError);
                return;
            }
            var summary = _previewCompiler.CompileDirty(_focusFile);
            if (summary == null)
                return;
            if (summary.FocusResult != null && summary.FocusResult.Success)
            {
                var session = _workspace.TryGet(_focusFile);
                _previewPane?.OnRecompiled(summary.FocusResult.LoadedAssembly, session?.BufferText);
            }
            // UB-15: every failed round names its FIRST real error in the pane —
            // including when the focus file itself was clean or skipped, the
            // case the old code reported as nothing at all.
            if (summary.Failures.Count > 0)
            {
                var (path, error) = summary.Failures[0];
                string skipNote = summary.Skipped.Count > 0
                    ? " (" + summary.Skipped.Count + " dependent file(s) skipped)"
                    : "";
                _previewPane?.ShowError(
                    "Preview compile failed in " + Path.GetFileName(path) + skipNote
                    + " — last good preview kept:\n" + Truncate(error, 220));
                Debug.LogWarning("[RUITK Builder] preview compile: " + path + ": " + error);
            }
        }

        private static string Truncate(string text, int max)
        {
            text = text ?? "";
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        /// <summary>POC "Import .uxml…": the one-way UI Builder import, run for
        /// real — pick a .uxml, convert it, drop the .uitkx beside the tree and
        /// open it on the canvas.</summary>
        private void ImportUxml()
        {
            string start = string.IsNullOrEmpty(_focusFile)
                ? Application.dataPath
                : Path.GetDirectoryName(_focusFile);
            string source = EditorUtility.OpenFilePanel("Import .uxml", start, "uxml");
            if (string.IsNullOrEmpty(source))
                return;
            string stem = Path.GetFileNameWithoutExtension(source);
            string componentName = char.ToUpperInvariant(stem[0]) + stem.Substring(1);
            var result = Ruitk.Language.Import.UxmlToUitkx.Convert(
                File.ReadAllText(source), componentName);
            if (string.IsNullOrEmpty(result.UitkxText))
            {
                Toast("UXML import failed: " + string.Join("; ", result.Warnings));
                return;
            }
            string target = Path.Combine(
                start ?? Path.GetDirectoryName(source) ?? "", componentName + ".uitkx");
            if (File.Exists(target))
            {
                Toast(componentName + ".uitkx already exists.");
                return;
            }
            File.WriteAllText(target, result.UitkxText);
            AssetDatabase.Refresh();
            foreach (string warning in result.Warnings)
                Debug.LogWarning("[RUITK Builder] UXML import: " + warning);
            Toast("Imported " + componentName + ".uitkx (one-way)");
            OpenAdditionalFile(target);
        }

        [System.NonSerialized] private Label _toast;
        [System.NonSerialized] private double _toastUntil;
        [System.NonSerialized] private bool _toastTicking;

        /// <summary>POC "#toast": a panel2 pill with an accent border, bottom
        /// centre, fading out after 3.2s — never Unity's centred notification
        /// overlay.</summary>
        internal void Toast(string message)
        {
            var root = rootVisualElement;
            if (root == null)
                return;
            if (_toast == null)
            {
                _toast = new Label
                {
                    pickingMode = PickingMode.Ignore,
                    style =
                    {
                        position = Position.Absolute,
                        bottom = 44f,
                        left = Length.Percent(50f),
                        translate = new Translate(Length.Percent(-50f), 0f),
                        backgroundColor = BuilderPalette.Panel2,
                        color = BuilderPalette.Text,
                        borderTopWidth = 1f, borderBottomWidth = 1f,
                        borderLeftWidth = 1f, borderRightWidth = 1f,
                        borderTopColor = BuilderPalette.Accent, borderBottomColor = BuilderPalette.Accent,
                        borderLeftColor = BuilderPalette.Accent, borderRightColor = BuilderPalette.Accent,
                        borderTopLeftRadius = 6f, borderTopRightRadius = 6f,
                        borderBottomLeftRadius = 6f, borderBottomRightRadius = 6f,
                        paddingLeft = 16f, paddingRight = 16f,
                        paddingTop = 8f, paddingBottom = 8f,
                    },
                };
                root.Add(_toast);
            }
            _toast.text = message;
            _toast.style.display = DisplayStyle.Flex;
            _toast.style.opacity = 1f;
            _toast.BringToFront();
            _toastUntil = EditorApplication.timeSinceStartup + 3.2;
            if (_toastTicking)
                return;
            _toastTicking = true;
            EditorApplication.update += TickToast;
        }

        private void TickToast()
        {
            if (_toast == null)
            {
                EditorApplication.update -= TickToast;
                _toastTicking = false;
                return;
            }
            double left = _toastUntil - EditorApplication.timeSinceStartup;
            if (left <= 0.0)
            {
                _toast.style.display = DisplayStyle.None;
                EditorApplication.update -= TickToast;
                _toastTicking = false;
                return;
            }
            _toast.style.opacity = left < 0.6 ? (float)(left / 0.6) : 1f;
        }

        [System.NonSerialized] private VisualElement _helpOverlay;

        private void ToggleHelp()
        {
            if (_helpOverlay != null)
            {
                _helpOverlay.RemoveFromHierarchy();
                _helpOverlay = null;
                return;
            }
            var canvas = rootVisualElement?.Q("builder-canvas");
            if (canvas == null)
                return;
            var help = new VisualElement
            {
                style =
                {
                    position = Position.Absolute,
                    top = 12f, left = 12f, width = 330f,
                    backgroundColor = new Color(0.137f, 0.137f, 0.161f, 0.97f),
                    borderTopWidth = 1f, borderBottomWidth = 1f,
                    borderLeftWidth = 1f, borderRightWidth = 1f,
                    borderTopColor = new Color(0.23f, 0.23f, 0.27f),
                    borderBottomColor = new Color(0.23f, 0.23f, 0.27f),
                    borderLeftColor = new Color(0.23f, 0.23f, 0.27f),
                    borderRightColor = new Color(0.23f, 0.23f, 0.27f),
                    borderTopLeftRadius = 8f, borderTopRightRadius = 8f,
                    borderBottomLeftRadius = 8f, borderBottomRightRadius = 8f,
                    paddingLeft = 14f, paddingRight = 14f, paddingTop = 12f, paddingBottom = 12f,
                },
            };
            help.Add(new Label("Drive it like this")
            {
                style =
                {
                    color = new Color(0.31f, 0.76f, 0.97f), fontSize = 13f, marginBottom = 6f,
                    unityFontStyleAndWeight = FontStyle.Bold,
                },
            });
            // POC <ol>: hanging numbers (18px indent) and the gesture nouns in
            // <b>. Fixture names are generalised — a user's tree has no
            // ShopScreen / shopStyles.
            string[] steps =
            {
                "<b>Wheel</b> zooms (to cursor), <b>drag background</b> pans, <b>drag a title bar</b> moves a card.",
                "Zoom all the way out — <b>L0</b> is the live architecture diagram.",
                "Click a component card — live preview + its source appear on the right.",
                "<b>Hover a hook chip</b> — every usage lights up in JSX and source.",
                "Zoom close (<b>L2</b>) — attributes, code islands and directive badges appear.",
                "Select a card, drag a props knob — watch the <b>@if</b> branch flip live.",
                "Click a JSX row → its source line highlights. Click an element in the preview → same.",
                "<b>Edit on the canvas</b> (at L2): click an attribute value, a directive badge, or a style entry to edit inline ({} and quotes stay outside the field); double-click a code island or hook chip to edit body code. Enter commits (Ctrl+Enter in a code island), Esc cancels → the source regenerates.",
                "<b>Drag from the Library</b> (left, searchable) onto a JSX row — top edge inserts <i>before</i>, bottom edge <i>after</i>, middle nests <i>inside</i>. Drag existing rows to reorder. Hooks drop onto BODY, style modules onto a card (adds the import).",
                "<b>Right-click a row</b> — searchable typed attributes (native schema / component props, untyped fallback), <b>remove attribute</b>, directives, delete. Emptying an attribute's value also removes it. <b>Right-click a card title</b> — delete. <b>Right-click empty canvas</b> or <b>+ new</b> — create component / style / hook / util module.",
                "<b>Style authoring</b> — on a style card, <b>+ entry</b> gives searchable keys then value helpers (Px/Pct/Hex/Rgba/FlexRow…); <b>+ style</b> adds another export.",
                "<b>Source pane is bidirectional</b> — click <b>edit</b> (or double-click the source), change the text, <b>apply</b> (Ctrl+Enter): it re-parses into the model, the card updates, and the source reformats canonically. Ctrl+Space completes; Save writes every dirty buffer in one batch.",
                "Select a component, then edit its <b>style module → an entry</b>'s BackgroundColor hex (try #2a1a3a) — the live preview repaints. Drag the splitters to resize panes.",
            };
            for (int i = 0; i < steps.Length; i++)
            {
                var stepRow = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, marginBottom = 5f },
                };
                stepRow.Add(new Label((i + 1) + ".")
                {
                    style =
                    {
                        color = BuilderPalette.Text, fontSize = 12f, width = 18f, flexShrink = 0f,
                        unityTextAlign = TextAnchor.UpperRight, paddingRight = 4f,
                    },
                });
                stepRow.Add(new Label(steps[i])
                {
                    enableRichText = true,
                    style =
                    {
                        color = BuilderPalette.Text, fontSize = 12f,
                        whiteSpace = WhiteSpace.Normal, flexShrink = 1f, flexGrow = 1f,
                    },
                });
                help.Add(stepRow);
            }
            canvas.Add(help);
            _helpOverlay = help;
        }

        [System.NonSerialized] private VisualElement _historyOverlay;
        [System.NonSerialized] private VisualElement _historyList;

        /// <summary>UB-73: the ledger made visible. Every action, newest last,
        /// with the cursor drawn as the live position — entries past it are the
        /// redo tail and render dimmed. Clicking any row walks the buffers to
        /// that point in one atomic step, which is the same operation Ctrl+Z
        /// performs one entry at a time.</summary>
        private void ToggleHistory()
        {
            if (_historyOverlay != null)
            {
                _historyOverlay.RemoveFromHierarchy();
                _historyOverlay = null;
                _historyList = null;
                return;
            }
            var canvas = rootVisualElement?.Q("builder-canvas");
            if (canvas == null)
                return;
            var panel = new VisualElement
            {
                style =
                {
                    position = Position.Absolute,
                    top = 12f, right = 12f, width = 300f, maxHeight = 420f,
                    backgroundColor = new Color(0.137f, 0.137f, 0.161f, 0.97f),
                    borderTopWidth = 1f, borderBottomWidth = 1f,
                    borderLeftWidth = 1f, borderRightWidth = 1f,
                    borderTopLeftRadius = 8f, borderTopRightRadius = 8f,
                    borderBottomLeftRadius = 8f, borderBottomRightRadius = 8f,
                    paddingLeft = 12f, paddingRight = 12f, paddingTop = 10f, paddingBottom = 10f,
                },
            };
            SetBorderColor(panel, new Color(0.23f, 0.23f, 0.27f));
            panel.Add(new Label("History")
            {
                style =
                {
                    color = new Color(0.31f, 0.76f, 0.97f), fontSize = 13f, marginBottom = 2f,
                    unityFontStyleAndWeight = FontStyle.Bold,
                },
            });
            panel.Add(new Label("Ctrl+Z undo · Ctrl+Shift+Z / Ctrl+Y redo · click a row to jump")
            {
                style =
                {
                    color = BuilderPalette.Dim, fontSize = 10f, marginBottom = 8f,
                    whiteSpace = WhiteSpace.Normal,
                },
            });
            var list = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1f } };
            StyleScrollers(list);
            panel.Add(list);
            canvas.Add(panel);
            _historyOverlay = panel;
            _historyList = list;
            RefreshHistoryPanel();
        }

        private void RefreshHistoryPanel()
        {
            if (_historyList == null)
                return;
            _historyList.Clear();
            var entries = _ledger.Entries;
            if (entries.Count == 0)
            {
                _historyList.Add(new Label("no actions yet")
                {
                    style = { color = BuilderPalette.Dim, fontSize = 11f },
                });
                return;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                bool undone = i >= _ledger.Cursor;
                bool current = i == _ledger.Cursor - 1;
                int target = i + 1;
                var row = new VisualElement
                {
                    style =
                    {
                        flexDirection = FlexDirection.Row,
                        justifyContent = Justify.SpaceBetween,
                        paddingLeft = 6f, paddingRight = 6f, paddingTop = 2f, paddingBottom = 2f,
                        marginBottom = 1f,
                        borderTopLeftRadius = 3f, borderTopRightRadius = 3f,
                        borderBottomLeftRadius = 3f, borderBottomRightRadius = 3f,
                        backgroundColor = current
                            ? new Color(0.31f, 0.76f, 0.97f, 0.16f)
                            : new Color(0f, 0f, 0f, 0f),
                    },
                };
                row.Add(new Label(entry.Description)
                {
                    style =
                    {
                        color = undone ? BuilderPalette.Dim : BuilderPalette.Text,
                        fontSize = 11f, flexShrink = 1f, overflow = Overflow.Hidden,
                    },
                });
                row.Add(new Label(entry.FileSummary)
                {
                    style = { color = BuilderPalette.Dim, fontSize = 10f, flexShrink = 0f },
                });
                BuilderCursor.Set(row, UnityEditor.MouseCursor.Link);
                row.RegisterCallback<PointerDownEvent>(_ =>
                    ApplyLedgerWrites(_ledger.WalkTo(target), "History → " + entry.Description));
                _historyList.Add(row);
            }
        }

        internal static void SetBorderColor(VisualElement element, Color color)
        {
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
        }

        /// <summary>The POC's panes are CSS "overflow: auto", so a scrollbar is a
        /// thin dark overlay that steals no width from the rows. Unity's default
        /// Scroller is a light editor control WITH arrow buttons laid out INSIDE
        /// the pane, which both re-coloured the panel and narrowed every row by its
        /// width. The scrollers are taken out of flow and repainted on the POC
        /// palette (track transparent, #3a3a44 thumb, #4a4a55 on hover).</summary>
        internal static void StyleScrollers(ScrollView view)
        {
            if (view == null)
                return;
            StyleScroller(view.verticalScroller, vertical: true);
            StyleScroller(view.horizontalScroller, vertical: false);
        }

        private static void StyleScroller(Scroller scroller, bool vertical)
        {
            if (scroller == null)
                return;
            scroller.style.position = Position.Absolute;
            if (vertical)
            {
                scroller.style.top = 0f;
                scroller.style.bottom = 0f;
                scroller.style.right = 0f;
                scroller.style.width = 8f;
            }
            else
            {
                scroller.style.left = 0f;
                scroller.style.right = 0f;
                scroller.style.bottom = 0f;
                scroller.style.height = 8f;
            }
            scroller.style.backgroundColor = BuilderPalette.Transparent;
            scroller.style.borderTopWidth = 0f;
            scroller.style.borderBottomWidth = 0f;
            scroller.style.borderLeftWidth = 0f;
            scroller.style.borderRightWidth = 0f;
            if (scroller.lowButton != null)
                scroller.lowButton.style.display = DisplayStyle.None;
            if (scroller.highButton != null)
                scroller.highButton.style.display = DisplayStyle.None;
            var slider = scroller.slider;
            if (slider == null)
                return;
            slider.style.marginTop = 0f;
            slider.style.marginBottom = 0f;
            slider.style.marginLeft = 0f;
            slider.style.marginRight = 0f;
            slider.style.flexGrow = 1f;
            var tracker = slider.Q("unity-tracker");
            if (tracker != null)
            {
                tracker.style.backgroundColor = BuilderPalette.Transparent;
                tracker.style.borderTopWidth = 0f;
                tracker.style.borderBottomWidth = 0f;
                tracker.style.borderLeftWidth = 0f;
                tracker.style.borderRightWidth = 0f;
                tracker.style.marginTop = 0f;
                tracker.style.marginBottom = 0f;
                tracker.style.marginLeft = 0f;
                tracker.style.marginRight = 0f;
            }
            var dragger = slider.Q("unity-dragger");
            if (dragger == null)
                return;
            var thumb = BuilderPalette.Line;
            dragger.style.backgroundColor = thumb;
            dragger.style.borderTopWidth = 0f;
            dragger.style.borderBottomWidth = 0f;
            dragger.style.borderLeftWidth = 0f;
            dragger.style.borderRightWidth = 0f;
            dragger.style.borderTopLeftRadius = 4f;
            dragger.style.borderTopRightRadius = 4f;
            dragger.style.borderBottomLeftRadius = 4f;
            dragger.style.borderBottomRightRadius = 4f;
            if (vertical)
            {
                dragger.style.width = 8f;
                dragger.style.marginLeft = 0f;
                dragger.style.marginRight = 0f;
            }
            else
            {
                dragger.style.height = 8f;
                dragger.style.marginTop = 0f;
                dragger.style.marginBottom = 0f;
            }
            dragger.RegisterCallback<MouseEnterEvent>(_ =>
                dragger.style.backgroundColor = new Color(0.290f, 0.290f, 0.333f));
            dragger.RegisterCallback<MouseLeaveEvent>(_ =>
                dragger.style.backgroundColor = thumb);
        }

        /// <summary>POC "#toolbar button": panel2 fill, line border, 4px radius,
        /// 12px label — the active mode flips to the accent fill, and
        /// "#toolbar button:hover { border-color: var(--accent) }".</summary>
        private static Button ToolbarButton(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.userData = BuilderPalette.Line;
            button.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(button, BuilderPalette.Accent));
            button.RegisterCallback<MouseLeaveEvent>(_ =>
                SetBorderColor(button, button.userData is Color resting ? resting : BuilderPalette.Line));
            BuilderCursor.Set(button, MouseCursor.Link);
            button.style.backgroundColor = BuilderPalette.Panel2;
            button.style.color = BuilderPalette.Text;
            button.style.fontSize = 12f;
            button.style.borderTopWidth = 1f;
            button.style.borderBottomWidth = 1f;
            button.style.borderLeftWidth = 1f;
            button.style.borderRightWidth = 1f;
            button.style.borderTopColor = BuilderPalette.Line;
            button.style.borderBottomColor = BuilderPalette.Line;
            button.style.borderLeftColor = BuilderPalette.Line;
            button.style.borderRightColor = BuilderPalette.Line;
            button.style.borderTopLeftRadius = 4f;
            button.style.borderTopRightRadius = 4f;
            button.style.borderBottomLeftRadius = 4f;
            button.style.borderBottomRightRadius = 4f;
            button.style.paddingLeft = 10f;
            button.style.paddingRight = 10f;
            button.style.paddingTop = 4f;
            button.style.paddingBottom = 4f;
            button.style.marginLeft = 4f;
            button.style.marginRight = 4f;
            button.style.unityFontStyleAndWeight = FontStyle.Normal;
            return button;
        }

        /// <summary>POC "#vsplit / #hsplit": a 6px gutter filled var(--panel) with
        /// a 1px var(--line) edge on each long side, turning var(--accent) on
        /// hover — not Unity's stock hairline drag handle.</summary>
        private static void StyleSplitter(TwoPaneSplitView split, bool vertical)
        {
            // Restyling Unity's dragline anchor was the previous attempt and it
            // photographed as the stock hairline: a screenshot probe of the shipped
            // build read ONE pixel of #232323 (Unity's own anchor colour) at the
            // canvas|side boundary and nothing at all at preview|source. The band is
            // therefore painted by an element we own, pinned to the boundary from
            // the first pane's resolved layout, and the anchor is still widened so
            // the grab area matches the band when Unity lets it through.
            var gutter = new VisualElement
            {
                name = "ruitk-splitter-gutter",
                pickingMode = PickingMode.Ignore,
                style = { position = Position.Absolute, backgroundColor = BuilderPalette.Panel },
            };
            if (vertical)
            {
                gutter.style.left = 0f;
                gutter.style.right = 0f;
                gutter.style.height = 6f;
                gutter.style.borderTopWidth = 1f;
                gutter.style.borderBottomWidth = 1f;
                gutter.style.borderTopColor = BuilderPalette.Line;
                gutter.style.borderBottomColor = BuilderPalette.Line;
            }
            else
            {
                gutter.style.top = 0f;
                gutter.style.bottom = 0f;
                gutter.style.width = 6f;
                gutter.style.borderLeftWidth = 1f;
                gutter.style.borderRightWidth = 1f;
                gutter.style.borderLeftColor = BuilderPalette.Line;
                gutter.style.borderRightColor = BuilderPalette.Line;
            }
            split.hierarchy.Add(gutter);

            VisualElement tracked = null;
            VisualElement trackedNext = null;

            // POC: the 6px splitter sits in the LANE between the two panes — on
            // poc-boot.png the preview body runs 72..451 (the full 380), the hsplit
            // is 452..457 and the SOURCE title starts at 458 with nothing between.
            // Centering the band on the first pane's edge instead left the lane's
            // trailing pixels bare whenever TwoPaneSplitView reserves one, which is
            // the 3px of empty panel that showed up above the SOURCE title.
            void Place()
            {
                if (split.childCount == 0)
                    return;
                var first = split[0];
                if (!ReferenceEquals(tracked, first))
                {
                    tracked = first;
                    first.RegisterCallback<GeometryChangedEvent>(_ => Place());
                }
                var second = split.childCount > 1 ? split[1] : null;
                if (second != null && !ReferenceEquals(trackedNext, second))
                {
                    trackedNext = second;
                    second.RegisterCallback<GeometryChangedEvent>(_ => Place());
                }
                var box = first.layout;
                if (float.IsNaN(box.width) || float.IsNaN(box.height))
                    return;
                var next = second != null ? second.layout : box;
                bool haveNext = second != null
                    && !float.IsNaN(next.width) && !float.IsNaN(next.height);
                float start = Mathf.Round(vertical ? box.yMax : box.xMax);
                float lane = haveNext
                    ? Mathf.Round(vertical ? next.yMin : next.xMin) - start
                    : 0f;
                bool inLane = lane >= 6f;
                // When TwoPaneSplitView reserves less than the 6px lane, the band
                // has to overhang one of the panes. Centering it on the boundary
                // put 2px of gutter INSIDE the second pane, which ate 2 of the
                // 12px inset the POC keeps between the splitter and "#preview"'s
                // stage. The overhang goes to the first pane (the canvas, an
                // infinite surface with no measurable chrome) instead, so the band
                // ends exactly where the second pane's content box starts.
                float trailing = start + lane;
                if (vertical)
                {
                    gutter.style.top = inLane ? start : trailing - 6f;
                    gutter.style.height = inLane ? lane : 6f;
                }
                else
                {
                    gutter.style.left = inLane ? start : trailing - 6f;
                    gutter.style.width = inLane ? lane : 6f;
                }
            }

            // TwoPaneSplitView rebuilds its resizer whenever it re-inits (first
            // geometry pass, child changes), so the pass is idempotent and re-runs.
            void Apply()
            {
                Place();
                var anchor = split.Q(null, "unity-two-pane-split-view__dragline-anchor");
                if (anchor == null)
                    return;
                var dragline = split.Q(null, "unity-two-pane-split-view__dragline");
                if (vertical)
                {
                    anchor.style.height = 6f;
                    if (dragline != null)
                    {
                        dragline.style.height = 6f;
                        dragline.style.top = 0f;
                    }
                    BuilderCursor.Set(anchor, MouseCursor.ResizeVertical);
                }
                else
                {
                    anchor.style.width = 6f;
                    if (dragline != null)
                    {
                        dragline.style.width = 6f;
                        dragline.style.left = 0f;
                    }
                    BuilderCursor.Set(anchor, MouseCursor.ResizeHorizontal);
                }
                anchor.style.backgroundColor = BuilderPalette.Transparent;
                if (dragline != null)
                    dragline.style.backgroundColor = BuilderPalette.Transparent;
                if (anchor.userData is string tag && tag == "ruitk-gutter")
                    return;
                anchor.userData = "ruitk-gutter";
                // POC "#vsplit:hover { background: var(--accent) }" — the band, not
                // the invisible anchor, is what the user sees light up.
                anchor.RegisterCallback<MouseEnterEvent>(_ => gutter.style.backgroundColor = BuilderPalette.Accent);
                anchor.RegisterCallback<MouseLeaveEvent>(_ => gutter.style.backgroundColor = BuilderPalette.Panel);
            }

            split.schedule.Execute(Apply).Every(120).ForDuration(2000);
            split.RegisterCallback<GeometryChangedEvent>(_ => Apply());
        }

        /// <summary>POC "#toolbar .sep { margin: 0 4px }" inside a "gap: 8px" row —
        /// 12px of air on each side. UI Toolkit has no row gap here (the buttons
        /// carry 4px margins instead), so the separator owns the other 8.</summary>
        private static VisualElement Separator() => new VisualElement
        {
            style =
            {
                width = 1f, height = 20f, backgroundColor = BuilderPalette.Line,
                marginLeft = 8f, marginRight = 8f, flexShrink = 0f,
            },
        };

        private void SetActiveMode(int lod)
        {
            _layerSelect?.SetValueWithoutNotify(
                s_layerLabels[Mathf.Clamp(lod, 0, s_layerLabels.Length - 1)]);
        }

        /// <summary>POC ".pane-title": uppercase 11px dim label on panel2 with a
        /// line under it, optional mini buttons, and an accent name pinned right.</summary>
        private static VisualElement PaneTitle(string left, out Label rightName, params Label[] buttons)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    justifyContent = Justify.SpaceBetween,
                    flexShrink = 0f,
                    backgroundColor = BuilderPalette.Panel2,
                    // POC ".pane-title { padding: 7px 12px }" measures a 30px band
                    // plus its 1px rule (poc-l1-cards.png column x=60: 41..70 fill,
                    // 71 border). Unity's font metrics add a pixel at the same
                    // padding, so the band is pinned rather than padded.
                    height = 31f,
                    paddingLeft = 12f, paddingRight = 12f,
                    paddingTop = 0f, paddingBottom = 0f,
                    borderBottomWidth = 1f,
                    borderBottomColor = BuilderPalette.Line,
                },
            };
            // POC ".pane-title { letter-spacing: .08em }" — tracked out like every
            // other uppercase label in the window (.sec-label / .lib-sec).
            row.Add(new Label(left) { style = { fontSize = 11f, color = BuilderPalette.Dim, letterSpacing = 1f } });
            var right = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center },
            };
            foreach (var button in buttons)
                right.Add(button);
            rightName = new Label { style = { fontSize = 11f, color = BuilderPalette.Accent, marginLeft = 8f } };
            right.Add(rightName);
            row.Add(right);
            return row;
        }

        /// <summary>POC ".pane-title button": a tiny panel2 pill with a line
        /// border — "edit", "apply (Ctrl+Enter)", "cancel (Esc)".</summary>
        private static Label MiniButton(string text, string tooltip, System.Action onClick)
        {
            var button = new Label(text)
            {
                tooltip = tooltip,
                style =
                {
                    fontSize = 10f,
                    color = BuilderPalette.Text,
                    backgroundColor = BuilderPalette.Panel2,
                    borderTopWidth = 1f, borderBottomWidth = 1f,
                    borderLeftWidth = 1f, borderRightWidth = 1f,
                    borderTopColor = BuilderPalette.Line, borderBottomColor = BuilderPalette.Line,
                    borderLeftColor = BuilderPalette.Line, borderRightColor = BuilderPalette.Line,
                    borderTopLeftRadius = 3f, borderTopRightRadius = 3f,
                    borderBottomLeftRadius = 3f, borderBottomRightRadius = 3f,
                    paddingLeft = 7f, paddingRight = 7f,
                    paddingTop = 1f, paddingBottom = 1f,
                    marginLeft = 6f,
                },
            };
            button.RegisterCallback<PointerDownEvent>(_ => onClick());
            // POC ".mini:hover { border-color: var(--accent) }".
            button.RegisterCallback<MouseEnterEvent>(_ => SetBorderColor(button, BuilderPalette.Accent));
            button.RegisterCallback<MouseLeaveEvent>(_ => SetBorderColor(button, BuilderPalette.Line));
            BuilderCursor.Set(button, MouseCursor.Link);
            return button;
        }

        private static VisualElement BuildLegend()
        {
            var legend = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    // POC ".legend { margin-left: auto }" and nothing on the right:
                    // the last label ends exactly on the toolbar's 12px padding.
                    marginLeft = StyleKeyword.Auto,
                },
            };
            void AddDot(string label, Color color)
            {
                legend.Add(new VisualElement
                {
                    style =
                    {
                        width = 9f, height = 9f, borderTopLeftRadius = 5f, borderTopRightRadius = 5f,
                        borderBottomLeftRadius = 5f, borderBottomRightRadius = 5f,
                        backgroundColor = color, marginRight = 4f, marginLeft = 12f,
                    },
                });
                legend.Add(new Label(label)
                {
                    style = { fontSize = 11f, color = new Color(0.55f, 0.55f, 0.59f) },
                });
            }
            AddDot("component", new Color(0.31f, 0.76f, 0.97f));
            AddDot("hook module", new Color(0.51f, 0.78f, 0.52f));
            AddDot("style module", new Color(0.81f, 0.58f, 0.85f));
            AddDot("usage edge", new Color(0.36f, 0.55f, 0.69f));
            return legend;
        }

        /// <summary>UB-89: fully consumes a key the builder handled.
        /// StopPropagation only ends UI Toolkit's own propagation — the
        /// underlying IMGUI event carries on to the Editor, which is why
        /// Ctrl+Z in the builder ALSO ran Unity's global Undo and Ctrl+Y its
        /// Redo, mutating the scene behind the user's back. Using the imgui
        /// event is what tells the Editor the keystroke is spoken for, so the
        /// builder's shortcuts stay inside the builder's window.</summary>
        private static void ConsumeKey(KeyDownEvent evt)
        {
            evt.StopImmediatePropagation();
            // PreventDefault is obsolete in Unity 6 (CS0618) and was redundant:
            // consuming the underlying IMGUI event is the part that stops the
            // Editor acting on the same keystroke.
            evt.imguiEvent?.Use();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (!evt.ctrlKey && !evt.commandKey)
            {
                // UB-74: unmodified Delete/Escape used to reach nothing at all.
                // Both are ignored while a text surface owns the keyboard, so
                // Delete still deletes CHARACTERS inside an editor.
                if (TypingTargetFocused())
                    return;
                if (evt.keyCode == KeyCode.Delete)
                {
                    DeleteSelection();
                    ConsumeKey(evt);
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    CancelActiveEdit();
                    ConsumeKey(evt);
                }
                return;
            }

            switch (evt.keyCode)
            {
                case KeyCode.S:
                    SaveAll();
                    ConsumeKey(evt);
                    break;
                // UB-73: undo walks the ACTION ledger, not the focus file's own
                // stack — a gesture that touched two files reverts as one step
                // and from whichever file happens to be in focus.
                case KeyCode.Z:
                    if (evt.shiftKey)
                        RedoAction();
                    else
                        UndoAction();
                    ConsumeKey(evt);
                    break;
                case KeyCode.Y:
                    RedoAction();
                    ConsumeKey(evt);
                    break;
            }
        }

        /// <summary>True while a text-editing surface holds focus. The canvas
        /// keyboard model must never fire under one — Delete there means "delete
        /// a character", and Escape is already the field's own cancel.</summary>
        private bool TypingTargetFocused()
        {
            if (_inlineEditor != null && _inlineEditor.IsOpen)
                return true;
            return IsTypingSurface(
                rootVisualElement?.focusController?.focusedElement as VisualElement);
        }

        /// <summary>Whether this element (or an ancestor) is something the user
        /// types into — a text field, or a selectable text element such as the
        /// diagnostics console. Those own their own Delete and Escape.</summary>
        private static bool IsTypingSurface(VisualElement element)
        {
            for (var walk = element; walk != null; walk = walk.parent)
                if (walk is TextField || (walk is TextElement && walk.focusable))
                    return true;
            return false;
        }

        /// <summary>UB-74: Delete removes whatever is selected. The ROW selection
        /// wins over the card selection — it is the finer-grained of the two and
        /// the one the user set most recently. Every guard the context menus
        /// enforce is enforced here too, by routing to the same methods: the
        /// return root is never deletable, a continuation clause deletes as a
        /// clause, a construct head deletes its whole block, and a card still
        /// has to pass the referenced-by check.</summary>
        private void DeleteSelection()
        {
            string rowPath = _canvasHost?.SelectedRowPath;
            int rowIdx = _canvasHost?.SelectedRowIndex ?? -1;
            if (!string.IsNullOrEmpty(rowPath) && rowIdx >= 0)
            {
                var node = _canvasHost.FindNode(Path.GetFullPath(rowPath));
                if (node != null && node.Markup != null && rowIdx < node.Markup.Count)
                {
                    var row = node.Markup[rowIdx];
                    if (row.Kind == BuilderCardLineKind.Directive)
                    {
                        if (row.ClauseIndex > 0)
                            DeleteClause(rowPath, row);
                        else
                            DeleteLinesInFile(
                                rowPath, row.DirectiveLine,
                                System.Math.Max(row.DirectiveLine, row.CloseLine),
                                "delete " + row.BadgeText + " block");
                        _canvasHost.ClearRowSelection();
                        return;
                    }
                    if (rowIdx == BuilderCanvasDrawing.FirstElementRow(node))
                    {
                        Toast("The return root can't be deleted — a component must return one node.");
                        return;
                    }
                    DeleteElementRow(rowPath, row);
                    _canvasHost.ClearRowSelection();
                    return;
                }
            }
            // UB-94: a selected hook chip / import / island / style entry is a
            // plain source-line range, so Delete removes exactly those lines
            // through the same primitive every menu delete uses.
            string linePath = _canvasHost?.SelectedLinePath;
            if (!string.IsNullOrEmpty(linePath) && _canvasHost.SelectedLineFrom > 0)
            {
                DeleteLinesInFile(
                    linePath, _canvasHost.SelectedLineFrom, _canvasHost.SelectedLineTo,
                    "delete " + _canvasHost.SelectedLineLabel);
                _canvasHost.ClearRowSelection();
                return;
            }

            // UB-87: deleting a CARD deletes a FILE off disk, and the card
            // selection is never empty — the window rings the focus file's card
            // from the frame it opens. So Delete with no row selected used to
            // destroy the file the user had just opened, and because deleting a
            // row clears the row selection, two Delete presses in a row did
            // exactly that. A file delete now always asks first, in a modal the
            // user has to read, naming the file.
            string cardPath = _canvasHost?.SelectedCardPath;
            int index = string.IsNullOrEmpty(cardPath) ? -1 : _canvasHost.NodeIndexOf(cardPath);
            if (index < 0)
            {
                Toast("Nothing selected to delete");
                return;
            }
            _canvasHost.RequestDeleteCard(index);
        }

        /// <summary>Escape cancels the innermost active edit: the floating inline
        /// editor first, then a source-pane edit session, then the selection
        /// itself.</summary>
        private void CancelActiveEdit()
        {
            if (_inlineEditor != null && _inlineEditor.IsOpen)
            {
                _inlineEditor.Close(commitIfChanged: false);
                return;
            }
            if (_sourceSnapshot != null)
            {
                CancelSourceEdit();
                return;
            }
            _canvasHost?.ClearRowSelection();
        }

        /// <summary>The in-memory home of a tree started from the empty state.
        /// Nothing is ever written here — Save relocates every session under it
        /// into the folder the user picks, and refuses to write until it has
        /// one. It sits under Assets deliberately: `IsReadOnlyLocation` treats
        /// anything outside the project as immutable, so a provisional path in
        /// the temp directory would open the first card READ-ONLY and refuse
        /// every edit.</summary>
        /// <para>GetFullPath, not Combine: `Application.dataPath` comes back with
        /// FORWARD slashes on Windows and Combine only inserts a separator
        /// without rewriting the ones already there, so the root kept forward
        /// slashes in its prefix while every session path had been through
        /// GetFullPath and was all backslashes. The prefix test never matched,
        /// the relocation was skipped, and Save wrote the module at its
        /// PROVISIONAL path (UB-119).</para>
        /// <para>UB-120: the folder name ends in '~', which Unity's Asset
        /// Database ignores wholesale. If a provisional path ever reaches disk
        /// again, Unity will not import it, will not generate a .meta, and the
        /// source generator will not compile it — instead of what happened
        /// once: a stray module became a real asset whose single bad token
        /// failed Assembly-CSharp and cascaded into Burst assembly-resolution
        /// errors across the whole project.</para>
        private static string UnsavedRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "__RuitkBuilderUnsaved__~"));

        /// <summary>UB-113: a tree begun from the empty state has no folder to
        /// infer, so Save asks for one, once, and moves the pending sessions
        /// there before writing. Returns false when the user cancels or picks
        /// somewhere Unity cannot see.</summary>
        private bool ResolveUnsavedLocation()
        {
            string root = UnsavedRoot;
            var pending = new System.Collections.Generic.List<string>();
            foreach (string path in _workspace.PendingNewFiles)
                if (Path.GetFullPath(path)
                    .StartsWith(root, System.StringComparison.OrdinalIgnoreCase))
                    pending.Add(path);
            if (pending.Count == 0)
                return true;

            string chosen = UnityEditor.EditorUtility.SaveFolderPanel(
                "Where should this UI live?", Application.dataPath, "");
            if (string.IsNullOrEmpty(chosen))
            {
                Toast("Save cancelled - pick a folder to save a new tree");
                return false;
            }
            string projectRoot =
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
            string full = Path.GetFullPath(chosen).Replace('\\', '/');
            if (!full.StartsWith(projectRoot, System.StringComparison.OrdinalIgnoreCase))
            {
                UnityEditor.EditorUtility.DisplayDialog(
                    "Outside the project",
                    "Pick a folder inside this Unity project - a .uitkx outside it is "
                    + "never compiled.",
                    "OK");
                return false;
            }

            foreach (string path in pending)
            {
                string relative = path.Substring(root.Length).TrimStart('\\', '/');
                string moved = Path.GetFullPath(Path.Combine(chosen, relative));
                if (File.Exists(moved))
                {
                    UnityEditor.EditorUtility.DisplayDialog(
                        "Already exists", Path.GetFileName(moved) + " is already there.", "OK");
                    return false;
                }
                if (!_workspace.Relocate(path, moved))
                    continue;
                if (string.Equals(_focusFile, path, System.StringComparison.OrdinalIgnoreCase))
                    _focusFile = moved;
                _relocatedOnSave = true;
            }
            return true;
        }

        /// <summary>UB-116: canvas edits splice LINES into the buffer, so an
        /// inserted tag carries the indentation the splice guessed rather than
        /// the file's canonical shape — the owner's "the formatting of the text
        /// doesnt work". Save runs every dirty buffer through the same AST
        /// formatter the source pane's apply uses. A buffer that does not format
        /// cleanly is left EXACTLY as it was: a half-typed file must still save,
        /// and the formatter's non-Formatted outcomes are data-loss guards, not
        /// results to write.</summary>
        private void FormatDirtyBuffers()
        {
            foreach (var session in _workspace.Sessions)
            {
                if (session.IsReadOnly || !session.IsDirty
                    || _workspace.IsPendingDelete(session.FilePath))
                    continue;
                string before = session.BufferText;
                string formatted;
                try
                {
                    formatted = BuilderLanguage.FormatText(
                        before, session.FilePath, out var outcome);
                    if (outcome != Ruitk.Language.Formatter.FormatOutcome.Formatted
                        || string.IsNullOrEmpty(formatted)
                        || string.Equals(formatted, before, System.StringComparison.Ordinal))
                        continue;
                }
                catch (System.Exception)
                {
                    continue;
                }
                session.ApplyEdit(formatted);
                _ledger.Record(session.FilePath, before, formatted);
                if (string.Equals(Path.GetFullPath(session.FilePath),
                        Path.GetFullPath(_focusFile), System.StringComparison.OrdinalIgnoreCase))
                    _codeField?.SetContent(formatted, _focusFile, KnownElementsOrNull());
                SyncLspBuffer(session.FilePath, formatted, open: false);
                _canvasHost?.RefreshGraph(session.FilePath, ReadBufferOrDisk);
            }
        }

        private void SaveAll()
        {
            if (!ResolveUnsavedLocation())
                return;
            _ledger.Begin("format on save");
            FormatDirtyBuffers();
            _ledger.End();
            // UB-88: Save is where a deletion stops being reversible, so it is
            // the only place worth asking — up to that moment the user can undo
            // it, abort, or simply never save. The list names every file.
            var pending = _workspace.PendingDeletes;
            if (pending.Count > 0)
            {
                var names = new System.Text.StringBuilder();
                foreach (string path in pending)
                    names.Append("  ").Append(Path.GetFileName(path)).Append('\n');
                if (!UnityEditor.EditorUtility.DisplayDialog(
                        "Save deletes files",
                        "Saving will delete " + pending.Count + " file(s) from the project:\n\n"
                        + names
                        + "\nThey are moved to the trash, not erased. Everything else "
                        + "in this save is a normal text edit.",
                        "Save and delete",
                        "Cancel"))
                {
                    Toast("Save cancelled");
                    return;
                }
            }
            bool hmrActive = Ruitk.EditorSupport.HMR.UitkxHmrController.IsActive;
            int written = _workspace.SaveAll();
            BuilderSaveMetrics.RecordSaveBatch(written, hmrActive);
            if (written > 0)
                Toast($"Saved {written} file(s)");
            RefreshChrome();
            // A relocated tree now exists on disk under a different path than
            // the graph was built from, so it is re-read rather than patched.
            if (_relocatedOnSave)
            {
                _relocatedOnSave = false;
                MountCanvas();
            }
        }

        [System.NonSerialized] private bool _relocatedOnSave;

        private void NewFile()
        {
            string dir = string.IsNullOrEmpty(_focusFile)
                ? null
                : Path.GetDirectoryName(_focusFile);
            if (dir == null)
            {
                Toast("Open a tree first - new files are created beside it");
                return;
            }
            if (_canvasHost == null)
                return;
            _canvasHost.ShowCreateMenuAtPointer();
        }

        private static readonly System.Collections.Generic.Dictionary<string, (string Title, string Placeholder)>
            s_createPrompts = new System.Collections.Generic.Dictionary<string, (string, string)>
            {
                ["Component"] = ("new component", "PascalCaseName"),
                ["Style"] = ("new style module", "camelCaseName"),
                ["Hooks"] = ("new hook module", "useSomething"),
                ["Utils"] = ("new util module", "camelCaseName"),
            };

        /// <summary>POC openCreateMenu → openNameMenu: an at-cursor popup with a
        /// title, a placeholder-only input, an inline error row and a "Create"
        /// row. An invalid or duplicate name shows the error IN PLACE.</summary>
        private void ShowCreatePrompt(string kind, float worldX, float worldY) =>
            CreateModule(kind, worldX, worldY);

        /// <summary>UB-113: with a tree open, a new module lands relative to the
        /// focus file. With NO tree, it lands under a provisional root that
        /// exists only in memory — the modules are real cards and real buffers,
        /// and Save is where the builder asks which folder they belong in. That
        /// keeps "nothing on disk until Save" true even for the very first
        /// file, which has no folder to be inferred from.</summary>
        private void CreateModule(string kind, float worldX, float worldY)
        {
            bool rooted = !string.IsNullOrEmpty(_focusFile);
            string dir = rooted
                ? Path.GetDirectoryName(_focusFile)
                : UnsavedRoot;
            if (dir == null)
            {
                Toast("Could not resolve a location for the new module");
                return;
            }
            var prompt = s_createPrompts.TryGetValue(kind, out var found)
                ? found
                : ("new file", "Name");
            BuilderSearchMenu.ShowNamePrompt(
                prompt.Item1,
                prompt.Item2,
                name => ValidateNewName(kind, name),
                name =>
                {
                    // A brand-new tree has no parent component, so its first
                    // component owns its folder rather than nesting under a
                    // "components" directory that has nothing above it.
                    string created = BuilderNewFileDialog.PathFor(dir, kind, name, asRoot: !rooted);
                    if (created == null || File.Exists(created)
                        || _workspace.TryGet(Path.GetFullPath(created)) != null)
                    {
                        Toast("Could not create " + name + " (already exists)");
                        return;
                    }
                    // UB-111: a new module is a PENDING session, not a file. Save
                    // writes it (creating its folder), Abort drops it, and the
                    // ledger entry lets Ctrl+Z take it back — the same contract
                    // every other edit already obeyed.
                    string full = Path.GetFullPath(created);
                    if (_workspace.CreateNew(
                            full, BuilderNewFileDialog.TemplateFor(kind, name),
                            needsLocation: !rooted) == null)
                    {
                        Toast("Could not create " + name);
                        return;
                    }
                    _ledger.Begin("create " + Path.GetFileName(created));
                    _ledger.RecordCreation(full);
                    _ledger.End();
                    RefreshHistoryPanel();
                    _canvasHost?.PlaceNewCard(full, worldX, worldY);
                    Toast("Created " + Path.GetFileName(created) + " - applies on Save");
                    RefreshChrome();
                    OpenAdditionalFile(full);
                });
        }

        private string ValidateNewName(string kind, string name)
        {
            if (string.IsNullOrEmpty(name))
                return "name required";
            bool pascal = kind == "Component";
            bool hook = kind == "Hooks";
            if (hook && !System.Text.RegularExpressions.Regex.IsMatch(name, "^use[A-Z][A-Za-z0-9]*$"))
                return "hook names start with 'use' (useSomething)";
            if (!hook && pascal
                && !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Z][A-Za-z0-9]*$"))
                return "PascalCase identifier required";
            if (!hook && !pascal
                && !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][A-Za-z0-9]*$"))
                return "camelCase identifier required";
            foreach (var node in _canvasHost?.Nodes
                ?? new System.Collections.Generic.List<BuilderCanvasNode>())
            {
                if (string.Equals(node.Title, name, System.StringComparison.OrdinalIgnoreCase))
                    return name + " already exists";
            }
            return null;
        }

        public void OpenAdditionalFile(string filePath)
        {
            _focusFile = Path.GetFullPath(filePath);
            _workspace.Open(_focusFile);
            MountCanvas();
            RefreshChrome();
        }

        private void AbortAll()
        {
            int reverted = _workspace.AbortAll();
            if (reverted > 0)
                Toast($"Discarded {reverted} buffer(s)");
            RefreshChrome();
        }

        private void OnWorkspaceChanged() => RefreshChrome();

        private void RefreshChrome()
        {
            hasUnsavedChanges = _workspace.HasUnsavedChanges;
            if (_statusLabel != null)
            {
                int open = 0, dirty = 0;
                foreach (var s in _workspace.Sessions)
                {
                    open++;
                    if (s.IsDirty)
                        dirty++;
                }
                _statusLabel.text = open == 0
                    ? "No tree open - use Assets > right-click > Open in RUITK UI Builder"
                    : $"{Path.GetFileName(_focusFile)}  |  {open} file(s), {dirty} dirty";
            }
        }

        /// <summary>Unity calls this from ITS prompt — closing the window, a
        /// domain reload, entering play mode. UB-118: it went straight to
        /// `_workspace.SaveAll()`, bypassing the window's own Save entirely, so
        /// a tree that had never been given a folder was written to the
        /// PROVISIONAL path and a buffer never got formatted. Routing through
        /// the same SaveAll the toolbar button uses means Unity's prompt asks
        /// for a location exactly like the button does, and a cancelled
        /// location leaves the window still dirty rather than reporting a save
        /// that did not happen.</summary>
        public override void SaveChanges()
        {
            SaveAll();
            if (_workspace.HasUnsavedChanges)
                return;
            base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            _workspace.AbortAll();
            base.DiscardChanges();
        }
    }
}
#endif
