using System;
using System.Collections.Generic;
using System.IO;
using Ruitk.Builder;

static class Program
{
    static int failures;

    static void Check(bool ok, string what)
    {
        if (ok) { Console.WriteLine("  PASS  " + what); return; }
        failures++;
        Console.WriteLine("  FAIL  " + what);
    }

    static string Root => Path.Combine(Path.GetTempPath(), "ruitk-tree-test", "Assets", "UI");

    static BuilderModule Make(string folder, string name, BuilderNodeKind kind, string text, bool onDisk)
    {
        var m = new BuilderModule
        {
            Id = BuilderModule.NewId(),
            Folder = folder,
            Name = name,
            Kind = kind,
            BufferText = text,
            ProjectedText = onDisk ? text : string.Empty,
            DiskPath = onDisk ? Path.Combine(folder, name + BuilderModule.SuffixFor(kind)) : null,
        };
        return m;
    }

    /// <summary>BuilderTree.ResolveRoot reads the FILESYSTEM, so these build the
    /// real folder shapes rather than asserting against a string. The layouts are
    /// the ones this package actually ships - including Samples/Components, whose
    /// name alone used to look like the house "components" nesting level.</summary>
    static void RootChecks()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "ruitk-root-test");
        if (Directory.Exists(sandbox))
            Directory.Delete(sandbox, true);

        void Module(params string[] segments)
        {
            string full = Path.Combine(sandbox, Path.Combine(segments));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, "export VirtualNode X() {}");
        }

        // Samples/Components/<26 demo trees>, each Page/Page.uitkx + components/
        Module("Samples", "Components", "ShowcaseDemoPage", "ShowcaseDemoPage.uitkx");
        Module("Samples", "Components", "ShowcaseDemoPage", "components",
               "ShowcaseFieldsPanel", "ShowcaseFieldsPanel.uitkx");
        Module("Samples", "Components", "ShowcaseDemoPage", "components",
               "ShowcaseTopBar", "ShowcaseTopBar.uitkx");
        Module("Samples", "Components", "DoomGame", "DoomGame.uitkx");

        string page = Path.Combine(sandbox, "Samples", "Components", "ShowcaseDemoPage");
        string child = Path.Combine(page, "components", "ShowcaseFieldsPanel",
                                    "ShowcaseFieldsPanel.uitkx");

        Check(string.Equals(BuilderTree.ResolveRoot(child), page,
                  StringComparison.OrdinalIgnoreCase),
              "a child under components/ resolves to the component that owns it");
        Check(string.Equals(
                  BuilderTree.ResolveRoot(Path.Combine(page, "ShowcaseDemoPage.uitkx")), page,
                  StringComparison.OrdinalIgnoreCase),
              "the root module resolves to its own folder");
        Check(!BuilderTree.ResolveRoot(child)
                  .EndsWith("Samples", StringComparison.OrdinalIgnoreCase),
              "a folder merely NAMED Components is not the house nesting level");

        // A companion beside its component, and a module in a plain folder.
        Module("Flat", "Loose.uitkx");
        Check(string.Equals(BuilderTree.ResolveRoot(Path.Combine(sandbox, "Flat", "Loose.uitkx")),
                  Path.Combine(sandbox, "Flat"), StringComparison.OrdinalIgnoreCase),
              "a module in a plain folder roots at that folder");

        // components/ WITHOUT an owning component above it is not a nesting level
        // either - the guard cuts both ways.
        Module("Orphan", "components", "Thing", "Thing.uitkx");
        Check(string.Equals(
                  BuilderTree.ResolveRoot(
                      Path.Combine(sandbox, "Orphan", "components", "Thing", "Thing.uitkx")),
                  Path.Combine(sandbox, "Orphan", "components", "Thing"),
                  StringComparison.OrdinalIgnoreCase),
              "components/ with no owning component above it is just a folder");

        Check(BuilderTree.ResolveRoot(null) == null, "no focus resolves to no root");

        Directory.Delete(sandbox, true);
    }

    static void Main()
    {
        string comp = Path.Combine(Root, "Showcase");
        string child = Path.Combine(comp, "components", "Sub");

        // ---- derived path and kind suffixes -------------------------------
        Console.WriteLine("derived path");
        var c = Make(comp, "Showcase", BuilderNodeKind.Component, "export VirtualNode Showcase() {}", true);
        var s = Make(comp, "showcaseStyle", BuilderNodeKind.Style, "", true);
        var h = Make(comp, "useThing", BuilderNodeKind.Hook, "", false);
        Check(c.FilePath.EndsWith("Showcase.uitkx"), "component suffix");
        Check(s.FilePath.EndsWith("showcaseStyle.style.uitkx"), "style suffix");
        Check(h.FilePath.EndsWith("useThing.hooks.uitkx"), "hook suffix");

        // ---- IsOnDisk survives the null-becomes-empty hazard ---------------
        Console.WriteLine("null vs empty DiskPath");
        Check(!h.IsOnDisk, "never-written module is not on disk (null)");
        h.DiskPath = "";                       // what Unity hands back after a reload
        Check(!h.IsOnDisk, "never-written module is not on disk (empty)");
        Check(c.IsOnDisk, "written module is on disk");

        // ---- ownership: a companion never owns the folder ------------------
        Console.WriteLine("folder ownership");
        Check(c.OwnsFolder, "component named after its folder owns it");
        Check(!s.OwnsFolder, "style companion does not own the folder");
        var sameName = Make(comp, "Showcase", BuilderNodeKind.Style, "", false);
        Check(!sameName.OwnsFolder, "style sharing the component name still does not own it");

        // ---- tree: indexing -----------------------------------------------
        Console.WriteLine("tree indexing");
        var tree = new BuilderTree();
        tree.Add(c); tree.Add(s);
        var sub = Make(child, "Sub", BuilderNodeKind.Component, "export VirtualNode Sub() {}", true);
        tree.Add(sub);
        Check(tree.ByPath(c.FilePath) == c, "ByPath finds a module");
        Check(tree.ById(c.Id) == c, "ById finds a module");
        Check(tree.ByPath(null) == null, "ByPath(null) is not-found, not a throw");
        Check(tree.ByPath(Path.Combine(Root, "Nope.uitkx")) == null, "unknown path is not-found");

        // ---- delete is absence --------------------------------------------
        Console.WriteLine("delete is absence");
        tree.SetProjection(new[] { c.FilePath, s.FilePath, sub.FilePath });
        Check(tree.OrphanedPaths().Count == 0, "nothing orphaned while all present");
        string goneStyle = s.FilePath;
        tree.Remove(s);
        Check(tree.ByPath(goneStyle) == null, "removed module is gone from the index");
        var orphans = tree.OrphanedPaths();
        Check(orphans.Count == 1 && orphans[0].EndsWith("showcaseStyle.style.uitkx"),
              "its file is reported orphaned, with no pending list anywhere");

        // the name is free again the instant it is removed
        Check(tree.ByPath(goneStyle) == null, "the deleted name is immediately reusable");

        // ---- rename moves the subtree --------------------------------------
        Console.WriteLine("folder-owning rename moves the subtree");
        string oldChildFolder = sub.Folder;
        tree.MoveTo(c, Path.Combine(Root, "Renamed"), "Renamed");
        Check(c.FilePath.EndsWith(Path.Combine("Renamed", "Renamed.uitkx")), "the module moved");
        Check(sub.Folder != oldChildFolder && sub.Folder.Contains("Renamed"),
              "the child moved with the folder");
        Check(sub.Folder.EndsWith(Path.Combine("components", "Sub")),
              "the child kept its position relative to the parent");
        Check(c.HasMoved && sub.HasMoved, "both report a pending move against their DiskPath");

        // ---- dirtiness is derived, not maintained --------------------------
        Console.WriteLine("dirtiness");
        var clean = Make(Root, "Clean", BuilderNodeKind.Component, "same", true);
        Check(!clean.IsDirty, "text equal to projected text is clean");
        clean.ApplyEdit("different");
        Check(clean.IsDirty, "an edit makes it dirty");
        clean.MarkProjected(clean.FilePath);
        Check(!clean.IsDirty, "projecting makes it clean again");

        // ---- read-only is the last line of defence -------------------------
        Console.WriteLine("read-only");
        var ro = Make(Root, "Package", BuilderNodeKind.Component, "x", true);
        ro.IsReadOnly = true;
        bool threw = false;
        try { ro.ApplyEdit("y"); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "a read-only module refuses an edit");

        // ---- the serialization shuttle -------------------------------------
        Console.WriteLine("serialization round trip");
        var round = new BuilderTree();
        var a1 = Make(Root, "A", BuilderNodeKind.Component, "textA", true);
        var b1 = Make(Root, "b", BuilderNodeKind.Style, "textB", false);   // DiskPath null
        round.Add(a1); round.Add(b1);
        round.SetProjection(new[] { a1.FilePath });

        round.OnBeforeSerialize();
        round.OnAfterDeserialize();            // indexes dropped, as after a reload
        Check(round.ByPath(a1.FilePath) == a1, "path index rebuilt lazily after deserialize");
        Check(round.ById(b1.Id) == b1, "id index rebuilt lazily after deserialize");
        Check(!b1.IsOnDisk, "a never-written module stays never-written across the trip");
        Check(round.Validate().Count == 0, "a healthy tree validates clean");

        // ---- validation catches what a broken round trip would look like ---
        Console.WriteLine("validation");
        var broken = new BuilderTree();
        var d1 = Make(Root, "Dup", BuilderNodeKind.Component, "1", true);
        var d2 = Make(Root, "Dup", BuilderNodeKind.Component, "2", true);
        broken.Add(d1); broken.Add(d2);
        Check(broken.Validate().Count > 0, "two modules claiming one path is reported");

        var noId = new BuilderTree();
        var n1 = Make(Root, "N", BuilderNodeKind.Component, "1", true);
        noId.Add(n1);
        n1.Id = null;
        Check(noId.Validate().Count > 0, "a module that lost its id is reported");

        // ---- a move is a move, not a delete plus a write --------------------
        Console.WriteLine("moving does not orphan");
        var mv = new BuilderTree();
        var m1 = Make(Root, "Moved", BuilderNodeKind.Component, "x", true);
        string wasAt = m1.DiskPath;
        mv.Add(m1);
        mv.SetProjection(new[] { m1.FilePath });
        mv.MoveTo(m1, Path.Combine(Root, "Elsewhere"), "Moved");
        Check(m1.HasMoved, "the module knows it has moved");
        Check(mv.OrphanedPaths().Count == 0,
              "its old file is NOT orphaned - Save moves the file, keeping its GUID and meta");
        m1.MarkProjected(m1.FilePath);
        mv.SetProjection(new[] { m1.FilePath });
        Check(!m1.HasMoved && mv.OrphanedPaths().Count == 0 && !mv.HasUnsavedWork(),
              "after the projection there is nothing left to do");
        Check(wasAt != m1.DiskPath, "and DiskPath followed the module");

        // ---- undoing a delete puts the SAME module back ---------------------
        Console.WriteLine("restore");
        var un = new BuilderTree();
        var u1 = Make(Root, "Undone", BuilderNodeKind.Component, "x", true);
        un.Add(u1);
        un.SetProjection(new[] { u1.FilePath });
        un.Remove(u1);
        Check(un.OrphanedPaths().Count == 1, "the deleted file is orphaned");
        un.Add(u1);
        Check(un.OrphanedPaths().Count == 0 && un.ByPath(u1.FilePath) == u1,
              "putting the module back un-orphans its file, identity and all");
        Check(!un.HasUnsavedWork(), "and the tree is back to having nothing to save");

        // ---- unsaved work is derived from the tree, never accumulated -------
        Console.WriteLine("unsaved work");
        var uw = new BuilderTree();
        var w1 = Make(Root, "Work", BuilderNodeKind.Component, "x", true);
        uw.Add(w1);
        uw.SetProjection(new[] { w1.FilePath });
        Check(!uw.HasUnsavedWork(), "a freshly loaded tree has nothing to save");
        w1.ApplyEdit("y");
        Check(uw.HasUnsavedWork(), "an edit is unsaved work");
        w1.MarkProjected(w1.FilePath);
        Check(!uw.HasUnsavedWork(), "projecting the edit settles it");
        var w2 = Make(Root, "Fresh", BuilderNodeKind.Component, "z", false);
        uw.Add(w2);
        Check(uw.HasUnsavedWork(), "a module that has never been written is unsaved work");
        var wro = Make(Root, "Locked", BuilderNodeKind.Component, "p", true);
        wro.IsReadOnly = true;
        uw.Remove(w2);
        uw.Add(wro);
        Check(!uw.HasUnsavedWork(),
              "a read-only module is never unsaved work - the builder cannot write it");

        // ---- abort is load re-run -------------------------------------------
        Console.WriteLine("abort");
        var ab = new BuilderTree();
        var ab1 = Make(Root, "Abort", BuilderNodeKind.Component, "x", true);
        ab.Add(ab1);
        ab.SetProjection(new[] { ab1.FilePath });
        ab.MoveTo(ab1, Path.Combine(Root, "Gone"), "Abort");
        ab.Remove(ab1);
        Check(ab.OrphanedPaths().Count == 1 && ab.HasUnsavedWork(), "the tree has pending work");
        var reloaded = Make(Root, "Abort", BuilderNodeKind.Component, "x", true);
        ab.Reset(new[] { reloaded }, new[] { reloaded.FilePath });
        Check(!ab.HasUnsavedWork() && ab.OrphanedPaths().Count == 0,
              "resetting from disk leaves nothing pending, whatever happened before");

        // ---- the same operation twice lands in the same place ---------------
        Console.WriteLine("idempotence");
        var id = new BuilderTree();
        var i1 = Make(Root, "Same", BuilderNodeKind.Component, "x", true);
        var i2 = Make(Path.Combine(Root, "Same", "components", "Leaf"), "Leaf",
                      BuilderNodeKind.Component, "y", true);
        i1.Folder = Path.Combine(Root, "Same");
        id.Add(i1); id.Add(i2);
        id.MoveTo(i1, Path.Combine(Root, "Twice"), "Twice");
        string after1 = i1.FilePath + "|" + i2.FilePath;
        id.MoveTo(i1, Path.Combine(Root, "Twice"), "Twice");
        Check(i1.FilePath + "|" + i2.FilePath == after1,
              "repeating a move changes nothing, subtree included");

        // ---- where a tree starts, against real folders on disk --------------
        Console.WriteLine("tree root");
        RootChecks();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL PASS"
            : failures + " FAILURE(S)");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
