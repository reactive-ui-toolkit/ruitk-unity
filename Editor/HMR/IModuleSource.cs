#if UNITY_EDITOR
using System.Collections.Generic;

namespace Ruitk.EditorSupport.HMR
{
    /// <summary>
    /// The compile pipeline's ONLY view of module truth.
    ///
    /// <para>
    /// Two callers ask this compiler to build `.uitkx` modules, and they disagree
    /// about where modules live. HMR watches FILES: disk is the truth, and reading
    /// it is the point. The RUITK Builder edits a TREE IN MEMORY: nothing reaches
    /// disk until Save, so an unsaved module has no file at all - only a synthetic
    /// path under a folder the Asset Database ignores, which is a fiction the
    /// compiler must not resolve against.
    /// </para>
    ///
    /// <para>
    /// Passing that distinction as an argument, rather than leaving the compiler to
    /// consult ambient state and hoping every call site remembers an overlay, is
    /// the whole point. Three defects in one wave were one crossing each answering
    /// from the wrong world: an importer bound to the SAVED copy of a style module
    /// (UB-203), a pending move refused because disk still held the file (UB-222),
    /// and a child component resolved by scanning loaded assemblies for a matching
    /// simple name (UB-223). Each was silent, and each cost a round of guessing.
    /// </para>
    ///
    /// <para>
    /// Implementations answer for their own world and never for the other's:
    /// <see cref="FileSystemModuleSource"/> is HMR's, and the builder supplies one
    /// backed by its tree that touches no disk at all.
    /// </para>
    /// </summary>
    internal interface IModuleSource
    {
        /// <summary>Whether a module exists AT ALL — on disk, or as an unsaved
        /// buffer. An import target that only exists in memory must answer true
        /// here or it cannot be wired into the tree being compiled.</summary>
        bool Exists(string uitkxPath);

        /// <summary>The module's current text, which for an edited module is the
        /// buffer and NOT what the file still says.</summary>
        string ReadText(string uitkxPath);

        /// <summary>Sibling modules in <paramref name="directory"/> whose file name
        /// starts with <paramref name="prefix"/> — the companion set.
        ///
        /// A directory glob cannot answer this for the builder: an unsaved
        /// companion has no file to enumerate, and a directory scan additionally
        /// picks up same-prefixed files that belong to nothing in the tree being
        /// compiled.</summary>
        IEnumerable<string> SiblingsWithPrefix(string directory, string prefix);
    }

    /// <summary>Module truth as HMR sees it: the filesystem, unmodified. This is
    /// the behaviour every caller had before the seam existed, so routing through
    /// it changes nothing for the watcher path.</summary>
    internal sealed class FileSystemModuleSource : IModuleSource
    {
        internal static readonly FileSystemModuleSource Instance = new FileSystemModuleSource();

        public bool Exists(string uitkxPath) =>
            !string.IsNullOrEmpty(uitkxPath) && System.IO.File.Exists(uitkxPath);

        public string ReadText(string uitkxPath) =>
            UitkxHmrCompiler.ReadTextWithRetry(uitkxPath);

        public IEnumerable<string> SiblingsWithPrefix(string directory, string prefix)
        {
            // An absent directory HAS no companions - that is an answer, not a
            // failure. The unguarded scan threw DirectoryNotFoundException on every
            // debounced recompile, which killed the preview and made typing crawl.
            if (string.IsNullOrEmpty(directory) || !System.IO.Directory.Exists(directory))
                return System.Array.Empty<string>();
            return System.IO.Directory.GetFiles(directory, prefix + "*.uitkx");
        }
    }
}
#endif
