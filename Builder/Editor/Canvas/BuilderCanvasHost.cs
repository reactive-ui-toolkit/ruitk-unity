#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;
using Ruitk;
using Ruitk.EditorSupport;
using Ruitk.Uitkx.Canvas.CanvasView;

namespace Ruitk.Builder
{
    /// <summary>
    /// Loads a tree's graph through the shared LSP client, overlays the
    /// persisted per-root layout, and mounts the dogfooded CanvasView into the
    /// window's canvas pane. Layout/camera changes are written straight through
    /// to <see cref="BuilderCanvasConfig"/> — the JSON is tiny and writes happen
    /// on drag-end/zoom, not per pointer-move.
    /// </summary>
    internal sealed class BuilderCanvasHost
    {
        private VisualElement _container;
        private BuilderGraph _graph;
        private BuilderCanvasConfig _config;
        private float _camX;
        private float _camY;
        private float _zoom = 1f;
        private int _viewVersion;
        private Action<string> _onOpenFile;

        /// <summary>Reports the live zoom so the toolbar's L0/L1/L2 buttons can
        /// show the active LOD (POC toolbar, not a canvas overlay).</summary>
        public Action<float> ZoomChanged;

        public float Zoom => _zoom;

        public Action<string, int> OnRowClick;
        public Action<string, int, int> OnRowContext;
        public Action<string, float, float> OnCreateRequested;
        public Action<int> OnSelect;
        public Action<string> OnToast;
        public Action<string, int, int, string> OnRowDrop;
        public Action<string, string, int> OnStyleAddEntry;
        public Action<string> OnAddHook;
        public Action<string> OnDeleteFile;
        public Action<string, int, int, string> OnAttrValueEdit;
        public Action<string, int, string> OnDirectiveEdit;
        public Action<string, int, string> OnLineRewrite;
        public Action<string, int, int, string> OnIslandEdit;
        public Action<string> OnAddStyleExport;
        public Action<string> OnAddUtilExport;
        public Action<string> OnTraceStates;

        public async void Mount(
            VisualElement container,
            string focusFile,
            Action<string> onOpenFile,
            Func<string, string> readText = null,
            Action<BuilderGraph> onGraphLoaded = null)
        {
            if (container == null || string.IsNullOrEmpty(focusFile))
                return;
            _container = container;
            ShowMessage("Loading tree…");

            BuilderGraph graph;
            try
            {
                var client = await BuilderLspService.GetOrStartAsync();
                graph = await BuilderGraphService.LoadTreeAsync(client, focusFile, readText);
                await CheckSchemaDrift(client);
            }
            catch (Exception ex)
            {
                ShowMessage("LSP unavailable: " + ex.Message);
                return;
            }
            if (_container == null || _container.panel == null)
                return;

            _graph = graph;
            onGraphLoaded?.Invoke(graph);
            _config = BuilderCanvasConfig.LoadForMember(focusFile)
                ?? BuilderCanvasConfig.LoadForRoot(_graph.RootPath);
            _config.ApplyTo(_graph);
            _camX = _config.CameraX;
            _camY = _config.CameraY;
            _zoom = _config.Zoom <= 0f ? 1f : _config.Zoom;

            _onOpenFile = onOpenFile;
            _container.Clear();
            ZoomChanged?.Invoke(_zoom);
            RenderCanvas();
        }

        /// <summary>POC toolbar L0/L1/L2: jump the camera to a zoom preset. The
        /// mounted CanvasView applies it through a bumped view version rather
        /// than a remount, so the graph is not reloaded.</summary>
        public void SetViewPreset(float zoom)
        {
            if (_container == null || _graph == null)
                return;
            _zoom = zoom;
            _camX = 60f;
            _camY = 30f;
            _viewVersion++;
            RenderCanvas();
            SaveLayout();
            ZoomChanged?.Invoke(_zoom);
        }

        /// <summary>POC commitNode(): a model edit rebuilds ONE card and redraws
        /// the edges — it never reloads the tree. Re-parses the changed file
        /// into the live graph node and re-renders in place, so zoom, camera,
        /// card selection and row selection all survive the commit.</summary>
        public void RefreshGraph(string filePath, Func<string, string> readText)
        {
            if (_graph == null || _container == null)
                return;
            int index = _graph.IndexOf(System.IO.Path.GetFullPath(filePath));
            if (index < 0)
                return;
            try
            {
                BuilderGraphService.PopulateCardDetail(_graph.Nodes[index], readText);
            }
            catch (Exception)
            {
                return;
            }
            RenderCanvas();
        }

        /// <summary>POC follow-up editors (addAttr / addHook / wrap-in-directive
        /// all open the new element's inline field): opens an inline editor on
        /// the mounted canvas without a remount.</summary>
        public void BeginEdit(string editKey, string editText)
        {
            if (_container == null || _graph == null)
                return;
            _beginEditKey = editKey;
            _beginEditText = editText ?? "";
            _beginEditVersion++;
            RenderCanvas();
        }

        private string _beginEditKey = "";
        private string _beginEditText = "";
        private int _beginEditVersion;
        private string _selectPath = "";
        private int _selectVersion;

        /// <summary>POC selectNode(): opening a file from any route (row
        /// double-click, preview click-through, library) moves the gold ring to
        /// that card, not just the source/preview panes.</summary>
        public void SelectByPath(string filePath)
        {
            if (_container == null || _graph == null || string.IsNullOrEmpty(filePath))
                return;
            string full = System.IO.Path.GetFullPath(filePath);
            if (_graph.IndexOf(full) < 0)
                return;
            if (string.Equals(_selectPath, full, StringComparison.OrdinalIgnoreCase))
                return;
            _selectPath = full;
            _selectVersion++;
            RenderCanvas();
        }

        /// <summary>Canvas row index of a source line, for BeginEdit keys.</summary>
        public int RowIndexOfLine(string filePath, int sourceLine)
        {
            var node = FindNode(System.IO.Path.GetFullPath(filePath));
            if (node == null)
                return -1;
            for (int i = 0; i < node.Markup.Count; i++)
                if (node.Markup[i].SourceLine == sourceLine)
                    return i;
            return -1;
        }

        /// <summary>Seeds the persisted layout slot for a file about to be
        /// created, so the new card appears where the user right-clicked.</summary>
        public void PlaceNewCard(string filePath, float x, float y)
        {
            if (_config == null || (x == 0f && y == 0f))
                return;
            _config.SetPosition(filePath, x, y);
            _config.Save();
        }

        public int NodeIndexOf(string filePath) =>
            _graph?.IndexOf(System.IO.Path.GetFullPath(filePath)) ?? -1;

        private void RenderCanvas()
        {
            var onOpenFile = _onOpenFile;
            EditorRootRendererUtility.Render(
                _container,
                V.Func(
                    CanvasView.Render,
                    new CanvasView.CanvasViewProps
                    {
                        Graph = _graph,
                        InitialCamX = _camX,
                        InitialCamY = _camY,
                        InitialZoom = _zoom,
                        ViewZoom = _zoom,
                        ViewCamX = _camX,
                        ViewCamY = _camY,
                        ViewVersion = _viewVersion,
                        BeginEditKey = _beginEditKey,
                        BeginEditText = _beginEditText,
                        BeginEditVersion = _beginEditVersion,
                        SelectPath = _selectPath,
                        SelectVersion = _selectVersion,
                        OnTraceStates = states => OnTraceStates?.Invoke(states),
                        OnOpenFile = onOpenFile,
                        OnLayoutChanged = SaveLayout,
                        OnSelect = index =>
                        {
                            if (_graph != null && index >= 0 && index < _graph.Nodes.Count)
                                OnSelect?.Invoke(index);
                        },
                        OnCameraChanged = (x, y, z) =>
                        {
                            _camX = x;
                            _camY = y;
                            bool lodChanged = LodOf(_zoom) != LodOf(z);
                            _zoom = z;
                            SaveLayout();
                            if (lodChanged)
                                ZoomChanged?.Invoke(z);
                        },
                        OnCardContext = ShowCardMenu,
                        OnRowClick = (path, line) => OnRowClick?.Invoke(path, line),
                        OnRowContext = (path, line, rowIdx) => OnRowContext?.Invoke(path, line, rowIdx),
                        OnCanvasContext = (wx, wy) => ShowCreateMenu(wx, wy),
                        OnRowDrop = (path, rowIdx, band, payload) =>
                            OnRowDrop?.Invoke(path, rowIdx, band, payload),
                        OnStyleAddEntry = (path, styleName, closeLine) =>
                            OnStyleAddEntry?.Invoke(path, styleName, closeLine),
                        OnAddHook = path => OnAddHook?.Invoke(path),
                        OnAttrValueEdit = (path, line, ai, value) =>
                            OnAttrValueEdit?.Invoke(path, line, ai, value),
                        OnDirectiveEdit = (path, line, text) =>
                            OnDirectiveEdit?.Invoke(path, line, text),
                        OnLineRewrite = (path, line, text) =>
                            OnLineRewrite?.Invoke(path, line, text),
                        OnIslandEdit = (path, start, end, text) =>
                            OnIslandEdit?.Invoke(path, start, end, text),
                        OnAddStyleExport = path => OnAddStyleExport?.Invoke(path),
                        OnAddUtilExport = path => OnAddUtilExport?.Invoke(path),
                        OnRowNavigate = tag =>
                        {
                            var target = FindNodeByTitle(tag);
                            if (target != null)
                                onOpenFile?.Invoke(target.FilePath);
                        },
                    }
                )
            );
        }

        /// <summary>POC LOD bands: &lt;0.45 = L0, &lt;1.05 = L1, else L2.</summary>
        public static int LodOf(float zoom) => zoom < 0.45f ? 0 : zoom < 1.05f ? 1 : 2;

        public BuilderCanvasNode FindNodeByTitle(string title)
        {
            if (_graph == null)
                return null;
            foreach (var node in _graph.Nodes)
                if (string.Equals(node.Title, title, StringComparison.OrdinalIgnoreCase))
                    return node;
            return null;
        }

        public List<BuilderCanvasNode> Nodes => _graph?.Nodes;

        public BuilderCanvasNode FindNode(string filePath)
        {
            if (_graph == null)
                return null;
            int i = _graph.IndexOf(filePath);
            return i < 0 ? null : _graph.Nodes[i];
        }

        /// <summary>POC btnLibNew: the Library's "+ new" opens the SAME four-item
        /// create menu the empty-canvas right-click does.</summary>
        public void ShowCreateMenuAtPointer()
        {
            // 0,0 leaves the new card to the default BFS placement — the
            // library button has no world position of its own.
            ShowCreateMenu(0f, 0f);
        }

        private void ShowCreateMenu(float worldX, float worldY)
        {
            BuilderSearchMenu.ShowSimple("create", new List<BuilderSearchMenu.Item>
            {
                new BuilderSearchMenu.Item
                {
                    Label = "New component  (.uitkx)",
                    OnPick = () => OnCreateRequested?.Invoke("Component", worldX, worldY),
                },
                new BuilderSearchMenu.Item
                {
                    Label = "New style module  (.style.uitkx)",
                    OnPick = () => OnCreateRequested?.Invoke("Style", worldX, worldY),
                },
                new BuilderSearchMenu.Item
                {
                    Label = "New hook module  (.hooks.uitkx)",
                    OnPick = () => OnCreateRequested?.Invoke("Hooks", worldX, worldY),
                },
                new BuilderSearchMenu.Item
                {
                    Label = "New util module  (.uitkx)",
                    OnPick = () => OnCreateRequested?.Invoke("Utils", worldX, worldY),
                },
            });
        }

        /// <summary>POC openCardMenu: exactly ONE item under the node-id title —
        /// "Delete &lt;file&gt;" — guarded by a non-blocking toast when anything
        /// still references the file.</summary>
        private void ShowCardMenu(int index)
        {
            if (_graph == null || index < 0 || index >= _graph.Nodes.Count)
                return;
            var node = _graph.Nodes[index];
            var items = new List<BuilderSearchMenu.Item>
            {
                new BuilderSearchMenu.Item
                {
                    Label = "Delete " + System.IO.Path.GetFileName(node.FilePath),
                    OnPick = () =>
                    {
                        var referencedBy = new List<string>();
                        foreach (var edge in _graph.Edges)
                            if (edge.ToIndex == index && edge.FromIndex >= 0
                                && !referencedBy.Contains(_graph.Nodes[edge.FromIndex].Title))
                                referencedBy.Add(_graph.Nodes[edge.FromIndex].Title);
                        if (referencedBy.Count > 0)
                        {
                            OnToast?.Invoke(
                                "Can't delete: still referenced by "
                                + string.Join(", ", referencedBy) + ".");
                            return;
                        }
                        OnDeleteFile?.Invoke(node.FilePath);
                    },
                },
            };
            BuilderSearchMenu.ShowSimple(node.Title, items);
        }

        public void Unmount()
        {
            if (_container != null)
            {
                EditorRootRendererUtility.Unmount(_container);
                _container = null;
            }
            _graph = null;
        }

        private void SaveLayout()
        {
            if (_graph == null || _config == null)
                return;
            _config.CaptureFrom(_graph, _camX, _camY, _zoom);
            _config.Save();
        }

        private static bool s_driftChecked;

        /// <summary>VE-16: the palette/completion vocabulary (embedded schema)
        /// and the live runtime registry must agree — a registered-but-unschema'd
        /// element renders but false-flags in editors; the converse is a palette
        /// entry that cannot render. Mismatches warn visibly, once per session.</summary>
        private static async System.Threading.Tasks.Task CheckSchemaDrift(BuilderLspClient client)
        {
            if (s_driftChecked)
                return;
            s_driftChecked = true;

            var schema = await client.RequestSchema();
            string json = schema?.Value<string>("json") ?? schema?.Value<string>("Json");
            if (string.IsNullOrEmpty(json))
                return;
            var schemaElements = new HashSet<string>(StringComparer.Ordinal);
            if (JObject.Parse(json)["elements"] is JObject elements)
                foreach (var prop in elements.Properties())
                    schemaElements.Add(prop.Name);

            var registry = Ruitk.Elements.ElementRegistryProvider.GetDefaultRegistry();
            var missingFromSchema = new List<string>();
            foreach (string name in registry.RegisteredNames)
                if (!schemaElements.Contains(name))
                    missingFromSchema.Add(name);

            if (missingFromSchema.Count > 0)
                UnityEngine.Debug.LogWarning(
                    "[RUITK Builder] schema/runtime drift: registered elements missing from the "
                    + "editor schema (palette and completion will not offer them): "
                    + string.Join(", ", missingFromSchema));
        }

        private void ShowMessage(string text)
        {
            if (_container == null)
                return;
            EditorRootRendererUtility.Unmount(_container);
            _container.Clear();
            _container.Add(new Label(text)
            {
                style = { marginTop = 12f, marginLeft = 12f, color = new UnityEngine.Color(0.6f, 0.6f, 0.65f) },
            });
        }
    }
}
#endif
