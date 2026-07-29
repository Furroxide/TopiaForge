using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.Chronos
{
    internal interface ITimeLeaseHost
    {
        bool ContainsLease(int id);
        void ReleaseLease(int id, bool wasSuspend);
    }

    // A ref-counted lease handle. ForceReset can invalidate the ledger entry without touching consumer-owned
    // handles, so activity is queried from the host instead of inferred from a non-null host reference.
    internal sealed class TimeLease : ITimeLease
    {
        private ITimeLeaseHost? host;
        private readonly int id;
        private readonly bool suspend;

        public TimeLease(ITimeLeaseHost host, int id, bool suspend)
        {
            this.host = host;
            this.id = id;
            this.suspend = suspend;
        }

        public bool IsActive
        {
            get
            {
                var current = Volatile.Read(ref host);
                return current != null && current.ContainsLease(id);
            }
        }

        public void Release()
        {
            Interlocked.Exchange(ref host, null)?.ReleaseLease(id, suspend);
        }

        public void Dispose() => Release();
    }
}
