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

        public Action<string, int> OnRowClick;
        public Action<string, int, int> OnRowContext;
        public Action<string> OnCreateRequested;

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

            _container.Clear();
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
                        OnOpenFile = onOpenFile,
                        OnLayoutChanged = SaveLayout,
                        OnSelect = null,
                        OnCameraChanged = (x, y, z) =>
                        {
                            _camX = x;
                            _camY = y;
                            _zoom = z;
                            SaveLayout();
                        },
                        OnCardContext = index => ShowCardMenu(index, onOpenFile),
                        OnRowClick = (path, line) => OnRowClick?.Invoke(path, line),
                        OnRowContext = (path, line, rowIdx) => OnRowContext?.Invoke(path, line, rowIdx),
                        OnCanvasContext = ShowCreateMenu,
                    }
                )
            );
        }

        public BuilderCanvasNode FindNode(string filePath)
        {
            if (_graph == null)
                return null;
            int i = _graph.IndexOf(filePath);
            return i < 0 ? null : _graph.Nodes[i];
        }

        private void ShowCreateMenu()
        {
            var menu = new UnityEditor.GenericMenu();
            menu.AddItem(new UnityEngine.GUIContent("New component  (.uitkx)"), false,
                () => OnCreateRequested?.Invoke("Component"));
            menu.AddItem(new UnityEngine.GUIContent("New style module  (.style.uitkx)"), false,
                () => OnCreateRequested?.Invoke("Style"));
            menu.AddItem(new UnityEngine.GUIContent("New hook module  (.hooks.uitkx)"), false,
                () => OnCreateRequested?.Invoke("Hooks"));
            menu.AddItem(new UnityEngine.GUIContent("New util module  (.uitkx)"), false,
                () => OnCreateRequested?.Invoke("Utils"));
            menu.ShowAsContext();
        }

        private void ShowCardMenu(int index, Action<string> onOpenFile)
        {
            if (_graph == null || index < 0 || index >= _graph.Nodes.Count)
                return;
            var node = _graph.Nodes[index];
            var menu = new UnityEditor.GenericMenu();
            menu.AddItem(new UnityEngine.GUIContent("Open"), false, () => onOpenFile?.Invoke(node.FilePath));
            menu.AddItem(new UnityEngine.GUIContent("Show in Project"), false, () =>
            {
                string assetPath = ToAssetPath(node.FilePath);
                var asset = assetPath == null
                    ? null
                    : UnityEditor.AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset != null)
                    UnityEditor.EditorGUIUtility.PingObject(asset);
            });
            menu.AddItem(new UnityEngine.GUIContent("Copy Path"), false, () =>
                UnityEditor.EditorGUIUtility.systemCopyBuffer = node.FilePath);
            menu.ShowAsContext();
        }

        private static string ToAssetPath(string fullPath)
        {
            try
            {
                string projectRoot = System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath);
                string full = System.IO.Path.GetFullPath(fullPath).Replace('\\', '/');
                string root = System.IO.Path.GetFullPath(projectRoot ?? "").Replace('\\', '/');
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(root.Length).TrimStart('/');
            }
            catch
            {
            }
            return null;
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
