#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
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

        /// <summary>UB-88: files to leave out of the tree even though they are
        /// still on disk — deletions the user has made but not yet saved.</summary>
        public Func<string, bool> IsFileHidden;
        public Action<string, int, int, string, VisualElement> OnEditAttrValue;
        public Action<string, int, string, VisualElement> OnEditDirective;
        public Action<string, int, string, string, VisualElement> OnEditLine;
        public Action<string, int, int, string, VisualElement> OnEditIsland;
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
                graph = await BuilderGraphService.LoadTreeAsync(
                    client, focusFile, readText, IsFileHidden);
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
            // A layout persisted under a different range loads outside the live
            // one and inverts the first zoom gesture; clamp on load so the
            // stored camera is always inside it.
            _zoom = _config.Zoom <= 0f
                ? 1f
                : Mathf.Clamp(_config.Zoom, BuilderCanvasDrawing.ZoomMin, BuilderCanvasDrawing.ZoomMax);

            _onOpenFile = onOpenFile;
            _container.Clear();
            // POC selectNode(): the file the window opened on wears the gold ring
            // from the first frame — including a file that was just created.
            _selectPath = System.IO.Path.GetFullPath(focusFile);
            // UB-30/31/32: the drag machine resolves targets by hit-test over
            // this host's live graph, drops through the same OnRowDrop sink the
            // rows used, repaints hints on the edge overlay, and cancels loudly.
            BuilderDragService.HitTester = HitTest;
            BuilderDragService.DropHandler =
                (path, rowIdx, band, payload) => OnRowDrop?.Invoke(path, rowIdx, band, payload);
            BuilderDragService.RepaintHints =
                () => _container?.Q("ruitk-edge-layer")?.MarkDirtyRepaint();
            BuilderDragService.OnBlockedDrop = message => OnToast?.Invoke(message);
            // UB-81: the cull window is measured from the container, and pans and
            // zooms recompute it inside the fiber from its own camera state. A
            // RESIZE changes nothing the fiber can see, so it is pushed here —
            // guarded on an actual size change so layout churn cannot loop.
            _container.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float w = _container?.resolvedStyle.width ?? 0f;
                float h = _container?.resolvedStyle.height ?? 0f;
                if (_graph == null
                    || (Mathf.Approximately(w, _viewportW) && Mathf.Approximately(h, _viewportH)))
                    return;
                _viewportW = w;
                _viewportH = h;
                _viewVersion++;
                RenderCanvas();
            });
            ZoomChanged?.Invoke(_zoom);
            RenderCanvas();
        }

        private float _viewportW;
        private float _viewportH;

        /// <summary>Panel point → drop target: pick, walk up to the first
        /// "row-{card}-{row}" (else "card-{index}") name, band from the row's
        /// worldBound thirds with the root/clause coercions applied.</summary>
        private BuilderDropTarget HitTest(Vector2 panelPos)
        {
            var panel = _container?.panel;
            if (panel == null || _graph == null)
                return default;
            VisualElement rowEl = null;
            VisualElement cardEl = null;
            bool inCanvas = false;
            var picked = panel.Pick(panelPos);
            // A release on a scroller thumb is not a drop target — treating it
            // as a card-level drop appended elements the user never aimed at.
            if (picked != null && picked.GetFirstAncestorOfType<Scroller>() != null)
                return default;
            for (var walk = picked; walk != null; walk = walk.parent)
            {
                if (walk == _container)
                {
                    inCanvas = true;
                    break;
                }
                string name = walk.name ?? "";
                if (rowEl == null && name.StartsWith("row-", StringComparison.Ordinal))
                    rowEl = walk;
                if (cardEl == null && name.StartsWith("card-", StringComparison.Ordinal))
                    cardEl = walk;
            }
            // Names are only trustworthy INSIDE the canvas pane — the preview
            // mounts arbitrary user components whose elements could carry a
            // "card-N" name of their own.
            if (!inCanvas || cardEl == null)
                return default;
            if (!int.TryParse(cardEl.name.Substring(5), out int cardIndex)
                || cardIndex < 0 || cardIndex >= _graph.Nodes.Count)
                return default;
            var node = _graph.Nodes[cardIndex];
            if (rowEl != null)
            {
                var parts = rowEl.name.Split('-');
                if (parts.Length == 3 && int.TryParse(parts[2], out int rowIdx)
                    && rowIdx >= 0 && rowIdx < node.Markup.Count)
                {
                    var row = node.Markup[rowIdx];
                    var bound = rowEl.worldBound;
                    float rel = bound.height <= 0f
                        ? 0.5f
                        : (panelPos.y - bound.yMin) / bound.height;
                    int band = rel < 0.3f ? 0 : rel > 0.7f ? 2 : 1;
                    if (rowIdx == BuilderCanvasDrawing.FirstElementRow(node)
                        || (row.Kind == BuilderCardLineKind.Directive && row.ClauseIndex > 0))
                        band = 1;
                    return new BuilderDropTarget
                    {
                        Valid = true,
                        Path = node.FilePath,
                        RowIdx = rowIdx,
                        Band = band,
                        RowElementName = rowEl.name,
                        CardIndex = cardIndex,
                    };
                }
            }
            return new BuilderDropTarget
            {
                Valid = true,
                Path = node.FilePath,
                RowIdx = -1,
                Band = 1,
                CardIndex = cardIndex,
            };
        }

        /// <summary>Ctrl+wheel zoom for pointers over a scrolling section —
        /// the same anchored-zoom math the canvas root runs, driven host-side
        /// because the section ScrollView consumes the plain wheel.</summary>
        private void WheelZoom(float deltaY, UnityEngine.Vector2 panelPos)
        {
            if (_container == null || _graph == null)
                return;
            float factor = deltaY < 0f ? 1.12f : 1f / 1.12f;
            float next = UnityEngine.Mathf.Clamp(
                _zoom * factor, BuilderCanvasDrawing.ZoomMin, BuilderCanvasDrawing.ZoomMax);
            if (UnityEngine.Mathf.Approximately(next, _zoom))
                return;
            var local = _container.WorldToLocal(panelPos);
            float scale = next / _zoom;
            _camX = local.x - (local.x - _camX) * scale;
            _camY = local.y - (local.y - _camY) * scale;
            _zoom = next;
            _viewVersion++;
            RenderCanvas();
            SaveLayout();
            ZoomChanged?.Invoke(_zoom);
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

        /// <summary>UB-76: follow-up editors (addAttr / addHook / wrap all open
        /// the new element's editor) are the window's floating inline editor
        /// now — this resolves the named canvas element the window anchors on,
        /// retrying while the re-render lands.</summary>
        public void WithCanvasElement(string elementName, Action<VisualElement> action, int attempts = 12)
        {
            if (_container == null || action == null)
                return;
            var found = _container.Q(elementName);
            if (found != null && found.worldBound.width > 0f)
            {
                action(found);
                return;
            }
            if (attempts <= 0)
                return;
            _container.schedule.Execute(
                () => WithCanvasElement(elementName, action, attempts - 1)).ExecuteLater(40);
        }

        private string _selectPath = "";
        private int _selectVersion;

        private string _selRowPath = "";
        private int _selRowIdx = -1;
        private int _selRowLine;

        /// <summary>UB-74: the fiber's row selection, mirrored out so the window
        /// can answer "what is selected right now" when Delete is pressed. The
        /// card ring already had this shape; rows did not.</summary>
        public string SelectedRowPath => _selRowPath;

        public int SelectedRowIndex => _selRowIdx;

        public int SelectedRowLine => _selRowLine;

        public string SelectedCardPath => _selectPath;

        public void ClearRowSelection()
        {
            _selRowPath = "";
            _selRowIdx = -1;
            _selRowLine = 0;
        }

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

        /// <summary>UB-80: FRAME a card — the palette's workspace list, which
        /// already knows every module on the canvas, becomes a way to GET to
        /// one. This is "frame selected" as every node editor means it: the zoom
        /// is solved so the card FILLS the viewport (owner: focusing must "fully
        /// zoom it", not just pan it into view), then the camera centres on it.
        /// <para>The fit is solved on WIDTH only. Fitting height too meant a long
        /// page card — ShowcaseDemoPage, hundreds of markup rows — solved to
        /// ZoomMin and read as "it panned but never zoomed" (owner report
        /// 2026-08-17). Card width is uniform per LOD, so a width fit also makes
        /// the gesture land at the same readable zoom for every card instead of
        /// one that swings with how much markup a file happens to hold; a card
        /// taller than the viewport is pinned near its top, where its title
        /// is.</para>
        /// <para>Width is LOD-dependent and LOD is zoom-dependent, so the fit is
        /// solved twice — the second pass uses the width the first pass's zoom
        /// implies. Two passes suffice because there are only three bands.</para></summary>
        public bool FocusNode(string filePath)
        {
            if (_container == null || _graph == null || string.IsNullOrEmpty(filePath))
                return false;
            string full = System.IO.Path.GetFullPath(filePath);
            int index = _graph.IndexOf(full);
            if (index < 0)
                return false;
            var node = _graph.Nodes[index];
            float width = _container.resolvedStyle.width;
            float height = _container.resolvedStyle.height;
            if (width <= 0f || height <= 0f)
                return false;
            float cardH = BuilderGraphService.CardHeightOf(node);
            if (cardH <= 0f)
                cardH = 1f;
            float zoom = _zoom <= 0f ? 1f : _zoom;
            float cardW = BuilderCanvasDrawing.CardWidthFor(LodOf(zoom));
            for (int pass = 0; pass < 2; pass++)
            {
                cardW = BuilderCanvasDrawing.CardWidthFor(LodOf(zoom));
                zoom = Mathf.Clamp(
                    width * FrameMargin / cardW,
                    BuilderCanvasDrawing.ZoomMin, BuilderCanvasDrawing.ZoomMax);
            }
            _zoom = zoom;
            _camX = width * 0.5f - (node.X + cardW * 0.5f) * zoom;
            _camY = cardH * zoom > height
                ? height * 0.06f - node.Y * zoom
                : height * 0.5f - (node.Y + cardH * 0.5f) * zoom;
            _selectPath = full;
            _selectVersion++;
            _viewVersion++;
            RenderCanvas();
            SaveLayout();
            ZoomChanged?.Invoke(_zoom);
            return true;
        }

        /// <summary>Fraction of the viewport a framed card fills, leaving a
        /// margin so the card does not touch the window edges.</summary>
        private const float FrameMargin = 0.88f;

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
                        SelectPath = _selectPath,
                        SelectVersion = _selectVersion,
                        ViewportW = _container?.resolvedStyle.width ?? 0f,
                        ViewportH = _container?.resolvedStyle.height ?? 0f,
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
                        OnRowSelect = (path, rowIdx, line) =>
                        {
                            _selRowPath = path;
                            _selRowIdx = rowIdx;
                            _selRowLine = line;
                        },
                        OnRowContext = (path, line, rowIdx) => OnRowContext?.Invoke(path, line, rowIdx),
                        OnCanvasContext = (wx, wy) => ShowCreateMenu(wx, wy),
                        OnStyleAddEntry = (path, styleName, closeLine) =>
                            OnStyleAddEntry?.Invoke(path, styleName, closeLine),
                        OnAddHook = path => OnAddHook?.Invoke(path),
                        OnEditAttrValue = (path, line, ai, seed, anchor) =>
                            OnEditAttrValue?.Invoke(path, line, ai, seed, anchor),
                        OnEditDirective = (path, line, seed, anchor) =>
                            OnEditDirective?.Invoke(path, line, seed, anchor),
                        OnEditLine = (path, line, seed, suffix, anchor) =>
                            OnEditLine?.Invoke(path, line, seed, suffix, anchor),
                        OnEditIsland = (path, start, end, seed, anchor) =>
                            OnEditIsland?.Invoke(path, start, end, seed, anchor),
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
            StyleIslandScrollers();
        }

        /// <summary>The L2 code islands scroll horizontally like the POC's
        /// ".code-island { overflow-x: auto }". Unity's stock scroller is a light
        /// control laid out INSIDE the island, which would both repaint the card
        /// in editor chrome and steal a line of height; the shared pass turns it
        /// into the same 8px dark overlay every other scroller in the window
        /// wears, so the island keeps the height CanvasView pinned on it.</summary>
        /// <summary>Re-runs the scroller pass after a zoom/LOD change — a
        /// wheel-driven LOD flip re-renders through the fiber without
        /// RenderCanvas, so freshly created section ScrollViews would otherwise
        /// keep stock chrome and no edge-repaint wire until the next mount.</summary>
        public void RestyleScrollers() => StyleIslandScrollers();

        private void StyleIslandScrollers()
        {
            if (_container == null)
                return;
            _container.schedule.Execute(() =>
            {
                if (_container == null)
                    return;
                foreach (var view in _container.Query<ScrollView>("ruitk-island-scroll").ToList())
                {
                    BuilderWindow.StyleScrollers(view);
                    if (view.horizontalScroller != null)
                        view.horizontalScroller.style.height = 6f;
                }
                // §8.2: capped card sections scroll; their scrollers get the same
                // dark chrome, and every scroll repaints the edge overlay so the
                // clamped anchors track live instead of lagging a frame.
                var edgeLayer = _container.Q("ruitk-edge-layer");
                foreach (var view in _container.Query<ScrollView>("ruitk-section-scroll").ToList())
                {
                    BuilderWindow.StyleScrollers(view);
                    if (view.verticalScroller != null)
                    {
                        view.verticalScroller.style.width = 6f;
                        if (edgeLayer != null && !ReferenceEquals(view.userData, s_scrollWired))
                        {
                            view.userData = s_scrollWired;
                            view.verticalScroller.valueChanged += _ => edgeLayer.MarkDirtyRepaint();
                            // A scrollable section consumes the plain wheel (it
                            // scrolls); Ctrl+wheel stays a zoom everywhere.
                            view.RegisterCallback<WheelEvent>(evt =>
                            {
                                if (!evt.ctrlKey)
                                    return;
                                evt.StopImmediatePropagation();
                                WheelZoom(evt.delta.y, evt.mousePosition);
                            }, TrickleDown.TrickleDown);
                        }
                    }
                }
            });
        }

        private static readonly object s_scrollWired = new object();

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

        /// <summary>The parsed import edges — what the preview pane's hook-module
        /// copy names its consumers from.</summary>
        public List<BuilderCanvasEdge> Edges => _graph?.Edges;

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
            // POC btnLibNew: wx = (wr.width / 2 - view.x) / view.s — the new card
            // lands in the MIDDLE of the current view, not at a BFS layout slot.
            float width = _container?.resolvedStyle.width ?? 0f;
            float height = _container?.resolvedStyle.height ?? 0f;
            float zoom = _zoom <= 0f ? 1f : _zoom;
            if (width <= 0f || height <= 0f)
            {
                ShowCreateMenu(0f, 0f);
                return;
            }
            ShowCreateMenu((width * 0.5f - _camX) / zoom, (height * 0.5f - _camY) / zoom);
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
                    OnPick = () => RequestDeleteCard(index),
                },
            };
            BuilderSearchMenu.ShowSimple(node.Title, items);
        }

        /// <summary>The card delete plus its referenced-by guard, in one place so
        /// the keyboard path (UB-74) cannot drift from the menu's rules. The sink
        /// only MARKS the file (UB-88) — nothing reaches disk before Save, which
        /// is what makes this reversible and is why it asks nothing here.</summary>
        public bool RequestDeleteCard(int index)
        {
            if (_graph == null || index < 0 || index >= _graph.Nodes.Count)
                return false;
            var node = _graph.Nodes[index];
            var referencedBy = new List<string>();
            foreach (var edge in _graph.Edges)
                if (edge.ToIndex == index && edge.FromIndex >= 0
                    && !referencedBy.Contains(_graph.Nodes[edge.FromIndex].Title))
                    referencedBy.Add(_graph.Nodes[edge.FromIndex].Title);
            if (referencedBy.Count > 0)
            {
                OnToast?.Invoke(
                    "Can't delete: still referenced by " + string.Join(", ", referencedBy) + ".");
                return false;
            }
            OnDeleteFile?.Invoke(node.FilePath);
            return true;
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
            {
                // Once per drift SET, not once per mount — the same seven names
                // repeated eleven times buried real console output.
                string drift = string.Join(", ", missingFromSchema);
                if (!string.Equals(drift, UnityEditor.SessionState.GetString(DriftWarnedKey, ""),
                        StringComparison.Ordinal))
                {
                    UnityEditor.SessionState.SetString(DriftWarnedKey, drift);
                    UnityEngine.Debug.LogWarning(
                        "[RUITK Builder] schema/runtime drift: registered elements missing from the "
                        + "editor schema (palette and completion will not offer them): " + drift);
                }
            }
        }

        private const string DriftWarnedKey = "Ruitk.Builder.SchemaDriftWarned";

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
