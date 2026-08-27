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
        /// <summary>Whether a path is free for a module to occupy.
        ///
        /// The TREE decides, not the disk. Disk is a projection of the tree and a
        /// stale one until Save, so a file sitting at this path is only an
        /// obstacle when the session cannot account for it. Two things it can
        /// account for: a module that CAME FROM the path and has since moved
        /// (Save vacates it), and an orphan (Save retires it first, which is
        /// exactly why SaveAll clears dead paths before it writes).
        ///
        /// Reading File.Exists alone made moving a module BACK to where it
        /// started fail with "already there" - pointing at the module's own
        /// pre-move copy, which no amount of retrying could clear because only
        /// Save can (UB-222).</summary>
        public bool IsPathAvailable(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;
            if (_tree.ByPath(filePath) != null)
                return false;
            if (!File.Exists(filePath))
                return true;

            string full = BuilderTree.Canon(filePath);
            foreach (var module in _tree.Modules)
                if (module != null && module.HasMoved
                    && string.Equals(BuilderTree.Canon(module.DiskPath), full,
                        StringComparison.OrdinalIgnoreCase))
                    return true;

            foreach (string orphan in _tree.OrphanedPaths())
                if (string.Equals(orphan, full, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
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

            string root = BuilderTree.ResolveRoot(focusFullPath);
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

        public BuilderModule CreateNew(string filePath, string initialBuffer)
        {
            string full = BuilderTree.Canon(filePath);
            if (!IsPathAvailable(full))
                return null;
            var module = BuilderModule.Fresh(
                Path.GetDirectoryName(full) ?? string.Empty,
                NameOf(full), KindOf(full), initialBuffer);
            _tree.Add(module);
            Changed?.Invoke();
            return module;
        }

        /// <summary>The in-memory home of a tree started from the empty state.
        /// Nothing is ever written here: Save re-homes every module under it into
        /// the folder the user picks, and refuses to write one that is still here.
        ///
        /// It sits under Assets deliberately - IsReadOnlyLocation treats anything
        /// outside the project as immutable, so a provisional path in the temp
        /// directory would open the first card READ-ONLY and refuse every edit -
        /// and its name ends in "~", which the Asset Database ignores wholesale,
        /// so a module that reaches disk here is never imported and never
        /// compiled.</summary>
        public static string UnsavedRoot => BuilderTree.Canon(
            Path.Combine(Application.dataPath, "__RuitkBuilderUnsaved__~"));

        /// <summary>Whether a module is still at the provisional location.
        ///
        /// DERIVED from where the module sits. It was a flag each caller set, and
        /// the caller that did not was the create flow for the SECOND module in a
        /// new tree: with a focus file present it passed false, so a style module
        /// created beside its component never asked for a location, was never
        /// re-homed, and Save happily wrote it under the provisional root - which
        /// the Asset Database ignores. The file existed, Unity never saw it, and
        /// the component's import compiled to "no file at ./x.style" (UB-178).
        /// A fact about the tree cannot be forgotten by a caller.</summary>
        public static bool IsUnlocated(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return false;
            string full = BuilderTree.Canon(filePath);
            string root = UnsavedRoot;
            return full.Length > root.Length
                && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (full[root.Length] == Path.DirectorySeparatorChar
                    || full[root.Length] == Path.AltDirectorySeparatorChar);
        }

        /// <summary>Every module still waiting to be told where it lives.</summary>
        public List<BuilderModule> UnlocatedModules()
        {
            var pending = new List<BuilderModule>();
            foreach (var module in _tree.Modules)
                if (IsUnlocated(module.FilePath))
                    pending.Add(module);
            return pending;
        }

        /// <summary>Gives a module its real home. A module carried along by a
        /// parent that owned its folder is already there, and moving it to where
        /// it already is changes nothing, so the walk is order-independent.</summary>
        public List<ImportRewrite> PlaceAt(BuilderModule module, string newFolder)
        {
            if (module == null || _tree.ByPath(module.FilePath) != module)
                return null;
            var snapshot = CaptureImports();
            _tree.MoveTo(module, newFolder, module.Name);
            var rewrites = ReconcileImports(snapshot);
            Changed?.Invoke();
            return rewrites;
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

        /// <summary>One buffer rewritten to keep an import true across a move.</summary>
        public sealed class ImportRewrite
        {
            public string FilePath;
            public string Before;
            public string After;
        }

        /// <summary>Moves a module, carrying its folder's contents when it owns
        /// the folder, and rewrites every specifier the move invalidated. Returns
        /// those rewrites - NULL when the move was refused - so the caller can put
        /// them in the ledger beside the move itself.
        ///
        /// Nothing happens on disk: Save sees DiskPath disagree with the derived
        /// path and projects the move.</summary>
        public List<ImportRewrite> MoveTo(string filePath, string newFolder, string newName)
        {
            var module = _tree.ByPath(filePath);
            if (module == null || module.IsReadOnly)
                return null;
            var snapshot = CaptureImports();
            _tree.MoveTo(module, newFolder, newName);
            var rewrites = ReconcileImports(snapshot);
            Changed?.Invoke();
            return rewrites;
        }

        /// <summary>Moves a module to a target PATH, splitting the folder, name
        /// and kind out of it. What the ledger replays: an entry records where a
        /// module went, and walking it back is the same operation the other way.</summary>
        public List<ImportRewrite> MoveToPath(string fromPath, string toPath)
        {
            string to = BuilderTree.Canon(toPath);
            if (string.IsNullOrEmpty(to))
                return null;
            return MoveTo(fromPath, Path.GetDirectoryName(to) ?? string.Empty, NameOf(to));
        }

        /// <summary>What every import in the tree POINTED AT, taken before an
        /// operation that will move things.
        ///
        /// Keyed by (importer, LINE) rather than by the specifier text. A rename
        /// edits specifier text in place before the move happens, so a snapshot
        /// keyed on that text could no longer find its own entries afterwards -
        /// which is how a specifier naming the moved FOLDER from outside it
        /// (`"../Panel/Panel"`) stayed broken: the name rewrite had already made
        /// it unresolvable, so nothing downstream could tell what it had meant. A
        /// line survives an edit within it.</summary>
        public sealed class ImportSnapshot
        {
            internal readonly Dictionary<(string ImporterId, int Line), string> Targets =
                new Dictionary<(string, int), string>();
        }

        public ImportSnapshot CaptureImports()
        {
            var snapshot = new ImportSnapshot();
            foreach (var module in _tree.Modules)
            {
                if (module.IsReadOnly)
                    continue;
                Ruitk.Language.Parser.ParseResult parsed;
                try
                {
                    parsed = BuilderLanguage.Parse(module.BufferText, module.FilePath);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var import in parsed.Directives.Imports)
                {
                    string spec = import.Specifier ?? string.Empty;
                    if (spec.Length == 0 || spec.StartsWith("@", StringComparison.Ordinal)
                        || import.Line <= 0)
                        continue;
                    string mapped = BuilderSpecifiers.Map(module.FilePath, spec);
                    var target = mapped == null ? null : _tree.ByPath(mapped);
                    if (target != null)
                        snapshot.Targets[(module.Id, import.Line)] = target.Id;
                }
            }
            return snapshot;
        }

        /// <summary>Re-spells every snapshotted import for where its two ends now
        /// sit, and returns the buffers it changed.
        ///
        /// Both ends matter: a module that MOVED changes how everyone reaches it,
        /// and an IMPORTER that moved changes how it reaches everyone else -
        /// rewriting only the importers of the moved module would leave a
        /// relocated component pointing at everything it used to sit beside.</summary>
        public List<ImportRewrite> ReconcileImports(ImportSnapshot snapshot)
        {
            var rewrites = new List<ImportRewrite>();
            if (snapshot == null || snapshot.Targets.Count == 0)
                return rewrites;

            var byImporter = new Dictionary<string, List<(int Line, string Wanted)>>(
                StringComparer.Ordinal);
            foreach (var pair in snapshot.Targets)
            {
                var importer = _tree.ById(pair.Key.ImporterId);
                var target = _tree.ById(pair.Value);
                if (importer == null || target == null || importer.IsReadOnly)
                    continue;
                string wanted = BuilderSpecifiers.Relative(importer.Folder, target.FilePath);
                if (string.IsNullOrEmpty(wanted))
                    continue;
                if (!byImporter.TryGetValue(pair.Key.ImporterId, out var list))
                    byImporter[pair.Key.ImporterId] = list = new List<(int, string)>();
                list.Add((pair.Key.Line, wanted));
            }

            foreach (var pair in byImporter)
            {
                var importer = _tree.ById(pair.Key);
                string before = importer.BufferText;
                string after = RewriteSpecifiers(importer, pair.Value);
                if (after == null || string.Equals(after, before, StringComparison.Ordinal))
                    continue;
                importer.ApplyEdit(after);
                rewrites.Add(new ImportRewrite
                {
                    FilePath = importer.FilePath, Before = before, After = after,
                });
            }
            return rewrites;
        }

        /// <summary>Replaces the quoted specifier of each named import, using the
        /// parser's own span rather than searching the text: a specifier is an
        /// ordinary string and can appear anywhere else in the file. Edits are
        /// applied from the LAST line backwards so the spans ahead of each one
        /// stay valid.</summary>
        private static string RewriteSpecifiers(
            BuilderModule importer, List<(int Line, string Wanted)> wanted)
        {
            Ruitk.Language.Parser.ParseResult parsed;
            try
            {
                parsed = BuilderLanguage.Parse(importer.BufferText, importer.FilePath);
            }
            catch (Exception)
            {
                return null;
            }

            var edits = new List<(int Line0, int Start, int Length, string Text)>();
            foreach (var import in parsed.Directives.Imports)
            {
                if (import.SpecifierColumn < 0 || import.Line <= 0)
                    continue;
                foreach (var (line, replacement) in wanted)
                {
                    if (import.Line != line
                        || string.Equals(import.Specifier, replacement, StringComparison.Ordinal))
                        continue;
                    edits.Add((
                        import.Line - 1,
                        import.SpecifierColumn,
                        (import.Specifier?.Length ?? 0) + 2,
                        "\"" + replacement + "\""));
                    break;
                }
            }
            if (edits.Count == 0)
                return null;

            var lines = new List<string>(importer.BufferText.Split('\n'));
            edits.Sort((a, b) => a.Line0 != b.Line0
                ? b.Line0.CompareTo(a.Line0)
                : b.Start.CompareTo(a.Start));
            foreach (var (line0, start, length, text) in edits)
            {
                if (line0 < 0 || line0 >= lines.Count)
                    continue;
                string line = lines[line0];
                if (start < 0 || start + length > line.Length)
                    continue;
                lines[line0] = line.Substring(0, start) + text + line.Substring(start + length);
            }
            return string.Join("\n", lines);
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
            var vacatedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    // The provisional root is checked HERE, at the write, so no
                    // route into Save can put a module there - the Asset Database
                    // ignores that folder, so a file written to it exists and is
                    // invisible, which is worse than not writing it at all.
                    if (module.IsReadOnly || IsUnlocated(module.FilePath))
                        continue;
                    string target = module.FilePath;
                    if (string.IsNullOrEmpty(target))
                        continue;

                    if (module.HasMoved)
                    {
                        // Where it came FROM, so a folder the move empties can be
                        // taken out with it. A move that leaves the old folder
                        // standing has not moved anything as far as the Project
                        // window is concerned.
                        string vacated = Path.GetDirectoryName(module.DiskPath);
                        if (!string.IsNullOrEmpty(vacated))
                            vacatedFolders.Add(vacated);
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
            PruneEmptyFolders(vacatedFolders);

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

        /// <summary>Takes out folders a move emptied, and their parents, up to but
        /// never including the tree they live in.
        ///
        /// A folder holding ANYTHING else stays - a .cs beside a component, a .uss,
        /// a texture. Only .meta files are ignored, because a .meta is not content,
        /// it is Unity's note about content that is no longer there.</summary>
        private static void PruneEmptyFolders(HashSet<string> folders)
        {
            foreach (string folder in folders)
            {
                string walk = folder;
                while (!string.IsNullOrEmpty(walk) && IsEmptyOfContent(walk))
                {
                    string parent = Path.GetDirectoryName(walk);
                    string assetPath = ToAssetPath(walk);
                    if (assetPath == null || !AssetDatabase.DeleteAsset(assetPath))
                        break;
                    walk = parent;
                }
            }
        }

        private static bool IsEmptyOfContent(string folder)
        {
            if (!Directory.Exists(folder))
                return false;
            try
            {
                foreach (string entry in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                    if (!entry.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ToDiskText(BuilderModule module) =>
            module.UsedCrlf ? module.BufferText.Replace("\n", "\r\n") : module.BufferText;

        /// <summary>Makes sure the folder a module is about to occupy exists - and,
        /// inside the project, that the ASSET DATABASE knows it exists.
        ///
        /// Directory.CreateDirectory alone puts it on the filesystem, where
        /// MoveAsset cannot see it: MoveAsset resolves the destination's parent by
        /// GUID, and a folder Unity has never imported has none. Re-filing a module
        /// into a folder that did not exist yet therefore failed the whole save
        /// with "Could not find parent directory GUID: 000..." - which the folder
        /// view makes an ordinary thing to do (UB-204).</summary>
        private static void EnsureDirectory(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir))
                return;
            string assetDir = ToAssetPath(dir);
            if (assetDir == null)
            {
                // Outside Assets/ and Packages/ there is no asset database to tell.
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return;
            }
            EnsureAssetFolder(assetDir);
        }

        /// <summary>Creates a project folder and every missing ancestor THROUGH the
        /// AssetDatabase, so each one gets a GUID and can be a move target.</summary>
        private static void EnsureAssetFolder(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath))
                return;
            string parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            string name = Path.GetFileName(assetPath);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                return;
            EnsureAssetFolder(parent);
            if (AssetDatabase.IsValidFolder(assetPath))
                return;
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, name)))
            {
                // Already on disk without a GUID - what an earlier save that failed
                // part way leaves behind. Importing gives it one; creating again
                // would put a "Folder 1" beside it.
                AssetDatabase.ImportAsset(
                    assetPath, ImportAssetOptions.ForceSynchronousImport);
            }
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
