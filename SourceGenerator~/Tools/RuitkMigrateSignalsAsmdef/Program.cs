using System;
using System.IO;
using System.Linq;

namespace Ruitk.SourceGenerator.Tools
{
    internal static class Program
    {
        private const string Usage =
            "RuitkMigrateSignalsAsmdef - adds the Ruitk.Signals reference after the 0.21.0 split.\n"
            + "\n"
            + "  dotnet run --project SourceGenerator~/Tools/RuitkMigrateSignalsAsmdef -- <projectPath> [--dry-run]\n"
            + "\n"
            + "  <projectPath>  a Unity project root (the folder holding Assets/ and Packages/),\n"
            + "                 or any folder beneath it.\n"
            + "  --dry-run      report what would change and write nothing.\n"
            + "\n"
            + "Adds the reference to the assembly definitions that need it and no others:\n"
            + "those that reference Ruitk.Shared / Runtime / Ugui / Editor AND whose own sources\n"
            + "name a signal type, or whose .uitkx files call useSignal. Idempotent - a second\n"
            + "run reports 0 changes. Never edits the package's own assembly definitions.";

        private static int Main(string[] args)
        {
            if (args.Length == 0 || args.Any(a => a == "-h" || a == "--help"))
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            bool dryRun = args.Any(a => a == "--dry-run");
            string? target = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));

            if (target == null)
            {
                Console.Error.WriteLine("x No project path given.\n\n" + Usage);
                return 2;
            }
            if (!Directory.Exists(target))
            {
                Console.Error.WriteLine("x Not a directory: " + target);
                return 2;
            }

            string root = Path.GetFullPath(target);
            Console.WriteLine("Scanning " + root + (dryRun ? "  (dry run)" : ""));

            var result = SignalsAsmdefMigrator.Run(root, dryRun);

            foreach (string file in result.Changed)
            {
                Console.WriteLine((dryRun ? "  would add " : "  added    ") + "Ruitk.Signals -> " + Relative(root, file));
            }
            foreach (string file in result.AlreadyCorrect)
            {
                Console.WriteLine("  ok        " + Relative(root, file));
            }
            foreach (string file in result.GuidReferences)
            {
                Console.WriteLine("  CHECK     " + Relative(root, file) + " - references assemblies by GUID, cannot be read by name");
            }
            foreach (string file in result.Failed)
            {
                Console.Error.WriteLine("  FAILED    " + file);
            }

            Console.WriteLine();
            Console.WriteLine(
                result.Changed.Count + " to change, "
                + result.AlreadyCorrect.Count + " already correct, "
                + result.NotAffected.Count + " unaffected, "
                + result.GuidReferences.Count + " to check by hand, "
                + result.Failed.Count + " failed.");

            if (result.GuidReferences.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(
                    "An assembly definition can reference others by GUID instead of by name, and a\n"
                    + "GUID cannot be resolved without Unity. Open each one listed as CHECK and add\n"
                    + "Ruitk.Signals if it uses signals - or let the in-editor check offer the fix.");
            }

            if (result.Failed.Count > 0)
            {
                return 1;
            }
            if (dryRun && result.Changed.Count > 0)
            {
                return 1;
            }
            return 0;
        }

        private static string Relative(string root, string file)
        {
            try
            {
                return Path.GetRelativePath(root, file);
            }
            catch
            {
                return file;
            }
        }
    }
}
