using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Starts one controller after the selected world is ready.</summary>
    public interface IGamemodeFactory
    {
        /// <summary>Creates and starts exactly one owned controller within the session scope.</summary>
        Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken cancellationToken);
    }

    /// <summary>Owns the gameplay behavior of one session.</summary>
    public interface IGamemodeController : IDisposable { }

    /// <summary>Immutable identity, readiness and scoped authority for one gamemode invocation.</summary>
    public interface IGamemodeSession : IWorldSession
    {
        /// <summary>Gets readiness established by the provider, without disposal authority.</summary>
        WorldReadiness World { get; }
        /// <summary>Gets the token signalled before session cleanup begins.</summary>
        CancellationToken CancellationToken { get; }
        /// <summary>Gets the same lifetime exposed by the scoped context.</summary>
        IModLifetime Lifetime { get; }
        /// <summary>Gets the owning package's session-scoped services.</summary>
        IModContext Context { get; }
    }

    /// <summary>
    /// A gamemode factory that also gives every interactive player a controller of their own, for that player's tools and
    /// HUD. Implement it on the declared gamemode type instead of <see cref="IGamemodeFactory"/>. Where the session's
    /// server runs, in single player and on the host, <see cref="IGamemodeFactory.StartAsync"/> starts the server
    /// controller first; then, in every player's own game, <see cref="StartParticipantAsync"/> starts that player's
    /// participant controller. A joined player's game starts only its participant controller.
    /// </summary>
    public interface IGamemodeParticipantFactory : IGamemodeFactory
    {
        /// <summary>
        /// Creates and starts exactly one owned controller for this game's own player, within the participant scope. It
        /// runs after the server controller started, where one runs, and before the session reports Running; a failure
        /// ends the session as a failed server start does, and the server controller is disposed.
        /// </summary>
        Task<OperationResult<IGamemodeParticipantController>> StartParticipantAsync(
            IGamemodeParticipantSession participant, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Owns one player's tools and HUD for one session. It is disposed when the session stops, before the server
    /// controller, and a failure while disposing it never keeps the server controller from stopping.
    /// </summary>
    public interface IGamemodeParticipantController : IDisposable { }

    /// <summary>
    /// Immutable identity, readiness and the participant scope of this game's own player for one gamemode invocation. The
    /// identity and bound operations are the session's; the context and lifetime are the player's own scope, apart from
    /// the server controller's.
    /// </summary>
    public interface IGamemodeParticipantSession : IWorldSession
    {
        /// <summary>Gets readiness established by the provider, without disposal authority.</summary>
        WorldReadiness World { get; }
        /// <summary>Gets the token signalled before session cleanup begins.</summary>
        CancellationToken CancellationToken { get; }
        /// <summary>Gets the participant scope's lifetime, which the scoped context exposes too.</summary>
        IModLifetime Lifetime { get; }
        /// <summary>Gets the owning package's services for this player, scoped to the participant.</summary>
        IModContext Context { get; }
    }
}
