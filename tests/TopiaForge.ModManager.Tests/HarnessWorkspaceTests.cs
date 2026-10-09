using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace TopiaForge.ModManager.Tests
{
    internal static class HarnessWorkspaceTests
    {
        internal static void Run()
        {
            var failures = new List<Exception>();
            foreach (var flag in new[] { "--sdk-lifecycle", "--session-lifecycle", "--manifest-v6" })
            {
                try { RunCase(flag); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0) throw new AggregateException("Harness workspace ownership failed.", failures);
            Console.WriteLine("HarnessWorkspaceTests passed (3 focused child processes).");
        }

        private static void RunCase(string flag)
        {
            var owned = Directory.CreateTempSubdirectory("TopiaForgeHarnessWorkspace-");
            var safeToClean = true;
            try
            {
                var sentinel = Path.Combine(owned.FullName, "unrelated-sibling.txt");
                File.WriteAllText(sentinel, "retain unrelated sibling");
                var host = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test process executable.");
                var start = new ProcessStartInfo(host)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(HarnessWorkspaceTests).Assembly.Location);
                start.ArgumentList.Add(flag);
                foreach (var name in new[] { "TMP", "TEMP", "TMPDIR" }) start.Environment[name] = owned.FullName;
                using var child = new Process { StartInfo = start };
                if (!child.Start()) throw new InvalidOperationException("Could not start " + flag + ".");
                safeToClean = false;
                var stdout = child.StandardOutput.ReadToEndAsync();
                var stderr = child.StandardError.ReadToEndAsync();
                try
                {
                    if (!child.WaitForExit(60000)) throw new TimeoutException(flag + " exceeded one minute.");
                    Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    if (child.ExitCode != 0)
                        throw new InvalidOperationException(flag + " failed: " + stdout.Result + stderr.Result);
                }
                finally
                {
                    // Keep the original process handle: never reopen a PID, and retire the child before
                    // deleting its private temporary parent. A failed drain leaves that parent intact.
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        if (!child.WaitForExit(10000)) throw new TimeoutException(flag + " did not exit after termination.");
                    }
                    Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    safeToClean = true;
                }
                var entries = owned.GetFileSystemInfos();
                if (entries.Length != 1 || entries[0].Name != "unrelated-sibling.txt" ||
                    File.ReadAllText(sentinel) != "retain unrelated sibling")
                    throw new InvalidOperationException(flag + " left fixture siblings outside its root or changed unrelated data.");
            }
            finally
            {
                if (safeToClean) owned.Delete(recursive: true);
            }
        }
    }
}
