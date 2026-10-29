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
        { this.owner = owner; this.identity = identity; Context = context; CancellationToken = token; World = world.ForContext(context); }
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

    /// <summary>
    /// What this game's own player's participant controller sees: the session's identity and bound operations, its
    /// readiness and cancellation, and the participant scope's context and lifetime, apart from the server controller's.
    /// </summary>
    internal sealed class GamemodeParticipantView : IGamemodeParticipantSession
    {
        private readonly GamemodeSessionOrchestrator owner;
        private readonly SessionIdentity identity;
        internal GamemodeParticipantView(GamemodeSessionOrchestrator owner, SessionIdentity identity,
            ModContext context, CancellationToken token, WorldReadiness world)
        { this.owner = owner; this.identity = identity; Context = context; CancellationToken = token; World = world.ForContext(context); }
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

    internal sealed class WorldLoadContext : IWorldLoadContext, TopiaForge.Mods.Internal.IInternalMapFollowContext
    {
        private readonly SessionIdentity identity;
        public TopiaForge.Mods.Internal.InternalMapIdentity? ExpectedMap { get; }
        public TopiaForge.Mods.Internal.InternalMapCopy? ReadHostMap { get; }
        public Func<OperationResult<bool>>? BeginMapHost { get; }
        internal WorldLoadContext(SessionIdentity identity, ModContext context, ModSpawnPolicy spawn, SessionFollowPlan? follow = null, Func<OperationResult<bool>>? beginMapHost = null)
        {
            this.identity = identity;
            BeginMapHost = beginMapHost;
            ExpectedMap = follow?.Map;
            ReadHostMap = follow?.ReadMap;
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
