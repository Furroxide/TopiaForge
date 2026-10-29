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
        private readonly Dictionary<IEntity, Hidden> hidden = new Dictionary<IEntity, Hidden>(EntityIdentity.Instance);
        private readonly Dictionary<long, Lease> leases = new Dictionary<long, Lease>();
        private string? environment;
        private string? appliedPlayer;
        private readonly HashSet<IEntity> waiting = new HashSet<IEntity>(EntityIdentity.Instance);
        private VisionFog? fog;
        private bool disposed;
        private readonly Func<string?> localPlayer;
        private readonly Func<IEntity, string?, bool>? canHide;
        private readonly Func<IEntity, IEntity?> resolve;
        private readonly Action<IEntity>? addedTarget;
        private readonly Action<IEntity>? removedTarget;
        internal long Version { get; private set; }
        internal event Action Ended = delegate { };
        internal WorldViewLeases(IInternalWorldViewRenderer renderer, Func<string?>? localPlayer = null, Func<IEntity, string?, bool>? canHide = null,
            Func<IEntity, IEntity?>? resolve = null, Action<IEntity>? addedTarget = null, Action<IEntity>? removedTarget = null)
        {
            this.renderer = renderer; this.localPlayer = localPlayer ?? (() => null); this.canHide = canHide;
            this.resolve = resolve ?? (entity => entity.IsAlive ? entity : null); this.addedTarget = addedTarget; this.removedTarget = removedTarget;
            renderer.Stopped += Dispose;
        }
        internal string? EnvironmentFor(string? player) => ledger.EnvironmentFor(player);
        internal VisionFog? FogFor(string? player)
        { var value = ledger.FogFor(player); return value.Distance == 0 ? (VisionFog?)null : VisionFog.Radius(value.Distance, (FogTint)value.Tint); }
        internal IEntity[] HiddenFor(string? player) => ledger.HiddenFor(player).ToArray();
        internal void RemovePlayer(string player)
        { foreach (var id in ledger.ForPlayer(player).ToArray()) if (leases.TryGetValue(id, out var lease)) lease.Dispose(); }
        internal void RefreshLocal()
        {
            if (disposed || !renderer.IsAlive) return;
            var changed = appliedPlayer != localPlayer();
            foreach (var item in hidden) if (!ReferenceEquals(resolve(item.Key), item.Value.Entity)) { changed = true; break; }
            if (!changed) foreach (var target in waiting) if (resolve(target) != null) { changed = true; break; }
            if (changed) Apply();
        }
        internal IViewControls Bind(IModLifetime lifetime, Func<ModErrorCode> authority, Action assertThread, string? player = null) =>
            new Controls(this, lifetime, authority, assertThread, player);
        private OperationResult<IViewLease> Add(Controls caller, WorldViewLedger<IEntity>.Entry entry)
        {
            var id = ledger.Add(entry);
            Version++;
            if (id == 0) return Failure(ModErrorCode.RateLimited, "A view supports at most 1,024 hidden objects and 4,096 active leases.");
            try
            {
                if (entry.Hidden != null) addedTarget?.Invoke(entry.Hidden);
                var applied = Apply();
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
            var player = localPlayer();
            appliedPlayer = player;
            waiting.Clear();
            var desired = new HashSet<IEntity>(ledger.HiddenFor(player), EntityIdentity.Instance);
            foreach (var target in hidden.Keys.ToArray())
                if (!desired.Contains(target) || !ReferenceEquals(resolve(target), hidden[target].Entity))
                { var hold = hidden[target]; hidden.Remove(target); hold.Lease.Dispose(); }
            foreach (var target in desired)
                if (!hidden.ContainsKey(target))
                {
                    var entity = resolve(target);
                    if (entity == null) { waiting.Add(target); continue; }
                    var result = renderer.Hide(entity);
                    if (!result.TryGetValue(out var hold)) return OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
                    hidden.Add(target, new Hidden(entity, hold));
                }
            var nextEnvironment = ledger.EnvironmentFor(player);
            var nextFog = FogFor(player);
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
            Version++;
            leases.TryGetValue(id, out var lease); leases.Remove(id);
            try
            {
                if (target != null && !ledger.IsHidden(target) && hidden.TryGetValue(target, out var resource))
                { hidden.Remove(target); resource.Lease.Dispose(); }
            }
            finally
            {
                if (target != null && !ledger.IsHidden(target)) removedTarget?.Invoke(target);
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
            ledger.Clear(); Version++;
            var failures = new List<Exception>();
            void Attempt(Action action) { try { action(); } catch { failures.Add(new InvalidOperationException("A view resource could not be restored.")); } }
            foreach (var resource in hidden.Values) Attempt(resource.Lease.Dispose);
            hidden.Clear(); waiting.Clear();
            foreach (var lease in leases.Values.ToArray()) Attempt(lease.Detach);
            leases.Clear();
            if (renderer.IsAlive && environment != null) Attempt(() => RequireRestore(renderer.SetEnvironment(null)));
            if (renderer.IsAlive && fog != null) Attempt(() => RequireRestore(renderer.SetFog(null)));
            environment = null; fog = null;
            Attempt(Ended); Ended = delegate { };
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
            private readonly string? player;
            internal Controls(WorldViewLeases owner, IModLifetime lifetime, Func<ModErrorCode> authority, Action assertThread, string? player)
            { this.owner = owner; Lifetime = lifetime; this.authority = authority; AssertThread = assertThread; this.player = player; }
            private OperationResult<IViewLease>? Refused()
            {
                AssertThread();
                var code = authority();
                if (code == ModErrorCode.NotAuthoritative) return Failure(code, "Only session authority changes the world view. Move this call to the gamemode's server controller.");
                if (Lifetime.IsStopping || owner.disposed) return Failure(ModErrorCode.Cancelled, "The part of the mod that asked for this is stopping.");
                if (code != ModErrorCode.None) return Failure(code, "This view is unavailable in the current context.");
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
                return owner.Add(this, new WorldViewLedger<IEntity>.Entry(preset, 0, 0, null, player));
            }
            public OperationResult<IViewLease> Fog(VisionFog fog)
            {
                var refused = Refused(); if (refused != null) return refused;
                if (float.IsNaN(fog.VisibleDistance) || float.IsInfinity(fog.VisibleDistance) || fog.VisibleDistance < 1 || fog.VisibleDistance > 500
                    || fog.Tint < FogTint.Dark || fog.Tint > FogTint.Smoke)
                    return Failure(ModErrorCode.InvalidArgument, "Create fog with VisionFog.Radius between one and 500 metres.");
                if (!owner.renderer.FogAvailable) return Failure(ModErrorCode.Unavailable, "This game build's renderer has no usable fog. Update TopiaForge.");
                return owner.Add(this, new WorldViewLedger<IEntity>.Entry(null, fog.VisibleDistance, (int)fog.Tint, null, player));
            }
            public OperationResult<IViewLease> Hide(IEntity entity)
            {
                var refused = Refused(); if (refused != null) return refused;
                if (entity == null) return Failure(ModErrorCode.InvalidArgument, "An entity is required.");
                if (!entity.IsAlive) return Failure(ModErrorCode.NotFound, "The entity no longer exists.");
                if (owner.canHide != null && !owner.canHide(entity, player)) return Failure(ModErrorCode.InvalidArgument,
                    "This object cannot be named on other players. Spawn it through Context.Assets, or hide a map entity.");
                return owner.Add(this, new WorldViewLedger<IEntity>.Entry(null, 0, 0, entity, player));
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
        private sealed class Hidden
        {
            internal readonly IEntity Entity;
            internal readonly IDisposable Lease;
            internal Hidden(IEntity entity, IDisposable lease) { Entity = entity; Lease = lease; }
        }
        private sealed class EntityIdentity : IEqualityComparer<IEntity>
        {
            internal static readonly EntityIdentity Instance = new EntityIdentity();
            public bool Equals(IEntity? x, IEntity? y) => ReferenceEquals(x, y);
            public int GetHashCode(IEntity obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
