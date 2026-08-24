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

        /// <param name="pendingText">Text for a module that is NOT on disk yet, or
        /// that is being replaced. It arrives as a pending buffer like every other
        /// edit, so Save is still the only thing that writes.</param>
        public static BuilderWindow OpenFor(string uitkxFilePath, string pendingText = null)
        {
            var window = OpenEmpty();
            window._focusFile = uitkxFilePath;
            window.LoadTreeFor(uitkxFilePath);
            if (pendingText == null)
            {
                window._workspace.Open(uitkxFilePath);
            }
            else if (window._workspace.CreateNew(uitkxFilePath, pendingText) == null)
            {
                var module = window._workspace.Open(uitkxFilePath);
                if (module != null && !module.IsReadOnly)
                    module.ApplyEdit(BuilderModule.NormalizeLf(pendingText));
            }
            window.MountCanvas();
            window.RefreshChrome();
            return window;
        }

        /// <summary>Brings in the whole TREE the file belongs to. Everything the
        /// canvas draws is a projection of what is loaded, so opening a single
        /// module drew a single card - the tree was there in the model and nothing
        /// ever entered it.
        ///
        /// A load REPLACES the tree, which is why unsaved work vetoes it: the file
        /// is opened into the tree already present instead, and the user is told
        /// why they are looking at one card. Losing an unsaved buffer to a
        /// right-click in the Project window is not a trade worth making.</summary>
        private void LoadTreeFor(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || _workspace.TryGet(filePath) != null)
                return;
            if (_workspace.HasUnsavedChanges)
            {
                _workspace.Open(filePath);
                Toast("Opened " + Path.GetFileName(filePath)
                    + " on its own - save or abort first to load its whole tree");
                return;
            }
            _workspace.LoadTree(filePath);
            // A focus the scan could not reach - a module outside any tree root -
            // still gets its own card rather than an empty canvas.
            if (_workspace.TryGet(filePath) == null)
                _workspace.Open(filePath);
        }

        private void OnEnable()
        {
            // Every ledger change records WHICH MODULE it belongs to, so a replay
            // still finds it after a rename has moved the path out from under it.
            // The ledger is NonSerialized and comes back empty from a domain
            // reload, so this is re-wired here rather than at mount time.
            _ledger.IdOf = path => _workspace.TryGet(path)?.Id;
            _workspace.Changed -= OnWorkspaceChanged;
            _workspace.Changed += OnWorkspaceChanged;
            BuilderLspService.DiagnosticsPublished -= OnLspDiagnosticsPublished;
            BuilderLspService.DiagnosticsPublished += OnLspDiagnosticsPublished;
            BuilderAssetEvents.UitkxImported -= OnUitkxImported;
            BuilderAssetEvents.UitkxImported += OnUitkxImported;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= JournalTree;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += JournalTree;
            // The tree has just come back through Unity's serializer. If anything
            // did not survive it, THIS is where it has to be said: a broken round
            // trip that goes unreported resurfaces later as an inexplicable bug in
            // whatever trusted it (Plans~/BUILDER_TREE_MODEL.md, mitigation 3).
            foreach (string problem in _workspace.Tree.Validate())
                Debug.LogError(
                    "[RUITK Builder] tree invariant broken after reload: " + problem);
            // Deferred: the window may be opening ON a file, and that load runs
            // after this. Asking then would offer to restore over a tree the user
            // just asked for; by the delayed call the tree is either there or the
            // window really did come up empty.
            UnityEditor.EditorApplication.delayCall += OfferJournalRestore;
            // Sessions just deserialized across the domain reload; any file
            // that changed externally WHILE the old domain was alive missed its
            // import event, so sweep once — the panes mount after this and
            // read the adopted buffers.
            var openPaths = new System.Collections.Generic.List<string>();
            foreach (var session in _workspace.Modules)
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
            string focusFull = FocusFull;
            // An external change alters what the preview would render, so it has to
            // rebuild. This was simply missing: the card refreshed and the preview
            // kept showing the pre-change build.
            if (changed.Count > 0)
                NotifyBufferChanged();
            foreach (string path in changed)
            {
                _canvasHost?.RefreshGraph(path);
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
                || !string.Equals(Path.GetFullPath(path), FocusFull,
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
            _traceButton = ToolbarButton("Trace", TogglePreviewTrace);
            toolbar.Add(_traceButton);
            _foldersButton = ToolbarButton("Folders", ToggleFolders);
            toolbar.Add(_foldersButton);
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
            // The centre holds two projections of the same tree - the canvas, and
            // the folders it lives in. One is visible at a time; both read the
            // workspace, so neither can be stale with respect to the other.
            var centerPane = new VisualElement
            {
                name = "builder-center",
                style = { minWidth = 300f, flexGrow = 1f, minHeight = 0f },
            };
            var canvasPane = new VisualElement
            {
                name = "builder-canvas",
                style = { flexGrow = 1f, minHeight = 0f },
            };
            // POC "#canvasWrap { cursor: grab }".
            BuilderCursor.Set(canvasPane, MouseCursor.Pan);
            var foldersPane = new VisualElement
            {
                name = "builder-folders",
                style = { flexGrow = 1f, minHeight = 0f, display = DisplayStyle.None },
            };
            centerPane.Add(canvasPane);
            centerPane.Add(foldersPane);
            innerSplit.Add(centerPane);
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
            // The choice of view survives a domain reload, so it has to be applied
            // after one - the visual tree is rebuilt showing the canvas.
            ApplyCenterView();
            RefreshChrome();
        }

        [System.NonSerialized] private BuilderFolderPane _folderPane;
        [System.NonSerialized] private Button _foldersButton;
        [SerializeField] private bool _showFolders;

        /// <summary>Swaps the centre between the canvas and the folder view. Both
        /// are projections of the same tree, so this shows the other one rather
        /// than loading anything.</summary>
        private void ToggleFolders()
        {
            _showFolders = !_showFolders;
            ApplyCenterView();
        }

        private void ApplyCenterView()
        {
            var canvas = rootVisualElement?.Q("builder-canvas");
            var folders = rootVisualElement?.Q("builder-folders");
            if (canvas == null || folders == null)
                return;
            canvas.style.display = _showFolders ? DisplayStyle.None : DisplayStyle.Flex;
            folders.style.display = _showFolders ? DisplayStyle.Flex : DisplayStyle.None;
            if (_foldersButton != null)
                _foldersButton.text = _showFolders ? "Canvas" : "Folders";
            if (_showFolders)
                MountFolders();
        }

        private void MountFolders()
        {
            var container = rootVisualElement?.Q("builder-folders");
            if (container == null)
                return;
            _folderPane ??= new BuilderFolderPane
            {
                Modules = () => _workspace.Modules,
                OnToast = Toast,
                OnOpen = OpenFileFromCanvas,
                OnMove = MoveModuleToFolder,
                OnMoveFolder = MoveFolderToFolder,
            };
            // A reload rebuilds the visual tree but not the pane, so an empty
            // container means the rows it holds belong to a panel that is gone.
            if (container.childCount == 0)
                _folderPane.Attach(container);
            else
                _folderPane.Rebuild();
        }

        /// <summary>A drop in the folder view. The move is a tree change like any
        /// other - it records, it re-derives every specifier it invalidates, and
        /// nothing reaches disk until Save.</summary>
        private void MoveModuleToFolder(string modulePath, string targetFolder)
        {
            var module = _workspace.TryGet(modulePath);
            if (module == null)
                return;
            if (module.IsReadOnly)
            {
                Toast("Read-only file");
                return;
            }
            // Dropping a component that OWNS its folder moves that FOLDER, rather
            // than tipping its file out into the destination: the house layout is
            // ComponentName/ComponentName.uitkx with its children inside.
            string destination = module.OwnsFolder
                ? Path.Combine(targetFolder, module.Name)
                : targetFolder;
            // ... and it cannot be dropped INTO its own subtree, which would make
            // the folder its own ancestor and drag every child in after it.
            if (module.OwnsFolder && IsInside(targetFolder, module.Folder))
            {
                Toast("Can't move " + module.Name + " inside itself");
                return;
            }
            string to = Path.GetFullPath(
                Path.Combine(destination, Path.GetFileName(modulePath)));
            if (!_workspace.IsPathAvailable(to))
            {
                Toast(Path.GetFileName(to) + " is already there");
                return;
            }
            _ledger.Begin("move " + Path.GetFileName(modulePath));
            if (!MoveModule(modulePath, to))
            {
                _ledger.End();
                Toast("Could not move " + Path.GetFileName(modulePath));
                return;
            }
            _ledger.RecordMove(modulePath, to);
            _ledger.End();
            RefreshHistoryPanel();
            RefreshChrome();
            MountCanvas();
            _folderPane?.Rebuild();
            Toast("Moved " + Path.GetFileName(to) + " - applies on Save");
        }

        /// <summary>A folder dropped on another folder.
        ///
        /// A folder is not something the tree HOLDS - it is where its modules
        /// sit - so moving one means moving everything under it. When a component
        /// owns the folder, that is exactly what moving the component already
        /// does, subtree and all; otherwise every module underneath is re-filed
        /// individually, keeping its position relative to the folder that moved.
        ///
        /// The imports are captured ONCE around the whole batch. Reconciling per
        /// move would re-spell specifiers against modules that are half-moved, and
        /// each pass would be correct about a state nobody ever sees.</summary>
        private void MoveFolderToFolder(string sourceFolder, string targetFolder)
        {
            string source = Path.GetFullPath(sourceFolder);
            string leaf = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
            if (leaf.Length == 0)
                return;
            string destination = Path.GetFullPath(Path.Combine(targetFolder, leaf));
            if (string.Equals(destination, source, System.StringComparison.OrdinalIgnoreCase))
                return;

            // The component that owns the folder carries it, which is the same
            // move by a shorter route - and the one that keeps the house layout.
            foreach (var module in _workspace.Modules)
                if (module.OwnsFolder
                    && string.Equals(Path.GetFullPath(module.Folder), source,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    MoveModuleToFolder(module.FilePath, targetFolder);
                    return;
                }

            var movers = new System.Collections.Generic.List<(BuilderModule Module, string To)>();
            foreach (var module in _workspace.Modules)
            {
                string folder = Path.GetFullPath(module.Folder ?? "");
                if (!IsInside(folder, source)
                    && !string.Equals(folder, source, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                if (module.IsReadOnly)
                {
                    Toast("Can't move " + leaf + " - it holds a read-only module");
                    return;
                }
                string relative = folder.Length > source.Length
                    ? folder.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar)
                    : string.Empty;
                movers.Add((module, Path.Combine(destination, relative)));
            }
            if (movers.Count == 0)
                return;

            _ledger.Begin("move " + leaf);
            var imports = _workspace.CaptureImports();
            foreach (var (module, to) in movers)
            {
                string from = module.FilePath;
                _workspace.MoveTo(from, to, module.Name);
                _canvasHost?.RepathLayout(from, module.FilePath, isFolder: false);
                if (string.Equals(FocusFull, Path.GetFullPath(from),
                        System.StringComparison.OrdinalIgnoreCase))
                    _focusFile = module.FilePath;
            }
            foreach (var rewrite in _workspace.ReconcileImports(imports))
                _ledger.Record(rewrite.FilePath, rewrite.Before, rewrite.After);
            _ledger.End();
            RefreshHistoryPanel();
            RefreshChrome();
            MountCanvas();
            _folderPane?.Rebuild();
            Toast("Moved " + leaf + " - applies on Save");
        }

        /// <summary>Whether <paramref name="path"/> is at or below
        /// <paramref name="folder"/>. Segment-wise: a character prefix would call
        /// "Panel" an ancestor of "PanelExtras".</summary>
        private static bool IsInside(string path, string folder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder))
                return false;
            string a = Path.GetFullPath(path);
            string b = Path.GetFullPath(folder);
            if (string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase))
                return true;
            return a.StartsWith(
                b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Takes the keyboard back for the WINDOW after a popup closes.
        ///
        /// Two things are racing it. The canvas remounts, replacing the element
        /// focus just landed on; and the create prompt is an EditorWindow that
        /// takes focus back on its way out. Unity then leaves the builder merely
        /// visible rather than active, which is why a right-click on the canvas
        /// did nothing until the user left-clicked it first (UB-199) - a popup
        /// cannot open from a window that is not the focused one.
        ///
        /// So it re-asserts rather than checking: a state test cannot see a
        /// closing window that has not taken focus back YET. It stops early only
        /// for a typing target, which means an editor the user is now in.</summary>
        private void ReclaimKeyboard(int attempts)
        {
            if (attempts <= 0)
                return;
            FocusExisting(focusRoot: true);
            rootVisualElement?.schedule.Execute(() =>
            {
                if (this == null || TypingTargetFocused())
                    return;
                ReclaimKeyboard(attempts - 1);
            }).ExecuteLater(30);
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
            _canvasHost.OnStyleRowContext = OnStyleRowContext;
            _canvasHost.OnToast = Toast;
            _canvasHost.OnSelect = index =>
            {
                var nodes = _canvasHost?.Nodes;
                if (nodes == null || index < 0 || index >= nodes.Count)
                    return;
                OpenFileFromCanvas(nodes[index].FilePath);
                _libraryPane?.SetSelected(nodes[index].FilePath, null);
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
            // UB-123: the alias is the name every reference to the module has to
            // spell, and the row truncates it. Double-click puts just the alias
            // on the clipboard — the specifier and the "import"/"from" chrome
            // are never what the user needs to paste.
            _canvasHost.OnImportContext = (importerPath, specifier) =>
            {
                string full = Path.GetFullPath(importerPath);
                string target = BuilderGraphService.MapSpecifier(full, specifier);
                var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>
                {
                    new BuilderSearchMenu.Item
                    {
                        Label = "remove import \"" + specifier + "\"",
                        OnPick = () => RemoveImportFrom(full, target, specifier),
                    },
                };
                // ShowSimple, like the card menu: one action needs no search field,
                // and Show placed the popup away from the row that opened it.
                BuilderSearchMenu.ShowSimple("import", items);
            };
            _canvasHost.OnCopyImportAlias = text =>
            {
                string alias = BuilderText.ImportAliasOf(text);
                if (string.IsNullOrEmpty(alias))
                {
                    Toast("Couldn't read that import's name");
                    return;
                }
                UnityEditor.EditorGUIUtility.systemCopyBuffer = alias;
                Toast("Copied \"" + alias + "\"");
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
            _canvasHost.OnRenameCard = ShowRenamePrompt;
            _canvasHost.Modules = () => _workspace.Modules;
            _canvasHost.ModuleAt = path => _workspace.TryGet(path);
            _canvasHost.OnDeleteFile = path =>
            {
                string full = Path.GetFullPath(path);
                if (BuilderWorkspace.IsReadOnlyLocation(full))
                {
                    Toast("Can't delete " + Path.GetFileName(full)
                        + " - read-only location: " + Path.GetDirectoryName(full));
                    return;
                }
                var module = _workspace.TryGet(full);
                if (module == null)
                {
                    Toast("Nothing to delete at " + Path.GetFileName(full));
                    return;
                }
                _ledger.Begin("delete " + Path.GetFileName(full));
                // One entry covers the module AND every reference to it, so a
                // single undo puts the tree back exactly as it was.
                StripReferencesTo(full);
                _ledger.RecordDeletion(full, module);
                _workspace.Delete(full);
                _ledger.End();
                RefreshHistoryPanel();
                Toast("Deleted " + Path.GetFileName(full) + " - applies on Save");
                RebindFocusIfMissing();
                RefreshChrome();
                MountCanvas();
            };
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
                container, _focusFile, OpenFileFromCanvas,
                graph =>
                {
                    _libraryPane?.SetWorkspaceEntries(graph);
                    _previewPane?.RefreshModuleNotes();
                    // The graph is what the set is built from, so this is where it
                    // stops being valid.
                    InvalidateKnownElements();
                    _codeField?.SetKnownElements(KnownElementsOrNull());
                });
        }

        /// <summary>UB-07: the element set for custom-tag colouring and the
        /// tier-2 unknown-element check — schema natives plus every export in
        /// the live graph, mirroring the LSP's BuildProjectElements. Null until
        /// BOTH sources are live, which suppresses UITKX0105/0109 instead of
        /// storming false errors during startup (same discipline as the LSP's
        /// initial-scan gate).</summary>
        [System.NonSerialized]
        private System.Collections.Generic.HashSet<string> _knownElementsCache;

        /// <summary>The known-element set changes only when the graph or the schema
        /// does, but it was REBUILT on every call - and it is passed to SetContent
        /// on every programmatic edit. That allocated a few hundred strings per
        /// edit, and worse: SetKnownElements skips its re-colour when the set is
        /// the SAME INSTANCE, so handing it a fresh one every time forced a second
        /// full re-colour of the source pane on each edit.</summary>
        private void InvalidateKnownElements() => _knownElementsCache = null;

        private System.Collections.Generic.HashSet<string> KnownElementsOrNull()
        {
            if (_knownElementsCache != null)
                return _knownElementsCache;
            _knownElementsCache = BuildKnownElements();
            return _knownElementsCache;
        }

        private System.Collections.Generic.HashSet<string> BuildKnownElements()
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
                var button = ToolbarButton(label, () => CreateModule(captured, 0f, 0f));
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
                _codeField.EditingFinished += NotifyBufferChanged;
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
            // Hand over the assembly this module was last BUILT into rather than
            // null. Passing null makes the pane scan for a matching [UitkxSource],
            // and every swap assembly in the session matches - so switching away
            // and back rendered whichever one the scan happened to reach first.
            _previewPane.ShowFile(
                _focusFile, session?.BufferText,
                _previewCompiler?.BuiltAssemblyFor(_focusFile));
            _codeField.SetContent(session?.BufferText ?? "", _focusFile, KnownElementsOrNull());
            _codeField.SetEditable(session != null && !session.IsReadOnly);
            // POC selectNode(): opening another file leaves source-edit mode.
            _codeField.SetEditing(_sourceSnapshot != null);
            SyncLspBuffer(_focusFile, session?.BufferText);
            ScheduleServerTokens();
            // The preview resolves its component out of a COMPILED assembly, and a
            // compile only ever ran in response to an EDIT. A tree nobody has edited
            // in THIS process had nothing to resolve and showed an empty stage - and
            // that is every unsaved tree after a domain reload or an editor restart,
            // because the buffers survive with the window while the assemblies do
            // not. So restarting Unity, the obvious thing to try, was the one thing
            // guaranteed to leave the preview blank. Mounting now asks for the
            // compile; it is debounced and skips modules whose text has not moved.
            NotifyBufferChanged();
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

        /// <summary>Pushes a buffer to the language server, opening the document
        /// first if it has not been opened yet. The open was previously gated on a
        /// caller flag AND the set insert, and && short-circuits: a path first
        /// synced by an edit rather than by a mount never entered the set, so it
        /// got didChange forever for a document the server had never been told
        /// about. A rename makes that routine, since the module arrives at a path
        /// nothing has opened.</summary>
        private async void SyncLspBuffer(string path, string textLf)
        {
            if (string.IsNullOrEmpty(path) || textLf == null)
                return;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (System.Exception)
            {
                return;
            }
            try
            {
                var client = await BuilderLspService.GetOrStartAsync();
                if (_lspOpened.Add(full))
                    client.DidOpen(full, textLf);
                else
                    client.DidChangeDebounced(full, textLf);
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

        /// <summary>Applies ONE history entry in one direction, and is the only
        /// place that knows what each change kind means. Undo walks the changes in
        /// reverse - a gesture that inserted then re-indented the same region has
        /// to unwind in the order it was written - and redo walks them forward.
        /// Buffer writes are collected rather than applied, so a jump across
        /// several entries settles the model first and redraws once.</summary>
        private bool ApplyEntry(
            BuilderActionLedger.Entry entry, bool undo,
            System.Collections.Generic.List<(string, string, string)> writes)
        {
            bool remounted = false;
            int count = entry.Changes.Count;
            for (int n = 0; n < count; n++)
            {
                var change = entry.Changes[undo ? count - 1 - n : n];
                if (change.IsDeletion)
                {
                    // Undo puts the SAME module back - identity, buffer and
                    // DiskPath intact - so a module that had a file still owns it.
                    if (undo)
                    {
                        remounted |= _workspace.Restore(change.Removed) != null;
                    }
                    else
                    {
                        change.Removed = _workspace.TryGet(change.FilePath);
                        remounted |= _workspace.Delete(change.FilePath);
                    }
                    continue;
                }
                if (change.IsCreation)
                {
                    // Nothing was written, so undoing a create removes the module.
                    // Its text rides on the entry so redo can put it back unchanged.
                    if (undo)
                    {
                        var pending = _workspace.TryGet(change.FilePath);
                        if (pending != null)
                            change.After = pending.BufferText;
                        remounted |= _workspace.Delete(change.FilePath);
                    }
                    else
                    {
                        remounted |=
                            _workspace.CreateNew(change.FilePath, change.After ?? "") != null;
                    }
                    continue;
                }
                if (change.IsMove)
                {
                    // ONE change covers the module and, when it owns its folder,
                    // everything inside it: the tree carries the subtree. There is
                    // no separate folder-move entry to keep in step any more.
                    string from = undo ? change.After : change.Before;
                    string to = undo ? change.Before : change.After;
                    remounted |= MoveModule(from, to);
                    continue;
                }
                writes.Add((change.ModuleId, change.FilePath,
                    undo ? change.Before : change.After));
            }
            return remounted;
        }

        private void UndoAction()
        {
            var entry = _ledger.Undo();
            if (entry == null)
            {
                Toast("Nothing to undo");
                return;
            }
            var writes = new System.Collections.Generic.List<(string, string, string)>();
            bool remounted = ApplyEntry(entry, undo: true, writes);
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
            var writes = new System.Collections.Generic.List<(string, string, string)>();
            bool remounted = ApplyEntry(entry, undo: false, writes);
            ApplyLedgerWrites(writes, "Redo " + entry.Description, remounted);
        }

        /// <summary>Jumps the history to a chosen point. It used to ask the ledger
        /// for the buffer writes alone, which meant a jump across a create, a delete
        /// or a rename moved the TEXT while leaving the shape of the tree where it
        /// was. It now replays whole entries through the same path undo and redo
        /// use, so every change kind is honoured, and redraws once at the end.</summary>
        private void JumpHistoryTo(int target, string label)
        {
            var writes = new System.Collections.Generic.List<(string, string, string)>();
            bool remounted = false;
            bool moved = false;
            while (_ledger.Cursor > target)
            {
                var entry = _ledger.Undo();
                if (entry == null)
                    break;
                remounted |= ApplyEntry(entry, undo: true, writes);
                moved = true;
            }
            while (_ledger.Cursor < target)
            {
                var entry = _ledger.Redo();
                if (entry == null)
                    break;
                remounted |= ApplyEntry(entry, undo: false, writes);
                moved = true;
            }
            if (!moved)
                return;
            ApplyLedgerWrites(writes, "History - " + label, remounted);
        }

        /// <summary>Writes a ledger step's buffers back with recording OFF, then
        /// re-syncs every surface the edit path normally touches. Read-only
        /// sessions are skipped rather than throwing — a package file cannot be
        /// in the ledger, but the guard is the same last line of defense the
        /// edit path uses.</summary>
        /// <summary>Replays one ledger step. Order matters and used to be wrong:
        /// the canvas was remounted BEFORE the buffers were written and before
        /// the focus was re-pointed, so an undo that removed the focused session
        /// rebuilt the whole window around a file that no longer existed. Now
        /// every model change lands first, then the focus is validated, then the
        /// views are refreshed exactly once.</summary>
        private void ApplyLedgerWrites(
            System.Collections.Generic.List<(string ModuleId, string FilePath, string Text)> writes,
            string label, bool remountCanvas = false)
        {
            if (writes.Count == 0 && !remountCanvas)
                return;

            using (_ledger.Suppress())
            {
                foreach (var (moduleId, filePath, text) in writes)
                {
                    // Identity first: the path this was recorded against may since
                    // have moved, and writing by path would either miss the module
                    // entirely or, worse, hit whatever now sits at that name.
                    var session = _workspace.ById(moduleId) ?? _workspace.TryGet(filePath);
                    if (session == null || session.IsReadOnly)
                        continue;
                    session.ApplyEdit(text);
                    SyncLspBuffer(session.FilePath, text);
                }
            }

            // UB-131: undoing a rename DISCARDS the renamed module's session, and
            // the window was still focused on it - so the source pane and the card
            // rendered emptiness over a tree that was perfectly intact. The focus
            // is validated BEFORE anything redraws.
            RebindFocusIfMissing();

            if (remountCanvas)
            {
                MountCanvas();
            }
            else
            {
                foreach (var (moduleId, filePath, _) in writes)
                    _canvasHost?.RefreshGraph(
                        _workspace.ById(moduleId)?.FilePath ?? filePath);
            }

            var focused = _workspace.TryGet(_focusFile);
            if (focused != null)
                _codeField?.SetContent(focused.BufferText, _focusFile, KnownElementsOrNull());
            RefreshChrome();
            NotifyBufferChanged();
            RefreshHistoryPanel();
            Toast(label);
        }


        /// <summary>Points the window at a session that actually exists. A
        /// ledger replay can remove the focused one (undoing a create, or the
        /// creation half of a rename), and every pane then renders emptiness
        /// over a tree that is perfectly intact.</summary>
        /// <summary>The focused file as a full path, or empty when there is no
        /// focus. An empty workspace has none - undoing the creation of the only
        /// module produces exactly that - and GetFullPath(null) throws, so every
        /// comparison against the focus went through this instead.</summary>
        private string FocusFull =>
            string.IsNullOrEmpty(_focusFile) ? string.Empty : Path.GetFullPath(_focusFile);

        /// <summary>Removes ONE import from ONE file, with whatever it bound. There
        /// was no way to do this at all: an import row had no delete, so a child
        /// component could only be detached by editing the source by hand.</summary>
        private void RemoveImportFrom(string importerFull, string targetFull, string specifier)
        {
            var session = EditSession(importerFull);
            if (session == null || session.IsReadOnly)
            {
                Toast("Read-only file");
                return;
            }
            if (targetFull == null)
            {
                Toast("Could not resolve " + specifier);
                return;
            }
            string updated = RemoveImportOf(session.BufferText, importerFull, targetFull);
            if (updated == null || string.Equals(
                    updated, session.BufferText, System.StringComparison.Ordinal))
            {
                Toast("Nothing to remove");
                return;
            }
            _ledger.Begin("remove import " + specifier);
            string before = session.BufferText;
            session.ApplyEdit(updated);
            _ledger.Record(importerFull, before, updated);
            _ledger.End();
            SyncLspBuffer(importerFull, updated);
            RefreshHistoryPanel();
            RefreshChrome();
            NotifyBufferChanged();
            MountCanvas();
            Toast("Removed import " + specifier);
        }

        /// <summary>Removes every reference to a module from the rest of the tree:
        /// the import that binds it, and any attribute whose value uses what that
        /// import bound. Deleting a module used to be REFUSED while anything still
        /// imported it, which is backwards - the builder knows exactly who refers
        /// to it and can unpick that itself.</summary>
        private void StripReferencesTo(string targetFull)
        {
            var nodes = _canvasHost?.Nodes;
            if (nodes == null)
                return;
            foreach (var other in nodes)
            {
                string otherFull;
                try
                {
                    otherFull = Path.GetFullPath(other.FilePath);
                }
                catch (System.Exception)
                {
                    continue;
                }
                if (string.Equals(otherFull, targetFull, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                var peer = EditSession(otherFull);
                if (peer == null || peer.IsReadOnly)
                    continue;
                string updated = RemoveImportOf(peer.BufferText, otherFull, targetFull);
                if (updated == null || string.Equals(
                        updated, peer.BufferText, System.StringComparison.Ordinal))
                    continue;
                string before = peer.BufferText;
                peer.ApplyEdit(updated);
                _ledger.Record(otherFull, before, updated);
                SyncLspBuffer(otherFull, updated);
            }
        }

        /// <summary>Drops the import of <paramref name="targetFull"/> from a file,
        /// and with it every attribute whose value uses a name that import bound.
        /// Leaving those behind would turn a delete into a broken build, which is
        /// the thing the old refusal was trying to avoid.</summary>
        private static string RemoveImportOf(
            string textLf, string importerPath, string targetFull)
        {
            Ruitk.Language.Parser.ParseResult parsed;
            try
            {
                parsed = BuilderLanguage.Parse(textLf, importerPath);
            }
            catch (System.Exception)
            {
                return null;
            }

            var boundNames = new System.Collections.Generic.List<string>();
            var dropLines = new System.Collections.Generic.HashSet<int>();
            foreach (var import in parsed.Directives.Imports)
            {
                string spec = import.Specifier ?? "";
                if (spec.Length == 0 || spec.StartsWith("@", System.StringComparison.Ordinal))
                    continue;
                string resolved = BuilderGraphService.MapSpecifier(importerPath, spec);
                if (resolved == null || !string.Equals(
                        resolved, targetFull, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                dropLines.Add(import.Line);
                if (import.IsStar && !string.IsNullOrEmpty(import.StarAlias))
                    boundNames.Add(import.StarAlias);
                if (import.IsDefault && !string.IsNullOrEmpty(import.DefaultAlias))
                    boundNames.Add(import.DefaultAlias);
                if (!import.Names.IsDefaultOrEmpty)
                {
                    for (int i = 0; i < import.Names.Length; i++)
                    {
                        string alias = import.Aliases.IsDefaultOrEmpty || import.Aliases.Length <= i
                            ? null
                            : import.Aliases[i];
                        boundNames.Add(string.IsNullOrEmpty(alias) ? import.Names[i] : alias);
                    }
                }
            }
            if (dropLines.Count == 0)
                return null;

            var lines = new System.Collections.Generic.List<string>(textLf.Split('\n'));
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (dropLines.Contains(i + 1))
                {
                    lines.RemoveAt(i);
                    continue;
                }
                foreach (string name in boundNames)
                {
                    // An attribute whose value reads through the bound name goes
                    // with it - style={Thing.container} is meaningless once Thing
                    // is gone.
                    var attr = new System.Text.RegularExpressions.Regex(
                        @"\s+[A-Za-z_][A-Za-z0-9_]*=\{[^}]*\b"
                        + System.Text.RegularExpressions.Regex.Escape(name)
                        + @"\b[^}]*\}");
                    lines[i] = attr.Replace(lines[i], "");
                }
            }
            return string.Join("\n", lines);
        }

        private void RebindFocusIfMissing()
        {
            if (!string.IsNullOrEmpty(_focusFile) && _workspace.TryGet(_focusFile) != null)
                return;
            foreach (var module in _workspace.Modules)
            {
                _focusFile = module.FilePath;
                return;
            }
            // Nothing is left to focus. Undoing the creation of the ONLY module
            // used to leave the focus naming a module that no longer existed, and
            // MountCanvas mounted it regardless - an empty card for a file with no
            // session behind it, which then could not be deleted because there was
            // nothing there to delete. An empty workspace shows the empty state.
            _focusFile = null;
        }

        private void OnCodeEdited(string bufferLf)
        {
            var session = _workspace.TryGet(_focusFile);
            if (session == null || session.IsReadOnly)
                return;
            string before = session.BufferText;
            session.ApplyEdit(bufferLf);
            // Typing is not an action. It coalesces into one history entry, and it
            // does NOT compile: a name typed into a field and then abandoned used to
            // cost a build of half-written code that could only fail. The CARD still
            // re-parses on every keystroke - that is cheap and local - and the
            // preview compiles when the edit is finished.
            _ledger.RecordTyping(_focusFile, before, bufferLf);
            SyncLspBuffer(_focusFile, bufferLf);
            RefreshChrome();
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
                _canvasHost?.RefreshGraph(_canvasRefreshFile);
        }

        /// <summary>POC 6.2: clicking a JSX row focuses its file and scrolls the
        /// source pane to that line (selected).</summary>
        private void OnCanvasRowClicked(string filePath, int sourceLine)
        {
            string full = Path.GetFullPath(filePath);
            if (!string.Equals(full, FocusFull, System.StringComparison.OrdinalIgnoreCase))
                OpenFileFromCanvas(full);
            _codeField?.FocusLine(sourceLine);
            // A markup row names an element - a native tag, or a custom
            // component the tree owns. The library says which of the two it is,
            // so pointing at the row points at its entry there.
            SyncLibrarySelection(full, sourceLine);
        }

        /// <summary>Mirrors a selected ROW into the library. A row that names a
        /// module the tree holds matches on that module's FILE; anything else
        /// matches on the tag name, which is what a native element is.</summary>
        private void SyncLibrarySelection(string full, int sourceLine)
        {
            if (_libraryPane == null)
                return;
            var node = NodeFor(full);
            var row = node?.Markup?.Find(r => r.SourceLine == sourceLine);
            if (row == null)
            {
                _libraryPane.SetSelected(full, null);
                return;
            }
            string tag = (row.Text ?? "").Trim().TrimStart('<').TrimEnd('>', '/', ' ');
            int cut = tag.IndexOfAny(new[] { ' ', '/', '>' });
            if (cut > 0)
                tag = tag.Substring(0, cut);
            if (tag.Length == 0)
            {
                _libraryPane.SetSelected(full, null);
                return;
            }
            // A tag that names a module in the tree points at that module.
            foreach (var other in _workspace.Modules)
                if (string.Equals(other.Name, tag, System.StringComparison.Ordinal))
                {
                    _libraryPane.SetSelected(other.FilePath, null);
                    return;
                }
            _libraryPane.SetSelected(null, tag);
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
                    bool imported = module != null && AlreadyImports(node, module.FilePath);

                    // A style module dropped ON AN ELEMENT is applied there. The
                    // gesture only ever added the IMPORT, whatever it was dropped
                    // on, and an import styles nothing - so the card gained a line,
                    // the preview looked identical, and the styling the drop was
                    // for had to be typed by hand as a style attribute.
                    if (kind == "stylemod" && hasRow && row != null && row.SourceLine > 0
                        && module != null && module.Exports.Count > 0)
                    {
                        ApplyStyleModuleToRow(full, row, module, name, imported);
                        break;
                    }

                    if (imported)
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
            DisarmStyleAdd();
            _inlineEditor.Show(anchor, seed, multiline: false,
                FragmentCompletion(full, (text, l0, c0) => MapLineFragment(full, sourceLine, text, suffix, c0)),
                text => OnLineRewritten(full, sourceLine, text + (suffix ?? "")),
                () => ResyncLspBuffer(full),
                advance: () => AdvanceStyleEntry(full, sourceLine));
        }

        /// <summary>The style entry the keyboard would add to next, as (file,
        /// style name). Empty when Enter means nothing in particular.</summary>
        [System.NonSerialized] private string _armedAddFile = "";
        [System.NonSerialized] private string _armedAddStyle = "";

        /// <summary>Enter finished a style entry, so move to the next one.
        /// Writing a style is a RUN of entries, and committing each by hand -
        /// click, type, Enter, click - made the keyboard useless for the one
        /// task on this card that is nothing but typing.
        ///
        /// On the LAST entry there is nothing to advance to, so the "+ entry"
        /// row is armed instead and lights up: Enter again opens its key menu,
        /// exactly as clicking it does. Deliberately two presses, not one - a
        /// menu that opened by itself after every entry would be a trap.</summary>
        private void AdvanceStyleEntry(string full, int sourceLine)
        {
            var node = NodeFor(full);
            if (node == null)
                return;
            int at = -1;
            for (int i = 0; i < node.ExportDetail.Count; i++)
            {
                if (node.ExportDetail[i].SourceLine == sourceLine
                    && node.ExportDetail[i].BadgeKind == 0
                    && node.ExportDetail[i].Depth == 1)
                {
                    at = i;
                    break;
                }
            }
            if (at < 0 || at + 1 >= node.ExportDetail.Count)
                return;

            var next = node.ExportDetail[at + 1];
            if (next.BadgeKind == 9)
            {
                ArmStyleAdd(full, next.AttrsText ?? "");
                return;
            }
            if (next.BadgeKind != 0 || next.Depth != 1)
                return;

            // The card re-rendered on the commit, so the row element for the
            // next line exists only after that render has landed.
            int line = next.SourceLine;
            OpenNextEntryWhenDrawn(full, line, 12);
        }

        /// <summary>Opens the editor on the next entry once the card has actually
        /// re-drawn it. The commit schedules a canvas refresh rather than doing it
        /// inline, so the row for the next line does not exist yet - and a single
        /// deferred tick was a race the chain lost more often than it won.</summary>
        private void OpenNextEntryWhenDrawn(string full, int line, int attempts)
        {
            if (attempts <= 0)
                return;
            rootVisualElement?.schedule.Execute(() =>
            {
                var anchor = _canvasHost?.RowElement(full, line);
                if (anchor == null)
                {
                    OpenNextEntryWhenDrawn(full, line, attempts - 1);
                    return;
                }
                var row = NodeFor(full)?.ExportDetail.Find(r => r.SourceLine == line);
                if (row == null)
                    return;
                string suffix = row.Text.EndsWith(",", System.StringComparison.Ordinal) ? "," : "";
                string seed = suffix.Length > 0
                    ? row.Text.Substring(0, row.Text.Length - suffix.Length)
                    : row.Text;
                ShowLineEditor(full, line, seed, suffix, anchor);
            }).ExecuteLater(1);
        }

        /// <summary>Takes the keyboard off a "+ entry" row. Anything that is not
        /// "add another entry" ends the run, so the highlight never outlives the
        /// meaning it stands for.</summary>
        private void DisarmStyleAdd()
        {
            if (_armedAddFile.Length == 0)
                return;
            _armedAddFile = "";
            _armedAddStyle = "";
            _canvasHost?.ArmStyleAdd("", "");
        }

        private BuilderCanvasNode NodeFor(string full)
        {
            var nodes = _canvasHost?.Nodes;
            if (nodes == null)
                return null;
            foreach (var node in nodes)
                if (string.Equals(Path.GetFullPath(node.FilePath), full,
                        System.StringComparison.OrdinalIgnoreCase))
                    return node;
            return null;
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

        /// <summary>UB-124: renames the module a card stands for. A rename is
        /// four edits that must land together or not at all - the EXPORT the
        /// module declares, the FILE name, the FOLDER when the module owns one,
        /// and every IMPORTER's specifier and binding. All of it is pending
        /// buffer work under the save-only contract, recorded as ONE ledger
        /// entry so a single Ctrl+Z takes the whole rename back.</summary>
        private void ShowRenamePrompt(string filePath)
        {
            string full = Path.GetFullPath(filePath);
            var node = _canvasHost?.FindNode(full);
            if (node == null)
            {
                Toast("That module is not on this canvas");
                return;
            }
            var session = EditSession(full);
            if (session == null || session.IsReadOnly)
            {
                Toast("Read-only file");
                return;
            }
            string oldName = node.Title;
            string kind = KindKeyOf(node.Kind, full);
            BuilderSearchMenu.ShowNamePrompt(
                "rename " + oldName,
                oldName,
                name => string.Equals(name, oldName, System.StringComparison.Ordinal)
                    ? "that is the current name"
                    : ValidateNewName(
                        kind, name, n => RenameTargetPath(full, oldName, n, out _, out _)),
                name => RenameModule(full, oldName, name),
                initialValue: oldName);
        }

        /// <summary>The create-prompt kind key for an EXISTING module, so rename
        /// validates a name exactly the way creation does.</summary>
        private static string KindKeyOf(BuilderNodeKind kind, string path)
        {
            string file = Path.GetFileName(path);
            if (file.EndsWith(".style.uitkx", System.StringComparison.OrdinalIgnoreCase))
                return "Style";
            if (file.EndsWith(".hooks.uitkx", System.StringComparison.OrdinalIgnoreCase))
                return "Hooks";
            return kind == BuilderNodeKind.Component ? "Component" : "Utils";
        }

        /// <summary>Follows the focused file across a folder move.</summary>
        private void RepointFocus(string oldDir, string newDir)
        {
            if (string.IsNullOrEmpty(_focusFile))
                return;
            string prefix = Path.GetFullPath(oldDir) + Path.DirectorySeparatorChar;
            string focus = FocusFull;
            if (focus.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                _focusFile = Path.Combine(newDir, focus.Substring(prefix.Length));
        }

        /// <summary>Moves a module and brings everything that TRACKS its path
        /// along - the saved card layout and the window focus. One helper, so the
        /// rename command and the ledger replaying that rename cannot disagree
        /// about what a move implies. Carrying the module's SUBTREE is the tree's
        /// business; this is the view's half of the same move.</summary>
        private bool MoveModule(string fromPath, string toPath)
        {
            string from = Path.GetFullPath(fromPath);
            string to = Path.GetFullPath(toPath);
            var module = _workspace.TryGet(from);
            if (module == null)
                return false;
            string fromDir = module.Folder;
            bool ownsFolder = module.OwnsFolder;
            string toDir = Path.GetDirectoryName(to) ?? "";
            var rewrites = _workspace.MoveToPath(from, to);
            if (rewrites == null)
                return false;
            // A move re-spells every specifier it invalidated. Those rewrites go
            // in the ledger beside the move, so the entry describes the file
            // contents completely - a redo that restored a buffer recorded
            // BEFORE the rewrite would otherwise put the stale specifier back.
            foreach (var rewrite in rewrites)
                _ledger.Record(rewrite.FilePath, rewrite.Before, rewrite.After);
            if (ownsFolder)
            {
                // The folder takes every card inside it along, this module's own
                // included - and that one still carries the OLD file name, so the
                // file repath below runs from where the card now sits.
                _canvasHost?.RepathLayout(fromDir, toDir, isFolder: true);
                RepointFocus(fromDir, toDir);
                from = Path.GetFullPath(Path.Combine(toDir, Path.GetFileName(from)));
            }
            _canvasHost?.RepathLayout(from, to, isFolder: false);
            if (string.Equals(FocusFull, from, System.StringComparison.OrdinalIgnoreCase))
                _focusFile = to;
            return true;
        }

        private void RenameModule(string full, string oldName, string newName)
        {
            var nodes = _canvasHost?.Nodes;
            if (nodes == null)
                return;
            string suffix = SuffixOf(Path.GetFileName(full));
            string newFull = RenameTargetPath(
                full, oldName, newName, out string newDir, out bool ownsFolder);
            if (File.Exists(newFull) || _workspace.TryGet(newFull) != null
                || (ownsFolder && Directory.Exists(newDir)))
            {
                Toast(newName + " already exists");
                return;
            }

            _ledger.Begin("rename " + oldName + " to " + newName);
            // Captured BEFORE the name rewrites below touch any specifier text.
            // Those rewrites replace the module's NAME wherever it appears, which
            // gets the last path segment right and leaves a FOLDER segment naming
            // the same module wrong ("../Panel/Panel" from outside the folder).
            // Reconciling at the end re-derives every specifier from where the
            // modules actually ended up, so the string surgery no longer has to be
            // right about paths - only about names.
            var imports = _workspace.CaptureImports();

            // 1. The module's own text: the export it declares.
            var own = EditSession(full);
            if (own != null && !own.IsReadOnly)
            {
                string renamed = RenameExportIn(own.BufferText, oldName, newName);
                if (!string.Equals(renamed, own.BufferText, System.StringComparison.Ordinal))
                {
                    string before = own.BufferText;
                    own.ApplyEdit(renamed);
                    _ledger.Record(full, before, renamed);
                }
            }

            // 2. Every importer: the specifier AND the binding it introduces,
            //    plus that binding's uses in the file.
            foreach (var other in nodes)
            {
                string otherFull = Path.GetFullPath(other.FilePath);
                if (string.Equals(otherFull, full, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                // EditSession, not TryGet: an importer the user has not opened is
                // still an importer, and skipping it left it pointing at a module
                // that had moved.
                var peer = EditSession(otherFull);
                if (peer == null || peer.IsReadOnly)
                    continue;
                string updated = RenameReferencesIn(peer.BufferText, oldName, newName, suffix);
                if (string.Equals(updated, peer.BufferText, System.StringComparison.Ordinal))
                    continue;
                string before = peer.BufferText;
                peer.ApplyEdit(updated);
                _ledger.Record(otherFull, before, updated);
            }

            // 3. The move - ONE operation. The tree carries the folder's whole
            //    contents when the module owns it, so sub-components, companions
            //    and everything the builder does not manage keep their position
            //    relative to their parent and every relative import inside the
            //    subtree stays correct. It took two steps and two ledger entries
            //    before, a folder move and a file move that had to be walked back
            //    in the right order or the tree came apart.
            if (!MoveModule(full, newFull))
            {
                _ledger.End();
                Toast("Could not rename " + oldName);
                return;
            }
            _ledger.RecordMove(full, newFull);
            foreach (var rewrite in _workspace.ReconcileImports(imports))
                _ledger.Record(rewrite.FilePath, rewrite.Before, rewrite.After);
            _ledger.End();
            RefreshHistoryPanel();
            RefreshChrome();
            MountCanvas();
            Toast("Renamed to " + newName + " - applies on Save");
        }

        /// <summary>Rewrites the module's own export DECLARATION only. A
        /// word-boundary replace over the whole file would also rewrite
        /// unrelated identifiers that merely share the name.</summary>
        private static string RenameExportIn(string text, string oldName, string newName)
        {
            var pattern = new System.Text.RegularExpressions.Regex(
                @"(\bexport\s+[^\r\n=;]*?\b)"
                + System.Text.RegularExpressions.Regex.Escape(oldName) + @"\b");
            return pattern.Replace(text, m => m.Groups[1].Value + newName, 1);
        }

        /// <summary>Rewrites one importer: the import SPECIFIER (the path stem),
        /// the BINDING it introduces, and that binding's uses. Specifier and
        /// binding move together, because renaming either alone leaves the file
        /// referring to something that no longer exists.</summary>
        private static string RenameReferencesIn(
            string text, string oldName, string newName, string suffix)
        {
            string stem = suffix == ".style.uitkx" ? ".style"
                : suffix == ".hooks.uitkx" ? ".hooks"
                : "";
            var withSpecifier = new System.Text.RegularExpressions.Regex(
                @"([""'/])" + System.Text.RegularExpressions.Regex.Escape(oldName)
                + System.Text.RegularExpressions.Regex.Escape(stem) + @"(?=[""'])");
            string updated = withSpecifier.Replace(
                text, m => m.Groups[1].Value + newName + stem);
            var binding = new System.Text.RegularExpressions.Regex(
                @"\b" + System.Text.RegularExpressions.Regex.Escape(oldName) + @"\b");
            return binding.Replace(updated, newName);
        }
        /// <summary>Right-click on a style row. Deleting was the one thing a
        /// style card could not do: entries and whole exports could be added and
        /// never removed, so a mistyped key stayed until the user opened the
        /// source pane.</summary>
        private void OnStyleRowContext(string filePath, int fromLine, int toLine, string what)
        {
            var module = _workspace.TryGet(filePath);
            if (module == null || module.IsReadOnly)
            {
                Toast("Read-only file");
                return;
            }
            int last = System.Math.Max(fromLine, toLine);
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>
            {
                new BuilderSearchMenu.Item
                {
                    Label = "Delete " + what,
                    OnPick = () => DeleteLinesInFile(filePath, fromLine, last, "delete " + what),
                },
            };
            BuilderSearchMenu.Show(what, "filter…", items);
        }

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
            // Adding an entry is a run, the same as editing one: the key menu and
            // the value menu are two Enters, and the third should start the next
            // entry. Arming here is what makes the whole sequence keyboard-only.
            ArmStyleAdd(filePath, styleName);
        }

        /// <summary>Points the keyboard at a style's "+ entry" row. The LINE is
        /// looked up when Enter fires rather than stored: inserting an entry moves
        /// the row down, and a remembered line would insert the next one in the
        /// wrong place.</summary>
        private void ArmStyleAdd(string filePath, string styleName)
        {
            _armedAddFile = Path.GetFullPath(filePath);
            _armedAddStyle = styleName ?? "";
            _canvasHost?.ArmStyleAdd(_armedAddFile, _armedAddStyle);
        }

        /// <summary>The line the armed style's "+ entry" row currently sits on,
        /// or 0 when the card no longer has one.</summary>
        private int ArmedAddLine()
        {
            var node = NodeFor(_armedAddFile);
            if (node == null)
                return 0;
            foreach (var row in node.ExportDetail)
                if (row.BadgeKind == 9
                    && string.Equals(row.AttrsText, _armedAddStyle, System.StringComparison.Ordinal))
                    return row.SourceLine;
            return 0;
        }

        private BuilderModule OpenSession(string filePath)
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
        private BuilderModule EditSession(string filePath) =>
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

        /// <summary>The name a star import binds, chosen so it cannot collide with
        /// something the file already means.
        ///
        /// PascalCasing the module name is the convention and it walks straight
        /// into the component's own name: a style module called someComponent,
        /// imported into SomeComponent, produced `SomeComponent.container` - which
        /// binds to the COMPONENT and fails with CS0117. That pairing is legal, and
        /// the folder convention positively encourages it, so the ALIAS is what has
        /// to give.
        ///
        /// A module that is ALREADY imported keeps whatever alias it was given, or
        /// a second element styled from the same module would reference a name the
        /// file never bound.</summary>
        private string ImportAliasFor(
            string importerPath, BuilderCanvasNode module, string moduleName, bool styleModule)
        {
            var taken = new System.Collections.Generic.HashSet<string>(
                System.StringComparer.Ordinal);
            // The component's own name. By convention it is the file stem, and that
            // is precisely the name a PascalCased companion collides with.
            taken.Add(Path.GetFileNameWithoutExtension(importerPath));

            string targetFull = module == null || string.IsNullOrEmpty(module.FilePath)
                ? null
                : Path.GetFullPath(module.FilePath);
            var session = _workspace.TryGet(Path.GetFullPath(importerPath));
            if (session != null)
            {
                try
                {
                    var parsed = BuilderLanguage.Parse(session.BufferText, importerPath);
                    foreach (var import in parsed.Directives.Imports)
                    {
                        string spec = import.Specifier;
                        string bound = import.IsStar ? import.StarAlias
                            : import.IsDefault ? import.DefaultAlias
                            : null;
                        if (targetFull != null && import.IsStar && !string.IsNullOrEmpty(bound)
                            && string.Equals(
                                BuilderGraphService.MapSpecifier(importerPath, spec), targetFull,
                                System.StringComparison.OrdinalIgnoreCase))
                            return bound;
                        if (!string.IsNullOrEmpty(bound))
                            taken.Add(bound);
                        if (!import.Names.IsDefaultOrEmpty)
                        {
                            for (int i = 0; i < import.Names.Length; i++)
                            {
                                string alias = import.Aliases.IsDefaultOrEmpty
                                    || import.Aliases.Length <= i
                                        ? null
                                        : import.Aliases[i];
                                taken.Add(string.IsNullOrEmpty(alias) ? import.Names[i] : alias);
                            }
                        }
                    }
                }
                catch (System.Exception)
                {
                    // A half-typed buffer just means fewer known names to avoid.
                }
            }

            string candidate = char.ToUpperInvariant(moduleName[0]) + moduleName.Substring(1);
            if (!taken.Contains(candidate))
                return candidate;
            string qualified = candidate + (styleModule ? "Style" : "Module");
            if (!taken.Contains(qualified))
                return qualified;
            for (int i = 2; i < 100; i++)
                if (!taken.Contains(qualified + i))
                    return qualified + i;
            return qualified;
        }

        private string BuildImportLine(
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
                    return "import * as " + ImportAliasFor(importerPath, module, name, true)
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
        /// <summary>Puts a style module's export on one element: sets its style
        /// attribute and adds the import if the file does not have it yet, as ONE
        /// undoable action. A module with several exports asks which.
        ///
        /// The order is load-bearing. The attribute is written FIRST, against the
        /// row's current source line; inserting the import at the top shifts every
        /// line below it by one, so doing that first would aim the attribute edit a
        /// line high.</summary>
        private void ApplyStyleModuleToRow(
            string filePath, BuilderCardLine row, BuilderCanvasNode module,
            string moduleName, bool alreadyImported)
        {
            // The same rule the import line uses, so the attribute references the
            // name the file actually binds - including when the module was already
            // imported under a disambiguated alias.
            string alias = ImportAliasFor(filePath, module, moduleName, true);

            void Apply(string exportName)
            {
                _ledger.Begin("style " + moduleName + "." + exportName);
                string value = "{" + alias + "." + exportName + "}";
                bool wrote = false;
                EditOpenTagInFile(filePath, row.SourceLine, tag =>
                {
                    var existing = System.Text.RegularExpressions.Regex.Match(
                        tag, @"\sstyle=(\{[^}]*\}|""[^""]*"")");
                    if (existing.Success)
                    {
                        wrote = true;
                        return tag.Substring(0, existing.Index) + " style=" + value
                            + tag.Substring(existing.Index + existing.Length);
                    }
                    int close = tag.LastIndexOf("/>", System.StringComparison.Ordinal);
                    if (close < 0)
                        close = tag.LastIndexOf('>');
                    if (close < 0)
                        return null;
                    wrote = true;
                    return tag.Substring(0, close).TrimEnd() + " style=" + value
                        + (tag.Substring(close).StartsWith("/") ? " " : "") + tag.Substring(close);
                }, "styled with " + exportName);

                if (!wrote)
                {
                    _ledger.End();
                    Toast("Couldn't find the open tag's end - style not applied.");
                    return;
                }
                if (!alreadyImported)
                {
                    string line = BuildImportLine(filePath, module, true, moduleName);
                    if (line != null)
                        InsertLinesInFile(filePath, 0, line, "style import " + moduleName);
                }
                _ledger.End();
                RefreshHistoryPanel();
                Toast("Applied " + moduleName + "." + exportName);
            }

            if (module.Exports.Count == 1)
            {
                Apply(module.Exports[0]);
                return;
            }
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
            foreach (string export in module.Exports)
            {
                string captured = export;
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = captured,
                    OnPick = () => Apply(captured),
                });
            }
            BuilderSearchMenu.Show(
                moduleName + " - which style", "search styles...", items);
        }

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
            if (string.Equals(Path.GetFullPath(filePath), FocusFull,
                    System.StringComparison.OrdinalIgnoreCase))
                _codeField?.SetContent(newBufferLf, _focusFile, KnownElementsOrNull());
            SyncLspBuffer(filePath, newBufferLf);
            RefreshChrome();
            NotifyBufferChanged();
            // POC commitNode(): rebuild ONLY the edited card and redraw the edges —
            // zoom, camera, card selection and row selection survive the commit.
            _canvasHost?.RefreshGraph(filePath);
            // POC commitNode(label): the toast names WHAT changed, not just the file.
            Toast(string.IsNullOrEmpty(what)
                ? "Committed edit → " + Path.GetFileName(filePath)
                : "Committed " + what + " → " + Path.GetFileName(filePath));
        }

        private void OnPreviewComponentPicked(string filePath)
        {
            string full = Path.GetFullPath(filePath);
            if (!string.Equals(full, FocusFull, System.StringComparison.OrdinalIgnoreCase))
                OpenFileFromCanvas(full);
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
        /// <summary>Set from the toolbar: writes one line per preview compile
        /// saying which modules were CONSIDERED, which rebuilt and why, and which
        /// were skipped.
        ///
        /// It exists because three separate fixes to this pipeline each read
        /// correctly and each failed, and the reason is that its failures are
        /// invisible: a module missing from the batch looks exactly like a module
        /// that compiled and changed nothing. The next report carries the answer
        /// rather than the symptom.</summary>
        [SerializeField] private bool _tracePreview;
        [System.NonSerialized] private Button _traceButton;

        private void TogglePreviewTrace()
        {
            _tracePreview = !_tracePreview;
            if (_traceButton != null)
                _traceButton.text = _tracePreview ? "Trace ON" : "Trace";
            Toast(_tracePreview
                ? "Preview trace ON - the console names what each edit rebuilds"
                : "Preview trace off");
        }

        private void TracePreviewCompile(BuilderCompileSummary summary, string rendered)
        {
            if (!_tracePreview)
                return;
            if (summary == null)
            {
                Debug.Log("[RUITK Builder] preview: nothing to build");
                return;
            }
            var line = new System.Text.StringBuilder("[RUITK Builder] preview: rendering ");
            line.Append(Path.GetFileName(rendered));
            line.Append(", focus ").Append(Path.GetFileName(_focusFile));
            line.Append("\n  considered: ");
            line.Append(summary.Considered.Count == 0
                ? "(none)"
                : string.Join(", ", summary.Considered.ConvertAll(Path.GetFileName)));
            line.Append("\n  rebuilt:    ");
            line.Append(summary.Reasons.Count == 0 ? "(none)" : "");
            foreach (var pair in summary.Reasons)
                line.Append(Path.GetFileName(pair.Key)).Append(" (").Append(pair.Value).Append(") ");
            if (summary.Failures.Count > 0)
            {
                line.Append("\n  FAILED:     ");
                foreach (var (path, error) in summary.Failures)
                    line.Append(Path.GetFileName(path)).Append(": ").Append(error).Append(" ");
            }
            if (summary.Skipped.Count > 0)
            {
                line.Append("\n  skipped:    ");
                foreach (var (path, blockedBy) in summary.Skipped)
                    line.Append(Path.GetFileName(path))
                        .Append(" (needs ").Append(Path.GetFileName(blockedBy)).Append(") ");
            }
            Debug.Log(line.ToString());
        }

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
            // What the PANE is rendering, which is not always the focus: clicking
            // a style entry to edit it moves the focus onto that style while the
            // preview goes on showing the component. The batch, the assembly and
            // the buffer below were all keyed on the focus, so editing a style
            // handed the pane the STYLE's assembly and the STYLE's text and asked
            // it to render a component - which it could not, so it kept the last
            // good render and the edit appeared to do nothing (UB-202).
            string rendered = _previewPane?.ShownFile ?? _focusFile;
            if (string.IsNullOrEmpty(rendered))
                rendered = _focusFile;
            var summary = _previewCompiler.CompileDirty(rendered);
            TracePreviewCompile(summary, rendered);
            var focusSession = _workspace.TryGet(rendered);
            // The pane is ALWAYS told which assembly to render, whether or not this
            // round rebuilt anything. A module that needs no rebuild still has a
            // current build, and leaving it to the pane to find one meant scanning
            // every loaded swap assembly for a matching [UitkxSource] - they all
            // match, so it found an arbitrary one and the preview fell back to an
            // older render that nothing then corrected.
            var focusAssembly =
                summary?.FocusResult != null && summary.FocusResult.Success
                    ? summary.FocusResult.LoadedAssembly
                    : _previewCompiler.BuiltAssemblyFor(rendered);
            if (focusAssembly != null)
                _previewPane?.OnRecompiled(focusAssembly, focusSession?.BufferText);
            if (summary == null)
                return;
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
            string target = Path.GetFullPath(Path.Combine(
                start ?? Path.GetDirectoryName(source) ?? "", componentName + ".uitkx"));
            if (File.Exists(target) || _workspace.TryGet(target) != null)
            {
                Toast(componentName + ".uitkx already exists.");
                return;
            }
            // An import is an edit like any other. It used to write the file and
            // refresh the AssetDatabase on the spot, which broke the save-only
            // contract the rest of the builder obeys: the module now arrives as a
            // pending buffer, Save writes it and Abort drops it.
            if (_workspace.CreateNew(target, result.UitkxText) == null)
            {
                Toast("Could not import " + componentName);
                return;
            }
            _ledger.Begin("import " + componentName + ".uitkx");
            _ledger.RecordCreation(target);
            _ledger.End();
            RefreshHistoryPanel();
            foreach (string warning in result.Warnings)
                Debug.LogWarning("[RUITK Builder] UXML import: " + warning);
            Toast("Imported " + componentName + ".uitkx (one-way) - applies on Save");
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
                    JumpHistoryTo(target, entry.Description));
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
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    if (_armedAddFile.Length == 0)
                        return;
                    string file = _armedAddFile;
                    string style = _armedAddStyle;
                    int line = ArmedAddLine();
                    DisarmStyleAdd();
                    if (line > 0)
                        OnStyleAddEntry(file, style, line);
                    ConsumeKey(evt);
                    return;
                }
                DisarmStyleAdd();
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

        /// <summary>UB-113: a tree begun from the empty state has no folder to
        /// infer, so Save asks for one, once, and moves the pending sessions
        /// there before writing. Returns false when the user cancels or picks
        /// somewhere Unity cannot see.</summary>
        private bool ResolveUnsavedLocation()
        {
            var pending = _workspace.UnlocatedModules();
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

            // Planned in full before anything moves, so a collision cancels the
            // whole relocation instead of leaving half the tree in the new folder
            // and half at the provisional path.
            string root = BuilderWorkspace.UnsavedRoot;
            _ledger.Begin("place the tree");
            var plan = new System.Collections.Generic.List<(BuilderModule Module, string Folder)>();
            foreach (var module in pending)
            {
                // Every pending module is under the provisional root by
                // definition - that is what makes it pending - so the relative
                // path is always there to take.
                string folder = Path.GetFullPath(module.Folder ?? "");
                string target = Path.GetFullPath(Path.Combine(
                    chosen, folder.Substring(root.Length).TrimStart('\\', '/')));
                string to = Path.Combine(target, Path.GetFileName(module.FilePath));
                if (!_workspace.IsPathAvailable(to))
                {
                    _ledger.End();
                    UnityEditor.EditorUtility.DisplayDialog(
                        "Already exists", Path.GetFileName(to) + " is already there.", "OK");
                    return false;
                }
                plan.Add((module, target));
            }

            foreach (var step in plan)
            {
                // A module the folder move already carried home is placed where
                // it now is, which changes nothing - so the walk does not care
                // what order the tree hands it back.
                string from = step.Module.FilePath;
                var rewrites = _workspace.PlaceAt(step.Module, step.Folder);
                if (rewrites == null)
                    continue;
                foreach (var rewrite in rewrites)
                    _ledger.Record(rewrite.FilePath, rewrite.Before, rewrite.After);
                string to = step.Module.FilePath;
                _canvasHost?.RepathLayout(from, to, isFolder: false);
                if (string.Equals(FocusFull, Path.GetFullPath(from),
                        System.StringComparison.OrdinalIgnoreCase))
                    _focusFile = to;
                _relocatedOnSave = true;
            }
            _ledger.End();
            RefreshHistoryPanel();
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
            foreach (var session in _workspace.Modules)
            {
                if (session.IsReadOnly || !session.IsDirty)
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
                        FocusFull, System.StringComparison.OrdinalIgnoreCase))
                    _codeField?.SetContent(formatted, _focusFile, KnownElementsOrNull());
                SyncLspBuffer(session.FilePath, formatted);
                _canvasHost?.RefreshGraph(session.FilePath);
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
            var pending = _workspace.Tree.OrphanedPaths();
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
            int written;
            try
            {
                written = _workspace.SaveAll();
            }
            catch (System.IO.IOException ex)
            {
                // A folder move the AssetDatabase refused is the one save step
                // that can fail outright. Everything it had not reached is still
                // pending, so say so and leave the session intact to retry.
                Debug.LogError("[RUITK Builder] save failed: " + ex.Message);
                Toast("Save failed - " + ex.Message);
                RefreshChrome();
                return;
            }
            BuilderSaveMetrics.RecordSaveBatch(written, hmrActive);
            // The work is on disk, so the journal has nothing left to protect.
            BuilderReloadJournal.Clear();
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

        /// <summary>Where a new module is BORN.
        ///
        /// A COMPONENT nests under the focus, which is the tree the user is
        /// building. A COMPANION - a style or hook module - joins the component it
        /// is named after, wherever that component lives: NewComponent,
        /// newComponent.style and useNewComponent.hooks are one family and share
        /// one folder. A companion that matches nothing, and every util module, is
        /// shared until proven otherwise and is born at the tree ROOT - the closest
        /// shared parent of the modules that will import it, which at birth is
        /// none of them.
        ///
        /// A DEFAULT, not a rule: nothing re-places a module afterwards, so the
        /// folder view can put anything anywhere and the convention will not argue
        /// with it.</summary>
        private string BirthPathFor(string kind, string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            // No focus means no tree to infer from: the first module of a new tree
            // owns its folder rather than nesting under a "components" directory
            // that has nothing above it.
            if (string.IsNullOrEmpty(_focusFile))
                return Full(BuilderNewFileDialog.PathFor(
                    BuilderWorkspace.UnsavedRoot, kind, name, asRoot: true));

            string focusDir = Path.GetDirectoryName(_focusFile) ?? "";
            if (kind == "Component")
                return Full(BuilderNewFileDialog.PathFor(focusDir, kind, name));

            var family = FamilyOwnerFor(kind, name);
            string folder = family?.Folder
                ?? BuilderTree.ResolveRoot(_focusFile)
                ?? focusDir;
            return Full(BuilderNewFileDialog.PathFor(folder, kind, name));
        }

        private static string Full(string path) =>
            string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);

        /// <summary>The component a new companion belongs to, or null. When more
        /// than one in the tree carries the family name the NEAREST to the focus
        /// wins, and an exact tie falls to the ordinal-smallest path so the answer
        /// does not depend on the order the tree was loaded in.</summary>
        private BuilderModule FamilyOwnerFor(string kind, string name)
        {
            var kindOf = kind == "Hooks" ? BuilderNodeKind.Hook
                : kind == "Style" ? BuilderNodeKind.Style
                : BuilderNodeKind.Util;
            // A util has no name to match on - it is a plain .uitkx - so it takes
            // the shared-until-proven-otherwise branch.
            if (kindOf == BuilderNodeKind.Util)
                return null;

            string focusDir = Path.GetDirectoryName(_focusFile) ?? "";
            BuilderModule best = null;
            int bestShared = -1;
            foreach (var module in _workspace.Modules)
            {
                if (module.Kind != BuilderNodeKind.Component
                    || !BuilderNaming.SameFamily(
                        kindOf, name, BuilderNodeKind.Component, module.Name))
                    continue;
                int shared = BuilderNaming.SharedPrefixLength(focusDir, module.Folder);
                if (best == null || shared > bestShared
                    || (shared == bestShared
                        && string.CompareOrdinal(module.FilePath, best.FilePath) < 0))
                {
                    best = module;
                    bestShared = shared;
                }
            }
            return best;
        }

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
            var prompt = s_createPrompts.TryGetValue(kind, out var found)
                ? found
                : ("new file", "Name");
            BuilderSearchMenu.ShowNamePrompt(
                prompt.Item1,
                prompt.Item2,
                name => ValidateNewName(kind, name, n => BirthPathFor(kind, n)),
                name =>
                {
                    string created = BirthPathFor(kind, name);
                    if (created == null
                        || !_workspace.IsPathAvailable(Path.GetFullPath(created)))
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
                            full, BuilderNewFileDialog.TemplateFor(kind, name)) == null)
                    {
                        Toast("Could not create " + name);
                        return;
                    }
                    _ledger.Begin("create " + Path.GetFileName(created));
                    _ledger.RecordCreation(full);
                    _ledger.End();
                    RefreshHistoryPanel();
                    _canvasHost?.PlaceNewCard(full, worldX, worldY);
                    // The convention can put a module somewhere other than where
                    // the user is looking, so the toast NAMES the folder. A file
                    // that appears silently in a folder you are not in is the same
                    // as a file that did not appear.
                    string where = Path.GetFileName(Path.GetDirectoryName(full) ?? "");
                    Toast("Created " + Path.GetFileName(created)
                        + (where.Length > 0 ? " in " + where : "") + " - applies on Save");
                    RefreshChrome();
                    OpenAdditionalFile(full);
                    // The create prompt took the keyboard and closing it hands it
                    // back to nothing, so the next shortcut went to Unity until the
                    // user clicked the canvas. Taking it back has to happen AFTER
                    // the remount above rebuilds the element that would hold it,
                    // and the remount does not finish this tick - which is why
                    // doing it inline worked only sometimes.
                    ReclaimKeyboard(4);
                });
        }

        /// <param name="targetPathFor">Maps a candidate name to the file it would
        /// produce. Null skips the collision check.</param>
        private string ValidateNewName(
            string kind, string name, System.Func<string, string> targetPathFor)
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
            // A name is taken only when the FILE it would produce is already
            // taken. This used to compare DISPLAY names, which have their
            // .style/.hooks stripped and are matched case-insensitively - so a
            // style module called someComponent was refused because a component
            // called SomeComponent existed, even though the two produce
            // someComponent.style.uitkx and SomeComponent.uitkx and are exactly
            // the pairing the folder convention is built around.
            string target = targetPathFor?.Invoke(name);
            if (!string.IsNullOrEmpty(target) && !_workspace.IsPathAvailable(target))
                return name + " already exists";
            return null;
        }

        /// <summary>Where a rename would put the module, and whether it takes its
        /// folder with it. One place, so the prompt's live validation and the
        /// rename itself can never disagree about what a name would produce.</summary>
        private static string RenameTargetPath(
            string full, string oldName, string newName, out string newDir, out bool ownsFolder)
        {
            string dir = Path.GetDirectoryName(full) ?? "";
            string file = Path.GetFileName(full);
            string suffix = SuffixOf(file);
            // A COMPANION never owns the folder: a card title has its
            // .style/.hooks stripped, so someComponent.style.uitkx sitting in
            // someComponent/ would otherwise match the folder just as its
            // component does, and renaming the companion would move the lot.
            ownsFolder = suffix == ".uitkx"
                && string.Equals(Path.GetFileName(dir), oldName, System.StringComparison.Ordinal);
            newDir = ownsFolder
                ? Path.Combine(Path.GetDirectoryName(dir) ?? "", newName)
                : dir;
            return Path.GetFullPath(Path.Combine(newDir, newName + suffix));
        }

        private static string SuffixOf(string fileName) =>
            fileName.EndsWith(".style.uitkx", System.StringComparison.OrdinalIgnoreCase)
                ? ".style.uitkx"
                : fileName.EndsWith(".hooks.uitkx", System.StringComparison.OrdinalIgnoreCase)
                    ? ".hooks.uitkx"
                    : ".uitkx";

        public void OpenAdditionalFile(string filePath)
        {
            _focusFile = Path.GetFullPath(filePath);
            _workspace.Open(_focusFile);
            MountCanvas();
            RefreshChrome();
        }

        /// <summary>Discards every pending change. Abort puts PATHS back as well
        /// as text - a renamed module returns to its old name, and a module that
        /// rode along inside a renamed folder returns with it - so the canvas has
        /// to be rebuilt rather than merely repainted, and the saved layout has to
        /// follow the modules back the same way it followed them out. What moved is
        /// read off the tree BEFORE the abort, because the abort is what forgets
        /// it: every module inside a moved folder reports the move itself, so there
        /// is no folder-level move to capture separately any more.</summary>
        private void AbortAll()
        {
            var moduleMoves = new System.Collections.Generic.List<(string From, string To)>();
            foreach (var module in _workspace.Modules)
                if (module.HasMoved)
                    moduleMoves.Add((module.FilePath, module.DiskPath));

            int reverted = _workspace.AbortAll();
            // Abort is the user throwing the work away deliberately; keeping a
            // journal of it would offer it back on the next open.
            BuilderReloadJournal.Clear();
            if (reverted <= 0)
            {
                RefreshChrome();
                return;
            }

            foreach (var move in moduleMoves)
            {
                _canvasHost?.RepathLayout(move.From, move.To, isFolder: false);
                if (string.Equals(FocusFull, Path.GetFullPath(move.From),
                        System.StringComparison.OrdinalIgnoreCase))
                    _focusFile = move.To;
            }

            Toast($"Discarded {reverted} buffer(s)");
            RebindFocusIfMissing();
            RefreshChrome();
            MountCanvas();
        }

        [System.NonSerialized] private double _lastJournalAt;

        private void OnWorkspaceChanged()
        {
            RefreshChrome();
            if (_showFolders)
                _folderPane?.Rebuild();
            // Crash cover. Throttled, so the journal can trail the tree by a few
            // seconds - which is the honest cost of not writing the whole tree on
            // every commit, and still the difference between losing a session and
            // losing a moment of it.
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (now - _lastJournalAt < 5.0)
                return;
            _lastJournalAt = now;
            BuilderReloadJournal.Capture(_workspace);
        }

        /// <summary>Dumps the tree before the domain goes away. Unthrottled: this
        /// is the moment the in-memory copy stops existing.</summary>
        private void JournalTree() => BuilderReloadJournal.Capture(_workspace);

        /// <summary>Offers unsaved work back after the builder came up with
        /// nothing. The journal only exists when work was unsaved, so its presence
        /// beside an empty tree IS the evidence something was lost - a reload that
        /// went fine leaves a tree here, and a clean session leaves no journal.</summary>
        private void OfferJournalRestore()
        {
            UnityEditor.EditorApplication.delayCall -= OfferJournalRestore;
            if (this == null || _workspace.Modules.Count > 0)
                return;
            if (!BuilderReloadJournal.TryPeek(out int modules, out string savedAt))
                return;
            if (!UnityEditor.EditorUtility.DisplayDialog(
                    "Restore unsaved work?",
                    "The builder has " + modules + " module(s) from " + savedAt
                    + " that were never written to disk.\n\nRestore them, or discard "
                    + "and start clean?",
                    "Restore",
                    "Discard"))
            {
                BuilderReloadJournal.Clear();
                return;
            }
            if (!BuilderReloadJournal.TryRestore(_workspace))
            {
                Toast("Could not restore - the journal did not read back");
                return;
            }
            RebindFocusIfMissing();
            RefreshChrome();
            MountCanvas();
            Toast("Restored " + modules + " unsaved module(s)");
        }

        private void RefreshChrome()
        {
            hasUnsavedChanges = _workspace.HasUnsavedChanges;
            if (_statusLabel != null)
            {
                int open = 0, dirty = 0;
                foreach (var s in _workspace.Modules)
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
