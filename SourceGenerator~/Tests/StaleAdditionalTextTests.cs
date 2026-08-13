using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// Regression coverage for the "cold-compile staleness" bug: under Unity, a
/// .uitkx-only edit (Play/HMR off) could hand the reused generator driver a STALE
/// <see cref="AdditionalText"/> (old content) while the file on disk already held
/// the edit — the generator trusted the AdditionalText and emitted the pre-edit
/// markup, so the compiled component never reflected the change until a full
/// reimport / Library clear / any .cs edit forced Unity to re-read the file.
///
/// The fix (UitkxGenerator.ReadUitkxSource) makes the on-disk file authoritative:
/// when disk content differs from the provided AdditionalText, disk wins. This test
/// simulates exactly that mismatch — a stale AdditionalText pointing at a fresh disk
/// file — and asserts the generator emits the DISK content.
/// </summary>
public sealed class StaleAdditionalTextTests
{
    private const string StubSource =
        "namespace Ruitk.Core { public abstract class VirtualNode { } }";

    private static string GeneratedFor(AdditionalText addl)
    {
        var stubTree = CSharpSyntaxTree.ParseText(StubSource, path: "_Stubs.g.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName: "Assembly-CSharp",
            syntaxTrees: new[] { stubTree },
            references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver
            .Create(new UitkxGenerator())
            .AddAdditionalTexts(ImmutableArray.Create(addl));
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        var sb = new StringBuilder();
        foreach (var result in driver.GetRunResult().Results)
            foreach (var src in result.GeneratedSources)
                sb.AppendLine(src.SourceText.ToString());
        return sb.ToString();
    }

    private static string Component(string labelText) =>
        "@namespace Ruitk.Samples.FunctionalComponents\n"
        + "\n"
        + "VirtualNode ColdProbe() {\n"
        + "  return (\n"
        + "    <Label text=\"" + labelText + "\" />\n"
        + "  );\n"
        + "}\n";

    [Fact]
    public void DiskContent_WinsOver_StaleAdditionalText()
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "uitkx_stale_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "ColdProbe.uitkx");
        try
        {
            // Disk holds the EDITED content...
            File.WriteAllText(path, Component("VERSION_B_FROM_DISK"));
            // ...but Unity hands the driver a STALE AdditionalText (pre-edit content).
            var staleAddl = new StaleText(path, Component("VERSION_A_STALE"));

            string generated = GeneratedFor(staleAddl);

            Assert.Contains("VERSION_B_FROM_DISK", generated);
            Assert.DoesNotContain("VERSION_A_STALE", generated);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void MatchingContent_IsUnaffected()
    {
        // When disk == AdditionalText (the normal / IDE case), output is the content.
        string dir = Path.Combine(
            Path.GetTempPath(), "uitkx_match_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "ColdProbe.uitkx");
        try
        {
            File.WriteAllText(path, Component("SAME_CONTENT"));
            var addl = new StaleText(path, Component("SAME_CONTENT"));

            string generated = GeneratedFor(addl);

            Assert.Contains("SAME_CONTENT", generated);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class StaleText : AdditionalText
    {
        private readonly string _content;
        public override string Path { get; }
        public StaleText(string path, string content) { Path = path; _content = content; }
        public override SourceText? GetText(CancellationToken ct = default) =>
            SourceText.From(_content, Encoding.UTF8);
    }
}
