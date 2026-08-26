using System;
using System.Collections.Generic;
using System.IO;
using Ruitk.Language;
using Ruitk.Language.Formatter;
using Ruitk.Language.Parser;
using Xunit;

namespace Ruitk.SourceGenerator.Tests
{
    /// <summary>
    /// VE-14: the printer entry point <c>Format(ParseResult, source, path, out outcome)</c>
    /// must be byte-identical to the parse-internally path for every input, and the
    /// outcome surface must expose the formatter's deliberate identity fallbacks
    /// (which the RUITK Builder treats as hard errors on dirty transactions).
    /// </summary>
    public sealed class AstPrinterEntryTests
    {
        private static string RepoRoot()
        {
            var dir = Directory.GetCurrentDirectory();
            while (dir != null && !File.Exists(Path.Combine(dir, "package.json")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir;
        }

        private static ParseResult Parse(string source, string filePath)
        {
            var diags = new List<ParseDiagnostic>();
            var directives = DirectiveParser.Parse(source, filePath, diags);
            var nodes = UitkxParser.Parse(source, filePath, directives, diags);
            return new ParseResult(directives, nodes, System.Collections.Immutable.ImmutableArray.CreateRange(diags));
        }

        [Fact]
        public void PrinterPath_IsByteIdentical_ToParsePath_AcrossSamplesCorpus()
        {
            string samples = Path.Combine(RepoRoot(), "Samples");
            Assert.True(Directory.Exists(samples), "Samples/ not found from test cwd");

            int checkedFiles = 0;
            foreach (string file in Directory.GetFiles(samples, "*.uitkx", SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(file).Replace("\r\n", "\n").Replace("\r", "\n");

                var viaParse = new AstFormatter();
                string expected = viaParse.Format(source, file, out FormatOutcome parseOutcome);

                var viaPrinter = new AstFormatter();
                string actual = viaPrinter.Format(Parse(source, file), source, file, out FormatOutcome printerOutcome);

                Assert.Equal(parseOutcome, printerOutcome);
                Assert.Equal(expected, actual);
                checkedFiles++;
            }

            Assert.True(checkedFiles > 30, $"corpus too small ({checkedFiles} files) - Samples/ layout changed?");
        }

        [Fact]
        public void Outcome_Formatted_ForOrdinaryComponent()
        {
            string source = "export VirtualNode Hello() {\n  return (\n    <Label text=\"hi\" />\n  );\n}\n";
            var f = new AstFormatter();
            f.Format(source, "Hello.uitkx", out FormatOutcome outcome);
            Assert.Equal(FormatOutcome.Formatted, outcome);
        }

        [Fact]
        public void Outcome_ParseErrors_SurfacedAndInputReturned()
        {
            string source = "export VirtualNode Broken() {\n  return (\n    <Label text=\n  );\n}\n";
            var f = new AstFormatter();
            string result = f.Format(source, "Broken.uitkx", out FormatOutcome outcome);
            Assert.Equal(FormatOutcome.ParseErrors, outcome);
            Assert.Equal(source, result);
        }

        [Fact]
        public void Outcome_MultipleComponents_SurfacedAndInputReturned()
        {
            string source =
                "export VirtualNode A() {\n  return (\n    <Label text=\"a\" />\n  );\n}\n\n"
                + "export VirtualNode B() {\n  return (\n    <Label text=\"b\" />\n  );\n}\n";
            var f = new AstFormatter();
            string result = f.Format(source, "Two.uitkx", out FormatOutcome outcome);
            Assert.Equal(FormatOutcome.MultipleComponents, outcome);
            Assert.Equal(source, result);
        }

        [Fact]
        public void PrinterOverload_RejectsCarriageReturns_Loudly()
        {
            string lf = "export VirtualNode X() {\n  return (\n    <Label text=\"x\" />\n  );\n}\n";
            var parsed = Parse(lf, "X.uitkx");
            string crlf = lf.Replace("\n", "\r\n");
            var f = new AstFormatter();
            Assert.Throws<ArgumentException>(() => f.Format(parsed, crlf, "X.uitkx", out _));
        }

        [Fact]
        public void PrinterOverload_NullArguments_Throw()
        {
            var f = new AstFormatter();
            Assert.Throws<ArgumentNullException>(() => f.Format((ParseResult)null, "x", "X.uitkx", out _));
            var parsed = Parse("", "X.uitkx");
            Assert.Throws<ArgumentNullException>(() => f.Format(parsed, null, "X.uitkx", out _));
        }
    }
}
