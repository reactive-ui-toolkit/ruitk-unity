using System;
using System.IO;
using System.Linq;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ruitk.Language.Parser;
using Ruitk.Language.Lowering;
using Ruitk.Language.Roslyn;
using Ruitk.Language;
using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Ruitk.SourceGenerator.Tests;

public class DebugDumpTest
{
    private readonly ITestOutputHelper _out;
    public DebugDumpTest(ITestOutputHelper output) => _out = output;

    [Fact]
    public void DumpGameOverPageOutput()
    {
        const string uitkxSrc = """
            @namespace UI.App.Pages
            @using UnityEngine.UIElements

            VirtualNode GameOverPage() {
                return (
                    <VisualElement style={Styles.TopContainerSafe}>
                        <Label text="Hello" />
                    </VisualElement>
                );
            }
            """;

        const string companionCs = """
            namespace UI.App.Pages
            {
                public partial class GameOverPage
                {
                    private static class Styles
                    {
                        public static readonly object TopContainerSafe = new object();
                    }
                }
            }
            """;

        // Run with the companion .cs included in the compilation
        var result = GeneratorTestHelper.RunWithExtraCSharp(
            uitkxSrc, companionCs, "GameOverPage.uitkx");

        // Show ALL generated sources
        _out.WriteLine($"Source was produced: {result.SourceWasProduced}");
        _out.WriteLine($"Generated source:\n{result.GeneratedSource ?? "(null)"}");
        _out.WriteLine("=== DIAGNOSTICS ===");
        foreach (var d in result.Diagnostics)
            _out.WriteLine($"  {d.Id} ({d.Severity}): {d.GetMessage()}");

        Assert.True(result.SourceWasProduced, "Expected source to be produced");
        Assert.True(result.SourceContains("partial class GameOverPage"),
            "Expected partial class GameOverPage in generated output");
    }

    [Fact]
    public void DumpVDoc_UitkxCounterFunc()
    {
        var samplesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(DebugDumpTest).Assembly.Location)!,
            "../../../../.."));
        var filePath = Path.Combine(samplesDir,
            "Samples", "Components", "UitkxCounterFunc", "UitkxCounterFunc.uitkx");

        Assert.True(File.Exists(filePath), $"File not found: {filePath}");
        var source = File.ReadAllText(filePath);

        var parseDiags = new System.Collections.Generic.List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(source, filePath, parseDiags);
        var parsedNodes = UitkxParser.Parse(source, filePath, directives, parseDiags);
        var nodes = CanonicalLowering.LowerToRenderRoots(directives, parsedNodes, filePath);
        var parseResult = new ParseResult(directives, nodes,
            ImmutableArray.CreateRange(parseDiags));

        var vdg = new VirtualDocumentGenerator();
        var vdoc = vdg.Generate(parseResult, source, filePath);

        // Write to temp file for inspection
        var outPath = Path.Combine(Path.GetTempPath(), "vdoc_UitkxCounterFunc.txt");
        File.WriteAllText(outPath, vdoc.Text);
        _out.WriteLine($"VDoc written to: {outPath}");
        _out.WriteLine($"VDoc length: {vdoc.Text.Length}");

        // Also output the first section where errors likely appear
        var lines = vdoc.Text.Split('\n');
        for (int i = 0; i < Math.Min(lines.Length, 500); i++)
            _out.WriteLine($"{i + 1,4}: {lines[i]}");

        Assert.NotEmpty(vdoc.Text);
    }

    [Fact]
    public void DumpVDoc_ListViewStatefulDemoFunc()
    {
        var samplesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(DebugDumpTest).Assembly.Location)!,
            "../../../../.."));
        var filePath = Path.Combine(samplesDir,
            "Samples", "Shared", "ListViewStatefulDemoFunc.uitkx");

        Assert.True(File.Exists(filePath), $"File not found: {filePath}");
        var source = File.ReadAllText(filePath);

        var parseDiags = new System.Collections.Generic.List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(source, filePath, parseDiags);
        var parsedNodes = UitkxParser.Parse(source, filePath, directives, parseDiags);
        var nodes = CanonicalLowering.LowerToRenderRoots(directives, parsedNodes, filePath);
        var parseResult = new ParseResult(directives, nodes,
            ImmutableArray.CreateRange(parseDiags));

        var vdg = new VirtualDocumentGenerator();
        var vdoc = vdg.Generate(parseResult, source, filePath);

        var outPath = Path.Combine(Path.GetTempPath(), "vdoc_ListViewStatefulDemoFunc.txt");
        File.WriteAllText(outPath, vdoc.Text);
        _out.WriteLine($"VDoc written to: {outPath}");
        _out.WriteLine($"VDoc length: {vdoc.Text.Length}");

        Assert.NotEmpty(vdoc.Text);
    }

    [Fact]
    public void DumpVDoc_SyntheticEventDemoFunc()
    {
        var samplesDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(DebugDumpTest).Assembly.Location)!,
            "../../../../.."));
        var filePath = Path.Combine(samplesDir,
            "Samples", "Components", "SyntheticEventDemoFunc", "SyntheticEventDemoFunc.uitkx");

        Assert.True(File.Exists(filePath), $"File not found: {filePath}");
        var source = File.ReadAllText(filePath);

        var parseDiags = new System.Collections.Generic.List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(source, filePath, parseDiags);
        var parsedNodes = UitkxParser.Parse(source, filePath, directives, parseDiags);
        var nodes = CanonicalLowering.LowerToRenderRoots(directives, parsedNodes, filePath);
        var parseResult = new ParseResult(directives, nodes,
            ImmutableArray.CreateRange(parseDiags));

        // Dump parse diagnostics
        foreach (var d in parseDiags)
            _out.WriteLine($"PARSE: [{d.Code}] L{d.SourceLine}: {d.Message}");

        var vdg = new VirtualDocumentGenerator();
        var vdoc = vdg.Generate(parseResult, source, filePath);

        var outPath = Path.Combine(Path.GetTempPath(), "vdoc_SyntheticEventDemoFunc.txt");
        File.WriteAllText(outPath, vdoc.Text);
        _out.WriteLine($"VDoc written to: {outPath}");

        Assert.NotEmpty(vdoc.Text);
    }
}
