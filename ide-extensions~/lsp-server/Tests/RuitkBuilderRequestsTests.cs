using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UitkxLanguageServer;
using Xunit;

namespace UitkxLanguageServer.Tests;

/// <summary>
/// VE-07: the four ruitk/* requests the RUITK Builder consumes, plus the
/// WorkspaceIndex buffer overlay that makes unsaved session files visible to
/// menus and the workspace graph.
/// </summary>
public sealed class RuitkBuilderRequestsTests : IDisposable
{
    private readonly string _root;

    public RuitkBuilderRequestsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "uitkx-ve07-" + Guid.NewGuid().ToString("N"), "Assets", "UI");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_root))!, recursive: true); } catch { }
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ── Buffer overlay ──────────────────────────────────────────────────────

    [Fact]
    public void Overlay_UnsavedFile_ContributesExports()
    {
        var buffers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = new WorkspaceIndex
        {
            TextOverlay = p => buffers.TryGetValue(p, out var t) ? t : null,
        };

        string phantom = Path.Combine(_root, "NeverSaved.uitkx");
        buffers[phantom] =
            "export VirtualNode NeverSaved() {\n  return (\n    <Label text=\"x\" />\n  );\n}\n";

        index.Refresh(phantom);

        var exports = index.GetExportsSnapshot();
        Assert.Contains(exports, e =>
            e.File == phantom
            && e.Name == "NeverSaved"
            && e.Kind == Ruitk.Language.StrictImportDetector.ExportKind.Component);
        Assert.Contains("NeverSaved", index.KnownElements);
    }

    [Fact]
    public void Overlay_BufferWinsOverDisk()
    {
        string path = WriteFile("Comp.uitkx",
            "export VirtualNode DiskName() {\n  return (\n    <Label text=\"d\" />\n  );\n}\n");

        var buffers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [path] = "export VirtualNode BufferName() {\n  return (\n    <Label text=\"b\" />\n  );\n}\n",
        };
        var index = new WorkspaceIndex
        {
            TextOverlay = p => buffers.TryGetValue(p, out var t) ? t : null,
        };

        index.Refresh(path);

        Assert.Contains("BufferName", index.KnownElements);
        Assert.DoesNotContain("DiskName", index.KnownElements);
    }

    [Fact]
    public void Overlay_Close_EvictsNeverSavedFile()
    {
        var buffers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = new WorkspaceIndex
        {
            TextOverlay = p => buffers.TryGetValue(p, out var t) ? t : null,
        };

        string phantom = Path.Combine(_root, "Ephemeral.uitkx");
        buffers[phantom] =
            "export VirtualNode Ephemeral() {\n  return (\n    <Label text=\"x\" />\n  );\n}\n";
        index.Refresh(phantom);
        Assert.Contains("Ephemeral", index.KnownElements);

        buffers.Remove(phantom);
        index.Refresh(phantom);
        Assert.DoesNotContain("Ephemeral", index.KnownElements);
    }

    [Fact]
    public void IndexChanged_FiresOnlyWhenSurfaceChanges()
    {
        var buffers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = new WorkspaceIndex
        {
            TextOverlay = p => buffers.TryGetValue(p, out var t) ? t : null,
        };
        int fired = 0;
        index.IndexChanged += () => Interlocked.Increment(ref fired);

        string path = Path.Combine(_root, "Stable.uitkx");
        buffers[path] =
            "export VirtualNode Stable(string title) {\n  return (\n    <Label text={title} />\n  );\n}\n";
        index.Refresh(path);
        int afterFirst = fired;
        Assert.True(afterFirst >= 1);

        // Body-only change: same exports, same props — no cross-file-visible drift.
        buffers[path] =
            "export VirtualNode Stable(string title) {\n  return (\n    <Label text={title} tooltip=\"t\" />\n  );\n}\n";
        index.Refresh(path);
        Assert.Equal(afterFirst, fired);

        // Prop surface change: other files' menus/diagnostics ARE affected.
        buffers[path] =
            "export VirtualNode Stable(string title, int gold) {\n  return (\n    <Label text={title} />\n  );\n}\n";
        index.Refresh(path);
        Assert.True(fired > afterFirst);
    }

    // ── ruitk/schema ────────────────────────────────────────────────────────

    [Fact]
    public async Task Schema_ReturnsEmbeddedJson()
    {
        var result = await new RuitkSchemaHandler().Handle(new RuitkSchemaParams(), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(result.Json));
        Assert.Contains("\"elements\"", result.Json);
        Assert.Contains("\"styleKeyValues\"", result.Json);
    }

    // ── ruitk/hooks ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Hooks_ReturnsAmbientNamesWithDocs()
    {
        var result = await new RuitkHooksHandler().Handle(new RuitkHooksParams(), CancellationToken.None);
        Assert.True(result.Hooks.Count >= 40, $"expected both casings of the hook table, got {result.Hooks.Count}");
        Assert.Contains(result.Hooks, h => h.Name == "useState" && h.Doc.Length > 0);
        Assert.Contains(result.Hooks, h => h.Name == "Hooks.UseState" || h.Name == "UseState");
    }

    // ── ruitk/componentProps ────────────────────────────────────────────────

    [Fact]
    public async Task ComponentProps_ReturnsIndexedProps()
    {
        var index = new WorkspaceIndex();
        string path = WriteFile("Header.uitkx",
            "export VirtualNode Header(string title, int gold) {\n  return (\n    <Label text={title} />\n  );\n}\n");
        index.Refresh(path);

        var handler = new RuitkComponentPropsHandler(index);
        var result = await handler.Handle(
            new RuitkComponentPropsParams { Name = "Header" }, CancellationToken.None);

        Assert.True(result.Found);
        Assert.Equal(path, result.FilePath);
        Assert.Contains(result.Props, p => p.Name == "title" && p.Type == "string");
        Assert.Contains(result.Props, p => p.Name == "gold" && p.Type == "int");

        var missing = await handler.Handle(
            new RuitkComponentPropsParams { Name = "Nope" }, CancellationToken.None);
        Assert.False(missing.Found);
    }

    // ── ruitk/workspaceGraph ────────────────────────────────────────────────

    [Fact]
    public async Task WorkspaceGraph_ResolvesEdgesAndKeepsUnresolvable()
    {
        var index = new WorkspaceIndex();
        string header = WriteFile("Header.uitkx",
            "export VirtualNode Header(string title) {\n  return (\n    <Label text={title} />\n  );\n}\n");
        string screen = WriteFile("Screen.uitkx",
            "import { Header } from \"./Header\"\nimport { Ghost } from \"./DoesNotExist\"\n\n"
            + "export VirtualNode Screen() {\n  return (\n    <Header title=\"hi\" />\n  );\n}\n");
        index.Refresh(header);
        index.Refresh(screen);

        var result = await new RuitkWorkspaceGraphHandler(index)
            .Handle(new RuitkWorkspaceGraphParams(), CancellationToken.None);

        Assert.Contains(result.Nodes, n => n.File == header
            && n.Exports.Any(e => e.Name == "Header" && e.Kind == "Component"));
        Assert.Contains(result.Nodes, n => n.File == screen
            && n.Exports.Any(e => e.Name == "Screen"));

        var resolved = result.Edges.FirstOrDefault(e => e.FromFile == screen && e.Specifier == "./Header");
        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(header), Path.GetFullPath(resolved!.ToFile));
        Assert.Contains("Header", resolved.Names);

        var broken = result.Edges.FirstOrDefault(e => e.FromFile == screen && e.Specifier == "./DoesNotExist");
        Assert.NotNull(broken);
    }
}
