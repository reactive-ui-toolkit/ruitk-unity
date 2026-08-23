#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Ruitk.EditorSupport.HMR;
using UnityEditor;
using UnityEngine;

namespace Ruitk.Builder
{
    /// <summary>
    /// The builder's document layer for one open tree: it owns the
    /// <see cref="BuilderTree"/>, loads it, and projects it back to disk at Save.
    ///
    /// Owns the save-only disk contract (VE-D2): during editing nothing here
    /// writes. Save walks the tree once and batches every write under a reload
    /// suppressor (none when HMR is active - it already holds the locks), then
    /// lets the normal UitkxChangeWatcher pipeline run for the batch.
    ///
    /// The shape this replaced kept a flat, path-keyed session store plus two
    /// side lists of INTENT - pending deletes and pending folder moves - and
    /// every consumer had to join the data against them. Delete now means the
    /// module is gone from the tree, and Save works out what that implies by
    /// diffing against the paths that were on disk last time. See
    /// Plans~/BUILDER_TREE_MODEL.md.
    /// </summary>
    [Serializable]
    public sealed class BuilderWorkspace
    {
        [SerializeField] private BuilderTree _tree = new BuilderTree();

        public BuilderTree Tree => _tree;

        /// <summary>Adopts a tree recovered from the reload journal. The only way
        /// in besides a load, and it exists because a tree that has never been
        /// written is otherwise gone the moment the process is.</summary>
        public void AdoptTree(BuilderTree tree)
        {
            if (tree == null)
                return;
            _tree = tree;
            Changed?.Invoke();
        }

        public event Action Changed;

        public IReadOnlyList<BuilderModule> Modules => _tree.Modules;

        /// <summary>Looks a module up by path. A null or unknown path is NOT
        /// FOUND, never an error - an empty tree has no focus, and asking about
        /// nothing should answer nothing.</summary>
        public BuilderModule TryGet(string filePath) => _tree.ByPath(filePath);

        public BuilderModule ById(string id) => _tree.ById(id);

        public bool HasUnsavedChanges => _tree.HasUnsavedWork();

        /// <summary>Whether a module can live at this path. THE one rule, so the
        /// name prompt and the creation itself cannot disagree. Nothing needs an
        /// exception for "deleted but not saved yet": a deleted module is not in
        /// the tree, so its name is free the instant it goes.</summary>
        public bool IsPathAvailable(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;
            return _tree.ByPath(filePath) == null && !File.Exists(filePath);
        }

        // ── Loading ──────────────────────────────────────────────────────────

        /// <summary>Reads the whole tree that <paramref name="focusFullPath"/>
        /// belongs to, once. Everything after this reads from memory: what the
        /// canvas shows no longer depends on which files happen to be open, and
        /// no mount touches the filesystem again.</summary>
        public void LoadTree(string focusFullPath)
        {
            var modules = new List<BuilderModule>();
            var projection = new List<string>();
            // Ordinal comparison would let the same file in twice: a specifier
            // spells a path in whatever case the user typed, and the filesystem
            // does not care.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string root = ResolveTreeRoot(focusFullPath);
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(root, "*.uitkx", SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    files = Array.Empty<string>();
                }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    string full = BuilderTree.Canon(file);
                    string raw;
                    try
                    {
                        raw = File.ReadAllText(full);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (!seen.Add(full))
                        continue;
                    modules.Add(BuilderModule.FromFile(full, raw, IsReadOnlyLocation(full)));
                    projection.Add(full);
                }
            }

            // Imports that leave the root pull their targets in, transitively.
            // A tree is what the focus can REACH; the folder scan is only its
            // seed. A shared module one folder over was outside the scan, so it
            // was missing from the model entirely and the import that named it
            // resolved to nothing - an anchor dot with no line, on a module that
            // was sitting on disk the whole time.
            for (int i = 0; i < modules.Count; i++)
            {
                foreach (string target in ImportTargetsOf(modules[i]))
                {
                    if (seen.Contains(target) || !File.Exists(target))
                        continue;
                    string raw;
                    try
                    {
                        raw = File.ReadAllText(target);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    seen.Add(target);
                    modules.Add(BuilderModule.FromFile(target, raw, IsReadOnlyLocation(target)));
                    projection.Add(target);
                }
            }

            _tree.Reset(modules, projection);
            Changed?.Invoke();
        }

        /// <summary>The absolute paths a module imports. Parsing is the only way
        /// to know: an import is text, and the module holding it may never have
        /// been written. A module that does not parse contributes nothing, which
        /// is the right answer for a file that is half-typed.</summary>
        private static IEnumerable<string> ImportTargetsOf(BuilderModule module)
        {
            Ruitk.Language.Parser.ParseResult parsed;
            try
            {
                parsed = BuilderLanguage.Parse(module.BufferText, module.FilePath);
            }
            catch (Exception)
            {
                yield break;
            }
            foreach (var import in parsed.Directives.Imports)
            {
                string spec = import.Specifier ?? string.Empty;
                if (spec.Length == 0 || spec.StartsWith("@", StringComparison.Ordinal))
                    continue;
                string mapped = BuilderGraphService.MapSpecifier(module.FilePath, spec);
                if (!string.IsNullOrEmpty(mapped))
                    yield return BuilderTree.Canon(mapped);
            }
        }

        /// <summary>The outermost folder of the tree the focus belongs to,
        /// following the house layout: a component owns the folder it is named
        /// after, and children nest under a "components" folder inside it. The
        /// walk stops at the first ancestor that is neither, which is the tree's
        /// own root.</summary>
        public static string ResolveTreeRoot(string focusFullPath)
        {
            if (string.IsNullOrEmpty(focusFullPath))
                return null;
            string dir;
            try
            {
                dir = Path.GetDirectoryName(BuilderTree.Canon(focusFullPath));
            }
            catch (Exception)
            {
                return null;
            }
            if (string.IsNullOrEmpty(dir))
                return null;

            while (true)
            {
                DirectoryInfo parent;
                try
                {
                    parent = Directory.GetParent(dir);
                }
                catch (Exception)
                {
                    break;
                }
                if (parent == null)
                    break;

                // "components" is a nesting level, never a tree root, so step
                // over it to the component that owns it.
                if (string.Equals(parent.Name, "components", StringComparison.OrdinalIgnoreCase))
                {
                    if (parent.Parent == null)
                        break;
                    dir = parent.Parent.FullName;
                    continue;
                }
                // A folder that owns a module named after itself is still inside
                // the tree, so keep climbing.
                if (File.Exists(Path.Combine(parent.FullName, parent.Name + ".uitkx")))
                {
                    dir = parent.FullName;
                    continue;
                }
                break;
            }
            return dir;
        }

        /// <summary>Brings a single file into the tree - opening a module from
        /// outside the loaded tree, or one the loader could not see. A file that
        /// is already present is returned as-is.</summary>
        public BuilderModule Open(string filePath)
        {
            string full = BuilderTree.Canon(filePath);
            var existing = _tree.ByPath(full);
            if (existing != null)
            {
                // Modules survive domain reloads by design (unsaved buffers),
                // which also means they survive EXTERNAL file changes. A clean
                // module re-checks disk or it serves stale text forever.
                if (!existing.IsDirty && File.Exists(full)
                    && existing.AdoptDiskText(File.ReadAllText(full)))
                    Changed?.Invoke();
                return existing;
            }

            bool onDisk = File.Exists(full);
            var module = onDisk
                ? BuilderModule.FromFile(full, File.ReadAllText(full), IsReadOnlyLocation(full))
                : BuilderModule.Fresh(
                    Path.GetDirectoryName(full) ?? string.Empty,
                    NameOf(full), KindOf(full), string.Empty);
            _tree.Add(module);
            Changed?.Invoke();
            return module;
        }

        /// <summary>External-change sweep (asset imports): clean modules adopt
        /// the new disk text; dirty ones keep the user's unsaved buffer. Returns
        /// the paths whose text changed.</summary>
        public List<string> ReloadCleanFromDisk(IEnumerable<string> fullPaths)
        {
            var changed = new List<string>();
            if (fullPaths == null)
                return changed;
            foreach (string path in fullPaths)
            {
                var module = _tree.ByPath(path);
                if (module == null || module.IsDirty || module.IsReadOnly)
                    continue;
                if (!File.Exists(path))
                    continue;
                if (module.AdoptDiskText(File.ReadAllText(path)))
                    changed.Add(BuilderTree.Canon(path));
            }
            if (changed.Count > 0)
                Changed?.Invoke();
            return changed;
        }

        // ── Manipulation ─────────────────────────────────────────────────────

        public BuilderModule CreateNew(
            string filePath, string initialBuffer, bool needsLocation = false)
        {
            string full = BuilderTree.Canon(filePath);
            if (!IsPathAvailable(full))
                return null;
            var module = BuilderModule.Fresh(
                Path.GetDirectoryName(full) ?? string.Empty,
                NameOf(full), KindOf(full), initialBuffer);
            module.NeedsLocation = needsLocation;
            _tree.Add(module);
            Changed?.Invoke();
            return module;
        }

        /// <summary>Every module still waiting to be told where it lives. THE
        /// question Save asks, answered by a flag on the module rather than by
        /// testing its path against the provisional root: the prefix test is
        /// exactly what silently skipped the relocation and wrote a module at its
        /// provisional path (UB-119).</summary>
        public List<BuilderModule> UnlocatedModules()
        {
            var pending = new List<BuilderModule>();
            foreach (var module in _tree.Modules)
                if (module.NeedsLocation)
                    pending.Add(module);
            return pending;
        }

        /// <summary>Gives a module its real home. The ONLY door from provisional
        /// to writable - Save refuses every module that still needs a location,
        /// whoever calls and however the paths compare - so clearing the flag and
        /// setting the folder happen in one place and cannot drift apart.</summary>
        public bool PlaceAt(BuilderModule module, string newFolder)
        {
            if (module == null || _tree.ByPath(module.FilePath) != module)
                return false;
            _tree.MoveTo(module, newFolder, module.Name);
            module.NeedsLocation = false;
            Changed?.Invoke();
            return true;
        }

        /// <summary>Deletes a module: it leaves the tree. Save works out that its
        /// file is orphaned by diffing against the last projection, so there is
        /// no mark to set, nothing to filter, and the name is free at once.</summary>
        public bool Delete(string filePath)
        {
            var module = _tree.ByPath(filePath);
            if (module == null || module.IsReadOnly)
                return false;
            _tree.Remove(module);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Puts a removed module back, for undo. It keeps its identity
        /// and its DiskPath, so a module that had a file still owns that file.</summary>
        public BuilderModule Restore(BuilderModule module)
        {
            if (module == null || _tree.ByPath(module.FilePath) != null)
                return null;
            _tree.Add(module);
            Changed?.Invoke();
            return module;
        }

        /// <summary>Moves a module, carrying its folder's contents when it owns
        /// the folder. Nothing happens on disk - Save sees DiskPath disagree with
        /// the derived path and projects the move.</summary>
        public bool MoveTo(string filePath, string newFolder, string newName)
        {
            var module = _tree.ByPath(filePath);
            if (module == null || module.IsReadOnly)
                return false;
            _tree.MoveTo(module, newFolder, newName);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Moves a module to a target PATH, splitting the folder, name
        /// and kind out of it. What the ledger replays: an entry records where a
        /// module went, and walking it back is the same operation the other way.</summary>
        public bool MoveToPath(string fromPath, string toPath)
        {
            string to = BuilderTree.Canon(toPath);
            if (string.IsNullOrEmpty(to))
                return false;
            return MoveTo(fromPath, Path.GetDirectoryName(to) ?? string.Empty, NameOf(to));
        }

        public void ApplyEdit(string filePath, string newBufferLf)
        {
            var module = _tree.ByPath(filePath);
            if (module == null)
                throw new InvalidOperationException(
                    $"no module open for '{filePath}' - open it before editing.");
            module.ApplyEdit(newBufferLf);
            Changed?.Invoke();
        }

        public void Close(string filePath)
        {
            if (_tree.RemoveByPath(filePath))
                Changed?.Invoke();
        }

        // ── Projection ───────────────────────────────────────────────────────

        /// <summary>
        /// Writes the tree to disk. A pure diff, so running it twice is a no-op:
        /// nothing is dirty, no DiskPath disagrees with its derived path, and
        /// nothing is orphaned.
        ///
        /// Moves go through AssetDatabase.MoveAsset rather than write-then-trash,
        /// so a renamed module keeps its GUID and its meta file. Deletions are
        /// trashed rather than erased, so even a confirmed, saved removal stays
        /// recoverable outside the builder.
        /// </summary>
        public int SaveAll()
        {
            if (!_tree.HasUnsavedWork())
                return 0;

            int written = 0;
            int removed = 0;
            bool createdAssets = false;
            bool hmrActive = UitkxHmrController.IsActive;
            AssemblyReloadSuppressor suppressor = null;
            try
            {
                if (!hmrActive)
                {
                    suppressor = new AssemblyReloadSuppressor();
                    suppressor.Lock();
                }

                // Orphans FIRST: a module that moved out of a folder and one that
                // was deleted from it can both be pending, and clearing the dead
                // paths before writing keeps a stale file from shadowing a new one
                // at the same location.
                foreach (string orphan in _tree.OrphanedPaths())
                {
                    Retire(orphan);
                    removed++;
                }

                foreach (var module in _tree.Modules)
                {
                    if (module.IsReadOnly || module.NeedsLocation)
                        continue;
                    string target = module.FilePath;
                    if (string.IsNullOrEmpty(target))
                        continue;

                    if (module.HasMoved)
                    {
                        EnsureDirectory(target);
                        MoveOnDisk(module.DiskPath, target);
                        module.DiskPath = target;
                        createdAssets = true;
                    }

                    if (!module.IsOnDisk || module.IsDirty)
                    {
                        EnsureDirectory(target);
                        createdAssets |= !module.IsOnDisk;
                        File.WriteAllText(target, ToDiskText(module));
                        written++;
                    }
                    module.MarkProjected(target);
                }
            }
            finally
            {
                suppressor?.Dispose();
            }

            var projection = new List<string>();
            foreach (var module in _tree.Modules)
                if (module.IsOnDisk)
                    projection.Add(module.DiskPath);
            _tree.SetProjection(projection);

            // Outside the reload suppressor: importing is what makes a new file
            // an asset with a .meta, and it must not run while the lock is held.
            if (createdAssets || removed > 0)
                AssetDatabase.Refresh();
            Changed?.Invoke();
            return written + removed;
        }

        /// <summary>Discards every pending change by re-reading the tree. Abort
        /// IS Load re-run, which is why it needs no bookkeeping of its own.</summary>
        public int AbortAll()
        {
            if (!_tree.HasUnsavedWork())
                return 0;
            int reverted = _tree.OrphanedPaths().Count;
            string anchor = null;
            foreach (var module in _tree.Modules)
            {
                if (module.IsDirty || !module.IsOnDisk || module.HasMoved)
                    reverted++;
                if (anchor == null && module.IsOnDisk)
                    anchor = module.DiskPath;
            }

            if (anchor == null)
            {
                // Nothing was ever written, so there is nothing to go back to.
                _tree.Reset(Array.Empty<BuilderModule>(), Array.Empty<string>());
                Changed?.Invoke();
            }
            else
            {
                LoadTree(anchor);
            }
            return reverted;
        }

        private static string ToDiskText(BuilderModule module) =>
            module.UsedCrlf ? module.BufferText.Replace("\n", "\r\n") : module.BufferText;

        private static void EnsureDirectory(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>Moves a file, through the AssetDatabase where there is one so
        /// the GUID and the meta file follow it.</summary>
        private static void MoveOnDisk(string from, string to)
        {
            if (!File.Exists(from))
                return;
            string fromAsset = ToAssetPath(from);
            string toAsset = ToAssetPath(to);
            if (fromAsset != null && toAsset != null)
            {
                string error = AssetDatabase.MoveAsset(fromAsset, toAsset);
                if (string.IsNullOrEmpty(error))
                    return;
                throw new IOException(
                    "could not move " + fromAsset + " to " + toAsset + ": " + error);
            }
            File.Move(from, to);
        }

        /// <summary>Takes one file out of the project. Trash rather than erase,
        /// so even a confirmed, saved removal stays recoverable; a path outside
        /// Assets/Packages has no asset to trash.</summary>
        private static void Retire(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            string assetPath = ToAssetPath(path);
            if (assetPath != null)
                AssetDatabase.MoveAssetToTrash(assetPath);
            else if (File.Exists(path))
                File.Delete(path);
        }

        // ── Policy ───────────────────────────────────────────────────────────

        /// <summary>
        /// Immutable-package detection done RIGHT (plan §4.3c): PackageInfo source,
        /// never the asmdef walk — its null return also means "default assembly",
        /// which would mark every Assets/ file read-only.
        /// </summary>
        public static bool IsReadOnlyLocation(string filePath)
        {
            string full = BuilderTree.Canon(filePath).Replace('\\', '/');
            string assetsRoot = Application.dataPath.Replace('\\', '/');
            if (full.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            foreach (var pkg in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (string.IsNullOrEmpty(pkg.resolvedPath))
                    continue;
                string pkgRoot = BuilderTree.Canon(pkg.resolvedPath).Replace('\\', '/');
                if (full.StartsWith(pkgRoot + "/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, pkgRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return pkg.source != UnityEditor.PackageManager.PackageSource.Embedded
                        && pkg.source != UnityEditor.PackageManager.PackageSource.Local;
                }
            }
            return true;
        }

        private static string ToAssetPath(string fullPath)
        {
            string normalized = BuilderTree.Canon(fullPath).Replace('\\', '/');
            int assets = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (assets >= 0)
                return normalized.Substring(assets + 1);
            int packages = normalized.IndexOf("/Packages/", StringComparison.OrdinalIgnoreCase);
            if (packages >= 0)
                return normalized.Substring(packages + 1);
            return null;
        }

        private static string NameOf(string fullPath)
        {
            BuilderModule.SplitFileName(Path.GetFileName(fullPath), out string name, out _);
            return name;
        }

        private static BuilderNodeKind KindOf(string fullPath)
        {
            BuilderModule.SplitFileName(Path.GetFileName(fullPath), out _, out var kind);
            return kind;
        }
    }
}
#endif
