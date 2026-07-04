using System;
using System.Collections.Generic;

namespace Robotopia.Mods
{
    /// <summary>
    /// Spawns and drives <b>standard-agent robots</b> — clones of the game's own robot that come up the way a
    /// native robot does (native body, humanoid animation, look-at, and <i>native locomotion</i>) so a mod can
    /// start from a default out-of-the-box robot and override only the behaviour and visuals it needs. Movement
    /// leans on the game's own pathing/locomotion (the robot walks, routes around geometry, animates, and recovers
    /// from being stuck exactly like a native robot); the mod expresses intent (go here, chase this) rather than
    /// re-implementing navigation. Also exposes the small amount of player access (position, the player object,
    /// damage, control suspension) that wave/combat gamemodes need.
    /// </summary>
    /// <remarks>
    /// Published by the <c>Robotopia.RobotKit</c> framework mod and resolved with
    /// <c>context.GetService&lt;IRobotAgentService&gt;()</c>, the same way <see cref="IWorldGamemodeService"/> is
    /// consumed. Declare a dependency on <c>robotopia.robotkit</c> (and <c>loadAfter</c> it) so the service is
    /// registered before your <c>OnLoad</c> runs. All operations degrade gracefully: when the game symbols this
    /// relies on are absent, <see cref="IsAvailable"/> is <c>false</c> and spawning returns <c>null</c> rather
    /// than throwing.
    /// </remarks>
    public interface IRobotAgentService
    {
        /// <summary>
        /// <c>true</c> when a spawnable robot prefab and the control symbols were resolved, so
        /// <see cref="Spawn"/> can produce robots. <c>false</c> means the game build does not expose what the
        /// service needs (spawning will return <c>null</c>). Robot prefabs only exist once a gameplay level is
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
        /// the next service tick, so this can briefly include an agent whose <see cref="IRobotAgent.IsAlive"/> is
        /// already <c>false</c>; check that property if you need a strictly-alive view.
        /// </summary>
        IReadOnlyList<IRobotAgent> ActiveAgents { get; }

        /// <summary>
        /// Spawns a standard-agent robot at <see cref="RobotAgentSpawnRequest.Position"/>. The robot comes up
        /// native (body, animation, locomotion); its brain is dormant by default
        /// (<see cref="RobotBrainMode.Dormant"/>) so the mod owns its decisions, or fully autonomous if
        /// requested. Returns a handle to drive it, or <c>null</c> when <see cref="IsAvailable"/> is <c>false</c>
        /// or no prefab could be resolved. The spawned <c>UnityEngine.GameObject</c> is exposed via
        /// <see cref="IRobotAgent.GameObject"/> so the caller can attach its own gameplay component.
        /// </summary>
        IRobotAgent? Spawn(RobotAgentSpawnRequest request);

        /// <summary>
        /// The distinct robot types (prefabs) the current level exposes, ordered default-first — the list to offer
        /// in a spawn UI. Empty until a gameplay level has loaded and the prefab scan has run (poll alongside
        /// <see cref="IsAvailable"/>). Pass a descriptor's <see cref="RobotTypeDescriptor.Id"/> as
        /// <see cref="RobotAgentSpawnRequest.RobotTypeId"/> to spawn that type.
        /// </summary>
        IReadOnlyList<RobotTypeDescriptor> RobotTypes { get; }

        /// <summary>
        /// <c>true</c> when the given <c>UnityEngine.GameObject</c> (prefab or instance, kept as
        /// <see cref="object"/> to stay Unity-free) is a robot — i.e. it carries the game's robot body. Use it to
        /// keep robots out of prop catalogs. Cheap, never throws, <c>false</c> for non-GameObjects.
        /// </summary>
        bool IsRobotPrefab(object gameObject);

        /// <summary>
        /// Gets the real player's world position. Returns <c>false</c> when there is no resolved
        /// <c>PlayerController</c> (e.g. a camera-only scene); callers that want a camera fallback should handle
        /// that themselves.
        /// </summary>
        bool TryGetPlayerPosition(out Vec3 position);

        /// <summary>
        /// Gets the real player's <c>UnityEngine.GameObject</c> (kept as <see cref="object"/> to keep the SDK
        /// Unity-free), suitable as a <see cref="IRobotAgent.Chase"/> target so the robot tracks the live player
        /// natively. Returns <c>false</c> when there is no resolved player.
        /// </summary>
        bool TryGetPlayerObject(out object gameObject);

        /// <summary>
        /// Applies <paramref name="amount"/> of damage to the player's health (labelled with
        /// <paramref name="source"/>). Returns <c>false</c> when no player health component is resolved.
        /// </summary>
        bool DamagePlayer(float amount, string source);

        /// <summary>
        /// Enables or disables the player's first-person controller. Disable it to freeze look/move input and
        /// stop the controller re-locking the cursor (e.g. while a game-over screen is up); re-enable to restore.
        /// </summary>
        void SetPlayerControlsEnabled(bool enabled);

        /// <summary>
        /// Begins an asynchronous search for a spawn point near <see cref="ReachableSpawnRequest.Origin"/> that an
        /// agent can stand on <i>and</i> actually reach the player from — it reuses the game's own pathfinder to
        /// confirm a complete navigation path exists, so enemies never land on rooftops, ledges, walled-off pockets,
        /// or islands the player cannot get to. The search runs across frames under the engine's pathfinding budget;
        /// poll the returned handle's <see cref="IReachableSpawn.IsComplete"/>, then read
        /// <see cref="IReachableSpawn.Found"/> / <see cref="IReachableSpawn.Position"/>. Cheap to start; the handle is
        /// driven by the service tick (call it from your own update loop and just poll). When the scene has no
        /// pathfinder (<see cref="IsNavigationAvailable"/> is <c>false</c>) the search degrades to a best-effort
        /// grounded point with no reachability guarantee. Abandon a handle simply by dropping it; the service tears
        /// the in-flight search down on the next scene change or when the service is disposed.
        /// </summary>
        IReachableSpawn BeginFindReachableSpawn(ReachableSpawnRequest request);
    }

    /// <summary>
    /// A pollable handle for an in-flight <see cref="IRobotAgentService.BeginFindReachableSpawn"/> search. Poll
    /// <see cref="IsComplete"/> each frame; once it is <c>true</c>, read <see cref="Found"/> and (if found)
    /// <see cref="Position"/>. The handle never throws and is safe to keep polling after completion.
    /// </summary>
    public interface IReachableSpawn
    {
        /// <summary>
        /// <c>true</c> once the search has finished — either a reachable point was found or every candidate was
        /// exhausted. Until then the service is still pathfinding candidates across frames.
        /// </summary>
        bool IsComplete { get; }

        /// <summary>
        /// <c>true</c> when a usable spawn point was found. Valid only once <see cref="IsComplete"/> is <c>true</c>;
        /// <c>false</c> means no candidate near the origin was both standable and reachable from the player (the
        /// caller should delay and retry rather than spawn anywhere).
        /// </summary>
        bool Found { get; }

        /// <summary>The chosen spawn point, ground-snapped to the navigation grid. Valid only when <see cref="Found"/> is <c>true</c>.</summary>
        Vec3 Position { get; }
    }

    /// <summary>Parameters for <see cref="IRobotAgentService.BeginFindReachableSpawn"/>.</summary>
    public sealed class ReachableSpawnRequest
    {
        /// <summary>Creates a request that searches a ring around <paramref name="origin"/> (typically the player position).</summary>
        /// <param name="origin">Centre of the search ring — usually the player's world position.</param>
        public ReachableSpawnRequest(Vec3 origin)
        {
            Origin = origin;
        }

        /// <summary>Centre of the search ring; candidates are generated around this point.</summary>
        public Vec3 Origin { get; }

        /// <summary>
        /// The point a candidate must be reachable from (a complete navigation path must connect them). <c>null</c>
        /// uses <see cref="Origin"/>. For a wave gamemode this is the player position.
        /// </summary>
        public Vec3? ReachableFrom { get; set; }

        /// <summary>
        /// Closest a candidate may be generated to <see cref="Origin"/>, in metres. Keep this comfortably above
        /// zero: candidates generated almost on top of the reachability anchor are skipped (the native pathfinder
        /// treats a start already at the goal as trivially reachable, which would bypass route validation).
        /// </summary>
        public float MinRadius { get; set; } = 8f;

        /// <summary>Farthest a candidate may be generated from <see cref="Origin"/>, in metres.</summary>
        public float MaxRadius { get; set; } = 24f;

        /// <summary>How many ring candidates to try before giving up (each is ground-tested, then reachability-tested).</summary>
        public int MaxCandidates { get; set; } = 16;

        /// <summary>Metres above a ring point to begin the downward ground probe (clears low overhangs at the sample point).</summary>
        public float VerticalScan { get; set; } = 3f;

        /// <summary>Length of the downward ground probe ray, in metres.</summary>
        public float GroundProbeDepth { get; set; } = 12f;

        /// <summary>Vertical offset added to the chosen ground point so the robot is not seated in the floor.</summary>
        public float HeightOffset { get; set; } = 0.25f;
    }

    /// <summary>
    /// A live standard-agent robot. Drive its behaviour with movement intents (<see cref="MoveTo"/>,
    /// <see cref="Chase"/>, <see cref="Stop"/>) that are carried out by the game's own locomotion — the robot
    /// path-finds, collides, grounds, and animates natively, and re-paths on its own as a chased target moves.
    /// Override visuals (<see cref="SetTint"/>, <see cref="SetEmote"/>, <see cref="SetName"/>,
    /// <see cref="SetScale"/>) and wire combat through the native health/ragdoll pipeline
    /// (<see cref="ApplyDamage"/>, <see cref="Kill"/>, <see cref="Ragdoll"/>, <see cref="Knockback"/>) — or
    /// attach your own component to <see cref="GameObject"/> for anything beyond this surface.
    /// </summary>
    public interface IRobotAgent
    {
        /// <summary>Stable id for this robot for the lifetime of the spawn.</summary>
        string Id { get; }

        /// <summary>The spawned <c>UnityEngine.GameObject</c> (kept as <see cref="object"/> to keep the SDK Unity-free).</summary>
        object GameObject { get; }

        /// <summary>
        /// <c>false</c> once the robot has been despawned, its game object destroyed, or it has died (the native
        /// death pipeline ran). A non-lethal ragdoll/knockdown does <i>not</i> set this <c>false</c> — the robot
        /// self-recovers and stays alive.
        /// </summary>
        bool IsAlive { get; }

        /// <summary>The robot's current world position (at its feet/base).</summary>
        Vec3 Position { get; }

        /// <summary>
        /// Approximate world position of the robot's head — the top-centre of its live rendered volume,
        /// scale-aware (so it tracks <see cref="SetScale"/>). Use it for hit-zone tests (e.g. headshots: compare a
        /// ray-hit height against this) and to anchor world-space combat HUD such as floating damage numbers or a
        /// health pip above an enemy, rather than hard-coding the native robot's height in your mod. Degrades
        /// gracefully: when no renderers are resolvable (e.g. mid-teardown) it estimates the head from
        /// <see cref="Position"/> plus the native robot's nominal height.
        /// </summary>
        Vec3 HeadPosition { get; }

        /// <summary>The robot's CURRENT brain mode (initially the spawn request's; changed by <see cref="SetBrainMode"/>).</summary>
        RobotBrainMode BrainMode { get; }

        /// <summary>
        /// Switches the robot's brain mode at runtime. To <see cref="RobotBrainMode.Dormant"/>: the native
        /// LLM brain and behaviour tree are suppressed and mod movement intents take over — the way to
        /// reprogram/override an autonomous robot. To <see cref="RobotBrainMode.Autonomous"/>: mod intents are
        /// cleared and the native brain is best-effort woken back up. Idempotent; never throws.
        /// </summary>
        void SetBrainMode(RobotBrainMode mode);

        /// <summary><c>true</c> while the robot is actively walking toward an intent target this frame.</summary>
        bool IsMoving { get; }

        /// <summary>
        /// <c>true</c> when the most recent <see cref="MoveTo"/>/<see cref="Chase"/> intent is satisfied — the
        /// robot is within <see cref="StopDistance"/> of its target.
        /// </summary>
        bool HasReachedTarget { get; }

        /// <summary>
        /// Optional override of the native gait speed in metres per second (the speed for the current
        /// <see cref="Gait"/>). <c>0</c> keeps the prefab's native speed. Best-effort: ignored if the game build
        /// does not expose the speed field.
        /// </summary>
        float MoveSpeed { get; set; }

        /// <summary>Optional best-effort override of the native turn speed in degrees per second; <c>0</c> keeps the prefab default.</summary>
        float TurnSpeed { get; set; }

        /// <summary>How close (metres) to the target counts as arrived; the native walk stops there.</summary>
        float StopDistance { get; set; }

        /// <summary>Which native speed tier the robot moves at (walk/run/sprint).</summary>
        RobotGait Gait { get; set; }

        /// <summary>Walks to a fixed world position once and stops there (a single native walk).</summary>
        void MoveTo(Vec3 position);

        /// <summary>
        /// Continuously pursues a live <c>UnityEngine.GameObject</c> (e.g. the player) — the native locomotion
        /// tracks and re-paths to the target as it moves, stopping within <see cref="StopDistance"/>. Cheap to
        /// call once; pass the same object to keep chasing. Pass a different object to retarget.
        /// </summary>
        void Chase(object targetGameObject);

        /// <summary>Clears the current intent so the robot stops moving and idles natively.</summary>
        void Stop();

        /// <summary>Tints the whole robot via a material property block (cheap, non-destructive). The default keeps native colours.</summary>
        void SetTint(RobotColor color);

        /// <summary>Sets the robot's facial emote from an emoji shortcode (native expression system); empty clears it.</summary>
        void SetEmote(string emojiShortcode);

        /// <summary>Renames the underlying game object (e.g. for clarity in the hierarchy/logs).</summary>
        void SetName(string name);

        /// <summary>Uniformly scales the robot (1 = native size).</summary>
        void SetScale(float scale);

        /// <summary>
        /// Updates how the player can interact with this robot: native talk, disabled native talk, a native talk
        /// distance override, or a custom synchronous callback. Custom interactions take precedence over native
        /// talk while installed.
        /// </summary>
        void SetInteraction(RobotInteractionOptions options);

        /// <summary>
        /// Deals damage through the robot's native <c>Health</c> component (driving the native hurt/death/ragdoll
        /// pipeline). Returns <c>false</c> when the robot has no resolvable health. Note the native health regen
        /// is always-on, so enemies with their own hit-points are better off tracking damage in the mod and
        /// calling <see cref="Kill"/> when defeated.
        /// </summary>
        bool ApplyDamage(float amount, RobotDamageType type, string source);

        /// <summary>
        /// Forces the robot's native death (ragdoll + corpse cleanup) immediately — the right call when the mod
        /// tracks its own hit-points and the enemy is defeated. Safe to call more than once.
        /// </summary>
        void Kill(RobotDamageType type, string source);

        /// <summary>Knocks the robot down into a native ragdoll without killing it; it self-recovers after a few seconds.</summary>
        void Ragdoll();

        /// <summary>Applies a physical impulse (native): a strong enough impulse knocks the robot into a ragdoll, like a hit reaction.</summary>
        void Knockback(Vec3 impulse);

        /// <summary>Removes and destroys this robot. Safe to call more than once.</summary>
        void Despawn();
    }

    /// <summary>Parameters for <see cref="IRobotAgentService.Spawn"/>.</summary>
    public sealed class RobotAgentSpawnRequest
    {
        /// <summary>Creates a spawn request at a world position, optionally facing a direction.</summary>
        /// <param name="position">World position to spawn at.</param>
        /// <param name="facing">Optional initial facing direction (need not be normalized); <c>null</c> keeps prefab rotation.</param>
        public RobotAgentSpawnRequest(Vec3 position, Vec3? facing = null)
        {
            Position = position;
            Facing = facing;
        }

        /// <summary>World position to spawn the robot at.</summary>
        public Vec3 Position { get; }

        /// <summary>Optional initial facing direction; <c>null</c> keeps the prefab's rotation.</summary>
        public Vec3? Facing { get; }

        /// <summary>
        /// Whether the robot's brain is dormant (default — mod drives it) or autonomous (the native LLM agent
        /// thinks for itself). See <see cref="RobotBrainMode"/>.
        /// </summary>
        public RobotBrainMode BrainMode { get; set; } = RobotBrainMode.Dormant;

        /// <summary>Which native speed tier the robot moves at; defaults to <see cref="RobotGait.Run"/>.</summary>
        public RobotGait Gait { get; set; } = RobotGait.Run;

        /// <summary>Optional gait-speed override in m/s applied to the spawned robot; <c>0</c> keeps the prefab default.</summary>
        public float MoveSpeed { get; set; }

        /// <summary>Optional turn-speed override in deg/s; <c>0</c> keeps the prefab default.</summary>
        public float TurnSpeed { get; set; }

        /// <summary>Initial <see cref="IRobotAgent.StopDistance"/> (metres).</summary>
        public float StopDistance { get; set; }

        /// <summary>Optional whole-body tint applied on spawn; <c>null</c> keeps native colours.</summary>
        public RobotColor? Tint { get; set; }

        /// <summary>Optional name for the spawned game object; <c>null</c> keeps a default.</summary>
        public string? Name { get; set; }

        /// <summary>Uniform spawn scale (1 = native size).</summary>
        public float Scale { get; set; } = 1f;

        /// <summary>
        /// Player-facing interaction policy for the spawned robot. Defaults to the game's native talk prompt.
        /// </summary>
        public RobotInteractionOptions Interaction { get; set; } = RobotInteractionOptions.NativeTalk();

        /// <summary>
        /// Which robot type (prefab) to spawn — an <see cref="RobotTypeDescriptor.Id"/> from
        /// <see cref="IRobotAgentService.RobotTypes"/>. <c>null</c> (default) spawns the default type. An unknown
        /// id logs a warning and falls back to the default rather than failing the spawn.
        /// </summary>
        public string? RobotTypeId { get; set; }
    }

    /// <summary>One spawnable robot type (a distinct robot prefab the current level exposes).</summary>
    public sealed class RobotTypeDescriptor
    {
        /// <summary>Creates a robot type descriptor.</summary>
        public RobotTypeDescriptor(string id, string displayName)
        {
            Id = id ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName;
        }

        /// <summary>Stable slug of the prefab name (e.g. <c>"worker-robot"</c>) — pass as <see cref="RobotAgentSpawnRequest.RobotTypeId"/>.</summary>
        public string Id { get; }

        /// <summary>Human-readable name for spawn UIs.</summary>
        public string DisplayName { get; }
    }

    /// <summary>How RobotKit should expose the game's native "talk to this robot" interaction.</summary>
    public enum RobotNativeTalkMode
    {
        /// <summary>Keep the native talk interaction enabled (default).</summary>
        Enabled,

        /// <summary>Disable the native talk interaction for this robot.</summary>
        Disabled
    }

    /// <summary>
    /// Player interaction policy for a RobotKit agent. Leave <see cref="CustomInteraction"/> <c>null</c> to use
    /// native talk behaviour; set it to expose a custom prompt and callback instead.
    /// </summary>
    public sealed class RobotInteractionOptions
    {
        /// <summary>Whether the base game's native talk interaction should be available.</summary>
        public RobotNativeTalkMode NativeTalkMode { get; set; } = RobotNativeTalkMode.Enabled;

        /// <summary>
        /// Optional native talk distance override in metres. Values less than or equal to zero keep the prefab's
        /// default speak distance.
        /// </summary>
        public float NativeTalkDistance { get; set; }

        /// <summary>
        /// Optional custom player interaction. When present, RobotKit disables native talk on this agent so the
        /// custom prompt is selected reliably.
        /// </summary>
        public RobotCustomInteraction? CustomInteraction { get; set; }

        /// <summary>Default policy: keep native talk enabled at the prefab's distance.</summary>
        public static RobotInteractionOptions NativeTalk()
        {
            return new RobotInteractionOptions();
        }

        /// <summary>Keep native talk enabled, overriding the prefab's speak distance.</summary>
        public static RobotInteractionOptions NativeTalkAtDistance(float distance)
        {
            return new RobotInteractionOptions { NativeTalkDistance = distance };
        }

        /// <summary>Disable the base game's native talk prompt for this robot.</summary>
        public static RobotInteractionOptions DisableNativeTalk()
        {
            return new RobotInteractionOptions { NativeTalkMode = RobotNativeTalkMode.Disabled };
        }

        /// <summary>Use a custom prompt and callback instead of the native talk prompt.</summary>
        public static RobotInteractionOptions Custom(RobotCustomInteraction interaction)
        {
            return new RobotInteractionOptions
            {
                NativeTalkMode = RobotNativeTalkMode.Disabled,
                CustomInteraction = interaction
            };
        }

        /// <summary>Returns a shallow copy; callback delegates are intentionally reused.</summary>
        public RobotInteractionOptions Clone()
        {
            return new RobotInteractionOptions
            {
                NativeTalkMode = NativeTalkMode,
                NativeTalkDistance = NativeTalkDistance,
                CustomInteraction = CustomInteraction
            };
        }
    }

    /// <summary>A custom player-facing interaction installed on a RobotKit agent.</summary>
    public sealed class RobotCustomInteraction
    {
        /// <summary>Creates a custom interaction with the prompt shown in the game's interaction UI.</summary>
        public RobotCustomInteraction(string prompt, Action<RobotInteractionContext>? interact = null)
        {
            Prompt = prompt ?? string.Empty;
            Interact = interact;
        }

        /// <summary>Prompt shown while the robot is the selected interactable.</summary>
        public string Prompt { get; set; }

        /// <summary>Maximum player-hand distance in metres. Values less than or equal to zero use 3 metres.</summary>
        public float Distance { get; set; } = 3f;

        /// <summary>How far the robot's screen-space bounds expand for center-reticle selection.</summary>
        public float ScreenRectExpansion { get; set; }

        /// <summary>Optional per-frame gate for whether the prompt can be selected.</summary>
        public Func<RobotInteractionContext, bool>? CanInteract { get; set; }

        /// <summary>Synchronous callback invoked when the player activates the prompt.</summary>
        public Action<RobotInteractionContext>? Interact { get; set; }
    }

    /// <summary>Unity-free context passed to custom RobotKit interaction callbacks.</summary>
    public sealed class RobotInteractionContext
    {
        public RobotInteractionContext(
            IRobotAgent agent,
            object? hand,
            Vec3 agentPosition,
            Vec3 handPosition,
            float distance)
        {
            Agent = agent ?? throw new ArgumentNullException(nameof(agent));
            Hand = hand;
            AgentPosition = agentPosition;
            HandPosition = handPosition;
            Distance = distance;
        }

        /// <summary>The agent whose custom interaction is being queried or invoked.</summary>
        public IRobotAgent Agent { get; }

        /// <summary>The native player hand transform, exposed as <see cref="object"/> to keep the SDK Unity-free.</summary>
        public object? Hand { get; }

        /// <summary>The agent's current world position.</summary>
        public Vec3 AgentPosition { get; }

        /// <summary>The player hand's current world position.</summary>
        public Vec3 HandPosition { get; }

        /// <summary>Current hand-to-agent distance in metres.</summary>
        public float Distance { get; }
    }

    /// <summary>How much of the native robot brain runs on a spawned <see cref="IRobotAgent"/>.</summary>
    public enum RobotBrainMode
    {
        /// <summary>
        /// Default. The robot comes up fully native (body, locomotion, animation) but its LLM brain is suppressed
        /// — no autonomous planning, no RoboAPI calls, no self-directed walking/talking. The mod owns its
        /// decisions via the movement/visual intents. Predictable and free; right for enemies and scripted NPCs.
        /// </summary>
        Dormant,

        /// <summary>
        /// The native LLM agent is left running, so the robot perceives, thinks, talks, and moves on its own like
        /// any game robot (a true out-of-the-box NPC/companion). Costs RoboAPI gateway calls. Mod movement
        /// intents are not the intended driver in this mode — the robot drives itself.
        /// </summary>
        Autonomous
    }

    /// <summary>The native locomotion speed tier a robot moves at.</summary>
    public enum RobotGait
    {
        /// <summary>Walking speed.</summary>
        Walk,

        /// <summary>Running speed (default).</summary>
        Run,

        /// <summary>Sprinting speed.</summary>
        Sprint
    }

    /// <summary>Mirrors the game's native damage types for <see cref="IRobotAgent.ApplyDamage"/>/<see cref="IRobotAgent.Kill"/>.</summary>
    public enum RobotDamageType
    {
        /// <summary>Generic/physical damage.</summary>
        Normal,

        /// <summary>Fire damage.</summary>
        Fire,

        /// <summary>Electricity damage.</summary>
        Electricity,

        /// <summary>Poison damage.</summary>
        Poison,

        /// <summary>Water damage.</summary>
        Water
    }
}
