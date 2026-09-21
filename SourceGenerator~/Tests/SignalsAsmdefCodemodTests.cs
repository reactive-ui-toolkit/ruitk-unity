using System;
using System.IO;
using System.Linq;
using Ruitk.SourceGenerator.Tools;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// Pins the 0.21.0 signals-split codemod. The rules it encodes are not obvious and two of
    /// them were settled by compiling rather than by reading, so they are worth holding:
    /// a <c>.uitkx</c> calling lowercase <c>useSignal</c> DOES need the reference (the
    /// generator emits a wrapper naming <c>Signal&lt;T&gt;</c>), while C# calling
    /// <c>Hooks.UseSignal("key", 0)</c> does NOT (that overload names nothing from the new
    /// assembly, verified against Ruitk.Ugui.Tests).
    /// </summary>
    public sealed class SignalsAsmdefCodemodTests : IDisposable
    {
        private readonly string _root;

        public SignalsAsmdefCodemodTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ruitk-signals-codemod-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string Asmdef(string assembly, params string[] references)
        {
            string dir = Path.Combine(_root, "Assets", assembly);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, assembly + ".asmdef");
            string refs = references.Length == 0
                ? "[]"
                : "[\n    " + string.Join(",\n    ", references.Select(r => "\"" + r + "\"")) + "\n  ]";
            File.WriteAllText(path, "{\n  \"name\": \"" + assembly + "\",\n  \"references\": " + refs + "\n}\n");
            return path;
        }

        private void Source(string assembly, string fileName, string contents) =>
            File.WriteAllText(Path.Combine(_root, "Assets", assembly, fileName), contents);

        private SignalsAsmdefMigrator.Result Run(bool dryRun = false) =>
            SignalsAsmdefMigrator.Run(_root, dryRun);

        // ── What needs the reference ────────────────────────────────────────

        [Fact]
        public void NamingASignalTypeInCSharpNeedsTheReference()
        {
            string asmdef = Asmdef("Game.Uses", "Ruitk.Shared");
            Source("Game.Uses", "A.cs", "using Ruitk.Signals;\npublic class A { Signal<int> s; }\n");

            Assert.Contains(asmdef, Run().Changed);
            Assert.Contains("\"Ruitk.Signals\"", File.ReadAllText(asmdef));
        }

        [Fact]
        public void AUitkxCallingLowercaseUseSignalNeedsTheReference()
        {
            // The generator emits `useSignal(Ruitk.Signals.Signal<T>)` into any file that calls
            // useSignal, so the dependency arrives through generated code the author never sees.
            string asmdef = Asmdef("Game.Markup", "Ruitk.Shared");
            Source("Game.Markup", "X.uitkx", "export VirtualNode X() {\n  var v = useSignal(\"key\", 0);\n  return (<Label text=\"x\" />);\n}\n");

            Assert.Contains(asmdef, Run().Changed);
        }

        // ── What does NOT ───────────────────────────────────────────────────

        [Fact]
        public void CSharpCallingTheStringKeyedHookDoesNotNeedTheReference()
        {
            // Hooks.UseSignal("key", 0) binds to an overload whose signature mentions nothing
            // from Ruitk.Signals. Ruitk.Ugui.Tests does exactly this and compiles untouched.
            string asmdef = Asmdef("Game.HookOnly", "Ruitk.Shared");
            Source("Game.HookOnly", "B.cs", "public class B { void M() { var v = Ruitk.Hooks.UseSignal<int>(\"k\", 0); } }\n");

            var result = Run();
            Assert.DoesNotContain(asmdef, result.Changed);
            Assert.DoesNotContain("\"Ruitk.Signals\"", File.ReadAllText(asmdef));
        }

        [Fact]
        public void AnAssemblyThatNeverTouchedSignalsIsLeftAlone()
        {
            string asmdef = Asmdef("Game.Plain", "Ruitk.Shared");
            Source("Game.Plain", "Y.uitkx", "export VirtualNode Y() {\n  var (n, set) = useState(0);\n  return (<Label text=\"y\" />);\n}\n");

            Assert.DoesNotContain(asmdef, Run().Changed);
        }

        [Fact]
        public void AnAssemblyThatNeverReferencedTheToolkitIsLeftAlone()
        {
            // No former carrier referenced means it could not have named a signal type before
            // the split either, so the split cannot have broken it.
            string asmdef = Asmdef("Game.Unrelated");
            Source("Game.Unrelated", "C.cs", "public class C { }\n");

            Assert.DoesNotContain(asmdef, Run().Changed);
        }

        [Fact]
        public void ThePackagesOwnAssembliesAreNeverEdited()
        {
            string dir = Path.Combine(_root, "Packages", "com.reactiveuitoolkit", "Shared");
            Directory.CreateDirectory(dir);
            string asmdef = Path.Combine(dir, "Ruitk.Shared.asmdef");
            File.WriteAllText(asmdef, "{\n  \"name\": \"Ruitk.Shared\",\n  \"references\": []\n}\n");
            File.WriteAllText(Path.Combine(dir, "S.cs"), "using Ruitk.Signals;\npublic class S { Signal<int> s; }\n");

            var result = Run();
            Assert.DoesNotContain(asmdef, result.Changed);
            Assert.DoesNotContain(asmdef, result.NotAffected);
        }

        // ── How it edits ────────────────────────────────────────────────────

        [Fact]
        public void TheEditIsIdempotent()
        {
            Asmdef("Game.Twice", "Ruitk.Shared");
            Source("Game.Twice", "A.cs", "using Ruitk.Signals;\npublic class A { Signal<int> s; }\n");

            Assert.Single(Run().Changed);
            var second = Run();
            Assert.Empty(second.Changed);
            Assert.Single(second.AlreadyCorrect);
        }

        [Fact]
        public void DryRunWritesNothing()
        {
            string asmdef = Asmdef("Game.Dry", "Ruitk.Shared");
            Source("Game.Dry", "A.cs", "using Ruitk.Signals;\npublic class A { Signal<int> s; }\n");
            string before = File.ReadAllText(asmdef);

            Assert.Single(Run(dryRun: true).Changed);
            Assert.Equal(before, File.ReadAllText(asmdef));
        }

        [Fact]
        public void AnEmptyReferencesArrayIsFilledRatherThanReplaced()
        {
            string text = "{\n  \"name\": \"X\",\n  \"references\": [],\n  \"autoReferenced\": true\n}\n";

            string updated = SignalsAsmdefMigrator.InsertReference(text, "Ruitk.Signals");

            Assert.Contains("\"Ruitk.Signals\"", updated);
            Assert.Contains("\"autoReferenced\": true", updated);
            Assert.DoesNotContain("[]", updated);
        }

        [Fact]
        public void TheInlineArrayFormIsHandled()
        {
            string text = "{ \"name\": \"X\", \"references\": [\"Ruitk.Shared\"] }";

            string updated = SignalsAsmdefMigrator.InsertReference(text, "Ruitk.Signals");

            Assert.Contains("\"Ruitk.Signals\", \"Ruitk.Shared\"", updated);
        }

        [Fact]
        public void SurroundingFormattingSurvives()
        {
            string asmdef = Asmdef("Game.Format", "Ruitk.Shared", "Ruitk.Runtime");
            Source("Game.Format", "A.cs", "using Ruitk.Signals;\npublic class A { Signal<int> s; }\n");

            Run();
            string text = File.ReadAllText(asmdef);

            // The one-line insertion keeps every other line byte-identical, so the diff a
            // reviewer sees is the added reference and nothing else.
            Assert.Contains("    \"Ruitk.Signals\",\n    \"Ruitk.Shared\",\n    \"Ruitk.Runtime\"", text.Replace("\r\n", "\n"));
        }

        [Fact]
        public void ANestedAssemblyDefinitionOwnsItsOwnSources()
        {
            // The outer assembly must not be migrated because of a signal used by an inner one.
            string outer = Asmdef("Game.Outer", "Ruitk.Shared");
            string innerDir = Path.Combine(_root, "Assets", "Game.Outer", "Inner");
            Directory.CreateDirectory(innerDir);
            File.WriteAllText(
                Path.Combine(innerDir, "Game.Inner.asmdef"),
                "{\n  \"name\": \"Game.Inner\",\n  \"references\": [\n    \"Ruitk.Shared\"\n  ]\n}\n");
            File.WriteAllText(
                Path.Combine(innerDir, "A.cs"),
                "using Ruitk.Signals;\npublic class A { Signal<int> s; }\n");

            var result = Run();

            Assert.DoesNotContain(outer, result.Changed);
            Assert.Contains(result.Changed, c => c.EndsWith("Game.Inner.asmdef", StringComparison.Ordinal));
        }

        [Fact]
        public void GuidStyleReferencesAreReportedRatherThanGuessedAt()
        {
            string dir = Path.Combine(_root, "Assets", "Game.Guid");
            Directory.CreateDirectory(dir);
            string asmdef = Path.Combine(dir, "Game.Guid.asmdef");
            File.WriteAllText(asmdef, "{\n  \"name\": \"Game.Guid\",\n  \"references\": [\n    \"GUID:0bd237d59e7aaf581270b181f50416d1\"\n  ]\n}\n");
            File.WriteAllText(Path.Combine(dir, "A.cs"), "using Ruitk.Signals;\npublic class A { Signal<int> s; }\n");

            var result = Run();

            Assert.Contains(asmdef, result.GuidReferences);
            Assert.DoesNotContain(asmdef, result.Changed);
        }
    }
}
