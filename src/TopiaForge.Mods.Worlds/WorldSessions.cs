using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Committed state of the authoritative world and gamemode session.</summary>
    public enum WorldSessionPhase
    {
        /// <summary>No session owns gameplay resources.</summary>
        Idle,
        /// <summary>Session scopes are being prepared.</summary>
        Preparing,
        /// <summary>The selected world is loading.</summary>
        LoadingWorld,
        /// <summary>The world is ready and its gamemode is starting.</summary>
        StartingMode,
        /// <summary>The selected world and gamemode are running.</summary>
        Running,
        /// <summary>Cancellation and resource cleanup are draining.</summary>
        Stopping
    }

    /// <summary>Immutable identity and operations bound to one session, without another package's context.</summary>
    public interface IWorldSession
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
        /// <summary>Requests cancellation. The terminal session outcome confirms cleanup; stale handles cannot stop successors.</summary>
        Task<OperationResult<bool>> StopAsync(CancellationToken cancellationToken = default);
        /// <summary>Revalidates and restarts this session's original selection.</summary>
        Task<OperationResult<bool>> RestartAsync(CancellationToken cancellationToken = default);
        /// <summary>Stops this session and returns to the menu through the shared transition executor.</summary>
        Task<OperationResult<bool>> ReturnToMainMenuAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>One atomic, immutable observation of committed session state.</summary>
    public sealed class WorldSessionSnapshot
    {
        /// <summary>Creates a committed state observation; readiness is available only after world loading succeeds.</summary>
        public WorldSessionSnapshot(WorldSessionPhase phase, IWorldSession? session, WorldReadiness? world, int sequence)
        {
            if (!Enum.IsDefined(typeof(WorldSessionPhase), phase)) throw new ArgumentOutOfRangeException(nameof(phase));
            if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            if ((phase == WorldSessionPhase.Idle) != (session == null)) throw new ArgumentException("Only Idle has no session identity.");
            if ((phase == WorldSessionPhase.Idle || phase == WorldSessionPhase.Preparing || phase == WorldSessionPhase.LoadingWorld) && world != null)
                throw new ArgumentException("World readiness cannot precede completed world loading.");
            if ((phase == WorldSessionPhase.StartingMode || phase == WorldSessionPhase.Running) && world == null)
                throw new ArgumentException("Starting and running require world readiness.");
            Phase = phase; Session = session; World = world; Sequence = sequence;
        }
        /// <summary>Gets the committed phase.</summary>
        public WorldSessionPhase Phase { get; }
        /// <summary>Gets immutable identity and bound operations, or null while Idle.</summary>
        public IWorldSession? Session { get; }
        /// <summary>Gets completed readiness, or null until the provider succeeds.</summary>
        public WorldReadiness? World { get; }
        /// <summary>Gets the process-local, increasing state sequence.</summary>
        public int Sequence { get; }
    }

    /// <summary>Observes the manager's session authority. Notifications never start gameplay.</summary>
    public interface IWorldSessionService
    {
        /// <summary>Gets one atomic committed state snapshot.</summary>
        WorldSessionSnapshot Current { get; }
        /// <summary>Occurs after a committed state change; subscriptions belong to the consuming context.</summary>
        event Action<WorldSessionSnapshot> StateChanged;
    }
}
