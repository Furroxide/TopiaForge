using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Owns checkpoint delivery and its native subscription through the caller lifetime.</summary>
    internal static class OwnerCheckpointSubscription
    {
        internal static IDisposable Subscribe(IModLifetime lifetime, Action<CheckpointSnapshot> handler,
            Func<Action<CheckpointSnapshot>, IDisposable> register, Action<Exception> report)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (lifetime.IsStopping) throw new ObjectDisposedException(nameof(IModLifetime),
                "A stopping context cannot register checkpoint callbacks.");
            return lifetime.Track(register(checkpoint =>
            {
                if (lifetime.IsStopping) return;
                try { handler(checkpoint); }
                catch (Exception exception)
                {
                    try { report(exception); } catch { }
                }
            }));
        }
    }
}
