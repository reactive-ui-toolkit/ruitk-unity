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

        [System.NonSerialized] private BuilderCanvasHost _canvasHost;
        [System.NonSerialized] private BuilderLibraryPane _libraryPane;
        [System.NonSerialized] private BuilderOutlinePane _outlinePane;
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
                    flexShrink = 0f,
                    paddingTop = 4f,
                    paddingBottom = 4f,
                    paddingLeft = 6f,
                    paddingRight = 6f,
                },
            };
            toolbar.Add(new Button(SaveAll) { text = "Save" });
            toolbar.Add(new Button(AbortAll) { text = "Abort" });
            toolbar.Add(new Button(NewFile) { text = "New File" });
            toolbar.Add(new Button(ToggleHelp) { text = "? How to drive it" });
            _statusLabel = new Label { style = { unityTextAlign = TextAnchor.MiddleLeft, marginLeft = 10f } };
            toolbar.Add(_statusLabel);
            toolbar.Add(BuildLegend());
            root.Add(toolbar);

            var outerSplit = new TwoPaneSplitView(0, 205f, TwoPaneSplitViewOrientation.Horizontal)
            {
                name = "builder-body",
                style = { flexGrow = 1f },
            };
            outerSplit.Add(new VisualElement { name = "builder-library", style = { minWidth = 160f } });
            var innerSplit = new TwoPaneSplitView(1, 440f, TwoPaneSplitViewOrientation.Horizontal);
            innerSplit.Add(new VisualElement { name = "builder-canvas", style = { minWidth = 300f } });
            innerSplit.Add(new VisualElement { name = "builder-side", style = { minWidth = 280f } });
            outerSplit.Add(innerSplit);
            root.Add(outerSplit);

            var footer = new Label(
                "Wheel: zoom • L0/L1/L2 set the zoom level • Click rows to jump to source; "
                + "Ctrl+Click the preview to jump to a component • Right-click rows / cards / canvas "
                + "for typed attributes, directives, delete, create • Palette click inserts; "
                + "Ctrl+Space completes • Drag the splitters to resize")
            {
                style =
                {
                    flexShrink = 0f,
                    fontSize = 11f,
                    color = new Color(0.55f, 0.55f, 0.59f),
                    paddingLeft = 12f,
                    paddingTop = 4f,
                    paddingBottom = 4f,
                    borderTopWidth = 1f,
                    borderTopColor = new Color(0.23f, 0.23f, 0.27f),
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
            _canvasHost.OnRowClick = OnCanvasRowClicked;
            _canvasHost.OnRowContext = OnCanvasRowContext;
            _canvasHost.OnRowDrop = OnCanvasRowDrop;
            _canvasHost.OnStyleAddEntry = OnStyleAddEntry;
            _canvasHost.OnAddHook = path =>
            {
                OpenSession(Path.GetFullPath(path));
                InsertBeforeLastReturn(Path.GetFullPath(path), "  var (value, setValue) = useState(0);");
            };
            _canvasHost.OnAttrValueEdit = OnAttrValueEdited;
            _canvasHost.OnDirectiveEdit = OnDirectiveEdited;
            _canvasHost.OnLineRewrite = (path, line, text) =>
                EditLineInFile(Path.GetFullPath(path), line, old =>
                {
                    int w = 0;
                    while (w < old.Length && old[w] == ' ')
                        w++;
                    return old.Substring(0, w) + text.Trim();
                });
            _canvasHost.OnIslandEdit = (path, start, end, text) =>
            {
                var session = _workspace.TryGet(Path.GetFullPath(path));
                if (session == null || session.IsReadOnly || start <= 0)
                    return;
                var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
                int from = Mathf.Clamp(start - 1, 0, lines.Count - 1);
                int to = Mathf.Clamp(end - 1, from, lines.Count - 1);
                lines.RemoveRange(from, to - from + 1);
                var replacement = new System.Collections.Generic.List<string>();
                foreach (string l in text.Replace("\r\n", "\n").Split('\n'))
                    if (l.Trim().Length > 0)
                        replacement.Add("  " + l.Trim());
                lines.InsertRange(from, replacement);
                ApplyProgrammaticEdit(Path.GetFullPath(path), string.Join("\n", lines));
            };
            _canvasHost.OnAddStyleExport = path =>
                BuilderSearchMenu.Show(
                    "new style export", "styleName",
                    new System.Collections.Generic.List<BuilderSearchMenu.Item>(),
                    free =>
                    {
                        if (!System.Text.RegularExpressions.Regex.IsMatch(free, "^[a-z][A-Za-z0-9]*$"))
                            return new BuilderSearchMenu.Item { Label = "camelCase identifier required" };
                        return new BuilderSearchMenu.Item
                        {
                            Label = "Create " + free,
                            OnPick = () => AppendToFile(path,
                                "\nexport Style " + free + " = new Style {\n  FlexGrow = 1,\n};"),
                        };
                    });
            _canvasHost.OnAddUtilExport = path =>
            {
                var menu = new UnityEditor.GenericMenu();
                menu.AddItem(new GUIContent("New function…"), false, () =>
                    BuilderSearchMenu.Show("new exported function", "FunctionName",
                        new System.Collections.Generic.List<BuilderSearchMenu.Item>(),
                        free =>
                        {
                            if (!System.Text.RegularExpressions.Regex.IsMatch(free, "^[A-Z][A-Za-z0-9]*$"))
                                return new BuilderSearchMenu.Item { Label = "PascalCase identifier required" };
                            return new BuilderSearchMenu.Item
                            {
                                Label = "Create " + free,
                                OnPick = () => AppendToFile(path,
                                    "\nexport int " + free + "(int value) {\n  return value;\n}"),
                            };
                        }));
                menu.AddItem(new GUIContent("New value…"), false, () =>
                    BuilderSearchMenu.Show("new exported value", "ValueName",
                        new System.Collections.Generic.List<BuilderSearchMenu.Item>(),
                        free =>
                        {
                            if (!System.Text.RegularExpressions.Regex.IsMatch(free, "^[A-Z][A-Za-z0-9]*$"))
                                return new BuilderSearchMenu.Item { Label = "PascalCase identifier required" };
                            return new BuilderSearchMenu.Item
                            {
                                Label = "Create " + free,
                                OnPick = () => AppendToFile(path, "\nexport int " + free + " = 0;"),
                            };
                        }));
                menu.ShowAsContext();
            };
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
                MountCanvas();
            };
            _canvasHost.OnCreateRequested = kind =>
            {
                string dir = string.IsNullOrEmpty(_focusFile) ? null : Path.GetDirectoryName(_focusFile);
                if (dir != null)
                    BuilderNewFileDialog.Show(dir, this, kind);
            };
            _canvasHost.Mount(
                container, _focusFile, OpenFileFromCanvas, ReadBufferOrDisk,
                graph => _libraryPane?.SetWorkspaceEntries(graph));
            MountPreview();
            MountLibrary();
        }

        private void MountLibrary()
        {
            var container = rootVisualElement?.Q("builder-library");
            if (container == null)
                return;
            if (_libraryPane == null)
            {
                container.Clear();
                var paletteSection = new VisualElement { style = { flexGrow = 1f } };
                var outlineSection = new VisualElement
                {
                    style =
                    {
                        flexGrow = 1f,
                        borderTopWidth = 1f,
                        borderTopColor = new Color(0.2f, 0.2f, 0.23f),
                    },
                };
                container.Add(paletteSection);
                container.Add(outlineSection);

                _libraryPane = new BuilderLibraryPane();
                _libraryPane.Attach(paletteSection, (snippet, section) =>
                {
                    bool markup = section == "Native elements"
                        || section == "Custom components"
                        || section == "Directives";
                    bool body = section == "Hooks" || section == "Hook modules";
                    if (markup)
                        _codeField?.InsertSnippet(snippet, isMarkup: true);
                    else if (body)
                        _codeField?.InsertSnippet(snippet, isMarkup: false);
                    else
                        _codeField?.InsertAtCaret(snippet);
                }, NewFile);
                _outlinePane = new BuilderOutlinePane();
                _outlinePane.Attach(
                    outlineSection,
                    () => _workspace.TryGet(_focusFile)?.BufferText,
                    ApplyOutlineEdit);
            }
            _outlinePane.ShowFile(_focusFile);
        }

        private void ApplyOutlineEdit(string bufferLf)
        {
            var session = _workspace.TryGet(_focusFile);
            if (session == null || session.IsReadOnly)
                return;
            session.ApplyEdit(bufferLf);
            _codeField?.SetContent(bufferLf, _focusFile, null);
            RefreshChrome();
            NotifyBufferChanged();
        }

        private void MountPreview()
        {
            var container = rootVisualElement?.Q("builder-side");
            if (container == null || string.IsNullOrEmpty(_focusFile))
                return;
            if (_previewPane == null)
            {
                container.Clear();
                var previewSection = new VisualElement { style = { flexGrow = 1f } };
                var codeSection = new VisualElement
                {
                    style =
                    {
                        flexGrow = 1f,
                        borderTopWidth = 1f,
                        borderTopColor = new Color(0.2f, 0.2f, 0.23f),
                    },
                };
                container.Add(previewSection);
                container.Add(codeSection);

                _previewPane = new BuilderPreviewPane();
                _previewPane.ComponentPicked += OnPreviewComponentPicked;
                _previewPane.Attach(previewSection);
                _codeField = new CodeField();
                _codeField.TextEdited += OnCodeEdited;
                _codeField.CompletionProvider = RequestCompletions;
                codeSection.Add(_codeField);
            }
            var session = _workspace.TryGet(_focusFile);
            _previewPane.ShowFile(_focusFile, session?.BufferText, null);
            _codeField.SetContent(session?.BufferText ?? "", _focusFile, null);
            _codeField.SetEditable(session != null && !session.IsReadOnly);
            SyncLspBuffer(_focusFile, session?.BufferText, open: true);
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
            _outlinePane?.Rebuild();
            SyncLspBuffer(_focusFile, bufferLf, open: false);
            RefreshChrome();
            NotifyBufferChanged();
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

            var menu = new UnityEditor.GenericMenu();
            menu.AddItem(new GUIContent("Add attribute (typed)…"), false, () =>
                ShowAttributeMenu(full, sourceLine, tag));
            if (!string.IsNullOrEmpty(row.AttrsText))
                menu.AddItem(new GUIContent("Remove attribute…"), false, () =>
                    ShowRemoveAttributeMenu(full, sourceLine, row.AttrsText));
            menu.AddItem(new GUIContent("Add child element…"), false, () =>
                InsertLinesInFile(full, sourceLine, IndentOf(full, sourceLine) + "  <VisualElement />"));
            menu.AddSeparator("");
            if (row.BadgeKind == 0)
            {
                menu.AddItem(new GUIContent("Wrap in @if"), false, () =>
                    WrapRowInDirective(full, row, "@if (condition) {"));
                menu.AddItem(new GUIContent("Wrap in @foreach"), false, () =>
                    WrapRowInDirective(full, row, "@foreach (var item in items) {"));
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Delete element"), false, () =>
                DeleteLinesInFile(full, row.SourceLine, row.EndLine > 0 ? row.EndLine : row.SourceLine));
            menu.ShowAsContext();
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
            if (node == null || rowIdx < 0 || rowIdx >= node.Markup.Count)
                return;
            var row = node.Markup[rowIdx];
            var session = _workspace.TryGet(full) ?? OpenSession(full);
            if (session == null || session.IsReadOnly)
            {
                ShowNotification(new GUIContent("Read-only file"));
                return;
            }

            int colon = payload.IndexOf(':');
            string kind = colon < 0 ? payload : payload.Substring(0, colon);
            string name = colon < 0 ? "" : payload.Substring(colon + 1);
            string indent = IndentOf(full, row.SourceLine);

            switch (kind)
            {
                case "element":
                case "component":
                {
                    string seeded = name == "Label" ? "<Label text=\"New label\" />"
                        : name == "Button" ? "<Button text=\"Click\" />"
                        : "<" + name + " />";
                    if (band == 0)
                        InsertLinesInFile(full, row.SourceLine - 1, indent + seeded);
                    else if (band == 2)
                        InsertLinesInFile(full, row.EndLine > 0 ? row.EndLine : row.SourceLine, indent + seeded);
                    else
                        InsertLinesInFile(full, row.SourceLine, indent + "  " + seeded);
                    break;
                }
                case "hook":
                {
                    string decl = name == "useState" ? "var (value, setValue) = useState(0);"
                        : name == "useEffect" ? "useEffect(() => { }, null);"
                        : name == "useMemo" ? "var memo = useMemo(() => 0, null);"
                        : name == "useRef" ? "var elRef = useRef<VisualElement?>(null);"
                        : "var value = " + name + "();";
                    InsertBeforeLastReturn(full, "  " + decl);
                    break;
                }
                case "stylemod":
                case "utilmod":
                {
                    var module = _canvasHost.FindNodeByTitle(
                        kind == "stylemod" ? name : name);
                    string import = BuildImportLine(full, module, kind == "stylemod", name);
                    if (import != null)
                        InsertLinesInFile(full, 0, import);
                    break;
                }
                case "snippet":
                    _codeField?.InsertAtCaret(name);
                    break;
                case "move":
                {
                    int split = name.LastIndexOf(':');
                    if (split < 0)
                        break;
                    string srcPath = Path.GetFullPath(name.Substring(0, split));
                    if (!int.TryParse(name.Substring(split + 1), out int srcRowIdx))
                        break;
                    if (!string.Equals(srcPath, full, System.StringComparison.OrdinalIgnoreCase))
                    {
                        ShowNotification(new GUIContent(
                            "Moving across components isn't supported — delete and re-add"));
                        break;
                    }
                    var srcNode = _canvasHost?.FindNode(srcPath);
                    if (srcNode == null || srcRowIdx < 0 || srcRowIdx >= srcNode.Markup.Count
                        || srcRowIdx == rowIdx)
                        break;
                    var srcRow = srcNode.Markup[srcRowIdx];
                    MoveLineRange(
                        full,
                        srcRow.SourceLine,
                        srcRow.EndLine > 0 ? srcRow.EndLine : srcRow.SourceLine,
                        band == 0 ? row.SourceLine - 1
                            : band == 2 ? (row.EndLine > 0 ? row.EndLine : row.SourceLine)
                            : row.SourceLine,
                        indent + (band == 1 ? "  " : ""));
                    break;
                }
            }
        }

        /// <summary>Relocates a 1-based inclusive line range to sit after
        /// <paramref name="afterLine1"/> (0 = top), guarding against moving a
        /// range into itself and re-indenting to the destination depth.</summary>
        private void MoveLineRange(
            string filePath, int fromLine1, int toLine1, int afterLine1, string destIndent)
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

            string srcIndent = "";
            int w = 0;
            while (w < moved[0].Length && moved[0][w] == ' ')
                w++;
            srcIndent = moved[0].Substring(0, w);
            for (int i = 0; i < moved.Count; i++)
            {
                if (moved[i].StartsWith(srcIndent, System.StringComparison.Ordinal))
                    moved[i] = destIndent + moved[i].Substring(srcIndent.Length);
            }

            lines.RemoveRange(from, to - from + 1);
            int insertAt = afterLine1 > to ? afterLine1 - (to - from + 1) : afterLine1;
            insertAt = Mathf.Clamp(insertAt, 0, lines.Count);
            lines.InsertRange(insertAt, moved);
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines));
        }


        /// <summary>POC 6.3 per-attr commit: rewrite ONE attribute's value on
        /// the row's open-tag line; an empty value removes the attribute.</summary>
        private void OnAttrValueEdited(string filePath, int sourceLine, int attrIdx, string newValue)
        {
            EditLineInFile(Path.GetFullPath(filePath), sourceLine, line =>
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    line, "(\\w+)=(\\{[^}]*\\}|\"[^\"]*\")");
                if (attrIdx < 0 || attrIdx >= matches.Count)
                    return line;
                var m = matches[attrIdx];
                if (string.IsNullOrWhiteSpace(newValue))
                {
                    string removed = line.Remove(m.Index, m.Length);
                    return removed.Replace("  ", " ");
                }
                string oldValue = m.Groups[2].Value;
                bool expr = oldValue.StartsWith("{", System.StringComparison.Ordinal);
                string wrapped = expr ? "{" + newValue + "}" : "\"" + newValue + "\"";
                return line.Substring(0, m.Groups[2].Index) + wrapped
                    + line.Substring(m.Groups[2].Index + m.Groups[2].Length);
            });
        }

        /// <summary>POC 6.3 directive commit: rewrite the directive header line
        /// (text + " {"), preserving indent; empty text jumps to the source
        /// line instead of guessing a block unwrap.</summary>
        private void OnDirectiveEdited(string filePath, int sourceLine, string newText)
        {
            string full = Path.GetFullPath(filePath);
            if (string.IsNullOrWhiteSpace(newText))
            {
                OnCanvasRowClicked(full, sourceLine);
                return;
            }
            EditLineInFile(full, sourceLine, line =>
            {
                int w = 0;
                while (w < line.Length && line[w] == ' ')
                    w++;
                return line.Substring(0, w) + newText.Trim().TrimEnd('{', ' ') + " {";
            });
        }

        private void AppendToFile(string filePath, string block)
        {
            string full = Path.GetFullPath(filePath);
            OpenSession(full);
            var session = _workspace.TryGet(full);
            if (session == null || session.IsReadOnly)
                return;
            ApplyProgrammaticEdit(full, session.BufferText.TrimEnd('\n') + block + "\n");
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
                    Label = full,
                    OnPick = () => EditLineInFile(filePath, sourceLine, line =>
                        line.Replace(" " + full, "").Replace(full, "")),
                });
            }
            BuilderSearchMenu.Show("remove attribute", "search attributes…", items);
        }

        /// <summary>POC 6.4 A.1: searchable typed-attribute menu with the
        /// untyped freeform fallback; the picked attribute lands on the row's
        /// open tag with its POC default value.</summary>
        private void ShowAttributeMenu(string filePath, int sourceLine, string tag)
        {
            void AddAttr(string name, string type)
            {
                EditLineInFile(filePath, sourceLine, line =>
                {
                    int close = line.LastIndexOf("/>", System.StringComparison.Ordinal);
                    if (close < 0)
                        close = line.LastIndexOf('>');
                    if (close < 0)
                        return line;
                    string value = BuilderSchemaCache.DefaultValueFor(name, type);
                    return line.Substring(0, close).TrimEnd() + " " + name + "=" + value
                        + (line.Substring(close).StartsWith("/") ? " " : "") + line.Substring(close);
                });
            }

            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
            foreach (var attr in BuilderSchemaCache.AttributesFor(tag))
            {
                string name = attr.Name;
                string type = attr.Type;
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = name,
                    Detail = type,
                    OnPick = () => AddAttr(name, type),
                });
            }
            BuilderSearchMenu.Show(
                "attributes — " + tag, "search attributes…", items,
                free => new BuilderSearchMenu.Item
                {
                    Label = "add \"" + free + "\" (untyped)",
                    OnPick = () => AddAttr(free, ""),
                });
        }

        private static readonly (string Key, string Type)[] s_styleKeys =
        {
            ("FlexGrow", "number"), ("FlexShrink", "number"), ("FlexDirection", "flex-direction"),
            ("JustifyContent", "justify"), ("AlignItems", "align"), ("AlignSelf", "align"),
            ("Width", "length"), ("Height", "length"), ("MinWidth", "length"), ("MaxWidth", "length"),
            ("MinHeight", "length"), ("MaxHeight", "length"), ("Padding", "length"), ("Margin", "length"),
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
            var items = new System.Collections.Generic.List<BuilderSearchMenu.Item>();
            foreach (var (key, type) in s_styleKeys)
            {
                string capturedKey = key;
                string capturedType = type;
                items.Add(new BuilderSearchMenu.Item
                {
                    Label = capturedKey,
                    Detail = capturedType,
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
                    OnPick = () => InsertStyleEntry(filePath, closeLine, key, captured),
                });
            }
            BuilderSearchMenu.Show(
                key + " — values & helpers", "value or helper…", items,
                free => new BuilderSearchMenu.Item
                {
                    Label = "use \"" + free + "\"",
                    OnPick = () => InsertStyleEntry(filePath, closeLine, key, free),
                });
        }

        private void InsertStyleEntry(string filePath, int closeLine, string key, string value)
        {
            OpenSession(filePath);
            InsertLinesInFile(filePath, closeLine - 1, "  " + key + " = " + value + ",");
        }

        private BuilderDocumentSession OpenSession(string filePath)
        {
            _workspace.Open(filePath);
            return _workspace.TryGet(filePath);
        }

        private void InsertBeforeLastReturn(string filePath, string line)
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
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines));
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
            string line = lines[line1 - 1];
            int i = 0;
            while (i < line.Length && line[i] == ' ')
                i++;
            return line.Substring(0, i);
        }

        private void EditLineInFile(string filePath, int line1, System.Func<string, string> transform)
        {
            var session = _workspace.TryGet(filePath);
            if (session == null || session.IsReadOnly)
                return;
            string[] lines = session.BufferText.Split('\n');
            if (line1 - 1 < 0 || line1 - 1 >= lines.Length)
                return;
            lines[line1 - 1] = transform(lines[line1 - 1]);
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines));
        }

        private void InsertLinesInFile(string filePath, int afterLine1, string newLine)
        {
            var session = _workspace.TryGet(filePath);
            if (session == null || session.IsReadOnly)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int at = Mathf.Clamp(afterLine1, 0, lines.Count);
            lines.Insert(at, newLine);
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines));
        }

        private void DeleteLinesInFile(string filePath, int fromLine1, int toLine1)
        {
            var session = _workspace.TryGet(filePath);
            if (session == null || session.IsReadOnly)
                return;
            var lines = new System.Collections.Generic.List<string>(session.BufferText.Split('\n'));
            int from = Mathf.Clamp(fromLine1 - 1, 0, lines.Count - 1);
            int to = Mathf.Clamp(toLine1 - 1, from, lines.Count - 1);
            lines.RemoveRange(from, to - from + 1);
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines));
        }

        private void WrapRowInDirective(string filePath, BuilderCardLine row, string header)
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
            lines.Insert(from, indent + header);
            ApplyProgrammaticEdit(filePath, string.Join("\n", lines));
        }

        private void ApplyProgrammaticEdit(string filePath, string newBufferLf)
        {
            var session = _workspace.TryGet(filePath);
            if (session == null)
                return;
            session.ApplyEdit(newBufferLf);
            if (string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(_focusFile),
                    System.StringComparison.OrdinalIgnoreCase))
                _codeField?.SetContent(newBufferLf, _focusFile, null);
            _outlinePane?.Rebuild();
            SyncLspBuffer(filePath, newBufferLf, open: false);
            RefreshChrome();
            NotifyBufferChanged();
            MountCanvas();
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
            MountPreview();
            RefreshChrome();
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
                    top = 12f, left = 12f, width = 340f,
                    backgroundColor = new Color(0.137f, 0.137f, 0.16f, 0.97f),
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
                style = { color = new Color(0.31f, 0.76f, 0.97f), fontSize = 13f, marginBottom = 6f },
            });
            string[] steps =
            {
                "1. Wheel zooms (to cursor), drag background pans, drag a card moves it.",
                "2. Zoom all the way out — L0 is the live architecture diagram.",
                "3. Click a card — its source and live preview appear on the right.",
                "4. Hover a hook chip — every row using its state lights up.",
                "5. Zoom close (L2) — attributes and style entries appear.",
                "6. Click a row — its source line highlights. Ctrl+Click the preview — same, per component.",
                "7. At L2 click an attrs string (or '+ attrs') to edit it in place; ✓ commits.",
                "8. Drag a Library item onto a row — top third before, bottom third after, middle nests inside. Hooks drop as declarations; style/util modules add the import.",
                "9. Right-click a row — searchable typed attributes, directives, delete. Right-click a card — open/ping/copy. Right-click empty canvas or New File — create.",
                "10. On a style card, '+ entry' chains a searchable key menu into value helpers.",
                "11. Edit state under STATE — LIVE HOOK VALUES; the preview re-renders.",
                "12. The code pane is bidirectional — type or Ctrl+Space; Save writes every dirty buffer in one batch.",
                "13. Drag the splitters to resize.",
            };
            foreach (string step in steps)
                help.Add(new Label(step)
                {
                    style =
                    {
                        color = new Color(0.84f, 0.84f, 0.86f), fontSize = 11f,
                        whiteSpace = WhiteSpace.Normal, marginBottom = 4f,
                    },
                });
            canvas.Add(help);
            _helpOverlay = help;
        }

        private static VisualElement BuildLegend()
        {
            var legend = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    marginLeft = StyleKeyword.Auto,
                    marginRight = 8f,
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
                ShowNotification(new GUIContent($"Saved {written} file(s)"));
            RefreshChrome();
        }

        private void NewFile()
        {
            string dir = string.IsNullOrEmpty(_focusFile)
                ? null
                : Path.GetDirectoryName(_focusFile);
            if (dir == null)
            {
                ShowNotification(new GUIContent("Open a tree first - new files are created beside it"));
                return;
            }
            BuilderNewFileDialog.Show(dir, this);
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
                ShowNotification(new GUIContent($"Discarded {reverted} buffer(s)"));
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
