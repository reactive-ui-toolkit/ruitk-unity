#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Ruitk.Builder
{
    /// <summary>
    /// The builder's data structure: every module of one tree, held in memory,
    /// with disk as a projection computed at Save.
    ///
    /// Load reads the tree once. Every manipulation - create, delete, rename,
    /// move, edit - happens here. Rendering reads from here. Save walks it and
    /// writes. Nothing else consults the filesystem while editing, so what the
    /// canvas shows no longer depends on which files happen to be open.
    ///
    /// DELETION IS ABSENCE. A module is removed from <see cref="Modules"/> and
    /// that is the whole of it. <see cref="LastProjection"/> remembers what was
    /// on disk, so Save can tell that a file is now orphaned and trash it. The
    /// shape this replaces kept intent in lists BESIDE the data - pending
    /// deletes, pending folder moves - and every consumer had to join the two;
    /// the ones that forgot are catalogued in Plans~/BUILDER_TREE_MODEL.md.
    ///
    /// Serialization: Unity cannot round-trip a dictionary, so the indexes are
    /// NonSerialized and rebuilt from the list. It also turns a null string into
    /// "" - which is why <see cref="BuilderModule.IsOnDisk"/> exists rather than
    /// a null test at each call site.
    /// </summary>
    [Serializable]
    public sealed class BuilderTree : ISerializationCallbackReceiver
    {
        [SerializeField] private List<BuilderModule> _modules = new List<BuilderModule>();

        /// <summary>The paths that were on disk as of the last Load or Save. The
        /// one piece of state that makes Save a diff rather than a set of
        /// remembered intents: anything in here that no module claims any more
        /// has been deleted, whatever route it left the tree by.</summary>
        [SerializeField] private List<string> _lastProjection = new List<string>();

        [NonSerialized] private Dictionary<string, BuilderModule> _byId;
        [NonSerialized] private Dictionary<string, BuilderModule> _byPath;

        public IReadOnlyList<BuilderModule> Modules => _modules;

        public IReadOnlyList<string> LastProjection => _lastProjection;

        // ── Lookup ───────────────────────────────────────────────────────────

        public BuilderModule ById(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            EnsureIndexes();
            return _byId.TryGetValue(id, out var module) ? module : null;
        }

        /// <summary>Lookup by path. A null or unknown path is NOT FOUND, never an
        /// error: an empty tree has no focus, and asking about nothing should
        /// answer nothing.</summary>
        public BuilderModule ByPath(string path)
        {
            string key = Canon(path);
            if (key.Length == 0)
                return null;
            EnsureIndexes();
            return _byPath.TryGetValue(key, out var module) ? module : null;
        }

        public bool Contains(string path) => ByPath(path) != null;

        // ── Mutation ─────────────────────────────────────────────────────────

        public BuilderModule Add(BuilderModule module)
        {
            if (module == null)
                return null;
            if (string.IsNullOrEmpty(module.Id))
                module.Id = BuilderModule.NewId();
            module.BufferText ??= string.Empty;
            module.ProjectedText ??= module.IsOnDisk ? module.BufferText : string.Empty;
            _modules.Add(module);
            Reindex();
            return module;
        }

        /// <summary>Removes a module. THIS is what deleting means - there is no
        /// mark, so nothing downstream has to filter and nothing can disagree
        /// about whether the module is present. Save notices the orphaned file
        /// through <see cref="LastProjection"/>.</summary>
        public bool Remove(BuilderModule module)
        {
            if (module == null || !_modules.Remove(module))
                return false;
            Reindex();
            return true;
        }

        public bool RemoveByPath(string path) => Remove(ByPath(path));

        /// <summary>Re-files a module and, when it owns its folder, everything
        /// inside that folder with it. The subtree moves because the FOLDER
        /// moves: every module keeps its position relative to the folder, so
        /// every relative import inside it stays correct without being touched.
        ///
        /// "Inside" means IN the folder as well as under it. Only the nested
        /// case was carried, so a component's own companions - the style and
        /// hook modules sitting beside it, which are the whole reason the folder
        /// exists - were left behind at a path their folder had vacated.</summary>
        public void MoveTo(BuilderModule module, string newFolder, string newName)
        {
            if (module == null)
                return;
            string oldFolder = module.Folder;
            bool ownsFolder = module.OwnsFolder;

            module.Folder = newFolder;
            module.Name = newName;

            if (ownsFolder && !string.IsNullOrEmpty(oldFolder))
            {
                string from = Canon(oldFolder);
                string prefix = from + System.IO.Path.DirectorySeparatorChar;
                foreach (var other in _modules)
                {
                    if (ReferenceEquals(other, module) || string.IsNullOrEmpty(other.Folder))
                        continue;
                    string folder = Canon(other.Folder);
                    if (string.Equals(folder, from, StringComparison.OrdinalIgnoreCase))
                    {
                        other.Folder = newFolder;
                        continue;
                    }
                    if (!folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    other.Folder = System.IO.Path.Combine(
                        newFolder, folder.Substring(prefix.Length));
                }
            }
            Reindex();
        }

        /// <summary>Replaces the whole contents - used by Load and by Abort,
        /// which is Load re-run. <paramref name="projection"/> is what was on
        /// disk at that moment.</summary>
        public void Reset(IEnumerable<BuilderModule> modules, IEnumerable<string> projection)
        {
            _modules.Clear();
            if (modules != null)
            {
                foreach (var module in modules)
                {
                    if (module == null)
                        continue;
                    if (string.IsNullOrEmpty(module.Id))
                        module.Id = BuilderModule.NewId();
                    _modules.Add(module);
                }
            }
            _lastProjection.Clear();
            if (projection != null)
            {
                foreach (string path in projection)
                {
                    string key = Canon(path);
                    if (key.Length > 0 && !_lastProjection.Contains(key))
                        _lastProjection.Add(key);
                }
            }
            Reindex();
        }

        /// <summary>Records the projection Save just performed: every path a
        /// module now occupies. What it no longer contains is what Save deleted.</summary>
        public void SetProjection(IEnumerable<string> paths)
        {
            _lastProjection.Clear();
            if (paths == null)
                return;
            foreach (string path in paths)
            {
                string key = Canon(path);
                if (key.Length > 0 && !_lastProjection.Contains(key))
                    _lastProjection.Add(key);
            }
        }

        /// <summary>Paths that WERE on disk and no longer belong to any module -
        /// the files Save has to remove. Computed, never accumulated, so it
        /// cannot drift from the tree.</summary>
        public List<string> OrphanedPaths()
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in _modules)
                if (module.IsOnDisk)
                    claimed.Add(Canon(module.DiskPath));

            var orphans = new List<string>();
            foreach (string path in _lastProjection)
                if (!claimed.Contains(path))
                    orphans.Add(path);
            return orphans;
        }

        public bool HasUnsavedWork()
        {
            if (OrphanedPaths().Count > 0)
                return true;
            foreach (var module in _modules)
                if (!module.IsReadOnly && (module.IsDirty || !module.IsOnDisk || module.HasMoved))
                    return true;
            return false;
        }

        // ── Serialization ────────────────────────────────────────────────────

        public void OnBeforeSerialize()
        {
        }

        /// <summary>Rebuilds the indexes. Runs on whatever thread Unity chooses,
        /// so it touches MANAGED DATA ONLY - no Unity APIs, and in particular no
        /// re-parsing. Anything derived is rebuilt lazily, on demand, by whoever
        /// needs it.</summary>
        public void OnAfterDeserialize()
        {
            _byId = null;
            _byPath = null;
        }

        private void EnsureIndexes()
        {
            if (_byId != null && _byPath != null)
                return;
            Reindex();
        }

        private void Reindex()
        {
            _byId = new Dictionary<string, BuilderModule>(StringComparer.Ordinal);
            _byPath = new Dictionary<string, BuilderModule>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in _modules)
            {
                if (module == null)
                    continue;
                if (string.IsNullOrEmpty(module.Id))
                    module.Id = BuilderModule.NewId();
                _byId[module.Id] = module;
                string path = Canon(module.FilePath);
                if (path.Length > 0)
                    _byPath[path] = module;
            }
        }

        // ── Self-check ───────────────────────────────────────────────────────

        /// <summary>Asserts the invariants a domain reload could break, and says
        /// so loudly when one has. The point is not the check: it is that a
        /// broken round-trip announces itself WHERE IT HAPPENS, instead of
        /// surfacing later as an inexplicable bug in something that trusted it.
        /// Returns the problems found; empty means healthy.</summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            EnsureIndexes();

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in _modules)
            {
                if (module == null)
                {
                    problems.Add("a null module survived serialization");
                    continue;
                }
                if (string.IsNullOrEmpty(module.Id))
                    problems.Add("module at " + module.FilePath + " lost its id");
                else if (!seenIds.Add(module.Id))
                    problems.Add("duplicate module id " + module.Id);

                string path = Canon(module.FilePath);
                if (path.Length == 0)
                    problems.Add("module " + module.Id + " has no derivable path");
                else if (!seenPaths.Add(path))
                    problems.Add("two modules claim " + path);

                if (module.BufferText == null)
                    problems.Add("module at " + module.FilePath + " lost its text");
            }

            if (_byId.Count != seenIds.Count || _byPath.Count != seenPaths.Count)
                problems.Add("the indexes disagree with the module list");

            return problems;
        }

        /// <summary>The outermost folder of the tree the focus belongs to,
        /// following the house layout: a component owns the folder it is named
        /// after, and children nest under a "components" folder inside it. The
        /// walk stops at the first ancestor that is neither, which is the tree's
        /// own root.</summary>
        /// <summary>The tree root decided from the MODULES rather than from disk.
        ///
        /// The filesystem answer is wrong for a tree that has never been saved:
        /// nothing exists yet, so the walk stops at the first folder and the root
        /// is wherever the FOCUS happens to be. Creating a nested component then
        /// moved the focus deeper, moved the root with it, and re-keyed the whole
        /// saved layout - which is the canvas rearranging itself for no reason the
        /// user could see (UB-189).</summary>
        public static string ResolveRoot(IReadOnlyList<BuilderModule> modules, string focusFullPath)
        {
            return ResolveRootCore(focusFullPath, folder =>
            {
                if (modules == null)
                    return false;
                string wanted = Canon(folder);
                foreach (var module in modules)
                    if (module != null && module.OwnsFolder
                        && string.Equals(Canon(module.Folder), wanted,
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            });
        }

        /// <summary>The same walk, answered from DISK. Used by the loader, which
        /// runs before there is a tree to ask.</summary>
        public static string ResolveRoot(string focusFullPath)
        {
            return ResolveRootCore(focusFullPath, folder => System.IO.File.Exists(
                System.IO.Path.Combine(folder, System.IO.Path.GetFileName(folder) + ".uitkx")));
        }

        /// <summary>Climbs to the outermost folder of the tree the focus belongs
        /// to. <paramref name="ownsAModule"/> answers "is there a component named
        /// after this folder" - the one question the walk asks, and the only thing
        /// that differs between asking the tree and asking the disk.</summary>
        private static string ResolveRootCore(
            string focusFullPath, Func<string, bool> ownsAModule)
        {
            if (string.IsNullOrEmpty(focusFullPath))
                return null;
            string dir;
            try
            {
                dir = System.IO.Path.GetDirectoryName(Canon(focusFullPath));
            }
            catch (Exception)
            {
                return null;
            }
            if (string.IsNullOrEmpty(dir))
                return null;

            while (true)
            {
                System.IO.DirectoryInfo parent;
                try
                {
                    parent = System.IO.Directory.GetParent(dir);
                }
                catch (Exception)
                {
                    break;
                }
                if (parent == null)
                    break;

                // "components" is a nesting level ONLY where it is the house
                // layout - a folder named "components" INSIDE a component that
                // owns it. The name alone is not enough: this package ships its
                // samples under Samples/Components/, which held 26 unrelated demo
                // trees, and matching on the name climbed straight past the real
                // root and loaded all of them into one canvas.
                if (string.Equals(parent.Name, "components", StringComparison.OrdinalIgnoreCase)
                    && parent.Parent != null
                    && ownsAModule(parent.Parent.FullName))
                {
                    dir = parent.Parent.FullName;
                    continue;
                }
                // A folder that owns a module named after itself is still inside
                // the tree, so keep climbing.
                if (ownsAModule(parent.FullName))
                {
                    dir = parent.FullName;
                    continue;
                }
                break;
            }
            return dir;
        }

        internal static string Canon(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            try
            {
                return System.IO.Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }
    }
}
#endif
