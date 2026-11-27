extern alias provider;
using System.Reflection;
using System.Runtime.CompilerServices;
using provider::TopiaForge.Multiplayer.Network;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// An identity load that fails, and the password key that waits for it, are observed however they are let go: when a
/// frame sees the failure, when hosting stopped first, and when closing comes before or after the read fails. The file
/// service's text must never reach global unobserved-exception subscribers.
/// </summary>
internal static class IdentityLoadObservationTests
{
    internal static void AFailedIdentityLoadIsObservedHoweverItIsLetGo()
    {
        var leaks = new List<string>();
        foreach (var password in new[] { false, true })
            foreach (var mode in new[] { "fails", "stops-first", "closes-first", "closes-after" })
            {
                var raised = 0;
                EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
                {
                    if (args.Exception.Flatten().InnerExceptions.Any(error => error is ReadFailure))
                    {
                        Interlocked.Increment(ref raised);
                        args.SetObserved();
                    }
                };
                TaskScheduler.UnobservedTaskException += handler;
                try
                {
                    var label = (password ? "password " : "open ") + mode;
                    var weak = Exercise(password, mode, label);
                    for (var count = 0; count < 10; count++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    }
                    TransportAssert.That(weak.All(item => !item.IsAlive), label + ": the failed identity tasks were collected");
                    if (Volatile.Read(ref raised) > 0) leaks.Add(label + " (" + raised + ")");
                }
                finally { TaskScheduler.UnobservedTaskException -= handler; }
            }

        TransportAssert.That(leaks.Count == 0, "no failed identity read reaches the unobserved-exception event: " + string.Join(", ", leaks));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Exercise(bool password, string mode, string label)
    {
        var rig = new LiveRig(221);
        try
        {
            var failure = new ReadFailure();
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.HostFiles.BeforeRead = path => path == IdentityStore.IdentityPath ? read.Task : Task.CompletedTask;
            var live = rig.Host.Live!;
            live.StartHosting(new HostSettings("Held") { Port = 0, Password = password ? "open sesame" : null });
            var load = live.PrepareAsync();
            var key = password ? KeyOf(live) : null;
            if (mode == "stops-first") live.StopHosting();
            if (mode == "closes-first") rig.Dispose();
            read.SetException(failure);
            if (!SpinWait.SpinUntil(() => load.IsCompleted && (key == null || key.IsCompleted), TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException("Assertion failed: " + label + ": the failed read lands");
            }

            if (mode == "closes-after") rig.Dispose();
            if (mode == "fails")
            {
                TransportAssert.That(rig.Run(() => live.HostPhase == HostPhase.Failed) && live.HostFailure == LiveFailure.IdentityUnavailable,
                    label + ": hosting fails as the identity is unavailable");
            }

            if (mode == "stops-first")
            {
                TransportAssert.That(rig.Run(() => live.IdentityFailed) && live.HostPhase == HostPhase.Idle,
                    label + ": the load fails after hosting stopped");
            }

            TransportAssert.That(load.IsFaulted && (key == null || key.IsFaulted), label + ": observing keeps each task's failure");
            var weak = new List<WeakReference> { new(load), new(failure) };
            if (key != null) weak.Add(new WeakReference(key));
            return weak.ToArray();
        }
        finally
        {
            rig.Dispose();
        }
    }

    /// <summary>The password key a pending host derives, as hosting holds it until the identity lands.</summary>
    private static Task KeyOf(LiveSessions live) =>
        typeof(LiveSessions).GetField("hostKey", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(live) as Task ??
        throw new InvalidOperationException("Assertion failed: a password host derives its key");

    private sealed class ReadFailure : IOException { }
}
