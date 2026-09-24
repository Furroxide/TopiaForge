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
}
