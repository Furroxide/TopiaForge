using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Spawns and drives <b>standard-agent robots</b> — clones of the game's own robot that come up the way a
    /// native robot does (native body, humanoid animation, look-at, and <i>native locomotion</i>) so a mod can
    /// start from a default out-of-the-box robot and override only the behaviour and visuals it needs. Movement
    /// leans on the game's own pathing/locomotion (the robot walks, routes around geometry, animates, and recovers
    /// from being stuck exactly like a native robot); the mod expresses intent (go here, chase this) rather than
    /// re-implementing navigation. Native engine objects never cross this contract boundary.
    /// </summary>
    /// <remarks>
    /// Published by the <c>TopiaForge.RobotKit</c> framework mod and resolved with
    /// <c>context.Extensions.TryGet&lt;IRobotAgentService&gt;()</c>. Declare a dependency on
    /// <c>io.github.furroxide.topiaforge.robotkit</c> so the service is
    /// registered before your <c>OnLoad</c> runs. All operations degrade gracefully: when the game symbols this
    /// relies on are absent, <see cref="IsAvailable"/> is <c>false</c> and spawning returns a stable
    /// <see cref="ModErrorCode.Unavailable"/> result rather than throwing.
    /// </remarks>
    public interface IRobotAgentService
    {
        /// <summary>
        /// <c>true</c> when a spawnable robot prefab and the control symbols were resolved, so
        /// <see cref="Spawn"/> can produce robots. <c>false</c> means the game build does not expose what the
        /// service needs (spawning returns an unavailable result). Robot prefabs only exist once a gameplay level is
        /// loaded, so poll this rather than assuming it at startup.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// <c>true</c> when the game's pathfinder is present in the current scene, so robots route around world
        /// geometry. When <c>false</c> (e.g. a scene with no robots), spawned robots can still stand and animate
        /// but cannot path to a target until a pathfinder exists.
        /// </summary>
        bool IsNavigationAvailable { get; }

        /// <summary>
        /// All robots the service is currently managing. An agent that has died (or been despawned) is removed on
        /// the next service tick, so this can briefly include an agent whose <see cref="IEntity.IsAlive"/> is
        /// already <c>false</c>; check that property if you need a strictly-alive view.
        /// </summary>
        IReadOnlyList<IRobotAgent> ActiveAgents { get; }

        /// <summary>
        /// Spawns a standard-agent robot at <see cref="RobotAgentSpawnRequest.Position"/>. The robot comes up
        /// native (body, animation, locomotion); its brain is dormant by default
        /// (<see cref="RobotBrainMode.Dormant"/>) so the mod owns its decisions, or fully autonomous if
        /// requested. Returns a successful opaque agent handle, or a stable failure result when the service is
        /// unavailable or no prefab could be resolved.
        /// </summary>
        OperationResult<IRobotAgent> Spawn(RobotAgentSpawnRequest request);

        /// <summary>
        /// The distinct robot types (prefabs) the current level exposes, ordered default-first — the list to offer
        /// in a spawn UI. Empty until a gameplay level has loaded and the prefab scan has run (poll alongside
        /// <see cref="IsAvailable"/>). Pass a descriptor's <see cref="RobotTypeDescriptor.Id"/> as
        /// <see cref="RobotAgentSpawnRequest.RobotTypeId"/> to spawn that type.
        /// </summary>
        IReadOnlyList<RobotTypeDescriptor> RobotTypes { get; }

        /// <summary>
        /// <c>true</c> when an opaque entity is a RobotKit-managed robot.
        /// </summary>
        bool TryGetRobot(IEntity entity, out IRobotAgent? agent);

        /// <summary>
        /// Begins an asynchronous search for a spawn point near <see cref="ReachableSpawnRequest.Origin"/> that an
        /// agent can stand on <i>and</i> actually reach the player from — it reuses the game's own pathfinder to
        /// confirm a complete navigation path exists, so enemies never land on rooftops, ledges, walled-off pockets,
        /// or islands the player cannot get to. The search runs asynchronously across frames under the engine's
        /// pathfinding budget. When the scene has no
        /// pathfinder (<see cref="IsNavigationAvailable"/> is <c>false</c>) the search degrades to a best-effort
        /// grounded point with no reachability guarantee. Caller cancellation and the owning mod lifetime both stop
        /// an in-flight search; scene changes also invalidate unfinished work.
        /// </summary>
        Task<OperationResult<ReachableSpawnResult>> FindReachableSpawnAsync(
            ReachableSpawnRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Optional RobotKit capability for resolving the current player as a safe, live entity. It is separate from
    /// <see cref="IRobotAgentService"/> so existing third-party service implementations remain source and binary
    /// compatible when this capability is unavailable.
    /// </summary>
    public interface IRobotPlayerEntitySource
    {
        /// <summary>
        /// Tries to obtain a safe, live entity for the current player. The returned handle remains backed by the
        /// native player object, so it can be passed once to <see cref="IRobotAgent.Chase"/> and native locomotion
        /// will continue tracking the player as they move.
        /// </summary>
        bool TryGetPlayerEntity(out IEntity? entity);
    }

    /// <summary>Compatibility-preserving optional RobotKit capabilities.</summary>
    public static class RobotAgentServiceExtensions
    {
        /// <summary>
        /// Tries to obtain the current player as a safe entity when the provider supports
        /// <see cref="IRobotPlayerEntitySource"/>. Returns <c>false</c> for older providers.
        /// </summary>
        public static bool TryGetPlayerEntity(this IRobotAgentService service, out IEntity? entity)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            if (service is IRobotPlayerEntitySource source)
            {
                return source.TryGetPlayerEntity(out entity);
            }

            entity = null;
            return false;
        }
    }

    /// <summary>Immutable successful result of a reachable spawn search.</summary>
    public sealed class ReachableSpawnResult
    {
        /// <summary>Creates a reachable spawn result.</summary>
        public ReachableSpawnResult(Vec3 position)
        {
            Position = position;
        }

        /// <summary>Gets the chosen ground-snapped position.</summary>
        public Vec3 Position { get; }
    }

    /// <summary>Immutable parameters for <see cref="IRobotAgentService.FindReachableSpawnAsync"/>.</summary>
    public sealed class ReachableSpawnRequest
    {
        /// <summary>Creates a request that searches a ring around <paramref name="origin"/> (typically the player position).</summary>
        /// <param name="origin">Centre of the search ring — usually the player's world position.</param>
        /// <param name="reachableFrom">Optional point that must have a complete path to a candidate.</param>
        /// <param name="minRadius">Closest candidate radius in metres.</param>
        /// <param name="maxRadius">Farthest candidate radius in metres.</param>
        /// <param name="maxCandidates">Maximum number of candidates to inspect.</param>
        /// <param name="verticalScan">Height above a candidate at which to begin the ground scan.</param>
        /// <param name="groundProbeDepth">Length of the downward ground scan.</param>
        /// <param name="heightOffset">Vertical offset applied to the selected ground point.</param>
        public ReachableSpawnRequest(
            Vec3 origin,
            Vec3? reachableFrom = null,
            float minRadius = 8f,
            float maxRadius = 24f,
            int maxCandidates = 16,
            float verticalScan = 3f,
            float groundProbeDepth = 12f,
            float heightOffset = 0.25f)
        {
            Origin = origin;
            ReachableFrom = reachableFrom;
            MinRadius = minRadius;
            MaxRadius = maxRadius;
            MaxCandidates = maxCandidates;
            VerticalScan = verticalScan;
            GroundProbeDepth = groundProbeDepth;
            HeightOffset = heightOffset;
        }

        /// <summary>Centre of the search ring; candidates are generated around this point.</summary>
        public Vec3 Origin { get; }

        /// <summary>
        /// The point a candidate must be reachable from (a complete navigation path must connect them). <c>null</c>
        /// uses <see cref="Origin"/>. For a wave gamemode this is the player position.
        /// </summary>
        public Vec3? ReachableFrom { get; }

        /// <summary>
        /// Closest a candidate may be generated to <see cref="Origin"/>, in metres. Keep this comfortably above
        /// zero: candidates generated almost on top of the reachability anchor are skipped (the native pathfinder
        /// treats a start already at the goal as trivially reachable, which would bypass route validation).
        /// </summary>
        public float MinRadius { get; }

        /// <summary>Farthest a candidate may be generated from <see cref="Origin"/>, in metres.</summary>
        public float MaxRadius { get; }

        /// <summary>How many ring candidates to try before giving up (each is ground-tested, then reachability-tested).</summary>
        public int MaxCandidates { get; }

        /// <summary>Metres above a ring point to begin the downward ground probe (clears low overhangs at the sample point).</summary>
        public float VerticalScan { get; }

        /// <summary>Length of the downward ground probe ray, in metres.</summary>
        public float GroundProbeDepth { get; }

        /// <summary>Vertical offset added to the chosen ground point so the robot is not seated in the floor.</summary>
        public float HeightOffset { get; }
    }
}
