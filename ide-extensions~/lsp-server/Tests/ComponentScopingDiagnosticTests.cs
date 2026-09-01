using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ruitk.Language;
using Ruitk.Language.Parser;
using UitkxLanguageServer;
using Xunit;

namespace UitkxLanguageServer.Tests
{
    /// <summary>
    /// Pins the namespace-aware scoping of the two workspace-level component
    /// diagnostics against the field regression found in the 0.15.0 samples
    /// sweep (three <c>GameScreen.uitkx</c> and two <c>MainMenu.uitkx</c> in
    /// one asmdef):
    /// <list type="bullet">
    ///   <item>UITKX0113 fires only when two declarants share the same
    ///   EFFECTIVE NAMESPACE — file-keyed namespaces (ES modules) make
    ///   same-named components in different folders legal, while legacy files
    ///   sharing an explicit <c>@namespace</c> still collide.</item>
    ///   <item>UITKX0109's attribute surface resolves through the CURRENT
    ///   file's imports (build-identical specifier rule), and falls open to
    ///   the union of all declarants when unresolvable — never validating a
    ///   tag against the wrong file's component.</item>
    /// </list>
    /// </summary>
    public sealed class ComponentScopingDiagnosticTests : IDisposable
    {
        private readonly string _root;
        private readonly string _uiDir;

        public ComponentScopingDiagnosticTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "uitkx-scoping-" + Guid.NewGuid().ToString("N"));
            _uiDir = Path.Combine(_root, "Assets", "UI");
            Directory.CreateDirectory(_uiDir);
            File.WriteAllText(Path.Combine(_uiDir, "Asm.asmdef"), "{ \"name\": \"Game.UI\" }");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string F(string relPath, string content)
        {
            string p = Path.Combine(_uiDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
            return p;
        }

        private static DirectiveSet Parse(string text, string path)
            => DirectiveParser.Parse(text, path, new List<ParseDiagnostic>());

        private DiagnosticsPublisher MakePublisher(out WorkspaceIndex index)
        {
            index = new WorkspaceIndex();
            index.EnsureScanned(_root);
            return new DiagnosticsPublisher(null!, new UitkxSchema(), index, new DocumentStore());
        }

        // ── UITKX0113 scoping ────────────────────────────────────────────────

        [Fact]
        public void SameName_DifferentFolders_FileKeyedNamespaces_No0113()
        {
            string a = F("SnakeGame/GameScreen.uitkx",
                "export VirtualNode GameScreen() {\n  return (<VisualElement />);\n}\n");
            F("GalagaGame/GameScreen.uitkx",
                "export VirtualNode GameScreen() {\n  return (<VisualElement />);\n}\n");

            var publisher = MakePublisher(out _);
            var ds = Parse(File.ReadAllText(a), a);

            Assert.Empty(publisher.ComputeDuplicateComponentDiagnostics(ds, a));
        }

        [Fact]
        public void SameName_SameExplicitNamespace_0113Fires()
        {
            string a = F("a/Board.uitkx",
                "@namespace Shared.Ns\nVirtualNode Board() {\n  return (<VisualElement />);\n}\n");
            F("b/Board.uitkx",
                "@namespace Shared.Ns\nVirtualNode Board() {\n  return (<VisualElement />);\n}\n");

            var publisher = MakePublisher(out _);
            var ds = Parse(File.ReadAllText(a), a);

            var d = Assert.Single(publisher.ComputeDuplicateComponentDiagnostics(ds, a));
            Assert.Equal("UITKX0113", d.Code);
            Assert.Contains("Shared.Ns", d.Message);
            Assert.Contains("Board.uitkx", d.Message);
        }

        // ── UITKX0109 attribute-surface resolution ───────────────────────────

        [Fact]
        public void KnownAttributes_PathImport_PicksImportedDeclarant()
        {
            F("MarioGame/MainMenu/MainMenu.uitkx",
                "export VirtualNode MainMenu(System.Action onStartGame) {\n"
                + "  return (<VisualElement />);\n}\n");
            F("GalagaGame/MainMenu/MainMenu.uitkx",
                "export VirtualNode MainMenu(System.Action onNewGame) {\n"
                + "  return (<VisualElement />);\n}\n");
            string importer = F("MarioGame/MarioGame.uitkx",
                "import { MainMenu } from \"./MainMenu/MainMenu\"\n"
                + "\n"
                + "export VirtualNode MarioGame() {\n"
                + "  return (<MainMenu onStartGame={() => {}} />);\n}\n");

            var publisher = MakePublisher(out _);
            var ds = Parse(File.ReadAllText(importer), importer);

            var known = publisher.BuildKnownAttributes(
                new HashSet<string>(), ds, importer);

            Assert.True(known.TryGetValue("MainMenu", out var attrs));
            Assert.Contains("onStartGame", attrs.Known);
            Assert.DoesNotContain("onNewGame", attrs.Known);
        }

        [Fact]
        public void KnownAttributes_NoImport_FallsOpenToUnion()
        {
            F("MarioGame/MainMenu/MainMenu.uitkx",
                "export VirtualNode MainMenu(System.Action onStartGame) {\n"
                + "  return (<VisualElement />);\n}\n");
            F("GalagaGame/MainMenu/MainMenu.uitkx",
                "export VirtualNode MainMenu(System.Action onNewGame) {\n"
                + "  return (<VisualElement />);\n}\n");
            string importer = F("Elsewhere/Screen.uitkx",
                "export VirtualNode Screen() {\n  return (<VisualElement />);\n}\n");

            var publisher = MakePublisher(out _);
            var ds = Parse(File.ReadAllText(importer), importer);

            var known = publisher.BuildKnownAttributes(
                new HashSet<string>(), ds, importer);

            Assert.True(known.TryGetValue("MainMenu", out var attrs));
            Assert.Contains("onStartGame", attrs.Known);
            Assert.Contains("onNewGame", attrs.Known);
        }

        [Fact]
        public void KnownAttributes_OwnDeclarationWins_OverPeerWithSameName()
        {
            string self = F("SnakeGame/GameScreen.uitkx",
                "export VirtualNode GameScreen(int restartVersion) {\n"
                + "  return (<VisualElement />);\n}\n");
            F("GalagaGame/GameScreen.uitkx",
                "export VirtualNode GameScreen(System.Action onQuit) {\n"
                + "  return (<VisualElement />);\n}\n");

            var publisher = MakePublisher(out _);
            var ds = Parse(File.ReadAllText(self), self);

            var known = publisher.BuildKnownAttributes(
                new HashSet<string>(), ds, self);

            Assert.True(known.TryGetValue("GameScreen", out var attrs));
            Assert.Contains("restartVersion", attrs.Known);
            Assert.DoesNotContain("onQuit", attrs.Known);
        }

        [Fact]
        public void RequiredProps_SingleDeclarant_ListsParamsWithNoDefault()
        {
            string self = F("SnakeGame/Screen.uitkx",
                "export VirtualNode Screen() {\n  return (<VisualElement />);\n}\n");
            F("SnakeGame/Hud.uitkx",
                "export VirtualNode Hud(int score, int lives = 3) {\n"
                + "  return (<VisualElement />);\n}\n");

            var publisher = MakePublisher(out _);
            var ds = Parse(File.ReadAllText(self), self);

            var known = publisher.BuildKnownAttributes(
                new HashSet<string>(), ds, self);

            Assert.True(known.TryGetValue("Hud", out var attrs));
            Assert.Contains("score", attrs.Required.Keys);
            Assert.DoesNotContain("lives", attrs.Required.Keys);
        }

        [Fact]
        public void RequiredProps_AmbiguousDeclarant_IsEmpty()
        {
            // The union fallback is right for "is this attribute known" and
            // exactly wrong for "is this attribute required" — requiring a prop
            // that only one candidate declares would error on a call site that
            // is correct for the component it actually binds to.
            F("MarioGame/MainMenu/MainMenu.uitkx",
                "export VirtualNode MainMenu(System.Action onStartGame) {\n"
                + "  return (<VisualElement />);\n}\n");
            F("GalagaGame/MainMenu/MainMenu.uitkx",
                "export VirtualNode MainMenu(System.Action onNewGame) {\n"
                + "  return (<VisualElement />);\n}\n");
            string importer = F("Elsewhere/Screen.uitkx",
                "export VirtualNode Screen() {\n  return (<VisualElement />);\n}\n");

            var publisher = MakePublisher(out _);
            var ds = Parse(File.ReadAllText(importer), importer);

            var known = publisher.BuildKnownAttributes(
                new HashSet<string>(), ds, importer);

            Assert.True(known.TryGetValue("MainMenu", out var attrs));
            Assert.Contains("onStartGame", attrs.Known);
            Assert.Contains("onNewGame", attrs.Known);
            Assert.Empty(attrs.Required);
        }

        [Fact]
        public void RequiredProps_BuiltInElement_IsEmpty()
        {
            string self = F("SnakeGame/Screen.uitkx",
                "export VirtualNode Screen() {\n  return (<VisualElement />);\n}\n");

            var publisher = MakePublisher(out _);
            var ds = Parse(File.ReadAllText(self), self);

            var known = publisher.BuildKnownAttributes(
                new HashSet<string>(), ds, self);

            Assert.True(known.TryGetValue("Label", out var attrs));
            Assert.Empty(attrs.Required);
        }

        [Fact]
        public void RequiredProps_MutableRefParam_IsExempt()
        {
            string self = F("SnakeGame/Screen.uitkx",
                "export VirtualNode Screen() {\n  return (<VisualElement />);\n}\n");
            F("SnakeGame/Field.uitkx",
                "export VirtualNode Field(Hooks.MutableRef<object> inputRef, string label) {\n"
                + "  return (<VisualElement />);\n}\n");

            var publisher = MakePublisher(out _);
            var ds = Parse(File.ReadAllText(self), self);

            var known = publisher.BuildKnownAttributes(
                new HashSet<string>(), ds, self);

            Assert.True(known.TryGetValue("Field", out var attrs));
            Assert.Contains("label", attrs.Required.Keys);
            Assert.DoesNotContain("inputRef", attrs.Required.Keys);
        }

        [Fact]
        public void RequiredProps_UnderscoreParam_KeyedByPropName()
        {
            string self = F("SnakeGame/Screen.uitkx",
                "export VirtualNode Screen() {\n  return (<VisualElement />);\n}\n");
            F("SnakeGame/Counter.uitkx",
                "export VirtualNode Counter(int _count) {\n"
                + "  return (<VisualElement />);\n}\n");

            var publisher = MakePublisher(out _);
            var ds = Parse(File.ReadAllText(self), self);

            var known = publisher.BuildKnownAttributes(
                new HashSet<string>(), ds, self);

            Assert.True(known.TryGetValue("Counter", out var attrs));
            Assert.True(attrs.Required.TryGetValue("count", out string paramName));
            Assert.Equal("_count", paramName);
        }
    }
}
