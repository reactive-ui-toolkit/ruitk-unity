using System.Collections.Immutable;
using Ruitk.Language.Parser;
using Ruitk.SourceGenerator;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// The hook-container injection seam (plan §6.2, <see cref="UitkxPipeline.ResolveInjectedUsings"/>):
    /// only the container(s) a file actually <c>import</c>s are exposed. (The pre-strict
    /// expose-every-container mode was removed with the dead StrictImports flag branches in the
    /// 0.16.0 legacy-removal wave.)
    /// </summary>
    public sealed class ImportScopedHookInjectionTests
    {
        private const string ScreenPath = "C:/proj/Assets/UI/Screen.uitkx";

        private static ImmutableArray<PeerHookContainerInfo> TwoContainers() => ImmutableArray.Create(
            new PeerHookContainerInfo("NsA", "CounterHooks") { SourceFilePath = "C:/proj/Assets/UI/Counter.hooks.uitkx" },
            new PeerHookContainerInfo("NsB", "OtherHooks") { SourceFilePath = "C:/proj/Assets/UI/Other.hooks.uitkx" });

        private static DirectiveSet ScreenImportingCounter()
        {
            var ds = new DirectiveSet(
                Namespace: "Ruitk.FunctionStyle",
                ComponentName: "Screen",
                PropsTypeName: null,
                DefaultKey: null,
                Usings: ImmutableArray<string>.Empty,
                UssFiles: ImmutableArray<string>.Empty,
                Injects: ImmutableArray<(string Type, string Name)>.Empty,
                MarkupStartLine: 1,
                MarkupStartIndex: 0);
            return ds with
            {
                Imports = ImmutableArray.Create(new ImportDeclaration(
                    ImmutableArray.Create("useCounter"),
                    "./Counter.hooks",
                    1, 0,
                    ImmutableArray.Create(8))),
            };
        }

        [Fact]
        public void ExposesOnlyImportedContainer()
        {
            var usings = UitkxPipeline.ResolveInjectedUsings(
                ScreenImportingCounter(), TwoContainers(), ScreenPath);

            Assert.Contains("static NsA.CounterHooks", usings);
            Assert.DoesNotContain("static NsB.OtherHooks", usings); // unimported container is NOT injected
        }

        [Fact]
        public void CrossNamespaceModule_Aliased_SameNamespace_Not()
        {
            var ds = new DirectiveSet(
                Namespace: "My.Screen",
                ComponentName: "Screen", PropsTypeName: null, DefaultKey: null,
                Usings: ImmutableArray<string>.Empty, UssFiles: ImmutableArray<string>.Empty,
                Injects: ImmutableArray<(string Type, string Name)>.Empty,
                MarkupStartLine: 1, MarkupStartIndex: 0)
            {
                Imports = ImmutableArray.Create(
                    new ImportDeclaration(ImmutableArray.Create("Palette"), "./Palette", 1, 0, ImmutableArray<int>.Empty),
                    new ImportDeclaration(ImmutableArray.Create("Local"), "./Local", 2, 0, ImmutableArray<int>.Empty)),
            };
            var modules = ImmutableArray.Create(
                new PeerModuleInfo("Palette", "Other.Ns", true) { SourceFilePath = "C:/proj/Assets/UI/Palette.uitkx" },
                new PeerModuleInfo("Local", "My.Screen", true) { SourceFilePath = "C:/proj/Assets/UI/Local.uitkx" });

            var usings = UitkxPipeline.ResolveInjectedUsings(ds, null, ScreenPath, modules);

            Assert.Contains("Palette = Other.Ns.Palette", usings);          // cross-namespace → aliased
            Assert.DoesNotContain(usings, u => u.StartsWith("Local =", System.StringComparison.Ordinal)); // same-ns → no alias
        }

        [Fact]
        public void CrossNamespaceModule_NamedLikeBuiltinAlias_NotAliased()
        {
            // A module named after one of the emitter's reserved type aliases (Color,
            // Length, …) must NOT be injected as `using Color = …` — that would be a
            // second alias for `Color` and CS1537. Regression for the module-alias
            // built-in collision found by the emit review.
            var ds = new DirectiveSet(
                Namespace: "My.Screen",
                ComponentName: "Screen", PropsTypeName: null, DefaultKey: null,
                Usings: ImmutableArray<string>.Empty, UssFiles: ImmutableArray<string>.Empty,
                Injects: ImmutableArray<(string Type, string Name)>.Empty,
                MarkupStartLine: 1, MarkupStartIndex: 0)
            {
                Imports = ImmutableArray.Create(
                    new ImportDeclaration(ImmutableArray.Create("Color"), "./Palette", 1, 0, ImmutableArray<int>.Empty)),
            };
            var modules = ImmutableArray.Create(
                new PeerModuleInfo("Color", "Other.Ns", true) { SourceFilePath = "C:/proj/Assets/UI/Palette.uitkx" });

            var usings = UitkxPipeline.ResolveInjectedUsings(ds, null, ScreenPath, modules);

            Assert.DoesNotContain(usings, u => u.StartsWith("Color =", System.StringComparison.Ordinal));
        }

        [Fact]
        public void NoImports_InjectsNothing()
        {
            var ds = new DirectiveSet(
                Namespace: "Ruitk.FunctionStyle",
                ComponentName: "Screen",
                PropsTypeName: null,
                DefaultKey: null,
                Usings: ImmutableArray<string>.Empty,
                UssFiles: ImmutableArray<string>.Empty,
                Injects: ImmutableArray<(string Type, string Name)>.Empty,
                MarkupStartLine: 1,
                MarkupStartIndex: 0);

            var usings = UitkxPipeline.ResolveInjectedUsings(ds, TwoContainers(), ScreenPath);

            Assert.DoesNotContain("static NsA.CounterHooks", usings);
            Assert.DoesNotContain("static NsB.OtherHooks", usings);
        }
    }
}
