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
            Text = text,
            ProjectedText = onDisk ? text : string.Empty,
            DiskPath = onDisk ? Path.Combine(folder, name + BuilderModule.SuffixFor(kind)) : null,
        };
        return m;
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
        Check(c.Path.EndsWith("Showcase.uitkx"), "component suffix");
        Check(s.Path.EndsWith("showcaseStyle.style.uitkx"), "style suffix");
        Check(h.Path.EndsWith("useThing.hooks.uitkx"), "hook suffix");

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
        Check(tree.ByPath(c.Path) == c, "ByPath finds a module");
        Check(tree.ById(c.Id) == c, "ById finds a module");
        Check(tree.ByPath(null) == null, "ByPath(null) is not-found, not a throw");
        Check(tree.ByPath(Path.Combine(Root, "Nope.uitkx")) == null, "unknown path is not-found");

        // ---- delete is absence --------------------------------------------
        Console.WriteLine("delete is absence");
        tree.SetProjection(new[] { c.Path, s.Path, sub.Path });
        Check(tree.OrphanedPaths().Count == 0, "nothing orphaned while all present");
        string goneStyle = s.Path;
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
        Check(c.Path.EndsWith(Path.Combine("Renamed", "Renamed.uitkx")), "the module moved");
        Check(sub.Folder != oldChildFolder && sub.Folder.Contains("Renamed"),
              "the child moved with the folder");
        Check(sub.Folder.EndsWith(Path.Combine("components", "Sub")),
              "the child kept its position relative to the parent");
        Check(c.HasMoved && sub.HasMoved, "both report a pending move against their DiskPath");

        // ---- dirtiness is derived, not maintained --------------------------
        Console.WriteLine("dirtiness");
        var clean = Make(Root, "Clean", BuilderNodeKind.Component, "same", true);
        Check(!clean.IsDirty, "text equal to projected text is clean");
        clean.SetText("different");
        Check(clean.IsDirty, "an edit makes it dirty");
        clean.MarkProjected(clean.Path);
        Check(!clean.IsDirty, "projecting makes it clean again");

        // ---- read-only is the last line of defence -------------------------
        Console.WriteLine("read-only");
        var ro = Make(Root, "Package", BuilderNodeKind.Component, "x", true);
        ro.IsReadOnly = true;
        bool threw = false;
        try { ro.SetText("y"); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "a read-only module refuses an edit");

        // ---- the serialization shuttle -------------------------------------
        Console.WriteLine("serialization round trip");
        var round = new BuilderTree();
        var a1 = Make(Root, "A", BuilderNodeKind.Component, "textA", true);
        var b1 = Make(Root, "b", BuilderNodeKind.Style, "textB", false);   // DiskPath null
        round.Add(a1); round.Add(b1);
        round.SetProjection(new[] { a1.Path });

        round.OnBeforeSerialize();
        round.OnAfterDeserialize();            // indexes dropped, as after a reload
        Check(round.ByPath(a1.Path) == a1, "path index rebuilt lazily after deserialize");
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

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ALL PASS"
            : failures + " FAILURE(S)");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
