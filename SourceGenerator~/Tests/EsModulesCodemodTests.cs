using System.Collections.Generic;
using System.Linq;
using Ruitk.SourceGenerator.Tools;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// ES-modules campaign (Plans~/archive/ES_MODULES_EXECUTION_PLAN.md M7, U-09/§7.1): the
    /// <c>--es-modules</c> codemod pass over in-memory file sets — each wrapper rewrite form,
    /// module→<c>* as</c> importer preservation, companion member-import insertion, companion-set
    /// atomicity, unmigratable-shape skip, and idempotence.
    /// </summary>
    public sealed class EsModulesCodemodTests
    {
        private const string Dir = "C:/proj/Assets/UI";

        private static MigratorFile F(string name, string text)
            => new($"{Dir}/{name}", "Game", text);

        private static Dictionary<string, string> Run(out List<UitkxMigrator.MigrationError> errors, params MigratorFile[] files)
            => EsModulesMigrator.Migrate(files, out errors);

        private static List<MigratorFile> Apply(IReadOnlyList<MigratorFile> input, Dictionary<string, string> changed)
            => input.Select(f => changed.TryGetValue(f.AbsPath, out var t) ? f with { Text = t } : f).ToList();

        // ── Wrapper rewrites ─────────────────────────────────────────────────

        [Fact]
        public void Component_RewritesToVirtualNode_ParameterlessGetsParens()
        {
            var file = F("Foo.uitkx", "export component Foo {\n    return (\n        <Box />\n    );\n}\n");
            var changed = Run(out var errors, file);

            Assert.Empty(errors);
            string outText = changed[file.AbsPath];
            Assert.Contains("export VirtualNode Foo() {", outText);
            Assert.DoesNotContain("component Foo", outText);
        }

        [Fact]
        public void Component_WithParams_KeepsParamList()
        {
            var file = F("Bar.uitkx",
                "export component Bar(string title = \"x\") {\n    return (\n        <Label text={title} />\n    );\n}\n");
            var changed = Run(out var errors, file);

            Assert.Empty(errors);
            Assert.Contains("export VirtualNode Bar(string title = \"x\") {", changed[file.AbsPath]);
        }

        [Fact]
        public void Hook_TupleReturn_MovesBeforeName()
        {
            var file = F("Counter.hooks.uitkx",
                "export hook useCounter(int initial = 0) -> (int count, Action increment) {\n" +
                "    var (count, setCount) = useState(initial);\n" +
                "    Action increment = () => setCount(c => c + 1);\n" +
                "    return (count, increment);\n" +
                "}\n");
            var changed = Run(out var errors, file);

            Assert.Empty(errors);
            string outText = changed[file.AbsPath];
            Assert.Contains("export (int count, Action increment) useCounter(int initial = 0) {", outText);
            Assert.DoesNotContain("hook useCounter", outText);
            Assert.Contains("var (count, setCount) = useState(initial);", outText);
        }

        [Fact]
        public void Hook_SingleTypeReturn_LosesNothing()
        {
            var file = F("Thing.hooks.uitkx",
                "export hook useThing() -> int {\n    return 42;\n}\n");
            var changed = Run(out var errors, file);

            Assert.Empty(errors);
            Assert.Contains("export int useThing() {", changed[file.AbsPath]);
        }

        [Fact]
        public void Module_ExplodesToPlainDeclarations()
        {
            var file = F("Tokens.uitkx",
                "export module Tokens {\n" +
                "    public static readonly int Gap = 8;\n" +
                "    public static string Fmt(int s) { return $\"{s}\"; }\n" +
                "    private static int hidden = 1;\n" +
                "}\n");
            var changed = Run(out var errors, file);

            Assert.Empty(errors);
            string outText = changed[file.AbsPath];
            Assert.Contains("export int Gap = 8;", outText);
            Assert.Contains("export string Fmt(int s) {", outText);
            Assert.Contains("int hidden = 1;", outText);
            Assert.DoesNotContain("module Tokens", outText);
        }

        // ── Importer rewrite (module → * as) ─────────────────────────────────

        [Fact]
        public void ModuleImporter_BecomesStarImport_CallSitesUntouched()
        {
            var tokens = F("Tokens.uitkx",
                "export module Tokens {\n    public static readonly int Gap = 8;\n}\n");
            var screen = F("Screen.uitkx",
                "import { Tokens } from \"./Tokens\"\ncomponent Screen {\n    var g = Tokens.Gap;\n    return (\n        <Box />\n    );\n}\n");
            var changed = Run(out var errors, tokens, screen);

            Assert.Empty(errors);
            string outText = changed[screen.AbsPath];
            Assert.Contains("import * as Tokens from \"./Tokens\"", outText);
            Assert.Contains("Tokens.Gap", outText);
            Assert.DoesNotContain("import { Tokens }", outText);
        }

        // ── Companion member imports ─────────────────────────────────────────

        [Fact]
        public void CompanionSet_MemberRefsGetImports()
        {
            // Legacy companion merge: Foo.style.uitkx's `module Foo` merged into component Foo,
            // so `container` resolved bare. Post-migration Foo.uitkx must import it.
            var style = F("Foo.style.uitkx",
                "export module Foo {\n    public static readonly int container = 1;\n}\n");
            var comp = F("Foo.uitkx",
                "component Foo {\n    var c = container;\n    return (\n        <Box />\n    );\n}\n");
            var changed = Run(out var errors, style, comp);

            Assert.Empty(errors);
            string outText = changed[comp.AbsPath];
            Assert.Contains("import { container } from \"./Foo.style\"", outText);
        }

        // ── Atomicity + unmigratable shapes ──────────────────────────────────

        [Fact]
        public void ModuleWithNestedType_WholeSetStaysLegacy_WithError()
        {
            var style = F("Baz.style.uitkx",
                "export module Baz {\n    public enum Kind { A, B }\n}\n");
            var comp = F("Baz.uitkx",
                "component Baz {\n    return (\n        <Box />\n    );\n}\n");
            var changed = Run(out var errors, style, comp);

            Assert.Contains(errors, e => e.Message.Contains("left legacy"));
            // Neither file switched grammar: the component keeps its wrapper keyword.
            string compText = changed.TryGetValue(comp.AbsPath, out var t) ? t : comp.Text;
            Assert.Contains("component Baz", compText);
            string styleText = changed.TryGetValue(style.AbsPath, out var s) ? s : style.Text;
            Assert.Contains("module Baz", styleText);
        }

        [Fact]
        public void CompanionModule_NonPublicMembers_AutoExport()
        {
            // T15: modifier-less companion members were reachable through the partial-class
            // merge; migration must keep them reachable (auto-export + warning), not strand
            // the base file at CS0103.
            var comp = F("Card.uitkx",
                "import { CardStyles } from \"./Card.style\"\ncomponent Card {\n    return (\n        <Box />\n    );\n}\n");
            var style = F("Card.style.uitkx",
                "export module CardStyles {\n    static readonly int Gap = 8;\n}\n");
            var changed = Run(out var errors, comp, style);

            string styleOut = changed[style.AbsPath];
            Assert.Contains("export int Gap = 8;", styleOut);
            Assert.Contains(errors, e => e.Message.Contains("auto-exported"));
        }

        [Fact]
        public void NonCompanionModule_NonPublicMembers_StayInternal()
        {
            var tokens = F("Tokens.uitkx",
                "export module Tokens {\n    static readonly int Hidden = 1;\n    public static readonly int Gap = 8;\n}\n");
            var changed = Run(out var errors, tokens);

            string outText = changed[tokens.AbsPath];
            Assert.Contains("export int Gap = 8;", outText);
            Assert.Contains("int Hidden = 1;", outText);
            Assert.DoesNotContain("export int Hidden", outText);
        }

        [Fact]
        public void GenericModuleMethod_MigratesToGenericDeclarationHead()
        {
            var utils = F("Utils.uitkx",
                "export module Utils {\n    public static T Pick<T>(T a, T b, bool first) { return first ? a : b; }\n}\n");
            var changed = Run(out var errors, utils);

            Assert.Empty(errors.Where(e => e.Message.Contains("generic")));
            string outText = changed[utils.AbsPath];
            Assert.Contains("export T Pick<T>(T a, T b, bool first)", outText);
        }

        [Fact]
        public void DeadNamespaceImport_GetsPc12bWarning()
        {
            // Widgets/ is folder-keyed pre-migration; Screen imports "@Game.Widgets" and also
            // migrates. Post-migration the folder namespace has no members left - the surviving
            // namespace import draws the PC-12b warning (never a silent rewrite).
            var widget = new MigratorFile("C:/proj/Assets/UI/Widgets/Chip.uitkx", "Game",
                "component Chip {\n    return (\n        <Box />\n    );\n}\n");
            string oldNs = Ruitk.Language.EffectiveNamespace.Resolve(
                hasExplicitNamespace: false, null, widget.AbsPath, fileKeyed: false)!;
            var screen = F("Screen.uitkx",
                $"import \"@{oldNs}\"\ncomponent Screen {{\n    return (\n        <Chip />\n    );\n}}\n");
            var changed = Run(out var errors, widget, screen);

            Assert.Contains(errors, e =>
                e.FilePath == screen.AbsPath && e.Message.Contains("pre-migration folder namespace"));
        }

        [Fact]
        public void GenericHook_MigratesToGenericDeclarationHead()
        {
            // F9 closed (0.16.0): the plain dialect has generic declaration heads, so the
            // codemod migrates generic hooks instead of freezing the file legacy.
            var file = F("Gen.hooks.uitkx",
                "export hook useGen<T>(T seed) -> T {\n    return seed;\n}\n");
            var changed = Run(out var errors, file);

            Assert.Empty(errors);
            string outText = changed[file.AbsPath];
            Assert.Contains("export T useGen<T>(T seed) {", outText);
            Assert.DoesNotContain("hook useGen", outText);
        }

        // ── Idempotence (the §7.1 acceptance) ────────────────────────────────

        [Fact]
        public void Migration_IsIdempotent()
        {
            var input = new List<MigratorFile>
            {
                F("Tokens.uitkx", "export module Tokens {\n    public static readonly int Gap = 8;\n}\n"),
                F("Screen.uitkx",
                    "import { Tokens } from \"./Tokens\"\ncomponent Screen {\n    var g = Tokens.Gap;\n    return (\n        <Box />\n    );\n}\n"),
                F("Counter.hooks.uitkx", "export hook useCounter() -> int {\n    return 1;\n}\n"),
            };

            var first = Run(out var errs1, input.ToArray());
            Assert.Empty(errs1);
            var afterFirst = Apply(input, first);

            var second = EsModulesMigrator.Migrate(afterFirst, out var errs2);
            Assert.Empty(errs2);
            Assert.Empty(second);
        }

        [Fact]
        public void MigratedFile_NeverGetsNamespaceStamp()
        {
            // The imports-leg identity-freezing @namespace stamp is WRONG here: it would pin every
            // migrated file to its pre-migration namespace (with the raw parsed fallback, no
            // less) and defeat G-01's file-keyed derivation. Found live: the first samples run
            // stamped `@namespace Ruitk.FunctionStyle` into all 165 files.
            var comp = F("Stamp.uitkx", "component Stamp {\n    return (\n        <Box />\n    );\n}\n");
            var changed = Run(out var errors, comp);

            Assert.Empty(errors);
            Assert.DoesNotContain("@namespace", changed[comp.AbsPath]);
        }

        [Fact]
        public void ExplicitNamespaceStamp_IsPreserved()
        {
            // An AUTHORED @namespace is the escape hatch (G-07/U-01) — it must survive migration.
            var comp = F("Stamped.uitkx",
                "@namespace My.Stamp\ncomponent Stamped {\n    return (\n        <Box />\n    );\n}\n");
            var changed = Run(out var errors, comp);

            Assert.Empty(errors);
            Assert.Contains("@namespace My.Stamp", changed[comp.AbsPath]);
        }

        [Fact]
        public void AlreadyMigratedFile_Untouched()
        {
            var file = F("Done.uitkx",
                "export VirtualNode Done() {\n  return (\n    <Box />\n  );\n}\n");
            var changed = Run(out var errors, file);

            Assert.Empty(errors);
            Assert.False(changed.ContainsKey(file.AbsPath));
        }

        // ── Close-brace on the last body line (A1) ───────────────────────────

        [Fact]
        public void Hook_CloseBraceOnLastStatementLine_DoesNotSwallowNextDeclaration()
        {
            var input = new List<MigratorFile>
            {
                F("Pair.hooks.uitkx",
                    "export hook useA() -> int {\n    return 1; }\nexport hook useB() -> int {\n    return 2;\n}\n"),
            };
            var changed = Run(out var errors, input.ToArray());

            Assert.Empty(errors);
            string outText = changed[input[0].AbsPath];
            Assert.Contains("export int useA()", outText);
            Assert.Contains("export int useB()", outText);
            Assert.Contains("return 2;", outText);
            Assert.DoesNotContain("hook useA", outText);

            var second = EsModulesMigrator.Migrate(Apply(input, changed), out var errs2);
            Assert.Empty(errs2);
            Assert.Empty(second);
        }

        [Fact]
        public void Module_CloseBraceOnLastMemberLine_DoesNotSwallowNextDeclaration()
        {
            var file = F("Packed.uitkx",
                "export module Packed {\n    public static readonly int A = 1; }\nexport hook useAfter() -> int {\n    return 2;\n}\n");
            var changed = Run(out var errors, file);

            Assert.Empty(errors);
            string outText = changed[file.AbsPath];
            Assert.Contains("export int A = 1;", outText);
            Assert.Contains("export int useAfter()", outText);
        }

        // ── Same-line adjacent declarations (A2) ─────────────────────────────

        [Fact]
        public void SameLineAdjacentDeclarations_WholeFileStaysLegacy_WithError()
        {
            var file = F("Adjacent.hooks.uitkx",
                "export hook useA() -> int {\n    return 1; } export hook useB() -> int {\n    return 2;\n}\n");
            var changed = Run(out var errors, file);

            Assert.Contains(errors, e => e.Message.Contains("own line"));
            string outText = changed.TryGetValue(file.AbsPath, out var t) ? t : file.Text;
            Assert.Contains("hook useA", outText);
            Assert.Contains("hook useB", outText);
        }

        // ── Module explosion fidelity (A3/A4) ────────────────────────────────

        [Fact]
        public void Module_MemberCommentsAndDocs_ArePreserved()
        {
            var input = new List<MigratorFile>
            {
                F("Docs.uitkx",
                    "export module Docs {\n" +
                    "    // banner comment\n" +
                    "    /// <summary>Doc.</summary>\n" +
                    "    public static readonly int A = 1;\n" +
                    "\n" +
                    "    // tail note\n" +
                    "}\n"),
            };
            var changed = Run(out var errors, input.ToArray());

            Assert.Empty(errors);
            string outText = changed[input[0].AbsPath];
            Assert.Contains("// banner comment", outText);
            Assert.Contains("/// <summary>Doc.</summary>", outText);
            Assert.Contains("// tail note", outText);
            Assert.Contains("export int A = 1;", outText);
            Assert.DoesNotContain("module Docs", outText);

            var second = EsModulesMigrator.Migrate(Apply(input, changed), out var errs2);
            Assert.Empty(errs2);
            Assert.Empty(second);
        }

        [Fact]
        public void Module_AttributedMember_WholeSetStaysLegacy_WithError()
        {
            var file = F("Attr.uitkx",
                "export module Attr {\n    [System.Obsolete]\n    public static readonly int Old = 1;\n}\n");
            var changed = Run(out var errors, file);

            Assert.Contains(errors, e => e.Message.Contains("Old") && e.Message.Contains("attribute"));
            string outText = changed.TryGetValue(file.AbsPath, out var t) ? t : file.Text;
            Assert.Contains("module Attr", outText);
            Assert.Contains("[System.Obsolete]", outText);
        }

        [Fact]
        public void Module_ConstMember_MigratesToPlainValue_WithWarning()
        {
            var file = F("Consts.uitkx",
                "export module Consts {\n    public const int Max = 3;\n}\n");
            var changed = Run(out var errors, file);

            string outText = changed[file.AbsPath];
            Assert.Contains("export int Max = 3;", outText);
            Assert.DoesNotContain("const", outText);
            Assert.Contains(errors, e => e.Message.Contains("Max") && e.Message.Contains("const"));
        }

        [Fact]
        public void Module_ModifierlessMember_IsNotExported()
        {
            // C# class members default to PRIVATE — a modifier-less module member must not
            // gain `export` (that would widen visibility on migration).
            var file = F("Mix.uitkx",
                "export module Mix {\n    static readonly int quiet = 1;\n    public static readonly int Loud = 2;\n}\n");
            var changed = Run(out var errors, file);

            Assert.Empty(errors);
            string outText = changed[file.AbsPath];
            Assert.Contains("export int Loud = 2;", outText);
            Assert.Contains("int quiet = 1;", outText);
            Assert.DoesNotContain("export int quiet", outText);
        }

        // ── Companion member imports in every direction (A5) ─────────────────

        [Fact]
        public void CompanionSet_CompanionReferencingBaseMember_GetsImport()
        {
            var baseFile = F("Foo.uitkx",
                "export module Foo {\n    public static readonly int baseVal = 1;\n}\n");
            var style = F("Foo.style.uitkx",
                "export module FooStyle {\n    public static readonly int pad = baseVal;\n}\n");
            var changed = Run(out var errors, baseFile, style);

            Assert.Empty(errors);
            Assert.Contains("import { baseVal } from \"./Foo\"", changed[style.AbsPath]);
        }

        [Fact]
        public void CompanionSet_SiblingCompanionReference_GetsImport()
        {
            var input = new List<MigratorFile>
            {
                F("Foo.uitkx", "component Foo {\n    return (\n        <Box />\n    );\n}\n"),
                F("Foo.style.uitkx", "export module FooStyle {\n    public static readonly int gapBase = 4;\n}\n"),
                F("Foo.anim.uitkx", "export module FooAnim {\n    public static readonly int dur = gapBase * 2;\n}\n"),
            };
            var changed = Run(out var errors, input.ToArray());

            Assert.Empty(errors);
            Assert.Contains("import { gapBase } from \"./Foo.style\"", changed[input[2].AbsPath]);

            var second = EsModulesMigrator.Migrate(Apply(input, changed), out var errs2);
            Assert.Empty(errs2);
            Assert.Empty(second);
        }

        // ── Importer rewrite reaches unmigrated importers too (A6) ───────────

        [Fact]
        public void FailedSetImporter_OfMigratedModule_GetsStarImport_WithNote()
        {
            // The failed set is forced by a module with a nested type (still unmigratable);
            // generic hooks migrate since F9 closed, so they can no longer play this role.
            var tokens = F("Tokens.uitkx",
                "export module Tokens {\n    public static readonly int Gap = 8;\n}\n");
            var broken = F("Broken.style.uitkx",
                "import { Tokens } from \"./Tokens\"\nexport module BrokenStyles {\n    public enum Kind { A, B }\n    public static readonly int Pad = Tokens.Gap;\n}\n");
            var changed = Run(out var errors, tokens, broken);

            Assert.Contains(errors, e => e.Message.Contains("BrokenStyles"));
            string outText = changed[broken.AbsPath];
            Assert.Contains("import * as Tokens from \"./Tokens\"", outText);
            Assert.Contains("module BrokenStyles", outText);
            Assert.DoesNotContain("import { Tokens }", outText);
            Assert.Contains(errors, e => e.Message.Contains("import * as"));
        }

        [Fact]
        public void NewModeImporterInFailedSet_OfMigratedModule_GetsStarImport()
        {
            var tokens = F("Tokens.uitkx",
                "export module Tokens {\n    public static readonly int Gap = 8;\n}\n");
            var panel = F("Panel.uitkx",
                "import { Tokens } from \"./Tokens\"\nexport VirtualNode Panel() {\n    return (\n        <Box />\n    );\n}\n");
            var panelStyle = F("Panel.style.uitkx",
                "export module PanelStyle {\n    public enum Kind { A, B }\n}\n");
            var changed = Run(out var errors, tokens, panel, panelStyle);

            Assert.Contains(errors, e => e.Message.Contains("left legacy"));
            string outText = changed[panel.AbsPath];
            Assert.Contains("import * as Tokens from \"./Tokens\"", outText);
            Assert.Contains("export VirtualNode Panel()", outText);
        }

        // ── `~/`-rooted importer rewrite (A7) ────────────────────────────────

        [Fact]
        public void TildeRootedModuleImport_GetsStarImport()
        {
            var tokens = new MigratorFile("C:/proj/Assets/Shared/Tokens.uitkx", "Game",
                "export module Tokens {\n    public static readonly int Gap = 8;\n}\n");
            var screen = F("Screen.uitkx",
                "import { Tokens } from \"~/Shared/Tokens\"\ncomponent Screen {\n    return (\n        <Box />\n    );\n}\n");
            var changed = Run(out var errors, tokens, screen);

            Assert.Empty(errors);
            Assert.Contains("import * as Tokens from \"~/Shared/Tokens\"", changed[screen.AbsPath]);
        }

        // ── Parse-error gate (A8) ────────────────────────────────────────────

        [Fact]
        public void FileWithParseErrors_IsSkippedWholeSet_WithReportedDiagnostics()
        {
            var file = F("Bad.uitkx",
                "export module Bad {\n    public static readonly int A = 1;\n}\nexport hook useBad()\n");
            var changed = Run(out var errors, file);

            Assert.Contains(errors, e => e.Message.Contains("parse error"));
            string outText = changed.TryGetValue(file.AbsPath, out var t) ? t : file.Text;
            Assert.Contains("module Bad", outText);
            Assert.Contains("export hook useBad()", outText);
        }
    }
}
