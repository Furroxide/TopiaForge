using System.Security.Cryptography;

namespace TopiaForge.Acceptance.Windows;

/// Developer verification of the headless launch/log/wait path against a minimal Unity player that
/// quits from its first Update. It reuses the exact process ownership, fixed arguments, deadline and
/// diagnostics of the QA observer, without QA identity, inventory or firewall admission. Its result is
/// fixture evidence about the launch mechanism; it says nothing about the game, isolation or a gate.
internal static class ProvisioningFixtureShutdown
{
    internal static int Run(string executablePath, string runRoot)
    {
        var executable = BoundedJson.PhysicalPath(executablePath);
        var directory = Path.GetDirectoryName(executable) ?? throw new InvalidDataException("Fixture executable has no directory.");
        if (!File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(directory, "UnityPlayer.dll")))
            throw new InvalidDataException("The fixture must be a Unity Windows player next to its UnityPlayer.dll.");
        var root = BoundedJson.PhysicalPath(runRoot);
        if (File.Exists(root) || Directory.Exists(root)) throw new InvalidDataException("Fixture run output must be new.");
        if (!Directory.Exists(Path.GetDirectoryName(root))) throw new InvalidDataException("Fixture run parent must exist.");
        Directory.CreateDirectory(root);
        var logPath = ProvisioningProcess.ValidatePlayerLogPath(Path.Combine(root, ProvisioningProcess.PlayerLogName), root);
        var id = "probe-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var identity = WindowsIdentityProbe.Read(NativeMethods.GetCurrentProcess());
        var started = DateTime.UtcNow;
        var failures = new List<string>();
        RuntimeWaitOutcome? outcome = null; ProcessStamp? stamp = null; string? commandLine = null;
        var forced = false; var exitConfirmed = false; var processStarted = false;
        try
        {
            using var process = ProvisioningProcess.StartSuspended(executable, directory, id, logPath);
            processStarted = true;
            commandLine = process.CommandLine;
            try
            {
                process.Prepare(identity);
                stamp = process.Stamp;
                process.Resume(identity);
                outcome = ProvisioningRuntimeWait.Wait(process, () => false);
                if (!outcome.Exited) { forced = true; failures.Add("original-process-timeout"); }
            }
            catch (Exception error) { failures.Add("fixture-refused-" + error.GetType().Name + ":" + error.Message); }
            finally
            {
                try { if (!process.HasExited) forced = true; exitConfirmed = process.HasExited || process.TerminateAndConfirm(); }
                catch { failures.Add("original-process-cleanup-unconfirmed"); }
            }
        }
        catch (Exception error) { failures.Add("fixture-start-" + error.GetType().Name + ":" + error.Message); }
        var log = PlayerLogFact.Read(logPath);
        var passed = processStarted && outcome != null && outcome.Exited && !forced && exitConfirmed && outcome.ExitCode == 0 && log.Exists && log.Bytes > 0 && failures.Count == 0;
        BoundedJson.WriteNew(Path.Combine(root, "fixture-shutdown-result.json"), new
        {
            schemaVersion = 1, kind = "sandbox-provisioning-fixture-shutdown-v1", requestId = id, startedAtUtc = started.ToString("O"),
            completedAtUtc = DateTime.UtcNow.ToString("O"), executablePath = executable, executableSha256 = BoundedJson.Hash(executable),
            unityPlayerSha256 = BoundedJson.Hash(Path.Combine(directory, "UnityPlayer.dll")), commandLine,
            launchArguments = ProvisioningProcess.LaunchArguments(logPath), deadlineSeconds = (int)ProvisioningRuntimeWait.Deadline.TotalSeconds,
            processStarted, process = stamp, exitedUnforced = outcome?.Exited == true && !forced, forceTerminated = forced,
            originalProcessExitConfirmed = exitConfirmed, exitCode = outcome?.ExitCode, runtimeMilliseconds = outcome?.ElapsedMilliseconds,
            playerLog = log, snapshots = outcome?.Snapshots ?? (IReadOnlyList<object>)Array.Empty<object>(), failures,
            status = passed ? "passed" : "failed", gameExecuted = false, isolationAdmitted = false, qualifiesRelease = false
        });
        Console.WriteLine(passed ? "Fixture player exited unforced with retained Unity log; launch/log/wait mechanism verified for this fixture only."
            : "Fixture shutdown verification did not pass; review the private run directory.");
        return passed ? 0 : 1;
    }
}
