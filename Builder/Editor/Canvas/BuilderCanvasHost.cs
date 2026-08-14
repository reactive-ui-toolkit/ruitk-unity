#if UNITY_EDITOR
using System;
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

        public async void Mount(VisualElement container, string focusFile, Action<string> onOpenFile)
        {
            if (container == null || string.IsNullOrEmpty(focusFile))
                return;
            _container = container;
            ShowMessage("Loading tree…");

            BuilderGraph graph;
            try
            {
                var client = await BuilderLspService.GetOrStartAsync();
                graph = await BuilderGraphService.LoadTreeAsync(client, focusFile);
            }
            catch (Exception ex)
            {
                ShowMessage("LSP unavailable: " + ex.Message);
                return;
            }
            if (_container == null || _container.panel == null)
                return;

            _graph = graph;
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
                    }
                )
            );
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
