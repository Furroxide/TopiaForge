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
    public interface IGamemodeSession
    {
        /// <summary>Gets the unique process session identity.</summary>
        string SessionId { get; }
        /// <summary>Gets the selected manifest launch target.</summary>
        string TargetId { get; }
        /// <summary>Gets the selected manifest gamemode.</summary>
        string GamemodeId { get; }
        /// <summary>Gets the concrete selected world identity.</summary>
        string WorldId { get; }
        /// <summary>Gets its discovered family, or null for static content.</summary>
        string? WorldFamilyId { get; }
        /// <summary>Gets readiness established by the world provider, without disposal authority.</summary>
        WorldReadiness World { get; }
        /// <summary>Gets the token signalled before session cleanup begins.</summary>
        CancellationToken CancellationToken { get; }
        /// <summary>Gets the same lifetime exposed by the scoped context.</summary>
        IModLifetime Lifetime { get; }
        /// <summary>Gets the owning package's session-scoped services.</summary>
        IModContext Context { get; }
        /// <summary>Requests this session to stop. Success acknowledges acceptance; the terminal session outcome confirms cleanup. A stale handle cannot affect a successor.</summary>
        Task<OperationResult<bool>> StopAsync(CancellationToken cancellationToken = default);
        /// <summary>Revalidates and restarts this session's original selection.</summary>
        Task<OperationResult<bool>> RestartAsync(CancellationToken cancellationToken = default);
        /// <summary>Stops this session and returns to the main menu through the shared scene executor.</summary>
        Task<OperationResult<bool>> ReturnToMainMenuAsync(CancellationToken cancellationToken = default);
    }
}
