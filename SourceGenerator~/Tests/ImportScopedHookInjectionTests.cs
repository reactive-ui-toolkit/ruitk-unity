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

        // (The module-alias injection tests were removed with the legacy module grammar —
        // 0.16.0. Component aliases keep the same ReservedTypeAliases/same-namespace guards,
        // exercised through the pipeline emit tests.)

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
