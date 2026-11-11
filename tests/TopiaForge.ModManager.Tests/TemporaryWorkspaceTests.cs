using System;
using System.IO;
using TopiaForge.TestSupport;

namespace TopiaForge.ModManager.Tests
{
    internal static class TemporaryWorkspaceTests
    {
        private static FileStream? heldUntilExit;

        internal static void Run()
        {
            var outer = Directory.CreateTempSubdirectory("TopiaForgeWorkspaceTests-").FullName;
            try
            {
                var outside = Directory.CreateDirectory(Path.Combine(outer, "outside")).FullName;
                var sentinel = Path.Combine(outside, "unrelated.txt");
                File.WriteAllText(sentinel, "retain unrelated data");
                foreach (var exitCode in new[] { 0, 23 })
                {
                    string? workspace = null;
                    var actual = IsolatedTestProcess.RunChild(
                        new[] { "--temporary-workspace-probe", exitCode.ToString() },
                        created =>
                        {
                            workspace = created;
                            CreateOutsideLink(created, outside);
                        }, temporaryParent: outer);
                    Check(actual == exitCode, "Workspace supervision must preserve the test exit code.");
                    Check(workspace != null && !Directory.Exists(workspace),
                        "Both successful and failed tests must remove their owned temporary workspace.");
                    Check(File.ReadAllText(sentinel) == "retain unrelated data",
                        "Workspace cleanup must preserve unrelated data and symbolic-link targets.");
                }
                var rejected = IsolatedTestProcess.RunChild(new[] { "--temporary-workspace-probe", "../not-owned" }, temporaryParent: outer);
                Check(rejected == 64, "Workspace probes must reject unrecognized probe modes.");
                rejected = IsolatedTestProcess.RunChild(new[] { "--temporary-workspace-probe", "0", "../not-owned" }, temporaryParent: outer);
                Check(rejected == 64, "Workspace probes must not accept a filesystem path argument.");
                if (OperatingSystem.IsWindows()) RejectSuccessfulExitWhenCleanupIsBlocked(outer);
                Console.WriteLine("Temporary workspace cleanup tests passed.");
            }
            finally { Directory.Delete(outer, recursive: true); }
        }

        internal static int Probe(string[] args)
        {
            var workspace = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            Check(workspace.StartsWith(Path.GetDirectoryName(workspace) + Path.DirectorySeparatorChar +
                "TopiaForgeHarness-", StringComparison.Ordinal), "A fresh child must use its parent's private workspace.");
            if (args[1] != "0" && args[1] != "23" && args[1] != "empty") return 64;
            if (args[1] == "empty") return 0;
            var root = Directory.CreateTempSubdirectory("fixture-").FullName;
            Directory.CreateDirectory(root + "-sibling");
            var file = Path.Combine(root, "read-only.txt");
            File.WriteAllText(file, "generated fixture");
            File.SetAttributes(file, FileAttributes.ReadOnly);
            var readOnlyDirectory = Directory.CreateDirectory(Path.Combine(root, "read-only-directory"));
            File.WriteAllText(Path.Combine(readOnlyDirectory.FullName, "child.txt"), "generated fixture");
            readOnlyDirectory.Attributes |= FileAttributes.ReadOnly;
            // Keep a delete-denying handle until process exit. The supervising parent must
            // wait for this process rather than attempting teardown in this process's finally.
            heldUntilExit = new FileStream(Path.Combine(root, "held-open.txt"), FileMode.Create, FileAccess.Write, FileShare.Read);
            return int.Parse(args[1]);
        }

        private static void CreateOutsideLink(string workspace, string outside)
        {
            try { Directory.CreateSymbolicLink(Path.Combine(workspace, "outside"), outside); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            { Console.WriteLine("Symbolic-link coverage requires privileges; the other workspace cases still run."); }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314)
            { Console.WriteLine("Symbolic-link coverage requires privileges; the other workspace cases still run."); }
        }

        private static void RejectSuccessfulExitWhenCleanupIsBlocked(string outer)
        {
            string? workspace = null;
            FileStream? blocked = null;
            try
            {
                var code = IsolatedTestProcess.RunChild(new[] { "--temporary-workspace-probe", "empty" }, created =>
                {
                    workspace = created;
                    blocked = new FileStream(Path.Combine(created, "parent-held.txt"), FileMode.Create,
                        FileAccess.Write, FileShare.None);
                }, temporaryParent: outer);
                Check(code != 0 && Directory.Exists(workspace),
                    "A cleanup failure must remain visible and must not report a successful test run.");
            }
            finally
            {
                blocked?.Dispose();
                if (workspace != null && Directory.Exists(workspace))
                {
                    File.Delete(Path.Combine(workspace, "parent-held.txt"));
                    Directory.Delete(workspace);
                }
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
