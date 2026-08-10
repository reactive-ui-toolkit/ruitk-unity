using System.Linq;
using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// ES-modules campaign (Plans~/archive/ES_MODULES_EXECUTION_PLAN.md M3, U-02/U-03/§4.2): the SG's
    /// __Exports emission for new-mode (plain-declaration) files — member shapes, accessibility,
    /// hook trampolines + refresh companion family keys, import lowering (static container /
    /// aliases / bridges), dotted tags, and the deprecation-window guarantees (legacy files emit
    /// with 2320/2107 warnings and nothing else changed).
    /// </summary>
    public sealed class ExportsEmitterTests
    {
        // ── Member-only file (short-circuit path) ────────────────────────────

        [Fact]
        public void MemberOnlyFile_EmitsExportsContainer()
        {
            var result = GeneratorTestHelper.Run(
                "@namespace Test.Ns\n" +
                "export int MaxItems = 5;\n" +
                "export string FormatScore(int score) { return $\"Score: {score}\"; }\n" +
                "int hidden = 1;\n",
                "Scoring.uitkx");

            Assert.True(result.SourceWasProduced);
            Assert.True(result.SourceContains("namespace Test.Ns"));
            Assert.True(result.SourceContains("public static partial class __Exports"));
            Assert.True(result.SourceContains("public static int MaxItems = 5"));
            Assert.True(result.SourceContains("internal static int hidden = 1"));
            Assert.Empty(result.SyntaxErrors());
        }

        [Fact]
        public void GenericHook_EmitsMethodInfoTrampoline()
        {
            // F9 (0.16.0): a generic hook flows through the same MethodInfo-cache trampoline
            // the legacy generic-hook path used — open MethodInfo field + per-closed-type
            // delegate cache + MakeGenericMethod, with the generic suffix on the public head.
            var result = GeneratorTestHelper.Run(
                "export (T value, System.Action<T> set) useSlot<T>(T seed) {\n" +
                "  return (seed, _ => {});\n" +
                "}\n",
                "Slot.hooks.uitkx");

            Assert.True(result.SourceWasProduced);
            Assert.True(result.SourceContains("useSlot<T>(T seed)"));
            Assert.True(result.SourceContains("__hmr_useSlot_cache"));
            Assert.True(result.SourceContains("MakeGenericMethod"));
            Assert.Empty(result.SyntaxErrors());
        }

        [Fact]
        public void GenericUtil_EmitsGenericMethod()
        {
            var result = GeneratorTestHelper.Run(
                "export System.Collections.Generic.List<T> repeat<T>(T item, int count) {\n" +
                "  var list = new System.Collections.Generic.List<T>();\n" +
                "  for (int i = 0; i < count; i++) list.Add(item);\n" +
                "  return list;\n" +
                "}\n",
                "Utils.uitkx");

            Assert.True(result.SourceWasProduced);
            Assert.True(result.SourceContains("repeat<T>(T item, int count)"));
            Assert.Empty(result.SyntaxErrors());
        }

        [Fact]
        public void ValueExport_GetsUitkxHmrSwapAttribute()
        {
            // U-02: values synthesize as `static readonly` and route through the
            // StaticReadonlyStripper so HMR refreshes the slot on every edit (readonly
            // stripped + [UitkxHmrSwap] stamped) — the legacy module static-readonly behavior.
            var result = GeneratorTestHelper.Run(
                "@namespace Test.Ns\nexport int MaxItems = 5;\n", "Tokens.uitkx");

            Assert.True(result.SourceContains("UitkxHmrSwap"));
            Assert.False(result.SourceContains("readonly int MaxItems"));
        }

        [Fact]
        public void InferredValue_TypeExtractedFromNewInitializer()
        {
            var result = GeneratorTestHelper.Run(
                "@namespace Test.Ns\nexport theme = new System.Text.StringBuilder(12);\n",
                "Theme.uitkx");

            Assert.True(result.SourceContains("static System.Text.StringBuilder theme = new System.Text.StringBuilder(12)"));
            Assert.Empty(result.SyntaxErrors());
        }

        [Fact]
        public void UtilExport_GetsHmrTrampolineShape()
        {
            var result = GeneratorTestHelper.Run(
                "@namespace Test.Ns\nexport string FormatScore(int score) { return $\"S{score}\"; }\n",
                "Scoring.uitkx");

            // ModuleBodyRewriter's swappable-method shape: delegate field + trampoline + body.
            Assert.True(result.SourceContains("__hmr_FormatScore"));
            Assert.Empty(result.SyntaxErrors());
        }

        [Fact]
        public void HookExport_TrampolineAndRefreshCompanion_WithExportsFamilyKey()
        {
            var result = GeneratorTestHelper.Run(
                "@namespace Test.Ns\n" +
                "export (int value, System.Action reset) useCountdown(int start) {\n" +
                "  return (start, null);\n" +
                "}\n",
                "Countdown.uitkx");

            Assert.True(result.SourceContains("public static (int value, System.Action reset) useCountdown(int start)"));
            Assert.True(result.SourceContains("__Exports__UitkxHookRefresh"));
            // U-06: family key = {ns}.__Exports::{hook}.
            Assert.True(result.SourceContains("Test.Ns.__Exports::useCountdown"));
            Assert.Empty(result.SyntaxErrors());
        }

        [Fact]
        public void PrivateHook_EmitsInternalTrampoline()
        {
            var result = GeneratorTestHelper.Run(
                "@namespace Test.Ns\n" +
                "(int value, System.Action reset) useLocal(int start) {\n  return (start, null);\n}\n",
                "Local.uitkx");

            Assert.True(result.SourceContains("internal static (int value, System.Action reset) useLocal(int start)"));
        }

        // ── Mixed file: components + members ────────────────────────────────

        [Fact]
        public void MixedFile_ComponentAndMembers_EmitsBothUnits_WithOwnExportsUsing()
        {
            var result = GeneratorTestHelper.Run(
                "@namespace Test.Ns\n" +
                "export Style container = new Style { };\n" +
                "export VirtualNode Panel(string title) {\n  return (<VisualElement />);\n}\n",
                "Panel.uitkx");

            Assert.True(result.AllSources.Any(s => s.Text.Contains("partial class Panel")));
            Assert.True(result.AllSources.Any(s => s.Text.Contains("public static partial class __Exports")));
            // U-02: every new-syntax file's own generated sources see their own members bare-name.
            Assert.True(result.AllSources.Any(s =>
                s.Text.Contains("partial class Panel") && s.Text.Contains("using static global::Test.Ns.__Exports;")));
            Assert.Empty(result.SyntaxErrors());
        }

        [Fact]
        public void ExportedComponent_Public_PrivateComponent_Internal()
        {
            var result = GeneratorTestHelper.Run(
                "@namespace Test.Ns\n" +
                "export VirtualNode Shown() {\n  return (<VisualElement />);\n}\n" +
                "VirtualNode Hidden() {\n  return (<VisualElement />);\n}\n",
                "Two.uitkx");

            Assert.True(result.AllSources.Any(s => s.Text.Contains("public partial class Shown")));
            Assert.True(result.AllSources.Any(s => s.Text.Contains("internal partial class Hidden")));
            Assert.Empty(result.SyntaxErrors());
        }

        // ── Import lowering (U-03) ───────────────────────────────────────────

        [Fact]
        public void MemberImport_InjectsStaticExportsContainer()
        {
            var result = GeneratorTestHelper.RunMultiple(
                new[]
                {
                    ("Tokens.uitkx", "@namespace Toks.Ns\nexport int Gap = 8;\n"),
                    ("Home.uitkx",
                        "import { Gap } from \"./Tokens\"\n" +
                        "export VirtualNode Home() {\n  return (<Label text={$\"{Gap}\"} />);\n}\n"),
                },
                "Home.uitkx");

            Assert.True(result.AllSources.Any(s =>
                s.Text.Contains("partial class Home") && s.Text.Contains("using static global::Toks.Ns.__Exports;")));
            Assert.Empty(result.SyntaxErrors());
        }

        [Fact]
        public void StarImport_InjectsExportsAlias()
        {
            var result = GeneratorTestHelper.RunMultiple(
                new[]
                {
                    ("Tokens.uitkx", "@namespace Toks.Ns\nexport int Gap = 8;\n"),
                    ("Home.uitkx",
                        "import * as Tokens from \"./Tokens\"\n" +
                        "export VirtualNode Home() {\n  return (<Label text={$\"{Tokens.Gap}\"} />);\n}\n"),
                },
                "Home.uitkx");

            Assert.True(result.AllSources.Any(s =>
                s.Text.Contains("partial class Home") && s.Text.Contains("using Tokens = global::Toks.Ns.__Exports;")));
        }

        [Fact]
        public void RenamedMemberImport_EmitsBridgeIntoConsumersExports()
        {
            var result = GeneratorTestHelper.RunMultiple(
                new[]
                {
                    ("Scoring.uitkx", "@namespace Sco.Ns\nexport string FormatScore(int score) { return \"\"; }\n"),
                    ("Home.uitkx",
                        "import { FormatScore as fmt } from \"./Scoring\"\n" +
                        "export VirtualNode Home() {\n  return (<Label text={fmt(1)} />);\n}\n"),
                },
                "Home.uitkx");

            Assert.True(result.AllSources.Any(s => s.Text.Contains(
                "internal static string fmt(int score) => global::Sco.Ns.__Exports.FormatScore(score);")));
            Assert.Empty(result.SyntaxErrors());
        }

        [Fact]
        public void RenamedValueImport_EmitsPropertyBridge()
        {
            var result = GeneratorTestHelper.RunMultiple(
                new[]
                {
                    ("Tokens.uitkx", "@namespace Toks.Ns\nexport int Gap = 8;\n"),
                    ("Home.uitkx",
                        "import { Gap as Spacing } from \"./Tokens\"\n" +
                        "export VirtualNode Home() {\n  return (<Label text={$\"{Spacing}\"} />);\n}\n"),
                },
                "Home.uitkx");

            Assert.True(result.AllSources.Any(s => s.Text.Contains(
                "internal static int Spacing => global::Toks.Ns.__Exports.Gap;")));
        }

        [Fact]
        public void DefaultComponentImport_InjectsAliasToDefaultName()
        {
            var result = GeneratorTestHelper.RunMultiple(
                new[]
                {
                    ("Panel.uitkx",
                        "@namespace Pan.Ns\n" +
                        "VirtualNode ScorePanel(string t) {\n  return (<VisualElement />);\n}\n" +
                        "export default ScorePanel;\n"),
                    ("Home.uitkx",
                        "import Panel from \"./Panel\"\n" +
                        "export VirtualNode Home() {\n  return (<Panel t=\"x\" />);\n}\n"),
                },
                "Home.uitkx");

            Assert.True(result.AllSources.Any(s =>
                s.Text.Contains("partial class Home") && s.Text.Contains("using Panel = global::Pan.Ns.ScorePanel;")));
            Assert.Empty(result.SyntaxErrors());
        }

        // ── Dotted tags (U-05) ───────────────────────────────────────────────

        [Fact]
        public void DottedTag_ThroughStarImport_ResolvesToTargetComponent()
        {
            var result = GeneratorTestHelper.RunMultiple(
                new[]
                {
                    ("Shapes.uitkx",
                        "@namespace Shp.Ns\n" +
                        "export VirtualNode Circle(int radius = 1) {\n  return (<VisualElement />);\n}\n"),
                    ("Home.uitkx",
                        "import * as Shapes from \"./Shapes\"\n" +
                        "export VirtualNode Home() {\n  return (<Shapes.Circle radius={5} />);\n}\n"),
                },
                "Home.uitkx");

            // The tag lowered to the TARGET's FQN component call, not a literal "Shapes.Circle" type.
            Assert.True(result.AllSources.Any(s =>
                s.Text.Contains("partial class Home") && s.Text.Contains("global::Shp.Ns.Circle")));
            Assert.Empty(result.SyntaxErrors());
        }

        // ── Legacy removal (0.16.0, ruling D3) ───────────────────────────────

        [Fact]
        public void LegacyFile_Emits2320Error_NamingTheCodemod()
        {
            // Keyword split across concatenation: deliberately-legacy fixture, must
            // survive any future fixture-modernizer sweep.
            var result = GeneratorTestHelper.Run(
                "compo" + "nent Foo {\n  return (<VisualElement />);\n}\n", "Foo.uitkx");

            Assert.True(result.HasDiagnostic("UITKX2320"));
            Assert.Contains(result.Diagnostics, d =>
                d.Id == "UITKX2320"
                && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                && d.GetMessage().Contains("UitkxMigrateImports"));
        }

        [Fact]
        public void NewModeFile_NoDeprecationWarnings()
        {
            var result = GeneratorTestHelper.Run(
                "@namespace Test.Ns\nexport VirtualNode Foo() {\n  return (<VisualElement />);\n}\n",
                "Foo.uitkx");

            Assert.False(result.HasDiagnostic("UITKX2320"));
            Assert.False(result.HasDiagnostic("UITKX2107"));
            Assert.False(result.HasDiagnostic("UITKX2108"));
            Assert.Empty(result.SyntaxErrors());
        }

        [Fact]
        public void GeneralPlanFixture_EmitsAllShapes_NoErrors()
        {
            // Plan §4.1/§4.2 normative fixture (imports trimmed to self-contained form).
            var result = GeneratorTestHelper.Run(
                "@namespace Fix.Ns\n" +
                "export int MaxItems = 5;\n" +
                "export theme = new System.Text.StringBuilder();\n" +
                "export string FormatScore(int score) { return $\"Score: {score}\"; }\n" +
                "export (int value, System.Action reset) useCountdown(int start) {\n" +
                "  return (start, null);\n" +
                "}\n" +
                "int rowGap = 2;\n" +
                "export VirtualNode ScoreRow(string label) {\n  return (<VisualElement />);\n}\n" +
                "VirtualNode ScorePanel(string title) {\n  return (<VisualElement />);\n}\n" +
                "export default ScorePanel;\n",
                "ScorePanel.uitkx");

            Assert.True(result.AllSources.Any(s => s.Text.Contains("public static partial class __Exports")));
            Assert.True(result.AllSources.Any(s => s.Text.Contains("public partial class ScoreRow")));
            // `export default ScorePanel;` marks ScorePanel exported (ES semantics — a default
            // export IS an export), so its partial is public despite the bare declaration head.
            Assert.True(result.AllSources.Any(s => s.Text.Contains("public partial class ScorePanel")));
            Assert.True(result.AllSources.Any(s => s.Text.Contains("Fix.Ns.__Exports::useCountdown")));
            Assert.True(result.AllSources.Any(s => s.Text.Contains("internal static int rowGap")));
            Assert.Empty(result.SyntaxErrors());
            Assert.False(result.Diagnostics.Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        }
    }
}
