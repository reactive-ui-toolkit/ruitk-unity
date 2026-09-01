using System;
using System.Collections.Generic;
using System.IO;
using Ruitk.Builder;
using Ruitk.EditorSupport.HMR;

/// <summary>
/// ISO-F — the guard the isolation campaign exists for.
///
/// <para>
/// Stages A–E fixed the leaks that were found. This is what stops the next one:
/// it hands <see cref="BuilderModuleSource"/> a fallback that THROWS on every
/// call, and asserts that anything the tree knows about is still answered. If a
/// future edit reaches past the tree for a module the tree owns, these fail
/// loudly here rather than showing up months later as "the preview renders the
/// wrong thing".
/// </para>
///
/// <para>
/// Three defects in one wave were exactly that reach: an importer bound to the
/// SAVED copy of a style module, a pending move refused because disk still held
/// the file, and a child component resolved by scanning loaded assemblies. Each
/// was silent. The point of a throwing fallback is that silence is no longer
/// available.
/// </para>
/// </summary>
internal static class IsolationChecks
{
    /// <summary>A module source that refuses to answer. Anything the tree owns
    /// must never get here.</summary>
    private sealed class ExplodingSource : IModuleSource
    {
        internal int Calls;

        public bool Exists(string uitkxPath)
        {
            Calls++;
            throw new InvalidOperationException(
                "the tree path reached DISK for " + uitkxPath);
        }

        public string ReadText(string uitkxPath)
        {
            Calls++;
            throw new InvalidOperationException(
                "the tree path reached DISK for " + uitkxPath);
        }

        public IEnumerable<string> SiblingsWithPrefix(string directory, string prefix)
        {
            Calls++;
            throw new InvalidOperationException(
                "the tree path reached DISK for " + directory);
        }
    }

    /// <summary>A fallback that answers nothing, for the cases where falling
    /// through is legitimate and we only care that the tree answered first.</summary>
    private sealed class EmptySource : IModuleSource
    {
        public bool Exists(string uitkxPath) => false;
        public string ReadText(string uitkxPath) => null;
        public IEnumerable<string> SiblingsWithPrefix(string directory, string prefix) =>
            Array.Empty<string>();
    }

    /// <summary>FilePath is DERIVED from folder + name + kind, so a module is
    /// built the way the tree builds one rather than by assigning a path.</summary>
    private static BuilderModule Module(
        string folder, string name, BuilderNodeKind kind, string text, bool onDisk)
    {
        return new BuilderModule
        {
            Id = BuilderModule.NewId(),
            Folder = folder,
            Name = name,
            Kind = kind,
            BufferText = text,
            ProjectedText = onDisk ? text : string.Empty,
            DiskPath = onDisk
                ? Path.Combine(folder, name + BuilderModule.SuffixFor(kind))
                : null,
        };
    }

    internal static void Run(Action<bool, string> check)
    {
        string root = Path.GetFullPath(Path.Combine("Assets", "UI", "Widget"));

        var tree = new BuilderTree();
        var component = Module(root, "Widget", BuilderNodeKind.Component,
            "export VirtualNode Widget() { return (<VisualElement />); }", onDisk: true);
        // A companion that exists ONLY in memory - never saved, no file anywhere.
        var style = Module(root, "widget", BuilderNodeKind.Style,
            "export Style card = new Style { };", onDisk: false);
        tree.Add(component);
        tree.Add(style);
        string componentPath = component.FilePath;
        string stylePath = style.FilePath;

        var exploding = new ExplodingSource();
        var source = new BuilderModuleSource(() => tree, exploding);

        // 1. A module the tree owns is answered by the tree, not the disk.
        bool existsOk;
        try { existsOk = source.Exists(componentPath); }
        catch (Exception) { existsOk = false; }
        check(existsOk, "ISO-F: a tree-owned module exists without touching disk");

        // 2. Its TEXT is the buffer, which is the whole point of the save-only
        //    contract - the file, if any, is stale by definition.
        string text = null;
        try { text = source.ReadText(componentPath); }
        catch (Exception) { }
        check(text != null && text.Contains("VirtualNode Widget"),
            "ISO-F: a tree-owned module reads from its buffer, not the file");

        // 3. An UNSAVED companion - no file anywhere - is still found.
        string unsaved = null;
        try { unsaved = source.ReadText(stylePath); }
        catch (Exception) { }
        check(unsaved != null && unsaved.Contains("Style card"),
            "ISO-F: an unsaved module reads from its buffer");

        bool unsavedExists;
        try { unsavedExists = source.Exists(stylePath); }
        catch (Exception) { unsavedExists = false; }
        check(unsavedExists, "ISO-F: an unsaved module exists");

        check(exploding.Calls == 0,
            "ISO-F: the disk was never consulted for a module the tree owns "
            + "(" + exploding.Calls + " call(s))");

        // 4. Companion discovery finds the unsaved sibling. The glob this
        //    replaced could not: there is no file to enumerate.
        var siblings = new List<string>(
            new BuilderModuleSource(() => tree, new EmptySource())
                .SiblingsWithPrefix(root, "widget."));
        check(siblings.Contains(stylePath),
            "ISO-F: companion discovery finds an unsaved sibling");

        // 5. A path the tree has NO opinion about falls through, because a
        //    hand-written module elsewhere is a legitimate import target.
        var empty = new EmptySource();
        var withEmpty = new BuilderModuleSource(() => tree, empty);
        check(!withEmpty.Exists(Path.Combine(root, "Stranger.uitkx")),
            "ISO-F: an unknown path falls through to the fallback");

        // 6. A module that MOVED does not answer from its old location. Disk
        //    still holds the file there until Save; the tree already said the
        //    module is elsewhere, and the tree wins.
        var moved = new BuilderTree();
        var relocated = Module(Path.Combine(root, "sub"), "Moved", BuilderNodeKind.Component,
            "export VirtualNode Moved() { return (<VisualElement />); }", onDisk: false);
        // It came FROM the parent folder and has not been saved since, so the file
        // is still sitting there.
        relocated.DiskPath = Path.Combine(root, "Moved.uitkx");
        moved.Add(relocated);
        string movedFrom = relocated.DiskPath;
        var afterMove = new List<string>(
            new BuilderModuleSource(() => moved, new StubDiskSource(movedFrom))
                .SiblingsWithPrefix(root, "Moved"));
        check(!afterMove.Contains(movedFrom),
            "ISO-F: a moved module is not re-admitted from its old path on disk");
    }

    /// <summary>A fallback that reports one file present on disk, so the
    /// "already accounted for" rule can be exercised.</summary>
    private sealed class StubDiskSource : IModuleSource
    {
        private readonly string _path;
        internal StubDiskSource(string path) { _path = path; }
        public bool Exists(string uitkxPath) =>
            string.Equals(uitkxPath, _path, StringComparison.OrdinalIgnoreCase);
        public string ReadText(string uitkxPath) => null;
        public IEnumerable<string> SiblingsWithPrefix(string directory, string prefix) =>
            new[] { _path };
    }
}
