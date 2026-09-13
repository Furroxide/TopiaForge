using System.Diagnostics;

namespace TopiaForge.Acceptance.Windows;

internal sealed record RuntimeWaitOutcome(bool Exited, bool Interrupted, long ElapsedMilliseconds, int? ExitCode, IReadOnlyList<object> Snapshots);

/// One bounded wait shared by the QA observer and the fixture verification. The deadline is fixed;
/// snapshots are read-only diagnostics taken while the original process runs and again before the
/// owner decides about termination. The wait itself never terminates anything.
internal static class ProvisioningRuntimeWait
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);
    internal static readonly int[] SnapshotSeconds = { 15, 45, 75 };
    internal static RuntimeWaitOutcome Wait(ProvisioningProcess process, Func<bool> interrupted)
    {
        var timer = Stopwatch.StartNew();
        var snapshots = new List<object>();
        var next = 0;
        while (!process.HasExited && !interrupted() && timer.Elapsed < Deadline)
        {
            if (next < SnapshotSeconds.Length && timer.Elapsed >= TimeSpan.FromSeconds(SnapshotSeconds[next]))
            {
                snapshots.Add(ProvisioningDiagnostics.Snapshot(process, "running-" + SnapshotSeconds[next] + "s", timer.ElapsedMilliseconds));
                next++;
            }
            Thread.Sleep(50);
        }
        var exited = process.HasExited;
        var wasInterrupted = !exited && interrupted();
        snapshots.Add(ProvisioningDiagnostics.Snapshot(process, exited ? "exited" : wasInterrupted ? "interrupted" : "deadline-before-termination", timer.ElapsedMilliseconds));
        return new(exited, wasInterrupted, timer.ElapsedMilliseconds, exited ? process.ExitCode : null, snapshots.AsReadOnly());
    }
}
internal sealed record PlayerLogFact(string Path, bool Exists, long Bytes, string? Sha256)
{
    internal static PlayerLogFact Read(string? path)
    {
        if (path == null || !File.Exists(path)) return new(ProvisioningProcess.PlayerLogName, false, 0, null);
        var length = new FileInfo(path).Length;
        // Retained in place; a log beyond this bound is kept but not hashed inline.
        return new(ProvisioningProcess.PlayerLogName, true, length, length <= 64L * 1024 * 1024 ? BoundedJson.Hash(path) : null);
    }
}
