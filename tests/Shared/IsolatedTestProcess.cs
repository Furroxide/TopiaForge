using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.TestSupport
{
    // A separate parent owns the workspace: Windows can retain mapped fixture assemblies
    // until the test process exits, even after its collectible runtime has been disposed.
    internal static class IsolatedTestProcess
    {
        private const string ChildMarker = "TOPIAFORGE_TEST_WORKSPACE_CHILD";

        internal static int Run(string[] args, Func<string[], int> tests) =>
            Environment.GetEnvironmentVariable(ChildMarker) == "1" ? tests(args) : RunChild(args);

        internal static int RunChild(string[] args, Action<string>? observeWorkspace = null)
        {
            var workspace = Directory.CreateTempSubdirectory("TopiaForgeHarness-").FullName;
            var code = 1;
            var safeToClean = true;
            try
            {
                observeWorkspace?.Invoke(workspace);
                var host = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test process host.");
                var start = new ProcessStartInfo(host)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
                foreach (var arg in args) start.ArgumentList.Add(arg);
                foreach (var variable in new[] { "TMP", "TEMP", "TMPDIR" }) start.Environment[variable] = workspace;
                start.Environment[ChildMarker] = "1";
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start test process.");
                safeToClean = false;
                ConsoleCancelEventHandler cancel = (_, e) =>
                {
                    e.Cancel = true;
                    try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { } // The child may have exited between the two calls.
                };
                Console.CancelKeyPress += cancel;
                try
                {
                    var stdout = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
                    var stderr = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
                    child.WaitForExit();
                    safeToClean = true;
                    code = child.ExitCode;
                    Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
                finally
                {
                    Console.CancelKeyPress -= cancel;
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit();
                    }
                    safeToClean = true;
                }
            }
            finally
            {
                // Never clean before process exit, and never walk through fixture links.
                if (!safeToClean) Console.Error.WriteLine("Test process did not retire; temporary workspace retained.");
                for (var attempt = 0; safeToClean; attempt++)
                {
                    try { DeleteTree(workspace); break; }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        if (attempt < 3) { Thread.Sleep(50 * (attempt + 1)); continue; }
                        Console.Error.WriteLine("Temporary test workspace cleanup failed after retries (" +
                            error.GetType().Name + ").");
                        if (code == 0) code = 1;
                        break;
                    }
                }
            }
            return code;
        }

        private static void DeleteTree(string directory)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory); }
            catch (DirectoryNotFoundException) { return; }
            catch (FileNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                new DirectoryInfo(directory).Delete();
                return;
            }
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (entry is DirectoryInfo link) link.Delete();
                    else entry.Delete();
                }
                else if (entry is DirectoryInfo child) DeleteTree(child.FullName);
                else
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                    entry.Delete();
                }
            }
            var root = new DirectoryInfo(directory);
            root.Attributes &= ~FileAttributes.ReadOnly;
            root.Delete();
        }
    }
}
