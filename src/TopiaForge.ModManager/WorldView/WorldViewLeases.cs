using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    // No Unity references: the production lease owner is exercised directly by the harness and testing kit.
    internal sealed class WorldViewLeases : IDisposable
    {
        private readonly IInternalWorldViewRenderer renderer;
        private readonly WorldViewLedger<IEntity> ledger = new WorldViewLedger<IEntity>(EntityIdentity.Instance);
        private readonly Dictionary<IEntity, IDisposable> hidden = new Dictionary<IEntity, IDisposable>(EntityIdentity.Instance);
        private readonly Dictionary<long, Lease> leases = new Dictionary<long, Lease>();
        private string? environment;
        private VisionFog? fog;
        private bool disposed;
        internal WorldViewLeases(IInternalWorldViewRenderer renderer)
        { this.renderer = renderer; renderer.Stopped += Dispose; }
        internal IViewControls Bind(IModLifetime lifetime, Func<ModErrorCode> authority, Action assertThread) =>
            new Controls(this, lifetime, authority, assertThread);
        private OperationResult<IViewLease> Add(Controls caller, WorldViewLedger<IEntity>.Entry entry)
        {
            var id = ledger.Add(entry);
            if (id == 0) return Failure(ModErrorCode.RateLimited, "A view supports at most 1,024 hidden objects and 4,096 active leases.");
            try
            {
                OperationResult<bool> applied;
                if (entry.Hidden != null && !hidden.ContainsKey(entry.Hidden))
                {
                    var result = renderer.Hide(entry.Hidden);
                    if (!result.TryGetValue(out var resource)) { ledger.Remove(id); return Failure(result.ErrorCode, result.ErrorMessage); }
                    hidden.Add(entry.Hidden, resource);
                }
                applied = Apply();
                if (!applied.Succeeded)
                { Remove(id, entry.Hidden); return Failure(applied.ErrorCode, applied.ErrorMessage); }
                var lease = new Lease(this, id, entry.Hidden, caller.AssertThread);
                leases.Add(id, lease);
                lease.Tracking = caller.Lifetime.Track(lease);
                return OperationResult<IViewLease>.Success(lease);
            }
            catch
            {
                // Native setters may throw after writing. Compensate before releasing bookkeeping.
                try { renderer.SetEnvironment(environment); } catch { }
                try { renderer.SetFog(fog); } catch { }
                try { Remove(id, entry.Hidden); } catch { }
                return Failure(caller.Lifetime.IsStopping ? ModErrorCode.Cancelled : ModErrorCode.External,
                    "The view change could not be applied; the lease was released.");
            }
        }
        private OperationResult<bool> Apply()
        {
            var nextEnvironment = ledger.Environment;
            var value = ledger.Fog;
            VisionFog? nextFog = value.Distance == 0 ? (VisionFog?)null : VisionFog.Radius(value.Distance, (FogTint)value.Tint);
            if (environment != nextEnvironment)
            {
                var result = renderer.SetEnvironment(nextEnvironment);
                if (!result.Succeeded) { renderer.SetEnvironment(environment); return result; }
                environment = nextEnvironment;
            }
            if (!Nullable.Equals(fog, nextFog))
            {
                var result = renderer.SetFog(nextFog);
                if (!result.Succeeded) { renderer.SetFog(fog); return result; }
                fog = nextFog;
            }
            return OperationResult<bool>.Success(true);
        }
        private void Remove(long id, IEntity? target)
        {
            if (!ledger.Remove(id)) return;
            leases.TryGetValue(id, out var lease); leases.Remove(id);
            try
            {
                if (target != null && !ledger.IsHidden(target) && hidden.TryGetValue(target, out var resource))
                { hidden.Remove(target); resource.Dispose(); }
            }
            finally
            {
                try
                {
                    if (!disposed && renderer.IsAlive)
                    {
                        var restored = Apply();
                        if (!restored.Succeeded) throw new InvalidOperationException("The remaining world view could not be restored.");
                    }
                }
                finally { lease?.Detach(); }
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            renderer.Stopped -= Dispose;
            ledger.Clear();
            var failures = new List<Exception>();
            void Attempt(Action action) { try { action(); } catch { failures.Add(new InvalidOperationException("A view resource could not be restored.")); } }
            foreach (var resource in hidden.Values) Attempt(resource.Dispose);
            hidden.Clear();
            foreach (var lease in leases.Values.ToArray()) Attempt(lease.Detach);
            leases.Clear();
            if (renderer.IsAlive && environment != null) Attempt(() => RequireRestore(renderer.SetEnvironment(null)));
            if (renderer.IsAlive && fog != null) Attempt(() => RequireRestore(renderer.SetFog(null)));
            environment = null; fog = null;
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        private static void RequireRestore(OperationResult<bool> result)
        { if (!result.Succeeded) throw new InvalidOperationException("The original world view could not be restored."); }
        private static OperationResult<IViewLease> Failure(ModErrorCode code, string message) => OperationResult<IViewLease>.Failure(code, message);
        private sealed class Controls : IViewControls
        {
            private readonly WorldViewLeases owner;
            private readonly Func<ModErrorCode> authority;
            internal readonly IModLifetime Lifetime;
            internal readonly Action AssertThread;
            internal Controls(WorldViewLeases owner, IModLifetime lifetime, Func<ModErrorCode> authority, Action assertThread)
            { this.owner = owner; Lifetime = lifetime; this.authority = authority; AssertThread = assertThread; }
            private OperationResult<IViewLease>? Refused()
            {
                AssertThread();
                var code = authority();
                if (code == ModErrorCode.NotAuthoritative) return Failure(code, "Only session authority changes the world view. Move this call to the gamemode's server controller.");
                if (Lifetime.IsStopping || owner.disposed) return Failure(ModErrorCode.Cancelled, "The part of the mod that asked for this is stopping.");
                if (code != ModErrorCode.None) return Failure(code, "World view changes currently require single player.");
                if (!owner.renderer.IsAlive) return Failure(ModErrorCode.Unavailable, "This world's view is no longer available.");
                return null;
            }
            public OperationResult<IViewLease> Environment(string preset)
            {
                var refused = Refused(); if (refused != null) return refused;
                if (!WorldViewLedger<IEntity>.ValidPreset(preset)) return Failure(ModErrorCode.InvalidArgument, "Use a preset key of one to 64 printable characters.");
                if (!owner.renderer.EnvironmentAvailable) return Failure(ModErrorCode.Unavailable, "This world has no environment presets; imported maps provide them.");
                if (!owner.renderer.HasPreset(preset)) return Failure(ModErrorCode.NotFound,
                    "There is no environment '" + preset + "'. This game has: " + string.Join(", ", owner.renderer.Presets.Take(16)) + ".");
                return owner.Add(this, new WorldViewLedger<IEntity>.Entry(preset, 0, 0, null));
            }
            public OperationResult<IViewLease> Fog(VisionFog fog)
            {
                var refused = Refused(); if (refused != null) return refused;
                if (float.IsNaN(fog.VisibleDistance) || float.IsInfinity(fog.VisibleDistance) || fog.VisibleDistance < 1 || fog.VisibleDistance > 500
                    || fog.Tint < FogTint.Dark || fog.Tint > FogTint.Smoke)
                    return Failure(ModErrorCode.InvalidArgument, "Create fog with VisionFog.Radius between one and 500 metres.");
                if (!owner.renderer.FogAvailable) return Failure(ModErrorCode.Unavailable, "This game build's renderer has no usable fog. Update TopiaForge.");
                return owner.Add(this, new WorldViewLedger<IEntity>.Entry(null, fog.VisibleDistance, (int)fog.Tint, null));
            }
            public OperationResult<IViewLease> Hide(IEntity entity)
            {
                var refused = Refused(); if (refused != null) return refused;
                if (entity == null) return Failure(ModErrorCode.InvalidArgument, "An entity is required.");
                if (!entity.IsAlive) return Failure(ModErrorCode.NotFound, "The entity no longer exists.");
                return owner.Add(this, new WorldViewLedger<IEntity>.Entry(null, 0, 0, entity));
            }
        }
        private sealed class Lease : IViewLease
        {
            private readonly WorldViewLeases owner;
            private readonly long id;
            private readonly IEntity? target;
            private readonly Action assertThread;
            internal IDisposable? Tracking;
            internal Lease(WorldViewLeases owner, long id, IEntity? target, Action assertThread)
            { this.owner = owner; this.id = id; this.target = target; this.assertThread = assertThread; }
            public bool IsActive => !owner.disposed && owner.renderer.IsAlive && owner.ledger.Contains(id);
            public void Dispose() { assertThread(); owner.Remove(id, target); }
            internal void Detach() { var value = Tracking; Tracking = null; value?.Dispose(); }
        }
        private sealed class EntityIdentity : IEqualityComparer<IEntity>
        {
            internal static readonly EntityIdentity Instance = new EntityIdentity();
            public bool Equals(IEntity? x, IEntity? y) => ReferenceEquals(x, y);
            public int GetHashCode(IEntity obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
