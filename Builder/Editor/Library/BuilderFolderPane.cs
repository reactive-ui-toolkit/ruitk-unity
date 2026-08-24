#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// The tree as FOLDERS - a second projection of the same modules the canvas
    /// draws, showing where each one lives rather than what imports it.
    ///
    /// Dragging a module or a whole folder onto another folder moves it. Nothing
    /// reaches disk: the move is a change to the tree like every other edit, so
    /// Save projects it (files move through the AssetDatabase, keeping their
    /// GUIDs) and Abort forgets it. Every specifier the move invalidates is
    /// re-derived before the drop returns, so anything can be put anywhere
    /// without breaking what imports it.
    ///
    /// The hierarchy is DERIVED from the modules' folders, never stored: there is
    /// no folder here that nothing lives in, and no list to keep in step with the
    /// tree. Only the COLLAPSED set is state of its own, because that is a fact
    /// about the viewer rather than about the tree.
    /// </summary>
    internal sealed class BuilderFolderPane
    {
        private sealed class Node
        {
            public string Path;
            public string Name;
            public readonly SortedDictionary<string, Node> Children =
                new SortedDictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            public readonly List<BuilderModule> Modules = new List<BuilderModule>();
        }

        private const float DragThresholdPx = 4f;

        private VisualElement _list;
        private readonly List<(VisualElement Row, string Folder, string Drag, bool IsFolder)> _rows =
            new List<(VisualElement, string, string, bool)>();

        private readonly HashSet<string> _collapsed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private string _dragSubject;
        private bool _dragIsFolder;
        private bool _dragMoved;
        private Vector2 _dragOrigin;
        private string _hoverFolder;

        /// <summary>The tree to show. A function, not a snapshot, so the pane
        /// always draws what the workspace holds right now.</summary>
        public Func<IReadOnlyList<BuilderModule>> Modules;

        /// <summary>(module path, destination folder).</summary>
        public Action<string, string> OnMove;

        /// <summary>(source folder, destination folder). A folder is not something
        /// the tree holds - it is where its modules sit - so moving one means
        /// moving everything under it, which is the window's business.</summary>
        public Action<string, string> OnMoveFolder;

        public Action<string> OnOpen;
        public Action<string> OnToast;

        public void Attach(VisualElement container)
        {
            container.Clear();
            _list = new ScrollView
            {
                style = { flexGrow = 1f, paddingTop = 6f, paddingLeft = 6f, paddingRight = 6f },
            };
            container.Add(_list);
            Rebuild();
        }

        public void Rebuild()
        {
            if (_list == null)
                return;
            _list.Clear();
            _rows.Clear();

            var modules = Modules?.Invoke();
            if (modules == null || modules.Count == 0)
            {
                _list.Add(Hint("No tree open."));
                return;
            }

            _list.Add(Hint(
                "Drag a module or a folder onto another folder to move it. "
                + "Click a folder to fold it. Double-click a module to open it.\n"
                + "Nothing moves on disk until Save."));
            Emit(BuildHierarchy(modules, out _), 0);
        }

        /// <summary>Folders from the modules themselves, rooted at the deepest
        /// folder they all share - which is the tree's own root when they are all
        /// inside it, and their common ancestor when the import closure has pulled
        /// one in from outside.</summary>
        private static Node BuildHierarchy(
            IReadOnlyList<BuilderModule> modules, out string rootPath)
        {
            rootPath = null;
            foreach (var module in modules)
            {
                string folder = Canon(module.Folder);
                rootPath = rootPath == null ? folder : CommonPrefix(rootPath, folder);
            }
            rootPath ??= string.Empty;

            var root = new Node { Path = rootPath, Name = LeafName(rootPath) };
            foreach (var module in modules)
            {
                var node = root;
                string folder = Canon(module.Folder);
                if (folder.Length > rootPath.Length)
                {
                    string rest = folder.Substring(rootPath.Length).Trim('\\', '/');
                    foreach (string segment in rest.Split('\\', '/'))
                    {
                        if (segment.Length == 0)
                            continue;
                        if (!node.Children.TryGetValue(segment, out var child))
                        {
                            child = new Node
                            {
                                Path = Path.Combine(node.Path, segment),
                                Name = segment,
                            };
                            node.Children[segment] = child;
                        }
                        node = child;
                    }
                }
                node.Modules.Add(module);
            }
            return root;
        }

        private void Emit(Node node, int depth)
        {
            bool hasContents = node.Children.Count > 0 || node.Modules.Count > 0;
            bool folded = hasContents && _collapsed.Contains(node.Path);
            _list.Add(FolderRow(node, depth, folded, hasContents));
            if (folded)
                return;

            foreach (var child in node.Children.Values)
                Emit(child, depth + 1);

            node.Modules.Sort((a, b) =>
                string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var module in node.Modules)
                _list.Add(ModuleRow(module, depth + 1));
        }

        private VisualElement FolderRow(Node node, int depth, bool folded, bool hasContents)
        {
            string caret = !hasContents ? "   " : folded ? "▸  " : "▾  ";
            var row = Row(depth, FolderIcon,
                caret + (node.Name.Length > 0 ? node.Name : node.Path),
                BuilderPalette.Text, node.Path, bold: true);
            BuilderCursor.Set(row, UnityEditor.MouseCursor.Link);
            Register(row, node.Path, node.Path, isFolder: true);
            return row;
        }

        private VisualElement ModuleRow(BuilderModule module, int depth)
        {
            var row = Row(depth, ModuleIcon, "   " + Path.GetFileName(module.FilePath),
                TintOf(module.Kind), module.FilePath, bold: false);
            BuilderCursor.Set(row, UnityEditor.MouseCursor.Pan);
            Register(row, Canon(module.Folder), Canon(module.FilePath), isFolder: false);
            return row;
        }

        /// <summary>One row: an icon and a name. The icon is what makes the shape
        /// of the tree readable at a glance - indentation alone reads as a wall of
        /// text, which is what the first cut of this pane was.</summary>
        private static VisualElement Row(
            int depth, Texture2D icon, string text, Color color, string tooltip, bool bold)
        {
            var row = new VisualElement
            {
                tooltip = tooltip,
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    paddingLeft = 6f + depth * 16f,
                    paddingTop = 2f, paddingBottom = 2f,
                    marginBottom = 1f,
                    borderTopLeftRadius = 4f, borderTopRightRadius = 4f,
                    borderBottomLeftRadius = 4f, borderBottomRightRadius = 4f,
                },
            };
            var glyph = new VisualElement
            {
                // A row with no icon still lines its NAME up with the rows that
                // have one, so a missing texture cannot ripple into the layout.
                pickingMode = PickingMode.Ignore,
                style =
                {
                    width = 16f, height = 16f, marginRight = 6f, flexShrink = 0f,
                    backgroundImage = icon == null ? null : Background.FromTexture2D(icon),
                },
            };
            row.Add(glyph);
            row.Add(new Label(text)
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    fontSize = 12f,
                    color = color,
                    unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal,
                    unityFontDefinition = bold
                        ? new StyleFontDefinition(StyleKeyword.Null)
                        : BuilderCanvasDrawing.MonoFontDefinition,
                    unityTextAlign = TextAnchor.MiddleLeft,
                },
            });
            return row;
        }

        /// <summary>Wires one row for hover, drag and drop. Every row is a drop
        /// TARGET - dropping onto a module means its folder, which is what a
        /// person aiming at a file among its neighbours means - and every row is
        /// also a drag SOURCE, a module by its path and a folder by its own.
        ///
        /// A press that never TRAVELS is a click, which folds a folder; one that
        /// does is a drag. Deciding by travel rather than by hit area means the
        /// whole row stays available to both.</summary>
        private void Register(VisualElement row, string folder, string drag, bool isFolder)
        {
            _rows.Add((row, folder, drag, isFolder));

            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0)
                    return;
                if (!isFolder && evt.clickCount >= 2)
                {
                    OnOpen?.Invoke(drag);
                    evt.StopPropagation();
                    return;
                }
                _dragSubject = drag;
                _dragIsFolder = isFolder;
                _dragMoved = false;
                _dragOrigin = evt.position;
                row.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });

            row.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (_dragSubject == null)
                    return;
                if (!_dragMoved
                    && ((Vector2)evt.position - _dragOrigin).magnitude < DragThresholdPx)
                    return;
                _dragMoved = true;
                string target = FolderUnder(evt.position);
                if (target == _hoverFolder)
                    return;
                _hoverFolder = target;
                Repaint();
            });

            row.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (_dragSubject == null)
                    return;
                string subject = _dragSubject;
                bool wasFolder = _dragIsFolder;
                bool moved = _dragMoved;
                string target = FolderUnder(evt.position);
                _dragSubject = null;
                _hoverFolder = null;
                _dragMoved = false;
                row.ReleasePointer(evt.pointerId);
                Repaint();

                if (!moved)
                {
                    if (wasFolder)
                        Toggle(subject);
                    return;
                }
                if (target == null)
                    return;
                if (wasFolder)
                {
                    if (string.Equals(target, subject, StringComparison.OrdinalIgnoreCase)
                        || IsInside(target, subject))
                    {
                        OnToast?.Invoke("Can't move a folder into itself");
                        return;
                    }
                    OnMoveFolder?.Invoke(subject, target);
                    return;
                }
                if (string.Equals(target, Canon(Path.GetDirectoryName(subject)),
                        StringComparison.OrdinalIgnoreCase))
                    return;
                OnMove?.Invoke(subject, target);
            });
        }

        private void Toggle(string folder)
        {
            if (!_collapsed.Remove(folder))
                _collapsed.Add(folder);
            Rebuild();
        }

        /// <summary>The folder a point is over. Hit-tested against the ROWS rather
        /// than by picking, because a row is the full width of the pane and the
        /// answer is the same either way.</summary>
        private string FolderUnder(Vector2 panelPos)
        {
            foreach (var entry in _rows)
                if (entry.Row.worldBound.Contains(panelPos))
                    return entry.Folder;
            return null;
        }

        private void Repaint()
        {
            foreach (var entry in _rows)
            {
                bool lit = _dragSubject != null
                    && _dragMoved
                    && _hoverFolder != null
                    && string.Equals(entry.Folder, _hoverFolder, StringComparison.OrdinalIgnoreCase);
                entry.Row.style.backgroundColor = lit
                    ? new Color(1f, 0.835f, 0.310f, 0.15f)
                    : BuilderPalette.Transparent;
            }
        }

        private static Label Hint(string text) => new Label(text)
        {
            style =
            {
                fontSize = 12f,
                color = BuilderPalette.Dim,
                whiteSpace = WhiteSpace.Normal,
                marginBottom = 12f,
                marginTop = 2f,
                paddingBottom = 8f,
                borderBottomWidth = 1f,
                borderBottomColor = BuilderPalette.Line,
            },
        };

        private static Color TintOf(BuilderNodeKind kind) => kind switch
        {
            BuilderNodeKind.Component => BuilderPalette.ComponentTint,
            BuilderNodeKind.Style => BuilderPalette.StyleTint,
            BuilderNodeKind.Hook => BuilderPalette.HookTint,
            _ => BuilderPalette.UtilTint,
        };

        /// <summary>Unity's own folder icon, so a folder here looks like a folder
        /// in the Project window rather than like a second convention.</summary>
        private static Texture2D FolderIcon =>
            UnityEditor.EditorGUIUtility.IconContent(
                UnityEditor.EditorGUIUtility.isProSkin ? "d_Folder Icon" : "Folder Icon")?.image
                as Texture2D;

        /// <summary>The .uitkx icon the IDE extensions use, so one file type has
        /// one face across the editor and VS Code. Found by SEARCH rather than by
        /// a path, because the package can be embedded or installed and its folder
        /// differs.</summary>
        private static Texture2D ModuleIcon
        {
            get
            {
                if (s_moduleIcon != null)
                    return s_moduleIcon;
                foreach (string guid in UnityEditor.AssetDatabase.FindAssets(
                             "uitkx-file t:Texture2D"))
                {
                    string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                    if (path.IndexOf("/Builder/Editor/Icons/", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    s_moduleIcon = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (s_moduleIcon != null)
                        return s_moduleIcon;
                }
                return null;
            }
        }

        private static Texture2D s_moduleIcon;

        private static string Canon(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        private static string LeafName(string path)
        {
            string name = Path.GetFileName(path?.TrimEnd('\\', '/') ?? string.Empty);
            return name.Length > 0 ? name : path ?? string.Empty;
        }

        /// <summary>Whether <paramref name="path"/> is below
        /// <paramref name="folder"/>, on whole SEGMENTS.</summary>
        private static bool IsInside(string path, string folder)
        {
            string a = Canon(path);
            string b = Canon(folder);
            if (a.Length == 0 || b.Length == 0)
                return false;
            return a.StartsWith(
                b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The deepest folder two paths share, on whole SEGMENTS - a
        /// character-wise prefix would call "Panel" and "PanelExtras" related.</summary>
        private static string CommonPrefix(string a, string b)
        {
            string[] x = a.Split('\\', '/');
            string[] y = b.Split('\\', '/');
            int n = Math.Min(x.Length, y.Length);
            int shared = 0;
            while (shared < n && string.Equals(x[shared], y[shared], StringComparison.OrdinalIgnoreCase))
                shared++;
            return shared == 0
                ? string.Empty
                : string.Join(Path.DirectorySeparatorChar.ToString(), x, 0, shared);
        }
    }
}
#endif
