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
            _statusLabel = new Label { style = { unityTextAlign = TextAnchor.MiddleLeft, marginLeft = 10f } };
            toolbar.Add(_statusLabel);
            root.Add(toolbar);

            var body = new VisualElement
            {
                name = "builder-body",
                style = { flexGrow = 1f, flexDirection = FlexDirection.Row },
            };
            body.Add(new VisualElement { name = "builder-library", style = { width = 220f, flexShrink = 0f } });
            body.Add(new VisualElement { name = "builder-canvas", style = { flexGrow = 1f } });
            body.Add(new VisualElement { name = "builder-side", style = { width = 420f, flexShrink = 0f } });
            root.Add(body);

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
                _libraryPane.Attach(paletteSection, snippet => _codeField?.InsertAtCaret(snippet));
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
                _previewPane?.ShowError("Compile failed: " + result.Error);
            }
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
