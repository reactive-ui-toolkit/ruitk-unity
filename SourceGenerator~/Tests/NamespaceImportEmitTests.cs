using System.Linq;
using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// Namespace-import unification plan, step 3 — emit equivalence. `import "@Ns"` desugars in the
    /// parser to the same <c>DirectiveSet.Usings</c> entry a <c>@using Ns</c> line produces, so the
    /// generated C# must be byte-identical between the two spellings. This is the load-bearing
    /// guarantee that lets the new syntax ship without touching any emitter or the HMR path.
    /// </summary>
    public sealed class NamespaceImportEmitTests
    {
        // NOTE: the test compilation references only the core assembly, so namespaces used here
        // must exist there (System.Text, System.Collections.Generic) — see UITKX2316 (unknown
        // namespace) which is now live and would flag e.g. UnityEngine.* in this minimal setup.

        [Fact]
        public void NamespaceImport_And_AtUsing_ProduceIdenticalComponentSource()
        {
            const string withAtUsing =
                "@namespace TestNs\n@using System.Text\nVirtualNode MyComp() {\n  return (<VisualElement />);\n}";
            const string withNsImport =
                "@namespace TestNs\nimport \"@System.Text\"\nVirtualNode MyComp() {\n  return (<VisualElement />);\n}";

            var a = GeneratorTestHelper.Run(withAtUsing);
            var b = GeneratorTestHelper.Run(withNsImport);

            Assert.True(a.SourceWasProduced);
            Assert.True(b.SourceWasProduced);
            Assert.Equal(a.GeneratedSource, b.GeneratedSource);
            // And the namespace actually made it into the emitted usings
            // (inside-namespace, global::-qualified — 0.16.0).
            Assert.Contains("using global::System.Text;", b.GeneratedSource!);
        }

        [Fact]
        public void NamespaceImport_And_AtUsing_ProduceIdenticalHookSource()
        {
            const string withAtUsing =
                "@namespace TestNs\n@using System.Text\nexport int useFoo(int initial = 0) {\n  return initial;\n}";
            const string withNsImport =
                "@namespace TestNs\nimport \"@System.Text\"\nexport int useFoo(int initial = 0) {\n  return initial;\n}";

            var a = GeneratorTestHelper.Run(withAtUsing, "UseFoo.uitkx");
            var b = GeneratorTestHelper.Run(withNsImport, "UseFoo.uitkx");

            Assert.True(a.SourceWasProduced);
            Assert.True(b.SourceWasProduced);
            Assert.Equal(a.GeneratedSource, b.GeneratedSource);
        }

        [Fact]
        public void NamespaceImport_StaticPayload_EmitsUsingStatic()
        {
            const string src =
                "@namespace TestNs\nimport \"@static System.Math\"\nVirtualNode MyComp() {\n  return (<VisualElement />);\n}";

            var result = GeneratorTestHelper.Run(src);

            Assert.True(result.SourceWasProduced);
            // 0.16.0: usings emit inside the namespace block, global::-qualified.
            Assert.Contains("using static global::System.Math;", result.GeneratedSource!);
        }

        [Fact]
        public void RouterNamespace_AutoInjected_NoImportNeeded()
        {
            // Feature 2: Ruitk.Router is in the baseline, so RouterHooks resolves with no
            // @using/import at all — and the generated file carries the using.
            const string src =
                "@namespace TestNs\nVirtualNode Screen() {\n  var nav = RouterHooks.UseNavigate();\n  return (<VisualElement />);\n}";

            var result = GeneratorTestHelper.Run(src);

            Assert.True(result.SourceWasProduced);
            Assert.Contains("using Ruitk.Router;", result.GeneratedSource!);
            // No unknown-namespace / unresolved-hook diagnostics from the bare RouterHooks call.
            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX2316" || d.Id == "UITKX2307");
        }

        [Fact]
        public void RouterImport_NowRedundant_Emits2317Hint_ViaTidyRule()
        {
            // Since Router is auto-injected, an explicit import "@Ruitk.Router" is redundant —
            // AutoInjectedUsings.IsRedundant reports it (drives the editor 2317 hint + --tidy strip).
            Assert.True(Ruitk.Language.AutoInjectedUsings.IsRedundant("Ruitk.Router"));
        }

        [Fact]
        public void RouterComponentTags_ResolveProps_WithNoImport()
        {
            // Regression (Publish #105 floor-Unity failure): the resolver's search-namespace list was
            // usings-only, so when the samples dropped `import "@Ruitk.Router"` (now baseline),
            // <Route path element> stopped resolving RouteFuncProps → UITKX0008 warnings + false
            // UITKX0109 errors, even though the EMITTED C# resolved fine via the baseline using.
            // BuildSearchNamespaces must include the auto-injected baseline. This mirrors the exact
            // failing shape: Router types in the real Ruitk.Router namespace, a <RouteFunc>
            // tag with attributes, and ZERO usings/imports in the .uitkx.
            const string extraCSharp = """
                using Ruitk.Core;
                using System.Collections.Generic;

                namespace Ruitk.Router
                {
                    public static class RouteFunc
                    {
                        public static VirtualNode Render(IProps rawProps, IReadOnlyList<VirtualNode> children)
                            => null;
                    }

                    public sealed class RouteFuncProps : IProps
                    {
                        public string Path { get; set; }
                        public object Element { get; set; }
                    }
                }
                """;

            const string uitkx = """
                @namespace MyApp.UI

                VirtualNode MyPage() {
                    return (<RouteFunc path="/home" element={null} />);
                }
                """;

            var result = GeneratorTestHelper.RunWithExtraCSharp(uitkx, extraCSharp, "MyPage.uitkx");

            Assert.True(result.SourceWasProduced,
                $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics)}");
            // No false UITKX0109 (unknown attribute) — Path/Element resolve via the baseline using.
            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX0109");
            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX0008");
            Assert.True(result.SourceContains("V.Func<global::Ruitk.Router.RouteFuncProps>"),
                $"Expected typed V.Func via the baseline namespace. Got:\n{result.GeneratedSource}");
            Assert.True(result.SourceContains("Path = \"/home\""),
                $"Expected Path prop forwarded. Got:\n{result.GeneratedSource}");
        }

        [Fact]
        public void RouterAliasTags_ResolveProps_WithNoImport()
        {
            // Same regression as above but through the ALIAS path the real samples use
            // (<Router>/<Routes>/<Route> → RouterTagAliases → *Func) — mirrors AppRoot.uitkx.
            const string extraCSharp = """
                using Ruitk.Core;
                using System.Collections.Generic;

                namespace Ruitk.Router
                {
                    public static class RouterFunc
                    {
                        public static VirtualNode Render(IProps rawProps, IReadOnlyList<VirtualNode> children) => null;
                    }
                    public sealed class RouterFuncProps : IProps
                    {
                        public string InitialPath { get; set; }
                    }
                    public static class RoutesFunc
                    {
                        public static VirtualNode Render(IProps rawProps, IReadOnlyList<VirtualNode> children) => null;
                    }
                    public static class RouteFunc
                    {
                        public static VirtualNode Render(IProps rawProps, IReadOnlyList<VirtualNode> children) => null;
                    }
                    public sealed class RouteFuncProps : IProps
                    {
                        public string Path { get; set; }
                        public object Element { get; set; }
                    }
                }
                """;

            const string uitkx = """
                @namespace MyApp.UI

                VirtualNode AppShell() {
                    return (
                        <Router initialPath="/home">
                            <Routes>
                                <Route path="/home" element={null} />
                            </Routes>
                        </Router>
                    );
                }
                """;

            var result = GeneratorTestHelper.RunWithExtraCSharp(uitkx, extraCSharp, "AppShell.uitkx");

            Assert.True(result.SourceWasProduced,
                $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics)}");
            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX0109");
            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX0008");
            Assert.True(result.SourceContains("Path = \"/home\""),
                $"Expected Path prop forwarded through the <Route> alias. Got:\n{result.GeneratedSource}");
        }

        [Fact]
        public void FileImportedComponent_CrossNamespace_AttributesValidate_WithoutNamespaceImport()
        {
            // Regression (JSO @namespace cleanup): an `import { X } from "./file"` names the exact
            // target file, so the peer's namespace must be visible to tag/props resolution ON ITS
            // OWN. Previously TryFindVisiblePeerComponent searched only {@namespace + usings +
            // baseline}, so a cross-namespace file-imported component silently required an EXTRA
            // `import "@Target.Ns"` line — without it, every attribute produced a false UITKX0109
            // ("Component declares no parameters"). The samples masked this by carrying
            // namespace-imports (e.g. PrettyUi AppRoot's import "@PrettyUi.App.Pages").
            var files = new[]
            {
                ("Widget.uitkx",
                    "@namespace My.Widgets\nexport VirtualNode Widget(string title = \"\", bool busy = false) {\n  return (<VisualElement />);\n}"),
                ("Screen.uitkx",
                    "@namespace My.Screens\nimport { Widget } from \"./Widget\"\nVirtualNode Screen() {\n  return (<Widget title=\"hi\" busy={true} />);\n}"),
            };

            var result = GeneratorTestHelper.RunMultiple(files, "Screen.uitkx");

            Assert.True(result.SourceWasProduced,
                $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics)}");
            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX0109");
            Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX0008");
            Assert.True(result.SourceContains("global::My.Widgets.Widget"),
                $"Expected the imported component emitted by FQN. Got:\n{result.GeneratedSource}");
        }

        [Fact]
        public void FileImportedComponent_CrossNamespace_GetsTypeAlias_ForCSharpBodyReferences()
        {
            // Regression (JSO Dialogs/TableView): C# BODY references to a file-imported component's
            // type (`GameOverDialogPreset.GameOverDialogPresetProps`, `TableView.Column`) got no
            // using — component imports only FQN-qualify TAGS — so a cross-namespace import hit
            // CS0246 in generated code. The §6 injection seam now aliases imported components
            // exactly like modules: `using Widget = My.Widgets.Widget;`.
            var files = new[]
            {
                ("Widget.uitkx",
                    "@namespace My.Widgets\nexport VirtualNode Widget(string title = \"\") {\n  return (<VisualElement />);\n}"),
                ("Panel.utils.uitkx",
                    "@namespace My.Screens\nimport { Widget } from \"./Widget\"\nexport object MakeProps() => new Widget.WidgetProps { Title = \"x\" };"),
            };

            var result = GeneratorTestHelper.RunMultiple(files, "Panel.utils.uitkx");

            Assert.True(result.SourceWasProduced,
                $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics)}");
            Assert.True(result.SourceContains("using Widget = global::My.Widgets.Widget;"),
                $"Expected the imported component aliased for C# body references. Got:\n{result.GeneratedSource}");
        }

        [Fact]
        public void FileImportedComponent_SameNamespace_NoAlias()
        {
            // Same-namespace alias would be CS0576 (conflicts with the declaration) — mirror the
            // module guard.
            var files = new[]
            {
                ("Widget.uitkx",
                    "@namespace My.Ns\nexport VirtualNode Widget(string title = \"\") {\n  return (<VisualElement />);\n}"),
                ("Panel.utils.uitkx",
                    "@namespace My.Ns\nimport { Widget } from \"./Widget\"\nexport module Panel {\n  public static object MakeProps() => new Widget.WidgetProps { Title = \"x\" };\n}"),
            };

            var result = GeneratorTestHelper.RunMultiple(files, "Panel.utils.uitkx");

            Assert.True(result.SourceWasProduced);
            Assert.False(result.SourceContains("using Widget = My.Ns.Widget;"),
                "Same-namespace component must NOT be aliased (CS0576).");
        }

        [Fact]
        public void NamespaceImport_NoDiagnosticsForValidNamespace()
        {
            const string src =
                "@namespace TestNs\nimport \"@System.Text\"\nVirtualNode MyComp() {\n  return (<VisualElement />);\n}";

            var result = GeneratorTestHelper.Run(src);

            // Legacy `component` wrapper keyword deprecation warning (UITKX2320, ES-modules
            // campaign G-10) is expected — this test is about namespace-import diagnostics
            // (2300-2317), not the unrelated 2320-2327 ES-modules band that happens to share
            // the "UITKX23" string prefix.
            Assert.DoesNotContain(result.Diagnostics, d => d.Id.StartsWith("UITKX23") && d.Id != "UITKX2320");
        }
    }
}
