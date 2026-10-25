using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>Owns blockers in the calling scope and routes them through the ordinary spawn ledger.</summary>
    internal sealed class NetPhysicsService : IPhysicsService, IDisposable
    {
        internal const string NotAuthoritative = "Only the host places blockers. Place them in the gamemode's server controller.";
        internal const string Stopping = "The part of the mod that asked for this is stopping. Place the blocker after a new scope starts.";
        internal const string Limit = "A mod can keep at most 1,024 blockers. Dispose of an existing blocker before adding another.";
        internal const string NotHeld = "A blocker cannot be held or thrown. Move it with Context.Entities.SetTransform from the code that placed it.";
        private readonly IPhysicsService inner;
        private readonly RuntimeReplication replication;
        private readonly IModLifetime lifetime;
        private readonly List<IBlocker> owned = new List<IBlocker>();
        private bool tracked;

        internal NetPhysicsService(IPhysicsService inner, RuntimeReplication replication, IModLifetime lifetime)
        { this.inner = inner; this.replication = replication; this.lifetime = lifetime; }

        public OperationResult<IBlocker> AddBlocker(BlockerRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            UnityMainThreadGuard.AssertCurrent();
            if (lifetime.IsStopping) return OperationResult<IBlocker>.Failure(ModErrorCode.Cancelled, Stopping);
            var intent = replication.CurrentIntent;
            var route = BlockerAuthorityRules.Route(replication.Services.Runtime.Realm, replication.Profile,
                (IntentSource)(int)intent.Source);
            if (route == ExecutionRoute.NotRun)
                return OperationResult<IBlocker>.Failure(ModErrorCode.NotAuthoritative, NotAuthoritative);
            var authority = replication.JoinedAuthority;
            if (route == ExecutionRoute.Shadow && (authority == null || !authority.CanShadow))
                return OperationResult<IBlocker>.Failure(ModErrorCode.Unavailable, RuntimeReplication.HostNotReady);
            var live = replication.Services.BlockersFor(replication.OwnerModId);
            live.RemoveAll(blocker => !blocker.IsAlive);
            // A scene unload can destroy the collider before the scope ends. Still release its
            // handle's auxiliary resources before dropping our last ownership reference.
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                if (owned[index].IsAlive) continue;
                owned[index].Dispose();
                owned.RemoveAt(index);
            }
            if (live.Count >= 1024) return OperationResult<IBlocker>.Failure(ModErrorCode.RateLimited, Limit);
            if (!tracked)
            {
                try { lifetime.Track(this); tracked = true; }
                catch (ObjectDisposedException) { return OperationResult<IBlocker>.Failure(ModErrorCode.Cancelled, Stopping); }
            }
            var made = inner.AddBlocker(request);
            if (!made.TryGetValue(out var blocker)) return made;
            try
            {
                owned.Add(blocker);
                var payload = BlockerDescriptor.Encode(request);
                if (route == ExecutionRoute.Shadow)
                    authority!.Shadow(replication.OwnerModId, BlockerDescriptor.Kind, payload, blocker, blocker, null);
                else
                    replication.RecordBlocker(blocker, new InternalSpawnDescriptor(BlockerDescriptor.Kind, request.Placement, payload));
                live.Add(blocker);
                return made;
            }
            catch (ObjectDisposedException)
            {
                blocker.Dispose();
                return OperationResult<IBlocker>.Failure(ModErrorCode.Cancelled, Stopping);
            }
            catch
            {
                blocker.Dispose();
                throw;
            }
        }

        public bool TryRaycast(Ray ray, float maximumDistance, out PhysicsHit? hit) => inner.TryRaycast(ray, maximumDistance, out hit);
        public bool TrySphereCast(Ray ray, float radius, float maximumDistance, out PhysicsHit? hit) => inner.TrySphereCast(ray, radius, maximumDistance, out hit);
        public IReadOnlyList<IEntity> Overlap(Bounds bounds, int maximumResults = 64) => inner.Overlap(bounds, maximumResults);

        public void Dispose()
        {
            var live = replication.Services.BlockersFor(replication.OwnerModId);
            List<Exception>? failures = null;
            foreach (var blocker in owned)
            {
                try { blocker.Dispose(); }
                catch (Exception error) { (failures ??= new List<Exception>()).Add(error); }
                finally { live.Remove(blocker); }
            }
            owned.Clear();
            if (failures != null) throw new AggregateException("Some blockers could not be removed. Restart the game before playing again.", failures);
        }
    }
}
