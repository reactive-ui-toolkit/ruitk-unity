using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Ruitk.SourceGenerator.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Regression: emitting a no-props user component that wraps element children
// must not produce CS8323 ("Named argument 'key' is used out-of-position but
// is followed by an unnamed argument").
//
// Triggering shape (real-world surface: PrettyUi/Assets/UI/AppRoot.uitkx):
//
//     <MenuPage>
//       <HomePage/>
//     </MenuPage>
//
// where MenuPage has zero parameters (no-props branch) and renders {__children}.
//
// The buggy emit was:   V.Func(MenuPage.Render, key: "k", HomePage_call())
// `V.Func`'s signature is  Func(render, IProps props=null, string key=null,
// params VirtualNode[] children).  `key:` lands at call slot 2, but its
// natural slot is 3 (slot 2 is `props`).  C# 7.2 non-trailing named
// argument rules require the named arg to sit in its natural slot when
// followed by positional arguments → CS8323.
//
// This test reproduces the shape, generates the C#, and then COMPILES the
// generated source against a real-shape `V.Func` stub.  It asserts no CS8323
// is produced.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class NoPropsFuncWithChildrenRegressionTest
{
    private readonly ITestOutputHelper _out;
    public NoPropsFuncWithChildrenRegressionTest(ITestOutputHelper output) => _out = output;

    // Minimal shape-faithful stub of the API the generator emits against.
    // Must mirror Shared/Core/V.cs `Func` (no-props overload) exactly so that
    // overload resolution + CS8323 analysis behaves like the real build.
    private const string ApiStub = """
        using System.Collections.Generic;

        namespace Ruitk.Core
        {
            public abstract class VirtualNode { }
            public interface IProps { }
            public static class EmptyProps { public static readonly IProps Instance = null; }
        }

        namespace Ruitk.Hooks
        {
            // Surface used by generated setup code (none here, but keeps emitter happy
            // if its preamble imports change).
        }

        namespace Ruitk
        {
            using Ruitk.Core;

            public static class V
            {
                // No-props overload — the one exercised by the bug.
                public static VirtualNode Func(
                    System.Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode> render,
                    IProps props = null,
                    string key = null,
                    params VirtualNode[] children
                ) => null;

                // Typed-props overload — also reachable from the emitter.
                public static VirtualNode Func<TProps>(
                    System.Func<IProps, IReadOnlyList<VirtualNode>, VirtualNode> render,
                    TProps typedProps,
                    string key = null,
                    params VirtualNode[] children
                ) where TProps : class, IProps => null;

                // Used by the emitter for fragments / wrappers in some shapes.
                public static VirtualNode Fragment(string key = null, params VirtualNode[] children) => null;
            }

            // __C is emitted by the generator for non-simple children; not used by
            // the simple-child fast path under test, but include a no-op stub so the
            // generated source still compiles in either branch.
            public static class GeneratedHelpers
            {
                public static VirtualNode[] __C(params object[] children) => System.Array.Empty<VirtualNode>();
            }
        }

        // Stubs of the user partial classes so that Inner.Render / Leaf.Render
        // resolve in the generated Outer source. Must match the generator's
        // expected static Render signature: (IProps, IReadOnlyList<VirtualNode>) -> VirtualNode.
        namespace UI.App
        {
            using System.Collections.Generic;
            using Ruitk.Core;

            public static class Inner
            {
                public static VirtualNode Render(IProps p, IReadOnlyList<VirtualNode> c) => null;
            }
            public static class Leaf
            {
                public static VirtualNode Render(IProps p, IReadOnlyList<VirtualNode> c) => null;
            }
        }
        """;

    [Fact]
    public void NoPropsComponent_WithElementChild_DoesNotProduceCS8323()
    {
        // Two .uitkx files: outer wraps inner (the PrettyUi MenuPage/HomePage shape).
        const string outerSrc = """
            @namespace UI.App

            VirtualNode Outer() {
                return (
                    <Inner>
                        <Leaf/>
                    </Inner>
                );
            }
            """;

        const string innerSrc = """
            @namespace UI.App

            VirtualNode Inner() {
                return (
                    <VisualElement>
                        {__children}
                    </VisualElement>
                );
            }
            """;

        const string leafSrc = """
            @namespace UI.App

            VirtualNode Leaf() {
                return (
                    <Label text="leaf"/>
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[]
            {
                ("Inner.uitkx", innerSrc),
                ("Leaf.uitkx", leafSrc),
                ("Outer.uitkx", outerSrc),
            },
            primaryFileName: "Outer.uitkx"
        );

        Assert.True(
            result.SourceWasProduced,
            $"Generator produced no source. Diagnostics:\n  {string.Join("\n  ", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}"))}"
        );

        _out.WriteLine("=== GENERATED Outer.uitkx ===");
        _out.WriteLine(result.GeneratedSource ?? "(null)");

        // Compile the generated source against the API stub and check for CS8323.
        var stubTree = CSharpSyntaxTree.ParseText(ApiStub, path: "_ApiStub.cs");
        var genTree = CSharpSyntaxTree.ParseText(result.GeneratedSource!, path: "Outer.g.cs");

        var compilation = CSharpCompilation.Create(
            assemblyName: "RegressionCheck",
            syntaxTrees: new[] { stubTree, genTree },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Immutable.ImmutableArray).Assembly.Location),
                MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
            },
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                // Most CS errors will fire (missing types from full Unity surface);
                // the assertion below filters specifically for CS8323.
                reportSuppressedDiagnostics: false
            )
        );

        var diags = compilation.GetDiagnostics();
        var cs8323 = diags
            .Where(d => d.Id == "CS8323" && d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        if (cs8323.Length > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Generated source produced CS8323 — emitter regression.");
            sb.AppendLine("-- CS8323 diagnostics --");
            foreach (var d in cs8323)
                sb.AppendLine($"  {d.Location.GetLineSpan()}: {d.GetMessage()}");
            sb.AppendLine();
            sb.AppendLine("-- Generated source --");
            sb.AppendLine(result.GeneratedSource);
            Assert.Fail(sb.ToString());
        }
    }
}
