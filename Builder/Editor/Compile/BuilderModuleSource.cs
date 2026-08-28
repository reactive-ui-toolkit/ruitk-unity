#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Ruitk.EditorSupport.HMR;

namespace Ruitk.Builder
{
    /// <summary>
    /// Module truth as the BUILDER sees it: the open tree, and nothing else.
    ///
    /// <para>
    /// The builder edits a data model. Disk is a projection of it, and a stale one
    /// until Save — a module may have been created, renamed, moved or edited with
    /// no file behind it, and a module still on disk may have been deleted from the
    /// tree. So the compile pipeline must be answered from the tree, or it answers
    /// from a world the user is not looking at.
    /// </para>
    ///
    /// <para>
    /// Read-only modules (a package-resident `.uitkx` the builder opened but does
    /// not own) are still in the tree, so they answer from here like any other. A
    /// path the tree does not know at all falls through to disk: that is a real
    /// file the builder does not manage — a hand-written module elsewhere in the
    /// project — and refusing to read it would break compiles that legitimately
    /// depend on one.
    /// </para>
    /// </summary>
    internal sealed class BuilderModuleSource : IModuleSource
    {
        private readonly Func<BuilderWorkspace> _workspace;

        internal BuilderModuleSource(Func<BuilderWorkspace> workspace)
        {
            _workspace = workspace;
        }

        private BuilderModule Find(string path)
        {
            var workspace = _workspace?.Invoke();
            if (workspace == null || string.IsNullOrEmpty(path))
                return null;
            try { return workspace.Tree?.ByPath(Path.GetFullPath(path)); }
            catch (Exception) { return null; }
        }

        public bool Exists(string uitkxPath)
        {
            if (Find(uitkxPath) != null)
                return true;
            // Not ours. A file the builder does not manage is still a legitimate
            // import target, so the question falls through rather than being
            // answered "no" on the strength of the tree alone.
            return FileSystemModuleSource.Instance.Exists(uitkxPath);
        }

        public string ReadText(string uitkxPath)
        {
            var module = Find(uitkxPath);
            if (module != null)
                return module.BufferText;
            return FileSystemModuleSource.Instance.ReadText(uitkxPath);
        }

        /// <summary>Companions from the TREE, so an unsaved one is found.
        ///
        /// The directory glob this replaces could not see a companion that has no
        /// file yet, and would additionally return same-prefixed files belonging to
        /// nothing in the tree. Both answers are wrong for a builder session.
        ///
        /// Not observed in the field: this closes the mechanism, it does not fix a
        /// reported defect. Recorded that way on purpose — the wave it belongs to
        /// cost three rounds to a claim asserted from reading (ISO-1).</summary>
        public IEnumerable<string> SiblingsWithPrefix(string directory, string prefix)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var workspace = _workspace?.Invoke();

            string dir;
            try { dir = string.IsNullOrEmpty(directory) ? null : Path.GetFullPath(directory); }
            catch (Exception) { dir = null; }

            if (workspace != null && dir != null)
            {
                foreach (var module in workspace.Modules)
                {
                    if (module?.FilePath == null)
                        continue;
                    string moduleDir;
                    try { moduleDir = Path.GetDirectoryName(Path.GetFullPath(module.FilePath)); }
                    catch (Exception) { continue; }
                    if (!string.Equals(moduleDir, dir, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string name = Path.GetFileName(module.FilePath);
                    if (string.IsNullOrEmpty(name)
                        || !name.StartsWith(prefix, StringComparison.Ordinal)
                        || !name.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (seen.Add(Path.GetFullPath(module.FilePath)))
                        found.Add(module.FilePath);
                }
            }

            // Files on disk the tree does not carry — a companion written by hand
            // and never opened here — still count. A module the tree HAS deleted or
            // moved away is deliberately not re-admitted: the tree already said so.
            foreach (string path in FileSystemModuleSource.Instance.SiblingsWithPrefix(directory, prefix))
            {
                string full;
                try { full = Path.GetFullPath(path); }
                catch (Exception) { continue; }
                if (workspace != null && IsAccountedFor(workspace, full))
                    continue;
                if (seen.Add(full))
                    found.Add(path);
            }
            return found;
        }

        /// <summary>Whether the tree already has an opinion about this on-disk path
        /// — either a module sits there now, or one came FROM there and has since
        /// moved or been deleted. Either way the tree's answer wins over the
        /// file's.</summary>
        private static bool IsAccountedFor(BuilderWorkspace workspace, string fullPath)
        {
            if (workspace.Tree?.ByPath(fullPath) != null)
                return true;
            foreach (var module in workspace.Modules)
            {
                if (module?.DiskPath == null)
                    continue;
                try
                {
                    if (string.Equals(Path.GetFullPath(module.DiskPath), fullPath,
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception) { }
            }
            return false;
        }
    }
}
#endif
