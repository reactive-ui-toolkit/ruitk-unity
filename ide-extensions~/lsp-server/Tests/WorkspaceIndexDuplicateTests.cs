using System;
using System.IO;
using System.Linq;
using Xunit;

namespace UitkxLanguageServer.Tests;

/// <summary>
/// Tests for the multi-valued <c>WorkspaceIndex</c> element store reshape
/// (TECH_DEBT_20_21_22_RESOLUTION_PLAN.md §2, Wave 1). The previous
/// single-valued <c>Dictionary&lt;string, ElementInfo&gt;</c> silently
/// overwrote a duplicate-name declaration on second-file index and then
/// evicted the SHARED name entirely on the winner's next edit — surfaced
/// by a folder copy+rename refactor in PrettyUi.
///
/// These tests pin the post-Wave-1 contract:
/// <list type="bullet">
///   <item>Two files declaring the same <c>component &lt;Name&gt;</c> both
///   stay visible via <see cref="WorkspaceIndex.GetAllElementInfo"/>.</item>
///   <item><see cref="WorkspaceIndex.GetDuplicateDeclarations"/> reports
///   the conflict so <c>UITKX0113</c> can fire.</item>
///   <item>Deleting one file leaves the other intact (no shared-bucket
///   eviction regression).</item>
///   <item>Editing one file to rename its component preserves the OTHER
///   file's claim on the original name.</item>
/// </list>
/// </summary>
public sealed class WorkspaceIndexDuplicateTests : IDisposable
{
    private readonly string _root;

    public WorkspaceIndexDuplicateTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "UitkxWorkspaceIndexTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string WriteUitkx(string relPath, string body)
    {
        var full = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        return full;
    }

    [Fact]
    public void TwoFilesDeclareSameComponent_BothVisible()
    {
        var idx = new WorkspaceIndex();

        var a = WriteUitkx("a/Foo.uitkx", "VirtualNode Foo() {\n  return ( <Label/> );\n}\n");
        var b = WriteUitkx("b/Foo.uitkx", "VirtualNode Foo() {\n  return ( <Label/> );\n}\n");

        idx.Refresh(a);
        idx.Refresh(b);

        var all = idx.GetAllElementInfo("Foo");
        Assert.Equal(2, all.Count);
        var paths = all.Select(e => e.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(a, paths);
        Assert.Contains(b, paths);
    }

    [Fact]
    public void GetDuplicateDeclarations_ReportsConflict()
    {
        var idx = new WorkspaceIndex();
        var a = WriteUitkx("x/Bar.uitkx", "VirtualNode Bar() {\n  return ( <Label/> );\n}\n");
        var b = WriteUitkx("y/Bar.uitkx", "VirtualNode Bar() {\n  return ( <Label/> );\n}\n");

        idx.Refresh(a);
        idx.Refresh(b);

        var dupes = idx.GetDuplicateDeclarations();
        var bar = dupes.FirstOrDefault(d => d.Name == "Bar");
        Assert.NotNull(bar.FilePaths);
        Assert.Equal(2, bar.FilePaths.Count);
    }

    [Fact]
    public void DeleteOneDuplicate_OtherSurvives()
    {
        // Wave-1 regression guard: pre-fix EvictElementsFromFile dropped
        // the shared name's whole bucket on a single-file delete, so the
        // surviving declarant disappeared from the index.
        var idx = new WorkspaceIndex();
        var a = WriteUitkx("p/Baz.uitkx", "VirtualNode Baz() {\n  return ( <Label/> );\n}\n");
        var b = WriteUitkx("q/Baz.uitkx", "VirtualNode Baz() {\n  return ( <Label/> );\n}\n");

        idx.Refresh(a);
        idx.Refresh(b);

        File.Delete(a);
        idx.Refresh(a);

        var all = idx.GetAllElementInfo("Baz");
        Assert.Single(all);
        Assert.Equal(b, all[0].FilePath, ignoreCase: true);
    }

    [Fact]
    public void EditRenamesOneDuplicate_OtherKeepsOriginalName()
    {
        // Wave-1 regression guard: pre-fix re-indexing a renamed file
        // would evict the shared name from the OTHER file too.
        var idx = new WorkspaceIndex();
        var a = WriteUitkx("m/Qux.uitkx", "VirtualNode Qux() {\n  return ( <Label/> );\n}\n");
        var b = WriteUitkx("n/Qux.uitkx", "VirtualNode Qux() {\n  return ( <Label/> );\n}\n");

        idx.Refresh(a);
        idx.Refresh(b);

        // Edit a to rename component Qux -> Qux2
        File.WriteAllText(a, "VirtualNode Qux2() {\n  return ( <Label/> );\n}\n");
        idx.Refresh(a);

        var qux = idx.GetAllElementInfo("Qux");
        Assert.Single(qux);
        Assert.Equal(b, qux[0].FilePath, ignoreCase: true);

        var qux2 = idx.GetAllElementInfo("Qux2");
        Assert.Single(qux2);
        Assert.Equal(a, qux2[0].FilePath, ignoreCase: true);
    }

    [Fact]
    public void DeleteSoleDeclarant_RemovesNameEntirely()
    {
        var idx = new WorkspaceIndex();
        var a = WriteUitkx("only/Solo.uitkx", "VirtualNode Solo() {\n  return ( <Label/> );\n}\n");
        idx.Refresh(a);
        Assert.Single(idx.GetAllElementInfo("Solo"));

        File.Delete(a);
        idx.Refresh(a);

        Assert.Empty(idx.GetAllElementInfo("Solo"));
        Assert.Null(idx.TryGetElementInfo("Solo"));
    }

    [Fact]
    public void NonDuplicate_DoesNotAppearInGetDuplicateDeclarations()
    {
        var idx = new WorkspaceIndex();
        var a = WriteUitkx("u/Unique.uitkx", "VirtualNode Unique() {\n  return ( <Label/> );\n}\n");
        idx.Refresh(a);

        var dupes = idx.GetDuplicateDeclarations();
        Assert.DoesNotContain(dupes, d => d.Name == "Unique");
    }
}
