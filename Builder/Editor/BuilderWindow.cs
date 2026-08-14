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
            _canvasHost.Mount(container, _focusFile, OpenFileFromCanvas);
        }

        private void OpenFileFromCanvas(string filePath)
        {
            _workspace.Open(filePath);
            _focusFile = filePath;
            RefreshChrome();
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
                        RefreshChrome();
                    }
                    evt.StopPropagation();
                    break;
                case KeyCode.Y:
                    var f = _workspace.TryGet(_focusFile);
                    if (f != null && f.CanRedo)
                    {
                        f.Redo();
                        RefreshChrome();
                    }
                    evt.StopPropagation();
                    break;
            }
        }

        private void SaveAll()
        {
            int written = _workspace.SaveAll();
            if (written > 0)
                ShowNotification(new GUIContent($"Saved {written} file(s)"));
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
