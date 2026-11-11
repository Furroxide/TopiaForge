using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TopiaForge.ModManager.Tests
{
    internal static class FocusedHarnessDirectoryTests
    {
        internal static void Run()
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeHarnessDirectories-");
            try
            {
                Check(owned, "sdk-lifecycle", "TopiaForgeSdkLifecycleTests-");
                Check(owned, "manifest-v6", "TopiaForgeManifestV6Tests-");
                Console.WriteLine("Focused harness directories: both real entry points clean their fixtures and preserve existing siblings.");
            }
            finally { owned.Delete(recursive: true); }
        }

        private static void Check(DirectoryInfo owned, string selector, string prefix)
        {
            var temporary = owned.CreateSubdirectory(selector);
            var sibling = temporary.CreateSubdirectory(prefix + "existing");
            var marker = Path.Combine(sibling.FullName, "keep.txt");
            const string expected = "Existing sibling data must survive fixture cleanup.";
            File.WriteAllText(marker, expected);
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test host is unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Program.FindRepoRoot(),
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--" + selector);
            // Each child gets its own real temporary parent on Windows and Unix. This catches both root leaks
            // and helper fixtures that concatenate a suffix onto the root and escape its cleanup boundary.
            start.Environment["TEMP"] = temporary.FullName;
            start.Environment["TMP"] = temporary.FullName;
            start.Environment["TMPDIR"] = temporary.FullName;
            using var child = Process.Start(start) ?? throw new InvalidOperationException("The focused test host did not start.");
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(60_000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
                throw new InvalidOperationException("Focused fixture cleanup check timed out: " + selector);
            }
            if (child.ExitCode != 0)
                throw new InvalidOperationException("Focused fixture check failed: " + selector + "\n"
                    + output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
            if (File.ReadAllText(marker) != expected)
                throw new InvalidOperationException("Focused fixture cleanup changed existing sibling data: " + selector);
            if (!Directory.EnumerateFileSystemEntries(temporary.FullName).Select(Path.GetFileName)
                .SequenceEqual(new[] { sibling.Name }))
                throw new InvalidOperationException("Focused fixture cleanup left data outside its owned root: " + selector);
        }
    }
}
