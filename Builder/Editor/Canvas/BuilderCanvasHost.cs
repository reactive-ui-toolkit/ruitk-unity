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

        /// <summary>Right-click a COMPONENT card and pick a create row: the
        /// gesture names the parent, so the placement needs nothing inferred.
        /// Carries "path|Kind". Companion cards offer none of it - a style module
        /// has no children.</summary>
        public Action<string> OnCreateUnder;

        /// <summary>The create rows a component card offers, in menu order.</summary>
        private static readonly List<KeyValuePair<string, string>> CreateKinds =
            new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Component", "New component"),
                new KeyValuePair<string, string>("Style", "New style module"),
                new KeyValuePair<string, string>("Hooks", "New hook module"),
                new KeyValuePair<string, string>("Utils", "New util module"),
            };
        public Action<int> OnSelect;
        public Action<string> OnToast;
        public Action<string, int, int, string> OnRowDrop;
        public Action<string, string, int> OnStyleAddEntry;
        public Action<string> OnAddHook;

        /// <summary>UB-117: "+ code" — a statement in the component body, which
        /// until now could only be written in the source pane.</summary>
        public Action<string> OnAddCode;

        /// <summary>UB-123: double-clicking an import row copies its alias.</summary>
        public Action<string> OnCopyImportAlias;

        /// <summary>Right-click on a style row: (file, first line, last line,
        /// what it is). One entry has the same line twice; a whole export spans
        /// its block. There was no gesture here at all, so a style entry - or the
        /// export wrapping a group of them - could be added and never removed.</summary>
        public Action<string, int, int, string> OnStyleRowContext;

        /// <summary>Right-click on an import row: (importer path, specifier).</summary>
        public Action<string, string> OnImportContext;
        /// <summary>UB-124: rename the module a card stands for - its export,
        /// its file, its folder when it owns one, and every importer.</summary>
        public Action<string> OnRenameCard;
        public Action<string> OnDeleteFile;

        /// <summary>The tree the canvas draws. The graph is a PROJECTION of it,
        /// so the host reads modules, never files: nothing it draws can be stale
        /// with respect to what the user has typed, and there is no set of
        /// exceptions - hidden files, pending files, overridden files - to keep in
        /// step with the tree.</summary>
        public Func<IReadOnlyList<BuilderModule>> Modules;

        /// <summary>One module of that tree, for a card rebuild.</summary>
        public Func<string, BuilderModule> ModuleAt;

        /// <summary>The style whose "+ entry" row the keyboard is pointed at,
        /// as (file, style name). Empty when nothing is armed.</summary>
        private string _armedAddFile = "";
        private string _armedAddStyle = "";

        /// <summary>Points the keyboard at a style's "+ entry" row, or clears
        /// it. Re-renders, because the row has to show that Enter will hit it -
        /// an armed affordance nobody can see is a trap, not an affordance.</summary>
        public void ArmStyleAdd(string filePath, string styleName)
        {
            string file = filePath ?? "";
            string style = styleName ?? "";
            if (_armedAddFile == file && _armedAddStyle == style)
                return;
            _armedAddFile = file;
            _armedAddStyle = style;
            if (_graph != null && _container != null)
                RenderCanvas();
        }

        /// <summary>The row element a card drew for one source line, so an
        /// editor can be opened over the NEXT row without waiting for a click.</summary>
        public VisualElement RowElement(string filePath, int sourceLine)
        {
            int index = NodeIndexOf(filePath);
            if (index < 0 || _container == null)
                return null;
            return _container.Q("ruitk-row-" + index + "-" + sourceLine);
        }

        public Action<string, int, int, string, VisualElement> OnEditAttrValue;
        public Action<string, int, string, VisualElement> OnEditDirective;
        public Action<string, int, string, string, VisualElement> OnEditLine;
        public Action<string, int, int, string, VisualElement> OnEditIsland;
        public Action<string> OnAddStyleExport;
        public Action<string> OnAddUtilExport;
        public Action<string> OnTraceStates;

        /// <summary>Draws the tree. SYNCHRONOUS: the graph is a projection of
        /// modules already in memory, so there is nothing to wait for. It used to
        /// await the language server before the first card appeared - every mount
        /// paid for starting a process, and a server that would not start left an
        /// empty window reading "LSP unavailable" where the tree should have
        /// been.</summary>
        public void Mount(
            VisualElement container,
            string focusFile,
            Action<string> onOpenFile,
            Action<BuilderGraph> onGraphLoaded = null)
        {
            if (container == null || string.IsNullOrEmpty(focusFile))
                return;
            _container = container;

            BuilderGraph graph;
            try
            {
                graph = BuilderGraphService.LoadTree(Modules?.Invoke(), focusFile);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                ShowMessage("Could not build the tree: " + ex.Message);
                return;
            }

            _graph = graph;
            onGraphLoaded?.Invoke(graph);
            // Root FIRST. It is derived from the tree, so every module in the tree
            // gives the same answer; the member scan is a fallback for a layout
            // saved before that was true, and it returns whichever file lists the
            // focus first in directory order.
            _config = BuilderCanvasConfig.TryLoadForRoot(_graph.RootPath)
                ?? BuilderCanvasConfig.LoadForMember(focusFile)
                ?? BuilderCanvasConfig.LoadForRoot(_graph.RootPath);
            _config.ApplyTo(_graph);
            // Freeze whatever slots the default layout just handed out, so the
            // next mount reproduces them exactly instead of recomputing a layout
            // that depends on how many cards there are (UB-180).
            if (_config.AdoptUnplaced(_graph))
                _config.Save();
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
            CheckSchemaDriftDetached();
        }

        /// <summary>The schema check needs the language server; the CANVAS does
        /// not. Detached, so a slow or absent server delays a warning rather than
        /// the tree.</summary>
        private async void CheckSchemaDriftDetached()
        {
            try
            {
                await CheckSchemaDrift(await BuilderLspService.GetOrStartAsync());
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    "[RUITK Builder] schema check skipped: " + ex.Message);
            }
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

        /// <summary>POC commitNode(): a model edit rebuilds ONE card and its edges
        /// — it never reloads the tree. Re-parses the changed file into the live
        /// graph node and re-renders in place, so zoom, camera, card selection and
        /// row selection all survive the commit.
        ///
        /// It used to redraw the edges without rebuilding them, so an import added
        /// since the last full load drew its anchor dot and no line.</summary>
        public void RefreshGraph(string filePath)
        {
            if (_graph == null || _container == null)
                return;
            int index = _graph.IndexOf(System.IO.Path.GetFullPath(filePath));
            if (index < 0)
                return;
            try
            {
                BuilderGraphService.PopulateCardDetail(
                    _graph.Nodes[index], ModuleAt?.Invoke(filePath)?.BufferText);
            }
            catch (Exception)
            {
                // UB-114: this used to RETURN, skipping the re-render — so a
                // parse that threw on a half-typed buffer left the card showing
                // its previous content, and the edit only appeared when a LATER
                // edit happened to parse ("the attribute didnt show until i
                // tried to add another attribute"). The node keeps whatever it
                // managed to populate and the canvas always redraws; diagnostics
                // are where a broken buffer is reported, not a frozen card.
            }
            // The card's imports may have changed, and an import is structure:
            // rebuild what this node points at before the canvas redraws.
            BuilderGraphService.RefreshEdgesFor(_graph, index);
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

        private string _selLinePath = "";
        private int _selLineFrom;
        private int _selLineTo;
        private string _selLineLabel = "";

        /// <summary>UB-94: the selected NON-markup thing as a source line range —
        /// a hook chip, an import, a code island, a style entry. Delete removes
        /// exactly these lines.</summary>
        public string SelectedLinePath => _selLinePath;

        public int SelectedLineFrom => _selLineFrom;

        public int SelectedLineTo => _selLineTo;

        public string SelectedLineLabel => _selLineLabel;

        public void ClearRowSelection()
        {
            _selRowPath = "";
            _selRowIdx = -1;
            _selRowLine = 0;
            _selLinePath = "";
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
        /// <summary>Seeds the layout slot for a card about to be created.
        ///
        /// A card is positioned by its TOP-LEFT, so placing it at the cursor put
        /// the whole card down and to the right of where the user pointed. It
        /// lands centred on the cursor instead, high enough that the pointer is
        /// over its title bar - which is what "create it here" looks like.
        ///
        /// (0, 0) means there was no cursor - the library's "+ new" button - so
        /// the card goes to the middle of what the user is currently looking at,
        /// rather than to a fixed world point they may have panned away from.</summary>
        public void PlaceNewCard(string filePath, float x, float y)
        {
            if (_config == null)
                return;
            if (x == 0f && y == 0f)
            {
                float w = _container?.resolvedStyle.width ?? 0f;
                float h = _container?.resolvedStyle.height ?? 0f;
                float zoom = _zoom <= 0f ? 1f : _zoom;
                x = (w * 0.5f - _camX) / zoom;
                y = (h * 0.5f - _camY) / zoom;
            }
            // Centred on BOTH axes. Lifting it by a fixed 18px left most of a card
            // hanging below the cursor, because a card is hundreds of pixels tall
            // and only its title bar was being aimed at. A module about to be
            // created has no card yet, so its height is the one a fresh template
            // draws - a header, a signature, and an empty body.
            float card = BuilderCanvasDrawing.CardWidthFor(LodOf(_zoom));
            _config.SetPosition(filePath, x - card * 0.5f, y - NewCardHeight * 0.5f);
            _config.Save();
        }

        /// <summary>What a just-created card stands about. Its real height is not
        /// knowable until the card is built from a module that does not exist yet,
        /// and being a little out is invisible - being a whole card out is not.</summary>
        private const float NewCardHeight = 200f;

        /// <summary>Puts a new card under the one that spawned it, so a child
        /// appears where the eye already is rather than wherever the pointer
        /// happened to be when the menu opened.</summary>
        public void PlaceNewCardUnder(string filePath, string parentPath)
        {
            if (_config == null || _graph == null)
            {
                PlaceNewCard(filePath, 0f, 0f);
                return;
            }
            int index = _graph.IndexOf(System.IO.Path.GetFullPath(parentPath ?? ""));
            if (index < 0)
            {
                PlaceNewCard(filePath, 0f, 0f);
                return;
            }
            var parent = _graph.Nodes[index];
            float row = parent.Y + BuilderCanvasDrawing.EstimatedCardHeight(parent) + 48f;
            float step = BuilderCanvasDrawing.CardWidthFor(LodOf(_zoom)) + 40f;
            // Children of one parent belong side by side, not on top of each
            // other. The first free slot along the row is taken, so a second and
            // third child land beside the first however the row was arranged.
            float x = parent.X;
            for (int guard = 0; guard < 64 && Occupied(x, row); guard++)
                x += step;
            _config.SetPosition(filePath, x, row);
            _config.Save();
        }

        /// <summary>Whether a card already sits at this slot. Compared loosely -
        /// a card the user has nudged still counts as being in the way.</summary>
        private bool Occupied(float x, float y)
        {
            if (_graph == null)
                return false;
            float w = BuilderCanvasDrawing.CardWidthFor(LodOf(_zoom));
            foreach (var node in _graph.Nodes)
            {
                if (Mathf.Abs(node.Y - y) > 24f)
                    continue;
                if (Mathf.Abs(node.X - x) < w * 0.75f)
                    return true;
            }
            return false;
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
                        OnCardContext = ShowCardMenuFor,
                        OnRowClick = (path, line) => OnRowClick?.Invoke(path, line),
                        OnRowSelect = (path, rowIdx, line) =>
                        {
                            _selRowPath = path;
                            _selRowIdx = rowIdx;
                            _selRowLine = line;
                            _selLinePath = "";
                        },
                        OnLineSelect = (path, from, to, label) =>
                        {
                            _selLinePath = path;
                            _selLineFrom = from;
                            _selLineTo = to;
                            _selLineLabel = label;
                            _selRowPath = "";
                            _selRowIdx = -1;
                        },
                        OnRowContext = (path, line, rowIdx) => OnRowContext?.Invoke(path, line, rowIdx),
                        OnCanvasContext = (wx, wy) => ShowCreateMenu(wx, wy),
                        OnStyleAddEntry = (path, styleName, closeLine) =>
                            OnStyleAddEntry?.Invoke(path, styleName, closeLine),
                        OnStyleRowContext = (path, from, to, label) =>
                            OnStyleRowContext?.Invoke(path, from, to, label),
                        ArmedAddFile = _armedAddFile,
                        ArmedAddStyle = _armedAddStyle,
                        OnAddHook = path => OnAddHook?.Invoke(path),
                        OnAddCode = path => OnAddCode?.Invoke(path),
                        OnCopyImportAlias = text => OnCopyImportAlias?.Invoke(text),
                        OnImportContext = (path, spec) => OnImportContext?.Invoke(path, spec),
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
        /// <summary>Which level of detail a zoom draws at.
        ///
        /// The boundaries sit LOW in each layer's range, so a layer can be zoomed
        /// out a good way before it gives up and drops to the one below - reading
        /// a card at Edit detail and pulling back for context should not cost you
        /// the detail at the first notch. The toolbar presets (0.30 / 0.75 / 1.25)
        /// still land one per layer.</summary>
        public static int LodOf(float zoom) => zoom < 0.32f ? 0 : zoom < 0.80f ? 1 : 2;

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
        private void ShowCardMenuFor(string filePath)
        {
            int index = _graph?.IndexOf(System.IO.Path.GetFullPath(filePath ?? "")) ?? -1;
            if (index < 0)
                return;
            ShowCardMenu(index);
        }

        private void ShowCardMenu(int index)
        {
            if (_graph == null || index < 0 || index >= _graph.Nodes.Count)
                return;
            var node = _graph.Nodes[index];
            // The menu acts on the MODULE, not on the index it happened to have
            // when the menu opened. A pick runs later - the graph can be rebuilt in
            // between - and RequestDeleteCard silently returned false for an index
            // that no longer addressed anything, so delete did nothing at all and
            // said nothing about why.
            string targetPath = node.FilePath;
            var items = new List<BuilderSearchMenu.Item>
            {
                new BuilderSearchMenu.Item
                {
                    Label = "Rename " + node.Title + "…",
                    OnPick = () => OnRenameCard?.Invoke(targetPath),
                },
                new BuilderSearchMenu.Item
                {
                    Label = "Delete " + System.IO.Path.GetFileName(targetPath),
                    OnPick = () => OnDeleteFile?.Invoke(targetPath),
                },
            };
            // A real submenu, opening BESIDE the menu rather than on top of it.
            // The kinds will not stay at four, so they belong behind one row.
            if (node.Kind == BuilderNodeKind.Component)
            {
                var kinds = new List<BuilderSearchMenu.Item>();
                foreach (var kind in CreateKinds)
                {
                    string captured = kind.Key;
                    kinds.Add(new BuilderSearchMenu.Item
                    {
                        Label = kind.Value,
                        Detail = captured == "Component" ? "child" : "beside",
                        OnPick = () => OnCreateUnder?.Invoke(targetPath + "|" + captured),
                    });
                }
                items.Insert(0, BuilderSearchMenu.Separator);
                items.Insert(0, new BuilderSearchMenu.Item
                {
                    Label = "New",
                    Children = kinds,
                });
            }
            BuilderSearchMenu.ShowSimple(node.Title, items);
        }

        /// <summary>The card delete plus its referenced-by guard, in one place so
        /// the keyboard path (UB-74) cannot drift from the menu's rules. The sink
        /// only MARKS the file (UB-88) — nothing reaches disk before Save, which
        /// is what makes this reversible and is why it asks nothing here.</summary>
        public bool RequestDeleteCard(int index)
        {
            if (_graph == null || index < 0 || index >= _graph.Nodes.Count)
            {
                // The keyboard path can outlive the selection it was aiming at.
                // Saying so beats returning false into silence.
                OnToast?.Invoke("Nothing selected to delete.");
                return false;
            }
            var node = _graph.Nodes[index];
            // Being referenced is no longer a refusal. It used to be, which left
            // the user to unpick every import by hand first - and since an import
            // row had no delete of its own, a child component could not be removed
            // at all. The delete now takes its references with it.
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

        /// <summary>Carries the saved layout across a rename, and writes it
        /// immediately: the canvas remounts straight after a rename and reloads the
        /// config from disk, so an in-memory re-keying alone would be thrown away.
        /// This is UserSettings state, not project content, so it is outside the
        /// save-only contract - the same reason dragging a card writes at once.</summary>
        public void RepathLayout(string oldPath, string newPath, bool isFolder)
        {
            if (_config == null)
                return;
            _config.Repath(oldPath, newPath, isFolder);
            _config.Save();
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

        /// <summary>UB-91: a small dim label in the top-left corner read as an
        /// empty canvas. The message is centred, large, and carries a spinner so
        /// a slow tree load is obviously WORK rather than a blank window.</summary>
        private void ShowMessage(string text)
        {
            if (_container == null)
                return;
            EditorRootRendererUtility.Unmount(_container);
            _container.Clear();
            var centre = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position = Position.Absolute,
                    top = 0f, left = 0f, right = 0f, bottom = 0f,
                    alignItems = Align.Center,
                    justifyContent = Justify.Center,
                },
            };
            var spinner = new VisualElement
            {
                style =
                {
                    width = 34f, height = 34f, marginBottom = 14f,
                    borderTopLeftRadius = 17f, borderTopRightRadius = 17f,
                    borderBottomLeftRadius = 17f, borderBottomRightRadius = 17f,
                    borderTopWidth = 3f, borderRightWidth = 3f,
                    borderBottomWidth = 3f, borderLeftWidth = 3f,
                    borderTopColor = BuilderPalette.Accent,
                    borderRightColor = new UnityEngine.Color(1f, 1f, 1f, 0.10f),
                    borderBottomColor = new UnityEngine.Color(1f, 1f, 1f, 0.10f),
                    borderLeftColor = new UnityEngine.Color(1f, 1f, 1f, 0.10f),
                },
            };
            // One arc lit out of four, rotated on a schedule — a border trick, so
            // it costs no texture and no repaint of anything but itself.
            int angle = 0;
            spinner.schedule.Execute(() =>
            {
                angle = (angle + 12) % 360;
                spinner.style.rotate = new StyleRotate(new Rotate(new Angle(angle, AngleUnit.Degree)));
            }).Every(16);
            centre.Add(spinner);
            centre.Add(new Label(text)
            {
                style =
                {
                    fontSize = 20f,
                    color = new UnityEngine.Color(0.72f, 0.72f, 0.78f),
                    unityTextAlign = TextAnchor.MiddleCenter,
                },
            });
            _container.Add(centre);
        }
    }
}
#endif
