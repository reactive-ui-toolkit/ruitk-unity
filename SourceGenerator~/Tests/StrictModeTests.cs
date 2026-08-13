using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Ruitk.Language.Parser;
using Ruitk.SourceGenerator;
using Xunit;
using static Ruitk.Language.StrictImportDetector;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// The strict reference detector (plan §6 / §15 StrictModeTests): 2305 (peer-exported but not
    /// imported, with the exact fix-it line), 2307 (used-like-a-hook but nothing exports it),
    /// ambient/builtin exemption, and the import/self-declaration satisfaction paths.
    /// </summary>
    public sealed class StrictModeTests
    {
        private const string Importer = "C:/proj/Assets/UI/Screen.uitkx";

        private static bool NoBuiltins(string _) => false;
        private static bool BuiltinUseState(string n) => n == "useState" || n == "useEffect";

        private static DirectiveSet DsWithImports(params (string[] names, string spec)[] imports)
        {
            var ds = new DirectiveSet(
                Namespace: "N", ComponentName: "Screen", PropsTypeName: null, DefaultKey: null,
                Usings: ImmutableArray<string>.Empty, UssFiles: ImmutableArray<string>.Empty,
                Injects: ImmutableArray<(string, string)>.Empty, MarkupStartLine: 1, MarkupStartIndex: 0);
            if (imports.Length == 0) return ds;
            var builder = ImmutableArray.CreateBuilder<ImportDeclaration>();
            foreach (var (names, spec) in imports)
                builder.Add(new ImportDeclaration(names.ToImmutableArray(), spec, 1, 0, ImmutableArray<int>.Empty));
            return ds with { Imports = builder.ToImmutable() };
        }

        private static readonly IReadOnlyList<PeerExport> Peers = new[]
        {
            new PeerExport("StatusChip", "C:/proj/Assets/UI/StatusChip.uitkx", ExportKind.Component),
            new PeerExport("useCounter", "C:/proj/Assets/UI/Counter.hooks.uitkx", ExportKind.Hook),
            new PeerExport("Palette", "C:/proj/Assets/UI/Palette.uitkx", ExportKind.Module),
        };

        [Fact]
        public void ComponentTag_NotImported_Emits2305_WithFixItLine()
        {
            var findings = Detect(DsWithImports(), Importer, "<StatusChip />", Peers, NoBuiltins);
            var f = Assert.Single(findings, x => x.Code == "UITKX2305");
            Assert.Contains("import { StatusChip } from \"./StatusChip\"", f.Message);
        }

        [Fact]
        public void ComponentTag_Imported_NoFinding()
        {
            var ds = DsWithImports((new[] { "StatusChip" }, "./StatusChip"));
            var findings = Detect(ds, Importer, "<StatusChip />", Peers, NoBuiltins);
            Assert.Empty(findings);
        }

        [Fact]
        public void HookCall_ExportedButNotImported_Emits2305()
        {
            var findings = Detect(DsWithImports(), Importer, "var c = useCounter();", Peers, BuiltinUseState);
            Assert.Single(findings, x => x.Code == "UITKX2305" && x.Message.Contains("useCounter"));
        }

        [Fact]
        public void HookCall_NoExporter_NotBuiltin_Emits2307()
        {
            var findings = Detect(DsWithImports(), Importer, "var x = useMystery();", Peers, BuiltinUseState);
            Assert.Single(findings, x => x.Code == "UITKX2307" && x.Message.Contains("useMystery"));
        }

        [Fact]
        public void BuiltinHook_Exempt_NoFinding()
        {
            var findings = Detect(DsWithImports(), Importer, "var (s, set) = useState(0);", Peers, BuiltinUseState);
            Assert.Empty(findings);
        }

        [Fact]
        public void ModuleAccess_NotImported_Emits2305()
        {
            var findings = Detect(DsWithImports(), Importer, "var g = Palette.Gap;", Peers, BuiltinUseState);
            Assert.Single(findings, x => x.Code == "UITKX2305" && x.Message.Contains("Palette"));
        }

        [Fact]
        public void SelfDeclaredName_NeverFlagged()
        {
            // Screen references a hook it declares itself → no import needed.
            var ds = new DirectiveSet(
                Namespace: "N", ComponentName: "Screen", PropsTypeName: null, DefaultKey: null,
                Usings: ImmutableArray<string>.Empty, UssFiles: ImmutableArray<string>.Empty,
                Injects: ImmutableArray<(string, string)>.Empty, MarkupStartLine: 1, MarkupStartIndex: 0)
            {
                MemberDeclarations = ImmutableArray.Create(new MemberDeclaration(
                    "useLocal", DeclKind.Hook, false, "int", "", "return 0;", false, 1, 0, 1, 0, 0)),
            };
            var peers = new[] { new PeerExport("useLocal", "C:/other.uitkx", ExportKind.Hook) };
            var findings = Detect(ds, Importer, "var x = useLocal();", peers, NoBuiltins);
            Assert.Empty(findings);
        }

        [Fact]
        public void StringLiteralAndComment_Scrubbed_NotScanned()
        {
            string code = "// <StatusChip />\nvar s = \"<StatusChip />\";";
            var findings = Detect(DsWithImports(), Importer, ScrubNonCode(code), Peers, NoBuiltins);
            Assert.Empty(findings); // the only StatusChip occurrences are in a comment and a string
        }

        // ── ValidateImports (2300/2301/2308/2314) ───────────────────────────────

        private static List<Finding> Validate(
            DirectiveSet ds, string importerDir, string rootDir, string? importerAsmdef,
            Func<string, bool> fileExists, Func<string, string?> owningAsmdefOf,
            Func<string, string, bool> exportedBy)
            => ValidateImports(ds, importerDir, rootDir, importerAsmdef, fileExists, owningAsmdefOf, exportedBy);

        [Fact]
        public void UnresolvableSpecifier_Emits2300()
        {
            var ds = DsWithImports((new[] { "X" }, "./missing"));
            var findings = Validate(ds, "C:/p/Assets/UI", "C:/p/Assets", "Game",
                _ => false, _ => "Game", (_, _) => true);
            Assert.Single(findings, f => f.Code == "UITKX2300" && f.Message.Contains("./missing"));
        }

        [Fact]
        public void ResolvedButNotExported_Emits2301()
        {
            var ds = DsWithImports((new[] { "Chip" }, "./Chip"));
            var findings = Validate(ds, "C:/p/Assets/UI", "C:/p/Assets", "Game",
                p => p.EndsWith("Chip.uitkx"), _ => "Game", (_, _) => false);
            Assert.Single(findings, f => f.Code == "UITKX2301" && f.Message.Contains("Chip"));
        }

        [Fact]
        public void ResolvedAndExported_NoFinding()
        {
            var ds = DsWithImports((new[] { "Chip" }, "./Chip"));
            var findings = Validate(ds, "C:/p/Assets/UI", "C:/p/Assets", "Game",
                p => p.EndsWith("Chip.uitkx"), _ => "Game", (_, _) => true);
            Assert.Empty(findings);
        }

        [Fact]
        public void CrossAsmdefBoundary_Emits2308()
        {
            var ds = DsWithImports((new[] { "Chip" }, "./Chip"));
            var findings = Validate(ds, "C:/p/Assets/UI", "C:/p/Assets", "Game",
                p => p.EndsWith("Chip.uitkx"), _ => "OtherAsm", (_, _) => true);
            Assert.Single(findings, f => f.Code == "UITKX2308");
        }

        // ── DetectUnusedImports (2304) ──────────────────────────────────────────

        [Fact]
        public void ImportedNameNeverReferenced_Emits2304()
        {
            var ds = DsWithImports((new[] { "StatusChip" }, "./StatusChip"));
            var findings = DetectUnusedImports(ds, "<Box />");
            Assert.Single(findings, f => f.Code == "UITKX2304" && f.Message.Contains("StatusChip"));
        }

        [Fact]
        public void ImportedNameReferenced_No2304()
        {
            // scannableCode is the full, line-aligned file text (import lines are excluded
            // from the reference universe so bindings can't self-count) — the reference
            // must sit BELOW the import line, as in a real file.
            var ds = DsWithImports((new[] { "StatusChip" }, "./StatusChip"));
            var findings = DetectUnusedImports(
                ds, "import { StatusChip } from \"./StatusChip\"\n<StatusChip />");
            Assert.Empty(findings);
        }

        [Fact]
        public void TildeEscapesRoot_Emits2314()
        {
            var ds = DsWithImports((new[] { "X" }, "~/../../evil"));
            var findings = Validate(ds, "Assets/UI", "Assets", "Game",
                _ => true, _ => "Game", (_, _) => true);
            Assert.Single(findings, f => f.Code == "UITKX2314");
        }

        // ── Squiggle anchoring (Finding.Column/EndColumn) ───────────────────────
        // Regression: every strict finding used to carry only a line, so editors
        // rendered a 1-char squiggle at column 0 (on `import`) instead of on the
        // offending token — the specifier string, imported name, or reference.

        /// <summary>An import of `X` as it appears in `import { X } from "spec"`: name at col 9, opening quote at col 18.</summary>
        private static DirectiveSet DsWithAnchoredImport(string name, string spec)
        {
            var ds = DsWithImports();
            return ds with
            {
                Imports = ImmutableArray.Create(new ImportDeclaration(
                    ImmutableArray.Create(name), spec, 1, 0,
                    ImmutableArray.Create(9), SpecifierColumn: 9 + name.Length + 8)),
            };
        }

        [Fact]
        public void UnresolvableSpecifier_2300_AnchorsToSpecifierString()
        {
            var ds = DsWithAnchoredImport("X", "./missing");
            var findings = Validate(ds, "C:/p/Assets/UI", "C:/p/Assets", "Game",
                _ => false, _ => "Game", (_, _) => true);
            var f = Assert.Single(findings, x => x.Code == "UITKX2300");
            Assert.Equal(18, f.Column);                              // opening quote
            Assert.Equal(18 + "./missing".Length + 2, f.EndColumn);  // past closing quote
        }

        [Fact]
        public void NotExported_2301_AnchorsToImportedName()
        {
            var ds = DsWithAnchoredImport("Chip", "./Chip");
            var findings = Validate(ds, "C:/p/Assets/UI", "C:/p/Assets", "Game",
                p => p.EndsWith("Chip.uitkx"), _ => "Game", (_, _) => false);
            var f = Assert.Single(findings, x => x.Code == "UITKX2301");
            Assert.Equal(9, f.Column);
            Assert.Equal(9 + "Chip".Length, f.EndColumn);
        }

        [Fact]
        public void UnusedImport_2304_AnchorsToImportedName()
        {
            var ds = DsWithAnchoredImport("StatusChip", "./StatusChip");
            var findings = DetectUnusedImports(ds, "<Box />");
            var f = Assert.Single(findings, x => x.Code == "UITKX2304");
            Assert.Equal(9, f.Column);
            Assert.Equal(9 + "StatusChip".Length, f.EndColumn);
        }

        [Fact]
        public void NotImportedReference_2305_AnchorsToIdentifier_MultiLine()
        {
            // Palette on line 2, col 8 of the scannable text.
            var findings = Detect(DsWithImports(), Importer, "var a = 1;\nvar g = Palette.Gap;", Peers, BuiltinUseState);
            var f = Assert.Single(findings, x => x.Code == "UITKX2305");
            Assert.Equal(2, f.Line);
            Assert.Equal(8, f.Column);
            Assert.Equal(8 + "Palette".Length, f.EndColumn);
        }

        [Fact]
        public void UnknownHook_2307_AnchorsToIdentifier()
        {
            var findings = Detect(DsWithImports(), Importer, "var x = useMystery();", Peers, BuiltinUseState);
            var f = Assert.Single(findings, x => x.Code == "UITKX2307");
            Assert.Equal(8, f.Column);
            Assert.Equal(8 + "useMystery".Length, f.EndColumn);
        }

        [Fact]
        public void UntrackedColumns_FallBackToMinusOne()
        {
            // Legacy 5-arg ImportDeclaration (no SpecifierColumn/NameColumns) → spans untracked,
            // consumers fall back to the old line-start anchor instead of a bogus span.
            var ds = DsWithImports((new[] { "X" }, "./missing"));
            var findings = Validate(ds, "C:/p/Assets/UI", "C:/p/Assets", "Game",
                _ => false, _ => "Game", (_, _) => true);
            var f = Assert.Single(findings, x => x.Code == "UITKX2300");
            Assert.Equal(-1, f.Column);
            Assert.Equal(-1, f.EndColumn);
        }
    }
}
