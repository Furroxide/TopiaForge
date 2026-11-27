using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Internal
{
    // Shared by the runtime and testing kit. Each instance belongs to one player incarnation.
    internal sealed class AvatarPresentation
    {
        internal const int MaximumLeases = 16;
        private readonly List<Lease> leases = new List<Lease>();
        private readonly Action? changed;
        private bool retired;
        internal AvatarPresentation(AvatarAppearance original, Action? changed = null) { Original = original; Current = original; this.changed = changed; }
        internal AvatarAppearance Original { get; }
        internal AvatarAppearance Current { get; private set; }
        internal string? Name { get; private set; }
        internal bool Visible { get; private set; } = true;
        internal bool PoweredDown { get; private set; }
        internal bool IsAlive => !retired;
        internal int Count => leases.Count;
        internal OperationResult<IPlayerAppearanceLease> Add(IModLifetime lifetime, byte kind, AvatarAppearance? appearance = null, string? name = null, bool flag = true)
        {
            if (retired || lifetime.IsStopping) return OperationResult<IPlayerAppearanceLease>.Failure(ModErrorCode.Cancelled, "The presentation scope has ended.");
            if (leases.Count >= MaximumLeases) return OperationResult<IPlayerAppearanceLease>.Failure(ModErrorCode.Conflict, "A participant can have at most 16 presentation leases. Dispose unused changes first.");
            var lease = new Lease(this, lifetime, kind, appearance, name, flag);
            leases.Add(lease);
            lease.Track();
            Recompute();
            return OperationResult<IPlayerAppearanceLease>.Success(lease);
        }
        internal void Take(AvatarAppearance appearance, string? name, bool visible, bool poweredDown)
        {
            if (Current.Equals(appearance) && Name == name && Visible == visible && PoweredDown == poweredDown) return;
            Current = appearance; Name = name; Visible = visible; PoweredDown = poweredDown;
            changed?.Invoke();
        }
        internal void Retire()
        {
            if (retired) return;
            retired = true;
            while (leases.Count != 0) leases[leases.Count - 1].Dispose();
        }
        private void Recompute()
        {
            var appearance = Original; string? name = null; var visible = true; var poweredDown = false;
            for (var i = 0; i < leases.Count; i++)
            {
                var lease = leases[i];
                if (!lease.IsActive) continue;
                if (lease.Kind == 0) appearance = lease.Appearance!;
                else if (lease.Kind == 1) { name = lease.Name; visible = lease.Flag; }
                else poweredDown = lease.Flag;
            }
            Take(appearance, name, visible, poweredDown);
        }
        private sealed class Lease : IPlayerAppearanceLease
        {
            private readonly AvatarPresentation owner;
            private readonly IModLifetime lifetime;
            private IDisposable? tracking;
            private bool disposed;
            internal readonly byte Kind;
            internal readonly AvatarAppearance? Appearance;
            internal readonly string? Name;
            internal readonly bool Flag;
            internal Lease(AvatarPresentation owner, IModLifetime lifetime, byte kind, AvatarAppearance? appearance, string? name, bool flag)
            { this.owner = owner; this.lifetime = lifetime; Kind = kind; Appearance = appearance; Name = name; Flag = flag; }
            internal void Track() { var hold = lifetime.Track(this); if (disposed) hold.Dispose(); else tracking = hold; }
            public bool IsActive => !disposed && owner.IsAlive && !lifetime.IsStopping;
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.leases.Remove(this);
                owner.Recompute();
                var hold = tracking; tracking = null; hold?.Dispose();
            }
        }
    }
}
