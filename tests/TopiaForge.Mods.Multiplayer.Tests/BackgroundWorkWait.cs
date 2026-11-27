using System.Diagnostics;
using System.Globalization;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Waits for named provider work without advancing a rig's virtual clock.</summary>
internal sealed class BackgroundWorkWait
{
    private readonly List<(string Peer, string Operation, Task Task)> pending = new();

    internal int Count => pending.Count;

    internal void Add(string peer, IEnumerable<(string Operation, Task Task)> work)
    {
        foreach (var item in work) pending.Add((peer, item.Operation, item.Task));
    }

    internal void Wait(Stopwatch waiting, TimeSpan bound)
    {
        if (waiting.Elapsed > bound)
        {
            // The rig supplies test labels; operation names are fixed provider labels. Never format a task or its result.
            throw new InvalidOperationException("Assertion failed: background work exceeded " +
                bound.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s of real time: " +
                string.Join("; ", pending.Select(item => item.Peer + ": " + item.Operation)));
        }

        Task.WaitAny(pending.Select(item => item.Task).ToArray(), 10);
    }
}
