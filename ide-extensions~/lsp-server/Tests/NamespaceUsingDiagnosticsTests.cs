using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ruitk.Language;
using Ruitk.Language.Parser;
using UitkxLanguageServer.Roslyn;
using Xunit;

namespace UitkxLanguageServer.Tests;

/// <summary>
/// Editor-tier UITKX2316 (namespace-import unification plan, step 5): the pure core
/// <see cref="RoslynHost.ValidateNamespaceUsings"/> validates <c>@using</c> / <c>import "@Ns"</c>
/// against a real Roslyn compilation. Unlike the build-time SG check (a warning), the editor tier
/// is an Error (a located squiggle) — the feedback the user asked for. Tested with a hand-built
/// compilation so it is decoupled from the workspace plumbing.
/// </summary>
public sealed class NamespaceUsingDiagnosticsTests
{
    // A compilation whose only reference is the core assembly, so System.* namespaces resolve
    // and everything else does not — plus a syntax tree that declares one project namespace.
    private static Compilation BuildComp(string extraCs = "namespace MyGame.Models { class Marker {} }")
    {
        return CSharpCompilation.Create(
            "TestAsm",
            new[] { CSharpSyntaxTree.ParseText(extraCs) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static DirectiveSet Parse(string src)
        => DirectiveParser.Parse(src, "C:/proj/Assets/UI/Screen.uitkx", new List<ParseDiagnostic>());

    [Fact]
    public void MisspelledNamespace_Error_AnchoredAtToken()
    {
        // @using Zzz.Totally.Bogus  →  payload starts at col 7
        var ds = Parse("@namespace TestNs\n@using Zzz.Totally.Bogus\nVirtualNode C() { return (<Box />); }\n");
        var diags = RoslynHost.ValidateNamespaceUsings(BuildComp(), ds);

        var d = Assert.Single(diags);
        Assert.Equal("UITKX2316", d.Code);
        Assert.Equal(ParseSeverity.Error, d.Severity);           // editor tier = error
        Assert.Equal(2, d.SourceLine);
        Assert.Equal(7, d.SourceColumn);                          // anchored at the namespace token
        Assert.Equal(7 + "Zzz.Totally.Bogus".Length, d.EndColumn);
        Assert.Contains("Zzz.Totally.Bogus", d.Message);
    }

    [Fact]
    public void ValidCoreNamespace_NoDiagnostic()
    {
        var ds = Parse("@namespace TestNs\n@using System.Collections.Generic\nVirtualNode C() { return (<Box />); }\n");
        Assert.Empty(RoslynHost.ValidateNamespaceUsings(BuildComp(), ds));
    }

    [Fact]
    public void NamespaceImportForm_AlsoValidated()
    {
        var ds = Parse("@namespace TestNs\nimport \"@Zzz.Bogus\"\nVirtualNode C() { return (<Box />); }\n");
        var d = Assert.Single(RoslynHost.ValidateNamespaceUsings(BuildComp(), ds));
        Assert.Equal("UITKX2316", d.Code);
        // The import form omits the "@using" hint tail from the message.
        Assert.DoesNotContain("remove the @using", d.Message);
    }

    [Fact]
    public void ProjectDeclaredNamespace_Resolves()
    {
        // MyGame.Models exists in the compilation's syntax tree → no 2316.
        var ds = Parse("@namespace TestNs\n@using MyGame.Models\nVirtualNode C() { return (<Box />); }\n");
        Assert.Empty(RoslynHost.ValidateNamespaceUsings(BuildComp(), ds));
    }

    [Fact]
    public void OwnNamespace_NeverFlagged()
    {
        // A file legally using its own (not-yet-compiled) namespace must not be flagged.
        var ds = Parse("@namespace My.Brand.New.Ns\n@using My.Brand.New.Ns\nVirtualNode C() { return (<Box />); }\n");
        Assert.Empty(RoslynHost.ValidateNamespaceUsings(BuildComp("namespace X {}"), ds));
    }

    [Fact]
    public void StaticPayload_NotFlagged()
    {
        var ds = Parse("@namespace TestNs\nimport \"@static Doom.Nope.Type\"\nVirtualNode C() { return (<Box />); }\n");
        Assert.Empty(RoslynHost.ValidateNamespaceUsings(BuildComp(), ds));
    }
}
