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

        [System.NonSerialized] private Button[] _modeButtons;
        [System.NonSerialized] private BuilderCanvasHost _canvasHost;
        [System.NonSerialized] private BuilderLibraryPane _libraryPane;
        [System.NonSerialized] private CodeField _codeField;
        [System.NonSerialized] private BuilderPreviewPane _previewPane;
        [System.NonSerialized] private BuilderPreviewCompiler _previewCompiler;
        [System.NonSerialized] private double _recompileDue;
        [System.NonSerialized] private bool _recompileScheduled;

        public BuilderWorkspace Workspace => _workspace;

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
            saveChangesMessage = "The RUITK Builder has unsaved component edits.";
        }

        private void OnDisable()
        {
            _workspace.Changed -= OnWorkspaceChanged;
            _canvasHost?.Unmount();
            _canvasHost = null;
            _previewPane?.Dispose();
            _previewPane = null;
            _previewCompiler?.Dispose();
            _previewCompiler = null;
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexGrow = 1f;

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
            _modeButtons = new Button[3];
            string[] modeLabels = { "L0 Architecture", "L1 Cards", "L2 Edit" };
            float[] modeZooms = { 0.30f, 0.75f, 1.25f };
            for (int i = 0; i < 3; i++)
            {
                float zoom = modeZooms[i];
                var button = ToolbarButton(modeLabels[i], () => _canvasHost?.SetViewPreset(zoom));
                _modeButtons[i] = button;
                toolbar.Add(button);
            }
            toolbar.Add(Separator());
            toolbar.Add(ToolbarButton("Import .uxml…", ImportUxml));
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
                "Wheel: zoom" + Bullet + "Drag Library items onto rows (top=before, bottom=after, "
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

            MountCanvas();
            RefreshChrome();
        }

        private void MountCanvas()
        {
            if (string.IsNullOrEmpty(_focusFile))
                return;
            var container = rootVisualElement?.Q("builder-canvas");
            if (container == null)
                return;
            _canvasHost?.Unmount();
            _canvasHost = new BuilderCanvasHost();
            _canvasHost.ZoomChanged = zoom => SetActiveMode(BuilderCanvasHost.LodOf(zoom));
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
                    _canvasHost?.BeginEdit(
                        $"hook:{cardIndex}:{chipIndex}", "var (value, setValue) = useState(0);");
            };
            _canvasHost.OnAttrValueEdit = OnAttrValueEdited;
            _canvasHost.OnDirectiveEdit = OnDirectiveEdited;
            _canvasHost.OnLineRewrite = (path, line, text) =>
                EditLineInFile(Path.GetFullPath(path), line, old =>
                {
                    int w = 0;
                    while (w < old.Length && old[w] == ' ')
                        w++;
                    string rewritten = old.Substring(0, w) + text.Trim();
                    // A line that DECLARES something can never lose its leading
                    // keyword to an inline edit — that would silently turn a style
                    // module into an unparseable file.
                    string trimmedOld = old.TrimStart();
                    if (trimmedOld.StartsWith("export ", System.StringComparison.Ordinal)
                        && !text.TrimStart().StartsWith("export ", System.StringComparison.Ordinal))
                    {
                        Toast("An export declaration keeps its 'export' keyword — edit skipped.");
                        return old;
                    }
                    return rewritten;
                }, "line edit");
            _canvasHost.OnIslandEdit = (path, start, end, text) =>
            {
                var session = _workspace.TryGet(Path.GetFullPath(path));
                if (session == null || session.IsReadOnly || start <= 0)
                    return;
                var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
                int from = Mathf.Clamp(start - 1, 0, lines.Count - 1);
                int to = Mathf.Clamp(end - 1, from, lines.Count - 1);
                lines.RemoveRange(from, to - from + 1);
                // The island now SHOWS its relative indentation, so committing an
                // edit must keep it: only the block's common indent is re-based
                // onto the body's two spaces, never every line flattened.
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
            };
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
            _canvasHost.OnDeleteFile = path =>
            {
                string projectRel = path.Replace('\\', '/');
                int idx = projectRel.IndexOf("/Assets/", System.StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    idx = projectRel.IndexOf("/Packages/", System.StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                    UnityEditor.AssetDatabase.DeleteAsset(projectRel.Substring(idx + 1));
                else
                    File.Delete(path);
                Toast("Deleted " + Path.GetFileName(path));
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
                container, _focusFile, OpenFileFromCanvas, ReadBufferOrDisk,
                graph =>
                {
                    _libraryPane?.SetWorkspaceEntries(graph);
                    _previewPane?.RefreshModuleNotes();
                });
        }

        private void MountLibrary()
        {
            var container = rootVisualElement?.Q("builder-library");
            if (container == null)
                return;
            if (_libraryPane != null)
                return;
            container.Clear();
            _libraryPane = new BuilderLibraryPane();
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
            _codeField.SetContent(session?.BufferText ?? "", _focusFile, null);
            _codeField.SetEditable(session != null && !session.IsReadOnly);
            // POC selectNode(): opening another file leaves source-edit mode.
            _codeField.SetEditing(_sourceSnapshot != null);
            SyncLspBuffer(_focusFile, session?.BufferText, open: true);
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
                session.ApplyEdit(restore);
                _codeField?.SetContent(restore, _focusFile, null);
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

            var items = response as Newtonsoft.Json.Linq.JArray
                ?? (response?["items"] ?? response?["Items"]) as Newtonsoft.Json.Linq.JArray;
            if (items == null)
                return results;
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
            return results;
        }

        private void RefreshEditedBuffer(BuilderDocumentSession session)
        {
            _codeField?.SetContent(session.BufferText, session.FilePath, null);
            RefreshChrome();
            NotifyBufferChanged();
        }

        private void OnCodeEdited(string bufferLf)
        {
            var session = _workspace.TryGet(_focusFile);
            if (session == null || session.IsReadOnly)
                return;
            session.ApplyEdit(bufferLf);
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
            int cardIndex = _canvasHost?.NodeIndexOf(full) ?? -1;
            if (row.BadgeKind == 0)
            {
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "Wrap in @if",
                    OnPick = () => WrapRowInDirective(full, row, "@if (condition)", cardIndex, rowIdx),
                });
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "Wrap in @foreach",
                    OnPick = () => WrapRowInDirective(
                        full, row, "@foreach (var item in items)", cardIndex, rowIdx),
                });
            }
            else
            {
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "Edit " + row.BadgeText.TrimStart('@') + " condition",
                    OnPick = () =>
                    {
                        if (cardIndex >= 0)
                            _canvasHost?.BeginEdit($"badge:{cardIndex}:{rowIdx}", row.DirectiveText);
                    },
                });
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = "Remove directive",
                    OnPick = () => RemoveDirectiveBlock(full, row.DirectiveLine),
                });
            }
            if (rowIdx > 0)
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
            foreach (string element in BuilderLibraryPane.NativeTagOrder)
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
            // always nests inside.
            if (rowIdx == 0)
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
                        InsertChildTag(full, node.Markup[0], name);
                        break;
                    }
                    if (band == 1)
                    {
                        InsertChildTag(full, row, name);
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
                    string decl = name == "useState" ? "var (value, setValue) = useState(0);"
                        : name == "useEffect" ? "useEffect(() => { }, null);"
                        : name == "useMemo" ? "var memo = useMemo(() => 0, null);"
                        : name == "useRef" ? "var elRef = useRef<VisualElement?>(null);"
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
                    if (!hasRow && node.Markup.Count > 0)
                    {
                        var rootRow = node.Markup[0];
                        row = rootRow;
                        rowIdx = 0;
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
                    // POC: the directive is a PROPERTY of the node, so a move
                    // carries the whole "@if (…) { return ( … ); }" wrapper and a
                    // sibling drop lands OUTSIDE it — never inside the block.
                    int srcFrom = srcRow.DirectiveLine > 0 ? srcRow.DirectiveLine : srcRow.SourceLine;
                    int srcTo = srcRow.DirectiveLine > 0
                        ? MatchingCloseLine(full, srcRow.DirectiveLine)
                        : (srcRow.EndLine > 0 ? srcRow.EndLine : srcRow.SourceLine);
                    if (srcTo <= 0)
                        srcTo = srcRow.EndLine > 0 ? srcRow.EndLine : srcRow.SourceLine;
                    if (row.SourceLine >= srcFrom && row.SourceLine <= srcTo)
                    {
                        Toast("Can't move an element into its own subtree.");
                        break;
                    }
                    bool intoSelfClosing = band == 1 && row.SelfClosing;
                    int destination = appendToRoot
                        ? (row.EndLine > row.SourceLine ? row.EndLine - 1 : row.SourceLine)
                        : band == 0 ? BeforeAnchor(row)
                            : band == 2 || intoSelfClosing ? AfterAnchor(full, row)
                            : (row.EndLine > row.SourceLine ? row.EndLine - 1 : row.SourceLine);
                    MoveLineRange(
                        full, srcFrom, srcTo, destination,
                        appendToRoot ? indent : indent + (band == 1 && !intoSelfClosing ? "  " : ""),
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
        /// below the row's directive block when it carries one, and below the
        /// LAST line of a wrapped self-closing tag otherwise.</summary>
        private int AfterAnchor(string filePath, BuilderCardLine row)
        {
            if (row.DirectiveLine > 0)
            {
                int close = MatchingCloseLine(filePath, row.DirectiveLine);
                if (close > 0)
                    return close;
            }
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
            var session = _workspace.TryGet(filePath);
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
                string wrapped = expr ? "{" + newValue + "}" : "\"" + newValue + "\"";
                return tag.Substring(0, m.Groups[2].Index) + wrapped
                    + tag.Substring(m.Groups[2].Index + m.Groups[2].Length);
            }, what);
        }

        /// <summary>POC 6.3 directive commit: rewrite the directive header line
        /// (text + " {"), preserving indent; empty text jumps to the source
        /// line instead of guessing a block unwrap.</summary>
        private void OnDirectiveEdited(string filePath, int sourceLine, string newText)
        {
            string full = Path.GetFullPath(filePath);
            if (string.IsNullOrWhiteSpace(newText))
            {
                // POC editDirectiveInline: an emptied badge removes the directive
                // and keeps the element.
                RemoveDirectiveBlock(full, sourceLine);
                return;
            }
            EditLineInFile(full, sourceLine, line =>
            {
                int w = 0;
                while (w < line.Length && line[w] == ' ')
                    w++;
                return line.Substring(0, w) + newText.Trim().TrimEnd('{', ' ') + " {";
            }, "directive");
        }

        private void AppendToFile(string filePath, string block, string what = null)
        {
            string full = Path.GetFullPath(filePath);
            OpenSession(full);
            var session = _workspace.TryGet(full);
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
        /// open tag with its POC default value.</summary>
        private void ShowAttributeMenu(
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
                    _canvasHost?.BeginEdit(
                        $"attr:{cardIndex}:{rowIdx}:{newAttrIndex}",
                        value.Length >= 2 ? value.Substring(1, value.Length - 2) : value);
            }

            var component = _canvasHost?.FindNodeByTitle(tag);
            bool custom = component != null && component.Kind == BuilderNodeKind.Component;
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
            if (custom)
            {
                foreach (var (name, type) in PropsOf(component))
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

        /// <summary>POC typed-props attribute source: the target component's own
        /// signature parameters (plus the always-available list "key").</summary>
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

        private static readonly (string Key, string Type)[] s_styleKeys =
        {
            ("FlexGrow", "number"), ("FlexShrink", "number"), ("FlexDirection", "flex-direction"),
            ("JustifyContent", "justify"), ("AlignItems", "align"), ("AlignSelf", "align"),
            ("Width", "length"), ("Height", "length"), ("MinWidth", "length"), ("MaxWidth", "length"),
            ("MinHeight", "length"), ("MaxHeight", "length"), ("Padding", "length"), ("Margin", "length"),
            ("Gap", "length"),
            ("BorderRadius", "length"), ("BorderWidth", "length"), ("BackgroundColor", "color"),
            ("Color", "color"), ("BorderColor", "color"), ("FontSize", "length"),
            ("UnityFontStyle", "font-style"), ("UnityTextAlign", "text-align"), ("Opacity", "number"),
            ("Display", "display"), ("Position", "position"),
        };

        private static string[] ValueTemplatesFor(string type) => type switch
        {
            "number" => new[] { "1", "0", "0.5f" },
            "length" => new[] { "Px(8)", "Px(16)", "Pct(100)", "Pct(50)" },
            "color" => new[] { "Hex(\"#1b1b1f\")", "Hex(\"#4fc3f7\")", "Rgba(0, 0, 0, 128)" },
            "flex-direction" => new[] { "FlexRow", "FlexColumn" },
            "justify" => new[] { "JustifyCenter", "JustifyFlexStart", "JustifyFlexEnd", "JustifySpaceBetween" },
            "align" => new[] { "AlignCenter", "AlignFlexStart", "AlignFlexEnd", "AlignStretch" },
            "font-style" => new[] { "FontBold", "FontItalic", "FontBoldAndItalic" },
            "text-align" => new[] { "TextMiddleCenter", "TextMiddleLeft", "TextUpperLeft" },
            "display" => new[] { "DisplayFlex", "DisplayNone" },
            "position" => new[] { "PosRelative", "PosAbsolute" },
            _ => new[] { "0", "Px(8)", "Pct(100)", "Hex(\"#ffffff\")" },
        };

        /// <summary>POC 6.5: "+ entry" → searchable key menu → value/helper menu
        /// → the entry lands before the export's closing brace.</summary>
        private void OnStyleAddEntry(string filePath, string styleName, int closeLine)
        {
            var used = UsedStyleKeys(filePath, styleName);
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
            foreach (var (key, type) in s_styleKeys)
            {
                if (used.Contains(key))
                    continue;
                string capturedKey = key;
                string capturedType = type;
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = capturedKey + "  :  " + capturedType,
                    OnPick = () => ShowStyleValueMenu(filePath, styleName, closeLine, capturedKey, capturedType),
                });
            }
            BuilderSearchMenu.Show(
                styleName + " — style keys", "search keys…", items,
                free => new BuilderSearchMenu.Item
                {
                    Label = "use key \"" + free + "\"",
                    OnPick = () => ShowStyleValueMenu(filePath, styleName, closeLine, free, ""),
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
            string filePath, string styleName, int closeLine, string key, string type)
        {
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
            foreach (string template in ValueTemplatesFor(type))
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

        private void InsertBeforeLastReturn(string filePath, string line, string what = null)
        {
            var session = _workspace.TryGet(filePath);
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
            var session = _workspace.TryGet(filePath);
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
            var session = _workspace.TryGet(filePath);
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
            var session = _workspace.TryGet(filePath);
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
            var session = _workspace.TryGet(filePath);
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
            var session = _workspace.TryGet(filePath);
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
            var session = _workspace.TryGet(filePath);
            if (session == null || session.IsReadOnly)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int from = Mathf.Clamp(row.SourceLine - 1, 0, lines.Count - 1);
            int to = Mathf.Clamp((row.EndLine > 0 ? row.EndLine : row.SourceLine) - 1, from, lines.Count - 1);
            string indent = IndentOf(filePath, row.SourceLine);
            for (int i = from; i <= to; i++)
                lines[i] = "    " + lines[i];
            lines.Insert(to + 1, indent + "    );");
            lines.Insert(to + 1 + 1, indent + "}");
            lines.Insert(from, indent + "  return (");
            lines.Insert(from, indent + header + " {");
            ApplyProgrammaticEdit(
                filePath, string.Join("\n", lines),
                header.StartsWith("@foreach", System.StringComparison.Ordinal) ? "@foreach" : "@if");
            if (cardIndex >= 0)
                _canvasHost?.BeginEdit($"badge:{cardIndex}:{rowIdx}", header);
        }

        /// <summary>POC "Remove directive" (j.directive = null): the ELEMENT
        /// survives, the wrapper disappears — header line, its <c>return (</c> /
        /// <c>);</c> scaffolding and the closing brace go, and the enclosed block
        /// de-indents by one level. The inverse of WrapRowInDirective.</summary>
        private void RemoveDirectiveBlock(string filePath, int headerLine1)
        {
            var session = _workspace.TryGet(filePath);
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
            var session = _workspace.TryGet(filePath);
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
            var session = _workspace.TryGet(filePath);
            if (session == null)
                return;
            session.ApplyEdit(newBufferLf);
            if (string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(_focusFile),
                    System.StringComparison.OrdinalIgnoreCase))
                _codeField?.SetContent(newBufferLf, _focusFile, null);
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
            var result = _previewCompiler.CompileDirty(_focusFile);
            if (result != null && result.Success)
            {
                var session = _workspace.TryGet(_focusFile);
                _previewPane?.OnRecompiled(result.LoadedAssembly, session?.BufferText);
            }
            else if (result != null)
            {
                _previewPane?.ShowError("Preview compile failed — fix the code pane diagnostics (last good preview kept)");
                Debug.LogWarning("[RUITK Builder] preview compile: " + result.Error);
            }
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
            if (_modeButtons == null)
                return;
            for (int i = 0; i < _modeButtons.Length; i++)
            {
                bool active = i == lod;
                var button = _modeButtons[i];
                button.style.backgroundColor = active ? BuilderPalette.Accent : BuilderPalette.Panel2;
                button.style.color = active ? new Color(0.063f, 0.133f, 0.173f) : BuilderPalette.Text;
                button.userData = active ? BuilderPalette.Accent : BuilderPalette.Line;
                SetBorderColor(button, active ? BuilderPalette.Accent : BuilderPalette.Line);
                button.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
            }
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

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (!evt.ctrlKey && !evt.commandKey)
                return;

            switch (evt.keyCode)
            {
                case KeyCode.S:
                    SaveAll();
                    evt.StopPropagation();
                    break;
                case KeyCode.Z:
                    var focused = _workspace.TryGet(_focusFile);
                    if (focused != null && focused.CanUndo)
                    {
                        focused.Undo();
                        RefreshEditedBuffer(focused);
                    }
                    evt.StopPropagation();
                    break;
                case KeyCode.Y:
                    var f = _workspace.TryGet(_focusFile);
                    if (f != null && f.CanRedo)
                    {
                        f.Redo();
                        RefreshEditedBuffer(f);
                    }
                    evt.StopPropagation();
                    break;
            }
        }

        private void SaveAll()
        {
            bool hmrActive = Ruitk.EditorSupport.HMR.UitkxHmrController.IsActive;
            int written = _workspace.SaveAll();
            BuilderSaveMetrics.RecordSaveBatch(written, hmrActive);
            if (written > 0)
                Toast($"Saved {written} file(s)");
            RefreshChrome();
        }

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
        private void ShowCreatePrompt(string kind, float worldX, float worldY)
        {
            string dir = string.IsNullOrEmpty(_focusFile) ? null : Path.GetDirectoryName(_focusFile);
            if (dir == null)
            {
                Toast("Open a tree first - new files are created beside it");
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
                    string created = BuilderNewFileDialog.Create(dir, kind, name);
                    if (created == null)
                    {
                        Toast("Could not create " + name);
                        return;
                    }
                    _canvasHost?.PlaceNewCard(created, worldX, worldY);
                    Toast("Created " + Path.GetFileName(created));
                    OpenAdditionalFile(created);
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

        public override void SaveChanges()
        {
            _workspace.SaveAll();
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
