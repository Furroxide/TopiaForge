using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Explicit session identity and captured bound operations for consumer tests.</summary>
    public sealed class FakeGamemodeSession : IGamemodeSession
    {
        /// <summary>Creates a ready session using the supplied context; it does not construct or start gameplay.</summary>
        public FakeGamemodeSession(IModContext context, WorldReadiness world, string sessionId = "test.session",
            string targetId = "test.target", string gamemodeId = "test.mode", string worldId = "test.world",
            string? worldFamilyId = null, CancellationToken cancellationToken = default)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            World = world ?? throw new ArgumentNullException(nameof(world));
            SessionId = sessionId; TargetId = targetId; GamemodeId = gamemodeId;
            WorldId = worldId; WorldFamilyId = worldFamilyId; CancellationToken = cancellationToken;
        }
        /// <inheritdoc/>
        public string SessionId { get; }
        /// <inheritdoc/>
        public string TargetId { get; }
        /// <inheritdoc/>
        public string GamemodeId { get; }
        /// <inheritdoc/>
        public string WorldId { get; }
        /// <inheritdoc/>
        public string? WorldFamilyId { get; }
        /// <inheritdoc/>
        public WorldReadiness World { get; }
        /// <inheritdoc/>
        public CancellationToken CancellationToken { get; }
        /// <inheritdoc/>
        public IModLifetime Lifetime => Context.Lifetime;
        /// <inheritdoc/>
        public IModContext Context { get; }
        /// <summary>Gets the number of captured stop requests.</summary>
        public int StopRequests { get; private set; }
        /// <summary>Gets the number of captured world restart requests.</summary>
        public int RestartRequests { get; private set; }
        /// <summary>Gets the number of captured main-menu requests.</summary>
        public int MainMenuRequests { get; private set; }
        /// <summary>Gets or sets deterministic stop behavior.</summary>
        public Func<CancellationToken, Task<OperationResult<bool>>>? OnStop { get; set; }
        /// <summary>Gets or sets deterministic world restart behavior.</summary>
        public Func<CancellationToken, Task<OperationResult<bool>>>? OnRestart { get; set; }
        /// <summary>Gets or sets deterministic main-menu behavior.</summary>
        public Func<CancellationToken, Task<OperationResult<bool>>>? OnReturnToMainMenu { get; set; }
        /// <inheritdoc/>
        public Task<OperationResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        { StopRequests++; return Invoke(OnStop, cancellationToken); }
        /// <inheritdoc/>
        public Task<OperationResult<bool>> RestartAsync(CancellationToken cancellationToken = default)
        { RestartRequests++; return Invoke(OnRestart, cancellationToken); }
        /// <inheritdoc/>
        public Task<OperationResult<bool>> ReturnToMainMenuAsync(CancellationToken cancellationToken = default)
        { MainMenuRequests++; return Invoke(OnReturnToMainMenu, cancellationToken); }
        private Task<OperationResult<bool>> Invoke(Func<CancellationToken, Task<OperationResult<bool>>>? callback,
            CancellationToken token) => token.IsCancellationRequested || CancellationToken.IsCancellationRequested || Lifetime.IsStopping
                ? Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The fake session is stopping."))
                : callback?.Invoke(token) ?? Task.FromResult(OperationResult<bool>.Success(true));
    }
}
