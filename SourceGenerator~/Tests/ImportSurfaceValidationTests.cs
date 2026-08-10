using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Ruitk.Language.Parser;
using Xunit;
using static Ruitk.Language.StrictImportDetector;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// ES-modules campaign (Plans~/archive/ES_MODULES_EXECUTION_PLAN.md M1, U-03/U-04):
    /// <see cref="Ruitk.Language.StrictImportDetector.ValidateImports"/>'s new full-import-
    /// surface checks — alias collision (2325), default-import-without-default-export (2326),
    /// namespace/default/rename against a legacy target (2109, Unity-local), and hook-rename
    /// dropping the 'use' prefix (2110, Unity-local).
    /// </summary>
    public sealed class ImportSurfaceValidationTests
    {
        private static DirectiveSet EmptyDs() => new DirectiveSet(
            Namespace: "N", ComponentName: "Screen", PropsTypeName: null, DefaultKey: null,
            Usings: ImmutableArray<string>.Empty, UssFiles: ImmutableArray<string>.Empty,
            Injects: ImmutableArray<(string, string)>.Empty, MarkupStartLine: 1, MarkupStartIndex: 0);

        private static List<Finding> Validate(
            DirectiveSet ds, Func<string, bool> fileExists, Func<string, DirectiveSet?> parseTargetFile,
            string importerDir = "C:/p/Assets/UI", string rootDir = "C:/p/Assets", string? asmdef = "Game")
            => ValidateImports(ds, importerDir, rootDir, asmdef, fileExists, _ => asmdef, (_, _) => true, parseTargetFile);

        // ── 2325: alias collides with a local declaration ───────────────────

        [Fact]
        public void RenameAlias_CollidesWithLocalMember_Emits2325()
        {
            var ds = EmptyDs() with
            {
                Imports = ImmutableArray.Create(new ImportDeclaration(
                    ImmutableArray.Create("Format"), "./Utils", 1, 0, ImmutableArray<int>.Empty,
                    Aliases: ImmutableArray.Create<string?>("MaxItems"))),
                MemberDeclarations = ImmutableArray.Create(new MemberDeclaration(
                    "MaxItems", DeclKind.Value, true, "int", null, "5", false, 2, 0, 2, 0, 0)),
            };
            var findings = Validate(ds, p => p.EndsWith("Utils.uitkx"), _ => null);
            Assert.Single(findings, f => f.Code == "UITKX2325" && f.Message.Contains("MaxItems"));
        }

        [Fact]
        public void RenameAlias_NoCollision_NoFinding()
        {
            var ds = EmptyDs() with
            {
                Imports = ImmutableArray.Create(new ImportDeclaration(
                    ImmutableArray.Create("Format"), "./Utils", 1, 0, ImmutableArray<int>.Empty,
                    Aliases: ImmutableArray.Create<string?>("FormatUtil"))),
            };
            var findings = Validate(ds, p => p.EndsWith("Utils.uitkx"), _ => null);
            Assert.DoesNotContain(findings, f => f.Code == "UITKX2325");
        }

        [Fact]
        public void StarAlias_CollidesWithLocalComponent_Emits2325()
        {
            var ds = EmptyDs() with
            {
                Imports = ImmutableArray.Create(new ImportDeclaration(
                    ImmutableArray<string>.Empty, "./Shapes", 1, 0, ImmutableArray<int>.Empty,
                    IsStar: true, StarAlias: "Panel")),
                ComponentDeclarations = ImmutableArray.Create(new ComponentDeclaration(
                    "Panel", true, null, null, ImmutableArray<FunctionParam>.Empty, null, -1, -1, 1, 0, -1, 1, 0, -1, -1)),
            };
            var findings = Validate(ds, p => p.EndsWith("Shapes.uitkx"), _ => null);
            Assert.Single(findings, f => f.Code == "UITKX2325" && f.Message.Contains("Panel"));
        }

        // ── 2326: default import against a target with no default export ────

        [Fact]
        public void DefaultImport_TargetHasNoDefaultExport_Emits2326()
        {
            var ds = EmptyDs() with
            {
                Imports = ImmutableArray.Create(new ImportDeclaration(
                    ImmutableArray<string>.Empty, "./ScorePanel", 1, 0, ImmutableArray<int>.Empty,
                    IsDefault: true, DefaultAlias: "ScorePanel")),
            };
            var target = EmptyDs() with { DefaultExportName = null };
            var findings = Validate(ds, p => p.EndsWith("ScorePanel.uitkx"), _ => target);
            Assert.Single(findings, f => f.Code == "UITKX2326");
        }

        [Fact]
        public void DefaultImport_TargetHasDefaultExport_NoFinding()
        {
            var ds = EmptyDs() with
            {
                Imports = ImmutableArray.Create(new ImportDeclaration(
                    ImmutableArray<string>.Empty, "./ScorePanel", 1, 0, ImmutableArray<int>.Empty,
                    IsDefault: true, DefaultAlias: "ScorePanel")),
            };
            var target = EmptyDs() with { DefaultExportName = "ScorePanel" };
            var findings = Validate(ds, p => p.EndsWith("ScorePanel.uitkx"), _ => target);
            Assert.DoesNotContain(findings, f => f.Code == "UITKX2326");
        }

        // (The UITKX2109 legacy-target tests were removed with the wrapper grammar —
        // 0.16.0. A legacy target now errors at parse, so the migrate-first gate has
        // no subject.)

        // ── 2110 (Unity-local): renaming a hook drops the 'use' prefix ──────

        [Fact]
        public void RenameHook_DropsUsePrefix_Emits2110()
        {
            var ds = EmptyDs() with
            {
                Imports = ImmutableArray.Create(new ImportDeclaration(
                    ImmutableArray.Create("useCountdown"), "./Countdown", 1, 0, ImmutableArray<int>.Empty,
                    Aliases: ImmutableArray.Create<string?>("countdown"))),
            };
            var target = EmptyDs() with
            {
                MemberDeclarations = ImmutableArray.Create(new MemberDeclaration(
                    "useCountdown", DeclKind.Hook, true, "(int,Action)", "int start", "…", false, 1, 0, 1, 0, 0)),
            };
            var findings = Validate(ds, p => p.EndsWith("Countdown.uitkx"), _ => target);
            Assert.Single(findings, f => f.Code == "UITKX2110" && f.Message.Contains("useCountdown") && f.Message.Contains("countdown"));
        }

        [Fact]
        public void RenameHook_KeepsUsePrefix_NoFinding()
        {
            var ds = EmptyDs() with
            {
                Imports = ImmutableArray.Create(new ImportDeclaration(
                    ImmutableArray.Create("useCountdown"), "./Countdown", 1, 0, ImmutableArray<int>.Empty,
                    Aliases: ImmutableArray.Create<string?>("useTimer"))),
            };
            var target = EmptyDs() with
            {
                MemberDeclarations = ImmutableArray.Create(new MemberDeclaration(
                    "useCountdown", DeclKind.Hook, true, "(int,Action)", "int start", "…", false, 1, 0, 1, 0, 0)),
            };
            var findings = Validate(ds, p => p.EndsWith("Countdown.uitkx"), _ => target);
            Assert.DoesNotContain(findings, f => f.Code == "UITKX2110");
        }

        [Fact]
        public void RenameNonHookMember_NoPrefixGuard()
        {
            var ds = EmptyDs() with
            {
                Imports = ImmutableArray.Create(new ImportDeclaration(
                    ImmutableArray.Create("FormatScore"), "./Scoring", 1, 0, ImmutableArray<int>.Empty,
                    Aliases: ImmutableArray.Create<string?>("fmt"))),
            };
            var target = EmptyDs() with
            {
                MemberDeclarations = ImmutableArray.Create(new MemberDeclaration(
                    "FormatScore", DeclKind.Util, true, "string", "int score", "…", false, 1, 0, 1, 0, 0)),
            };
            var findings = Validate(ds, p => p.EndsWith("Scoring.uitkx"), _ => target);
            Assert.DoesNotContain(findings, f => f.Code == "UITKX2110");
        }
    }
}
