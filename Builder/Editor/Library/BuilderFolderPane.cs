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
    /// Dragging a module onto a folder moves it. Nothing reaches disk: the move
    /// is a change to the tree like every other edit, so Save projects it (files
    /// move through the AssetDatabase, keeping their GUIDs) and Abort forgets it.
    /// Every specifier the move invalidates is re-derived before the drop
    /// returns, so a module can be put anywhere without breaking what imports it.
    ///
    /// The hierarchy is DERIVED from the modules' folders, never stored: there is
    /// no folder here that nothing lives in, and no list to keep in step with the
    /// tree.
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

        private VisualElement _container;
        private VisualElement _list;
        private readonly List<(VisualElement Row, string Folder, string ModulePath)> _rows =
            new List<(VisualElement, string, string)>();

        private string _dragPath;
        private string _hoverFolder;

        /// <summary>The tree to show. A function, not a snapshot, so the pane
        /// always draws what the workspace holds right now.</summary>
        public Func<IReadOnlyList<BuilderModule>> Modules;

        /// <summary>(module path, destination folder). The window turns it into a
        /// tree move, which is what records it and re-derives the specifiers.</summary>
        public Action<string, string> OnMove;

        public Action<string> OnOpen;
        public Action<string> OnToast;

        public void Attach(VisualElement container)
        {
            _container = container;
            _container.Clear();
            _list = new ScrollView
            {
                style = { flexGrow = 1f, paddingTop = 6f, paddingLeft = 6f, paddingRight = 6f },
            };
            _container.Add(_list);
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

            var root = BuildHierarchy(modules, out string rootPath);
            _list.Add(Hint(
                "Drag a module onto a folder to move it. Nothing moves on disk until Save."));
            Emit(root, 0, rootPath);
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

        private void Emit(Node node, int depth, string rootPath)
        {
            _list.Add(FolderRow(node, depth));
            foreach (var child in node.Children.Values)
                Emit(child, depth + 1, rootPath);

            node.Modules.Sort((a, b) =>
                string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var module in node.Modules)
                _list.Add(ModuleRow(module, depth + 1));
        }

        private VisualElement FolderRow(Node node, int depth)
        {
            var row = new Label(node.Name.Length > 0 ? node.Name : node.Path)
            {
                tooltip = node.Path,
                style =
                {
                    fontSize = 12f,
                    color = BuilderPalette.Text,
                    unityFontStyleAndWeight = FontStyle.Bold,
                    paddingLeft = 6f + depth * 14f,
                    paddingTop = 3f, paddingBottom = 3f,
                    marginBottom = 1f,
                    borderTopLeftRadius = 4f, borderTopRightRadius = 4f,
                    borderBottomLeftRadius = 4f, borderBottomRightRadius = 4f,
                },
            };
            Register(row, node.Path, null);
            return row;
        }

        private VisualElement ModuleRow(BuilderModule module, int depth)
        {
            var row = new Label(Path.GetFileName(module.FilePath))
            {
                tooltip = module.FilePath,
                style =
                {
                    fontSize = 12f,
                    color = TintOf(module.Kind),
                    unityFontDefinition = BuilderCanvasDrawing.MonoFontDefinition,
                    paddingLeft = 6f + depth * 14f,
                    paddingTop = 2f, paddingBottom = 2f,
                    marginBottom = 1f,
                    borderTopLeftRadius = 4f, borderTopRightRadius = 4f,
                    borderBottomLeftRadius = 4f, borderBottomRightRadius = 4f,
                },
            };
            BuilderCursor.Set(row, UnityEditor.MouseCursor.Pan);
            Register(row, Canon(module.Folder), Canon(module.FilePath));
            return row;
        }

        /// <summary>Wires one row for hover, drag and drop. A row is a drop TARGET
        /// whether it is a folder or a module - dropping onto a module means its
        /// folder, which is what a person aiming at a file next to its neighbours
        /// means.</summary>
        private void Register(VisualElement row, string folder, string modulePath)
        {
            _rows.Add((row, folder, modulePath));

            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || modulePath == null)
                    return;
                if (evt.clickCount >= 2)
                {
                    OnOpen?.Invoke(modulePath);
                    evt.StopPropagation();
                    return;
                }
                _dragPath = modulePath;
                row.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });

            row.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (_dragPath == null)
                    return;
                string target = FolderUnder(evt.position);
                if (target == _hoverFolder)
                    return;
                _hoverFolder = target;
                Repaint();
            });

            row.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (_dragPath == null)
                    return;
                string dragged = _dragPath;
                string target = FolderUnder(evt.position);
                _dragPath = null;
                _hoverFolder = null;
                row.ReleasePointer(evt.pointerId);
                Repaint();
                if (target == null || dragged == null)
                    return;
                if (string.Equals(target, Canon(Path.GetDirectoryName(dragged)),
                        StringComparison.OrdinalIgnoreCase))
                    return;
                OnMove?.Invoke(dragged, target);
            });
        }

        /// <summary>The folder a point is over. Hit-tested against the ROWS rather
        /// than by picking, because a row's Label is the whole width of the pane
        /// and the answer is the same either way.</summary>
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
                bool lit = _dragPath != null
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
                fontSize = 10.5f,
                color = BuilderPalette.Dim,
                whiteSpace = WhiteSpace.Normal,
                marginBottom = 8f,
            },
        };

        private static Color TintOf(BuilderNodeKind kind) => kind switch
        {
            BuilderNodeKind.Component => BuilderPalette.ComponentTint,
            BuilderNodeKind.Style => BuilderPalette.StyleTint,
            BuilderNodeKind.Hook => BuilderPalette.HookTint,
            _ => BuilderPalette.UtilTint,
        };

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
            return shared == 0 ? string.Empty : string.Join(Path.DirectorySeparatorChar.ToString(), x, 0, shared);
        }
    }
}
#endif
