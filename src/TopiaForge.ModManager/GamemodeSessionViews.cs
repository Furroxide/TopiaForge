using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed class GamemodeSessionView : IGamemodeSession
    {
        private readonly GamemodeSessionOrchestrator owner;
        private readonly SessionIdentity identity;
        internal GamemodeSessionView(GamemodeSessionOrchestrator owner, SessionIdentity identity,
            ModContext context, CancellationToken token, WorldReadiness world)
        { this.owner = owner; this.identity = identity; Context = context; CancellationToken = token; World = world; }
        public string SessionId => identity.SessionId;
        public string TargetId => identity.Selection.TargetId;
        public string GamemodeId => identity.Selection.GamemodeId;
        public string WorldId => identity.Selection.WorldId;
        public string? WorldFamilyId => identity.Selection.WorldFamilyId;
        public WorldReadiness World { get; }
        public CancellationToken CancellationToken { get; }
        public IModLifetime Lifetime => Context.Lifetime;
        public IModContext Context { get; }
        public Task<OperationResult<bool>> StopAsync(CancellationToken cancellationToken = default) => owner.StopAsync(SessionId, cancellationToken);
        public Task<OperationResult<bool>> RestartAsync(CancellationToken cancellationToken = default) => owner.RestartAsync(SessionId, cancellationToken);
        public Task<OperationResult<bool>> ReturnToMainMenuAsync(CancellationToken cancellationToken = default) => owner.ReturnToMainMenuAsync(SessionId, cancellationToken);
    }

    internal sealed class WorldLoadContext : IWorldLoadContext
    {
        private readonly SessionIdentity identity;
        internal WorldLoadContext(SessionIdentity identity, ModContext context, ModSpawnPolicy spawn)
        {
            this.identity = identity;
            Context = context;
            Transition = identity.Selection.Transition == ModTransitions.SceneReplacement ? WorldLoadTransition.SceneReplacement : WorldLoadTransition.AdditiveArena;
            SpawnPolicy = new WorldSpawnPolicy(spawn.Kind == ModSpawnPolicy.AuthoredMarkerKind ? WorldSpawnKind.AuthoredMarker : WorldSpawnKind.ProviderDefault,
                spawn.Kind == ModSpawnPolicy.AuthoredMarkerKind ? spawn.MarkerName : null);
        }
        public string SessionId => identity.SessionId;
        public string TargetId => identity.Selection.TargetId;
        public string WorldId => identity.Selection.WorldId;
        public string? WorldFamilyId => identity.Selection.WorldFamilyId;
        public WorldLoadTransition Transition { get; }
        public WorldSpawnPolicy SpawnPolicy { get; }
        public IModContext Context { get; }
    }
}
