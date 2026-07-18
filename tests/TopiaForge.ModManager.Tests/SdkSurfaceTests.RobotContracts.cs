using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    // Exercises the Unity-free V1 contracts and specialist module data types. No GameCode/UnityEngine involved.
    internal static partial class SdkSurfaceTests
    {
        private static void TestVec3RoundTrip()
        {
            var v = new Vec3(1.5f, -2f, 3.25f);
            Assert(v.X == 1.5f && v.Y == -2f && v.Z == 3.25f, "Vec3 components should round-trip");

            var array = v.ToArray();
            Assert(array.Length == 3 && array[0] == 1.5f && array[1] == -2f && array[2] == 3.25f, "ToArray should be [x,y,z]");

            var back = Vec3.FromArray(array);
            Assert(back.Equals(v), "FromArray(ToArray()) should round-trip");

            Assert(Vec3.FromArray(null).Equals(Vec3.Zero), "FromArray(null) should be Zero");
            Assert(Vec3.FromArray(new[] { 1f }).Equals(Vec3.Zero), "FromArray of a too-short array should be Zero");
            Assert(Vec3.Zero.Equals(new Vec3(0f, 0f, 0f)), "Zero should equal (0,0,0)");
        }

        private static void TestVec3Equality()
        {
            var a = new Vec3(1f, 2f, 3f);
            var b = new Vec3(1f, 2f, 3f);
            var c = new Vec3(1f, 2f, 4f);
            Assert(a.Equals(b) && a.GetHashCode() == b.GetHashCode(), "equal Vec3 values should be equal and hash equally");
            Assert(!a.Equals(c), "different Vec3 values should not be equal");
            Assert(a.Equals((object)b) && !a.Equals((object)"x"), "object Equals should match value and reject other types");
        }

        private static void TestRobotColor()
        {
            var c = new RobotColor(0.55f, 1f, 0.35f);
            Assert(c.R == 0.55f && c.G == 1f && c.B == 0.35f && c.A == 1f, "RobotColor should default alpha to opaque");

            var explicitAlpha = new RobotColor(0.1f, 0.2f, 0.3f, 0.4f);
            Assert(explicitAlpha.A == 0.4f, "RobotColor should keep an explicit alpha");

            var same = new RobotColor(0.55f, 1f, 0.35f, 1f);
            Assert(c.Equals(same) && c.GetHashCode() == same.GetHashCode(), "equal RobotColor values should be equal and hash equally");
            Assert(!c.Equals(new RobotColor(0f, 0f, 0f)), "different RobotColor values should not be equal");
            Assert(c.Equals((object)same) && !c.Equals((object)"x"), "object Equals should match value and reject other types");
            Assert(RobotColor.White.Equals(new RobotColor(1f, 1f, 1f, 1f)), "White should be opaque white");
        }

        private static void TestRobotAgentSpawnRequestDefaults()
        {
            var request = new RobotAgentSpawnRequest(new Vec3(1f, 2f, 3f));
            Assert(request.Position.Equals(new Vec3(1f, 2f, 3f)), "spawn request should keep its position");
            Assert(request.Facing == null, "facing should default to null");
            Assert(request.BrainMode == RobotBrainMode.Dormant, "a default robot's brain should be dormant");
            Assert(request.Gait == RobotGait.Run, "the default gait should be Run");
            Assert(request.MoveSpeed == 0f && request.TurnSpeed == 0f, "speed overrides should default to 0 (keep prefab default)");
            Assert(request.StopDistance == 0f, "stop distance should default to 0");
            Assert(request.Tint == null, "tint should default to null (native colours)");
            Assert(request.Name == null, "name should default to null");
            Assert(request.Scale == 1f, "scale should default to 1 (native size)");
            var interaction = request.Interaction ?? throw new InvalidOperationException("interaction should default to a policy object");
            Assert(interaction.NativeTalkMode == RobotNativeTalkMode.Enabled, "interaction should default to native talk");
            Assert(interaction.NativeTalkDistance == 0f, "native talk distance should default to prefab distance");
            Assert(interaction.CustomInteraction == null, "custom interaction should default to null");

            var facing = new RobotAgentSpawnRequest(
                Vec3.Zero,
                new Vec3(0f, 0f, 1f),
                brainMode: RobotBrainMode.Autonomous,
                robotTypeId: "worker-robot");
            Assert(facing.Facing.HasValue && facing.Facing.Value.Equals(new Vec3(0f, 0f, 1f)), "facing should round-trip when provided");
            Assert(facing.BrainMode == RobotBrainMode.Autonomous, "brain mode should be settable to Autonomous");

            Assert(request.RobotTypeId == null, "robot type should default to null (default type)");
            Assert(facing.RobotTypeId == "worker-robot", "robot type id should round-trip immutably");
        }

        // The robot type catalog and runtime brain-switch surface: a spawn UI's contract with RobotKit.
        private static void TestRobotTypeAndBrainSwitchContracts()
        {
            var descriptor = new RobotTypeDescriptor("worker-robot", "Worker Robot");
            Assert(descriptor.Id == "worker-robot" && descriptor.DisplayName == "Worker Robot",
                "RobotTypeDescriptor keeps id and display name");
            Assert(new RobotTypeDescriptor("slug", " ").DisplayName == "slug",
                "a blank display name falls back to the id");

            var types = typeof(IRobotAgentService).GetProperty("RobotTypes");
            Assert(types != null && typeof(IReadOnlyList<RobotTypeDescriptor>).IsAssignableFrom(types.PropertyType),
                "IRobotAgentService exposes the RobotTypes list");
            Assert(typeof(IRobotAgentService).GetMethod("TryGetPlayerEntity") == null,
                "optional player lookup must not add an abstract member to the stable RobotKit provider contract");
            var playerEntity = typeof(IRobotPlayerEntitySource).GetMethod("TryGetPlayerEntity");
            Assert(playerEntity != null && playerEntity.ReturnType == typeof(bool) &&
                   playerEntity.GetParameters().Length == 1 &&
                   playerEntity.GetParameters()[0].IsOut &&
                   playerEntity.GetParameters()[0].ParameterType == typeof(IEntity).MakeByRefType(),
                "RobotKit exposes safe live-player lookup as an optional, compatibility-preserving capability");

            var agent = new FakeRobotAgent();
            Assert(agent.BrainMode == RobotBrainMode.Dormant, "the fake starts dormant");
            agent.SetBrainMode(RobotBrainMode.Autonomous);
            Assert(agent.BrainMode == RobotBrainMode.Autonomous, "SetBrainMode switches the reported mode");

            var kinds = (RobotTargetKind[])Enum.GetValues(typeof(RobotTargetKind));
            Assert(kinds[0] == RobotTargetKind.Custom, "RobotTargetKind.Custom is the default (0)");
            var info = new RobotTargetInfo("  robot 2 ", RobotTargetKind.Robot, "a red one");
            Assert(info.Name == "ROBOT 2" && info.Kind == RobotTargetKind.Robot && info.Description == "a red one",
                "RobotTargetInfo normalises the name and keeps kind/description");
            Assert(typeof(IRobotObjectiveService).GetProperty("Targets") != null
                && typeof(IRobotObjectiveService).GetMethod("TryGetTargetInfo") != null,
                "IRobotObjectiveService exposes the target metadata view");
        }

        private static void TestRobotInteractionContracts()
        {
            Assert((int)RobotNativeTalkMode.Enabled == 0 && (int)RobotNativeTalkMode.Disabled == 1,
                "native talk mode order should be Enabled, Disabled");

            var native = RobotInteractionOptions.NativeTalk();
            Assert(native.NativeTalkMode == RobotNativeTalkMode.Enabled && native.NativeTalkDistance == 0f && native.CustomInteraction == null,
                "NativeTalk should keep the game's talk interaction");

            var distant = RobotInteractionOptions.NativeTalkAtDistance(12f);
            Assert(distant.NativeTalkMode == RobotNativeTalkMode.Enabled && distant.NativeTalkDistance == 12f,
                "NativeTalkAtDistance should keep native talk and store the distance");

            var disabled = RobotInteractionOptions.DisableNativeTalk();
            Assert(disabled.NativeTalkMode == RobotNativeTalkMode.Disabled && disabled.CustomInteraction == null,
                "DisableNativeTalk should disable native talk without installing a callback");

            var invoked = false;
            var custom = new RobotCustomInteraction(
                "Hack robot",
                _ => invoked = true,
                distance: 9f,
                screenRectExpansion: 0.2f,
                canInteract: ctx => ctx.Distance < 9f);
            var customOptions = RobotInteractionOptions.Custom(custom);
            Assert(customOptions.NativeTalkMode == RobotNativeTalkMode.Disabled && ReferenceEquals(customOptions.CustomInteraction, custom),
                "Custom should disable native talk and keep the custom interaction");
            Assert(custom.Prompt == "Hack robot" && custom.Distance == 9f && Math.Abs(custom.ScreenRectExpansion - 0.2f) < 1e-6,
                "custom interaction should keep prompt, distance, and screen expansion");

            var context = new RobotInteractionContext(
                new FakeRobotAgent(),
                new Vec3(1f, 2f, 3f),
                new Vec3(1f, 2f, 7f),
                4f);
            Assert(context.Agent != null, "interaction context should keep the selected agent");
            Assert(context.AgentPosition.Equals(new Vec3(1f, 2f, 3f)) && context.HandPosition.Equals(new Vec3(1f, 2f, 7f)),
                "interaction context should keep positions");
            Assert(context.Distance == 4f && custom.CanInteract!(context), "interaction context should keep distance");
            custom.Interact!(context);
            Assert(invoked, "custom interaction callback should be invokable");

            var setInteraction = typeof(IRobotAgent).GetMethod("SetInteraction");
            Assert(setInteraction != null && setInteraction.GetParameters().Length == 1 &&
                setInteraction.GetParameters()[0].ParameterType == typeof(RobotInteractionOptions),
                "IRobotAgent should expose SetInteraction(RobotInteractionOptions)");
        }

        private static void TestReachableSpawnRequestDefaults()
        {
            var request = new ReachableSpawnRequest(new Vec3(4f, 5f, 6f));
            Assert(request.Origin.Equals(new Vec3(4f, 5f, 6f)), "reachable-spawn request should keep its origin");
            Assert(request.ReachableFrom == null, "ReachableFrom should default to null (uses Origin)");
            Assert(request.MinRadius == 8f, "MinRadius should default to 8");
            Assert(request.MaxRadius == 24f, "MaxRadius should default to 24");
            Assert(request.MaxCandidates == 16, "MaxCandidates should default to 16");
            Assert(request.VerticalScan == 3f, "VerticalScan should default to 3");
            Assert(request.GroundProbeDepth == 12f, "GroundProbeDepth should default to 12");
            Assert(request.HeightOffset == 0.25f, "HeightOffset should default to 0.25");

            var anchored = new ReachableSpawnRequest(
                Vec3.Zero,
                reachableFrom: new Vec3(1f, 0f, 2f),
                minRadius: 5f,
                maxRadius: 30f,
                maxCandidates: 24);
            Assert(anchored.ReachableFrom.HasValue && anchored.ReachableFrom.Value.Equals(new Vec3(1f, 0f, 2f)), "ReachableFrom should round-trip");
            Assert(anchored.MinRadius == 5f && anchored.MaxRadius == 30f && anchored.MaxCandidates == 24, "request radii/attempts should be settable");
        }

        // HeadPosition is the head/aim anchor the SDK exposes for hit-zone tests (headshots) and world-anchored
        // combat HUD; guard its presence and read-only Vec3 shape so the contract cannot regress silently. The
        // interface is Unity-free, so reflecting its own members loads no UnityEngine types.
        private static void TestRobotAgentSurface()
        {
            var headPosition = typeof(IRobotAgent).GetProperty("HeadPosition");
            Assert(headPosition != null, "IRobotAgent should expose a HeadPosition property");
            Assert(headPosition!.PropertyType == typeof(Vec3), "HeadPosition should be a Vec3");
            Assert(headPosition.CanRead && !headPosition.CanWrite, "HeadPosition should be a read-only property");
        }

        private static void TestRobotAgentEnums()
        {
            // RobotDamageType must mirror the game's native DamageType ordering (Normal, Fire, Electricity, Poison, Water).
            Assert((int)RobotDamageType.Normal == 0, "Normal must be 0");
            Assert((int)RobotDamageType.Fire == 1, "Fire must be 1");
            Assert((int)RobotDamageType.Electricity == 2, "Electricity must be 2");
            Assert((int)RobotDamageType.Poison == 3, "Poison must be 3");
            Assert((int)RobotDamageType.Water == 4, "Water must be 4");

            Assert((int)RobotBrainMode.Dormant == 0, "Dormant must be the default (0) brain mode");
            Assert((int)RobotGait.Walk == 0 && (int)RobotGait.Run == 1 && (int)RobotGait.Sprint == 2, "gait order should be Walk, Run, Sprint");
        }

        private interface IFakeService
        {
        }

        private sealed class FakeService : IFakeService
        {
        }

        private sealed class FakeRobotAgent : IRobotAgent
        {
            public string Id => "fake";
            public string Name => "Fake";
            public bool IsAlive => true;
            public Vec3 Position => Vec3.Zero;
            public Vec3 HeadPosition => Vec3.Zero;
            public RobotBrainMode BrainMode { get; private set; } = RobotBrainMode.Dormant;
            public bool IsMoving => false;
            public bool HasReachedTarget => false;
            public float MoveSpeed { get; set; }
            public float TurnSpeed { get; set; }
            public float StopDistance { get; set; }
            public RobotGait Gait { get; set; }
            public OperationResult<bool> MoveTo(Vec3 position) => OperationResult<bool>.Success(true);
            public OperationResult<bool> Chase(IEntity target) => OperationResult<bool>.Success(true);
            public OperationResult<bool> Stop() => OperationResult<bool>.Success(true);
            public OperationResult<bool> SetBrainMode(RobotBrainMode mode)
            {
                BrainMode = mode;
                return OperationResult<bool>.Success(true);
            }
            public OperationResult<bool> ConfigureMovement(RobotMovementSettings settings) =>
                OperationResult<bool>.Success(true);
            public OperationResult<bool> SetTint(RobotColor color) => OperationResult<bool>.Success(true);
            public OperationResult<bool> SetEmote(string emojiShortcode) => OperationResult<bool>.Success(true);
            public OperationResult<bool> SetName(string name) => OperationResult<bool>.Success(true);
            public OperationResult<bool> SetScale(float scale) => OperationResult<bool>.Success(true);
            public OperationResult<bool> SetInteraction(RobotInteractionOptions options) =>
                OperationResult<bool>.Success(true);
            public OperationResult<bool> ApplyDamage(float amount, RobotDamageType type, string source) =>
                OperationResult<bool>.Success(false);
            public OperationResult<bool> Kill(RobotDamageType type, string source) => OperationResult<bool>.Success(true);
            public OperationResult<bool> Ragdoll() => OperationResult<bool>.Success(true);
            public OperationResult<bool> Knockback(Vec3 impulse) => OperationResult<bool>.Success(true);
            public OperationResult<bool> Despawn() => OperationResult<bool>.Success(true);
            public void Dispose() { }
        }


    }
}
