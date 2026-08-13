using System;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ruitk.Language;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

// UPM package layout (dev repo consumed via "file:"/git): the assembly's sources —
// and its .uitkx files — physically live OUTSIDE the host project's Assets folder,
// so the generator's Assets-rooted fallback disk scan finds zero files and every
// generated component type silently vanishes (CS0234 in the assembly's consumers;
// the "Found 0 .uitkx file(s) via disk scan" failure mode). The fix derives a second
// scan root from the compilation's own syntax trees: the directory of the .asmdef
// whose "name" equals the compilation's assembly name
// (UitkxPipeline.FindCompilationAsmdefRoot). These tests pin:
//
//   1. the package layout resolves the asmdef directory as a scan root,
//   2. classic Assets layouts resolve NO extra root (Assembly-CSharp) — the
//      pre-fix discovery set stays byte-identical,
//   3. a nearest-asmdef name mismatch never vouches for a root,
//   4. namespace derivation is layout-INDEPENDENT: the same asmdef + config
//      prefix yields the identical namespace embedded under Assets and at a
//      package root (generated namespaces are public API — consumers compile
//      against them).
public class PackageLayoutDiscoveryTests
{
    private static string NewTempDir(string name)
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "uitkx_pkg_layout", name + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Compilation CompilationWithTree(string assemblyName, string treePath)
    {
        var tree = CSharpSyntaxTree.ParseText("internal static class __Probe { }", path: treePath);
        return CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: new[] { tree },
            references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Fact]
    public void PackageOutsideAssets_ResolvesOwningAsmdefDirAsScanRoot()
    {
        // Mirrors the real layout: <pkg>/Samples/Ruitk.Samples.asmdef with sources below it,
        // no "Assets" segment anywhere in the path.
        string pkg = NewTempDir("pkg");
        string samplesDir = Path.Combine(pkg, "ruitk-unity", "Samples");
        string srcDir = Path.Combine(samplesDir, "Components", "MarioGame");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(
            Path.Combine(samplesDir, "Ruitk.Samples.asmdef"),
            "{ \"name\": \"Ruitk.Samples\" }");

        var compilation = CompilationWithTree(
            "Ruitk.Samples", Path.Combine(srcDir, "MarioGameTypes.cs"));

        Assert.Equal(samplesDir, UitkxPipeline.FindCompilationAsmdefRoot(compilation));
    }

    [Fact]
    public void AssetsResident_NoAsmdef_ResolvesNoExtraRoot()
    {
        // Assembly-CSharp under Assets: the walk stops at the Assets boundary without an
        // asmdef, so no extra scan root is added — classic discovery is untouched.
        string proj = NewTempDir("proj");
        string scriptsDir = Path.Combine(proj, "Assets", "Scripts");
        Directory.CreateDirectory(scriptsDir);

        var compilation = CompilationWithTree(
            "Assembly-CSharp", Path.Combine(scriptsDir, "Game.cs"));

        Assert.Null(UitkxPipeline.FindCompilationAsmdefRoot(compilation));
    }

    [Fact]
    public void NearestAsmdefNameMismatch_ResolvesNoRoot()
    {
        // The nearest asmdef belongs to a DIFFERENT assembly — it must not vouch for
        // this compilation's root (same nearest-asmdef rule as IsOwnedByCompilation).
        string pkg = NewTempDir("mismatch");
        string otherDir = Path.Combine(pkg, "Other");
        string srcDir = Path.Combine(otherDir, "Sub");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(
            Path.Combine(otherDir, "Some.Other.asmdef"),
            "{ \"name\": \"Some.Other\" }");

        var compilation = CompilationWithTree(
            "Ruitk.Samples", Path.Combine(srcDir, "File.cs"));

        Assert.Null(UitkxPipeline.FindCompilationAsmdefRoot(compilation));
    }

    [Fact]
    public void PathlessAndHintNameTrees_NeverResolveARoot_OrCrash()
    {
        // In-memory trees carry an empty FilePath (skipped outright); hint-style
        // relative names are resolved against the CWD (the same rule the generator's
        // project-root strategy 2 already applies to relative Compile paths) and find
        // no matching asmdef. Either way: null, no crash.
        var pathless = CSharpSyntaxTree.ParseText("class __A { }", path: "");
        var hintName = CSharpSyntaxTree.ParseText("class __B { }", path: "UITKX_Loaded.g.cs");
        var compilation = CSharpCompilation.Create(
            "Ruitk.Samples",
            syntaxTrees: new[] { pathless, hintName },
            references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Null(UitkxPipeline.FindCompilationAsmdefRoot(compilation));
    }

    [Fact]
    public void DiskScanFallback_GeneratesComponent_FromPackageResidentUitkx()
    {
        // End-to-end pin of the 294xCS0234 failure mode: NO AdditionalTexts (Unity's
        // in-editor compile never injects them), package .uitkx outside any Assets
        // folder — the fallback disk scan must find it via the compilation's asmdef
        // root and emit the component in its config-prefixed, path-derived namespace.
        string pkg = NewTempDir("e2e");
        string samplesDir = Path.Combine(pkg, "ruitk-unity", "Samples");
        string compDir = Path.Combine(samplesDir, "Components", "HelloPkg");
        Directory.CreateDirectory(compDir);
        File.WriteAllText(
            Path.Combine(samplesDir, "Ruitk.Samples.asmdef"),
            "{ \"name\": \"Ruitk.Samples\" }");
        File.WriteAllText(
            Path.Combine(samplesDir, "uitkx.config.json"),
            "{ \"namespacePrefix\": \"Ruitk.Samples\" }");
        File.WriteAllText(
            Path.Combine(compDir, "HelloPkg.uitkx"),
            "export VirtualNode HelloPkg() {\n  return (<Box />);\n}");

        // The VirtualNode stub satisfies pipeline Guard 1; its tree path (inside the
        // package) is what FindCompilationAsmdefRoot derives the scan root from.
        var stubTree = CSharpSyntaxTree.ParseText(
            "namespace Ruitk.Core { public abstract class VirtualNode { } }",
            path: Path.Combine(compDir, "HelloPkgTypes.cs"));
        var compilation = CSharpCompilation.Create(
            "Ruitk.Samples",
            syntaxTrees: new[] { stubTree },
            references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = (CSharpGeneratorDriver)CSharpGeneratorDriver
            .Create(new UitkxGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        var runResult = driver.GetRunResult();

        bool found = false;
        foreach (var r in runResult.Results)
            foreach (var src in r.GeneratedSources)
                if (src.SourceText.ToString()
                        .Contains("namespace Ruitk.Samples.Components.HelloPkg"))
                    found = true;

        Assert.True(found,
            "Expected the disk-scan fallback to discover the package-resident .uitkx "
            + "and emit it under Ruitk.Samples.Components.HelloPkg.*");
    }

    [Fact]
    public void NamespaceDerivation_IsIdentical_EmbeddedUnderAssets_And_AtPackageRoot()
    {
        // Generated namespaces are public API: the SAME asmdef + uitkx.config.json
        // ("namespacePrefix": "Ruitk.Samples") must yield byte-identical namespaces
        // whether the package sits under Assets (embedded era / Asset Store channel)
        // or at an external package root (UPM "file:"/git channel).
        string embeddedRoot = NewTempDir("embedded");
        string packageRoot = NewTempDir("package");

        string relAsmdefDir = "Samples";
        string relFile = Path.Combine(
            "Samples", "Components", "MarioGame", "components", "Block", "Block.uitkx");

        string embeddedBase = Path.Combine(embeddedRoot, "Host", "Assets", "ReactiveUIToolKit");
        string packageBase = Path.Combine(packageRoot, "ruitk-unity");

        foreach (string baseDir in new[] { embeddedBase, packageBase })
        {
            string asmdefDir = Path.Combine(baseDir, relAsmdefDir);
            string filePath = Path.Combine(baseDir, relFile);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            Directory.CreateDirectory(asmdefDir);
            File.WriteAllText(
                Path.Combine(asmdefDir, "Ruitk.Samples.asmdef"),
                "{ \"name\": \"Ruitk.Samples\" }");
            File.WriteAllText(
                Path.Combine(asmdefDir, "uitkx.config.json"),
                "{\n  \"namespacePrefix\": \"Ruitk.Samples\"\n}\n");
        }

        string? embeddedNs = EffectiveNamespace.Resolve(
            hasExplicitNamespace: false, rawNamespace: null,
            Path.Combine(embeddedBase, relFile), fileKeyed: true);
        string? packageNs = EffectiveNamespace.Resolve(
            hasExplicitNamespace: false, rawNamespace: null,
            Path.Combine(packageBase, relFile), fileKeyed: true);

        Assert.Equal("Ruitk.Samples.Components.MarioGame.components.Block.Block", embeddedNs);
        Assert.Equal(embeddedNs, packageNs);

        // Folder-keyed (legacy-syntax) derivation must agree across layouts too.
        string? embeddedFolderNs = EffectiveNamespace.Resolve(
            hasExplicitNamespace: false, rawNamespace: null,
            Path.Combine(embeddedBase, relFile), fileKeyed: false);
        string? packageFolderNs = EffectiveNamespace.Resolve(
            hasExplicitNamespace: false, rawNamespace: null,
            Path.Combine(packageBase, relFile), fileKeyed: false);

        Assert.Equal("Ruitk.Samples.Components.MarioGame.components.Block", embeddedFolderNs);
        Assert.Equal(embeddedFolderNs, packageFolderNs);
    }
}
