using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    // Exercises the Unity-free V1 contracts and specialist module data types. No GameCode/UnityEngine involved.
    internal static class SdkSurfaceTests
    {
        public static void Run()
        {
            TestVec3RoundTrip();
            TestVec3Equality();
            TestRobotColor();
            TestRobotAgentSpawnRequestDefaults();
            TestRobotTypeAndBrainSwitchContracts();
            TestRobotInteractionContracts();
            TestReachableSpawnRequestDefaults();
            TestRobotAgentEnums();
            TestRobotAgentSurface();
            TestBrainQueryContracts();
            TestConversationContracts();
            TestDialogueInputContracts();
            TestGameScenesClassifier();
            TestWorldSessionEndContracts();
            TestUnifiedExpectedFailureContracts();
            TestShopContracts();
            TestRobotObjectiveProgramContracts();
            Console.WriteLine("All SDK surface tests passed.");
        }

        private static void TestUnifiedExpectedFailureContracts()
        {
            var bespokeOperationResults = typeof(TopiaForgeMod).Assembly.GetExportedTypes()
                .Where(type => type != typeof(OperationResult<>))
                .Where(type => type.GetProperty("Succeeded") != null && type.GetProperty("ErrorCode") != null)
                .Select(type => type.FullName)
                .ToArray();
            Assert(bespokeOperationResults.Length == 0,
                "expected failures must not introduce result wrappers alongside OperationResult<T>: " +
                string.Join(", ", bespokeOperationResults));

            var configType = typeof(ConfigDefinition<object>);
            Assert(configType.GetProperty("Validate")?.PropertyType ==
                   typeof(Func<object, OperationResult<bool>>) &&
                   configType.GetProperty("Migrate")?.PropertyType ==
                   typeof(Func<int, object, OperationResult<object>>),
                "config validation and migration use the common stable result contract");

            var register = typeof(ICommandService).GetMethod("Register");
            Assert(register != null && register.GetParameters()[1].ParameterType ==
                   typeof(Func<CommandInvocation, OperationResult<string>>),
                "command handlers use OperationResult<string> for display text and stable failures");

            Assert(typeof(IRuntimeInfo).GetProperty("GameVersion") == null &&
                   typeof(IRuntimeInfo).GetMethod("TryGetGameVersion") != null,
                "optional runtime version discovery follows the cheap Try-query convention");
        }

        // The shared scene classifier every mod uses to agree on what counts as "the menu" vs gameplay.
        private static void TestGameScenesClassifier()
        {
            Assert(GameScenes.MainMenuSceneName == "TestCityStartMenu", "MainMenuSceneName is pinned to the verified menu scene");
            Assert(GameScenes.IsMainMenuScene("TestCityStartMenu") && GameScenes.IsMainMenuScene("testcitystartmenu"),
                "IsMainMenuScene matches the menu scene case-insensitively");
            Assert(!GameScenes.IsMainMenuScene("TestCity") && !GameScenes.IsMainMenuScene(null!),
                "IsMainMenuScene rejects other scenes and null");

            foreach (var scene in new[] { "TestCityStartMenu", "MainMenu_X", "BootScene", "LevelLoader", "SplashIntro" })
            {
                Assert(GameScenes.IsNonGameplayScene(scene), scene + " should classify as non-gameplay");
            }

            foreach (var scene in new[] { "UgcPlay", "TestCity", "02 City Streets" })
            {
                Assert(!GameScenes.IsNonGameplayScene(scene), scene + " should classify as gameplay");
            }

            Assert(!GameScenes.IsNonGameplayScene(null!) && !GameScenes.IsNonGameplayScene(string.Empty),
                "IsNonGameplayScene is null/empty safe");
        }

        // The session-end lifecycle contract (the fix for gamemodes staying active over the menu).
        private static void TestWorldSessionEndContracts()
        {
            var sessionEnded = typeof(IWorldGamemodeService).GetEvent("SessionEnded");
            Assert(sessionEnded != null && sessionEnded.EventHandlerType == typeof(Action<WorldSessionEnd>),
                "IWorldGamemodeService exposes SessionEnded as Action<WorldSessionEnd>");
            var endSession = typeof(IWorldGamemodeService).GetMethod("EndSession");
            Assert(endSession != null && endSession.GetParameters().Length == 1
                && endSession.GetParameters()[0].ParameterType == typeof(WorldSessionEndReason),
                "IWorldGamemodeService exposes EndSession(WorldSessionEndReason)");

            // Pin the reason set: mods switch on these, so a silent rename/reorder is a breaking change.
            Assert((int)WorldSessionEndReason.MenuReached == 0 && (int)WorldSessionEndReason.EndedByGamemode == 1
                && (int)WorldSessionEndReason.Superseded == 2 && (int)WorldSessionEndReason.ProviderUnloading == 3
                && (int)WorldSessionEndReason.SceneReplaced == 4 && (int)WorldSessionEndReason.LoadFailed == 5,
                "WorldSessionEndReason order must append SceneReplaced and LoadFailed after the original reasons");

            var inFlight = typeof(IWorldTransitionState).GetProperty("IsTransitionInFlight");
            Assert(inFlight != null && inFlight.PropertyType == typeof(bool) && inFlight.CanRead && !inFlight.CanWrite,
                "IWorldTransitionState exposes read-only bool IsTransitionInFlight");
            Assert(typeof(IWorldGamemodeService).GetProperty("IsTransitionInFlight") == null,
                "scene-load state stays on its focused optional capability interface");

            var session = new WorldSession("world", "gamemode", "gameScene", "Scene", DateTime.UtcNow);
            var end = new WorldSessionEnd(session, WorldSessionEndReason.MenuReached);
            Assert(ReferenceEquals(end.Session, session) && end.Reason == WorldSessionEndReason.MenuReached,
                "WorldSessionEnd carries the ended session and the reason");

            var threw = false;
            try
            {
                _ = new WorldSessionEnd(null!, WorldSessionEndReason.MenuReached);
            }
            catch (ArgumentNullException)
            {
                threw = true;
            }

            Assert(threw, "WorldSessionEnd null-guards the session");
        }


        // The wander/flee/reprogram objective additions (RobotKit 0.8.0): appended enum members (mods switch on
        // these, so a silent reorder is a breaking change), the new factories and their defaults, the courier
        // payload rules, and the ProgramDelivered event contract.
        private static void TestRobotObjectiveProgramContracts()
        {
            // Pin the enum orders: additions must append.
            Assert((int)RobotObjectiveKind.Idle == 0 && (int)RobotObjectiveKind.GoTo == 1
                && (int)RobotObjectiveKind.Follow == 2 && (int)RobotObjectiveKind.Patrol == 3
                && (int)RobotObjectiveKind.Wander == 4 && (int)RobotObjectiveKind.Flee == 5
                && (int)RobotObjectiveKind.Reprogram == 6,
                "RobotObjectiveKind order must be Idle, GoTo, Follow, Patrol, Wander, Flee, Reprogram");
            Assert((int)RobotObjectiveState.Idle == 0 && (int)RobotObjectiveState.Seeking == 1
                && (int)RobotObjectiveState.Arrived == 2 && (int)RobotObjectiveState.Dwelling == 3
                && (int)RobotObjectiveState.TargetMissing == 4 && (int)RobotObjectiveState.Cancelled == 5
                && (int)RobotObjectiveState.Delivered == 6,
                "RobotObjectiveState order must end with the appended Delivered");

            // Wander factories: home = nothing (agent position), a named target, or a fixed point.
            var wanderHere = RobotObjective.Wander();
            Assert(wanderHere.Kind == RobotObjectiveKind.Wander && wanderHere.TargetName == null
                && wanderHere.TargetPoint == null && wanderHere.Payload == null,
                "Wander() roams the set-time position and carries no payload");
            Assert(Math.Abs(wanderHere.WanderRadius - 8f) < 1e-6f, "WanderRadius defaults to 8 m");
            var tunedWander = RobotObjective.Wander(new RobotObjectiveOptions(wanderRadius: 3f));
            Assert(Math.Abs(tunedWander.WanderRadius - 3f) < 1e-6f,
                "immutable objective options configure the wander radius");
            Assert(RobotObjective.Wander("PAD").TargetName == "PAD", "Wander(name) anchors to the named target");
            Assert(RobotObjective.Wander(new Vec3(1f, 2f, 3f)).TargetPoint != null, "Wander(point) anchors to the point");

            // Flee factory and its distance knob.
            var flee = RobotObjective.Flee("PLAYER");
            Assert(flee.Kind == RobotObjectiveKind.Flee && flee.TargetName == "PLAYER" && flee.Payload == null,
                "Flee(name) targets the threat by name");
            Assert(Math.Abs(flee.FleeDistance - 8f) < 1e-6f, "FleeDistance defaults to 8 m");

            // Reprogram: courier + payload, by reference, with the no-chain-letters guard.
            var payload = RobotObjective.Follow("PLAYER");
            var courier = RobotObjective.Reprogram("ROBOT 2", payload);
            Assert(courier.Kind == RobotObjectiveKind.Reprogram && courier.TargetName == "ROBOT 2"
                && ReferenceEquals(courier.Payload, payload),
                "Reprogram keeps the recipient name and the payload by reference");

            var threwNull = false;
            try
            {
                _ = RobotObjective.Reprogram("ROBOT 2", null!);
            }
            catch (ArgumentNullException)
            {
                threwNull = true;
            }

            Assert(threwNull, "Reprogram null-guards the payload");

            var threwNested = false;
            try
            {
                _ = RobotObjective.Reprogram("ROBOT 3", courier);
            }
            catch (ArgumentException)
            {
                threwNested = true;
            }

            Assert(threwNested, "a Reprogram payload cannot itself be a Reprogram");

            // Describe() covers the new kinds (HUD badges and ground-truth facts read these).
            Assert(RobotObjective.Wander().Describe() == "WANDER", "Wander() describes as WANDER");
            Assert(RobotObjective.Wander("RED MARKER").Describe() == "WANDER NEAR RED MARKER",
                "a named wander describes its anchor");
            Assert(flee.Describe() == "FLEE FROM PLAYER", "a flee describes its threat");
            Assert(courier.Describe() == "REPROGRAM ROBOT 2: FOLLOW PLAYER", "a courier describes recipient and payload");

            // The delivery event contract.
            var delivered = typeof(IRobotObjectiveService).GetEvent("ProgramDelivered");
            Assert(delivered != null && delivered.EventHandlerType == typeof(Action<RobotProgramDelivery>),
                "IRobotObjectiveService exposes ProgramDelivered as Action<RobotProgramDelivery>");

            var threwDelivery = false;
            try
            {
                _ = new RobotProgramDelivery(null!, null!, payload);
            }
            catch (ArgumentNullException)
            {
                threwDelivery = true;
            }

            Assert(threwDelivery, "RobotProgramDelivery null-guards its parts");
        }

        // The multi-turn conversation primitive: immutable requests and awaited lifetime-owned turns.
        private static void TestConversationContracts()
        {
            var request = new RobotConversationRequest("frame", new[] { "CONVERT", "REFUSE" });
            Assert(request.SystemFrame == "frame", "RobotConversationRequest keeps the system frame");
            Assert(request.DecisionOptions.Count == 2 && request.DecisionOptions[0] == "CONVERT", "decision options are kept in order");
            Assert(request.MaxTurns == 3, "MaxTurns defaults to 3");
            Assert(Math.Abs(request.Temperature - 0.7f) < 1e-6, "Temperature defaults to 0.7");
            Assert(request.MaxReplyChars == 200, "MaxReplyChars defaults to 200");
            Assert(request.Usage == "robot-conversation", "Usage defaults");

            var nullRequest = new RobotConversationRequest(null!, null!);
            Assert(nullRequest.SystemFrame == string.Empty && nullRequest.DecisionOptions.Count == 0, "request null-guards frame/options");

            Assert(request.LiveFacts == null, "LiveFacts defaults to null (static facts only)");
            var liveRequest = new RobotConversationRequest(
                "frame",
                new[] { "CONVERT" },
                liveFacts: () => new Dictionary<string, string> { ["k"] = "v" });
            var liveFacts = liveRequest.LiveFacts?.Invoke();
            Assert(liveFacts != null && liveFacts["k"] == "v",
                "LiveFacts is supplied immutably at construction");

            var begin = typeof(IRobotConversationService).GetMethod("BeginConversation");
            Assert(begin != null && begin.ReturnType == typeof(OperationResult<IRobotConversation>),
                "BeginConversation returns a stable result containing a lifetime-owned conversation");
            Assert(typeof(IRobotConversationService).GetProperty("IsAvailable") != null, "service exposes IsAvailable");
            foreach (var member in new[] { "IsEnded", "TurnCount", "MaxTurns" })
            {
                Assert(typeof(IRobotConversation).GetProperty(member) != null, "IRobotConversation should expose " + member);
            }

            var submit = typeof(IRobotConversation).GetMethod("SubmitAsync");
            Assert(submit != null && submit.ReturnType ==
                typeof(System.Threading.Tasks.Task<OperationResult<RobotConversationTurnResult>>),
                "IRobotConversation exposes awaited result-based turns");
            Assert(typeof(IDisposable).IsAssignableFrom(typeof(IRobotConversation)),
                "conversation handles are explicitly disposable for early release");
        }

        // The player dialogue input (text + voice) contract surface.
        private static void TestDialogueInputContracts()
        {
            var begin = typeof(IPlayerDialogueInputService).GetMethod("BeginVoiceCapture");
            Assert(begin != null && begin.ReturnType == typeof(OperationResult<IVoiceCapture>),
                "BeginVoiceCapture returns a stable result");
            Assert(typeof(IPlayerDialogueInputService).GetProperty("IsVoiceAvailable") != null, "service exposes IsVoiceAvailable");
            Assert(typeof(IVoiceCapture).GetProperty("IsRecording")?.PropertyType == typeof(bool),
                "IVoiceCapture exposes recording state");
            var stop = typeof(IVoiceCapture).GetMethod("StopAsync");
            Assert(stop != null && stop.ReturnType ==
                typeof(System.Threading.Tasks.Task<OperationResult<VoiceTranscriptResult>>),
                "voice capture exposes awaited transcription");
            Assert(typeof(IDisposable).IsAssignableFrom(typeof(IVoiceCapture)),
                "voice captures are explicitly disposable for cancellation");

            // TextInputBuffer is a concrete shared helper — exercise its core behaviour.
            var buffer = new TextInputBuffer(4);
            buffer.Append("ab");
            buffer.Append("cdef"); // clamps at 4
            Assert(buffer.Text == "abcd", "TextInputBuffer clamps to maxChars");
            buffer.Append("\b");
            Assert(buffer.Text == "abc", "TextInputBuffer honours backspace");
            buffer.Append("\n");
            Assert(buffer.ConsumeSubmit() && !buffer.ConsumeSubmit(), "TextInputBuffer submit is one-shot");
        }

        // The structured brain-query primitive: guard immutable requests and Task/result completion.
        private static void TestBrainQueryContracts()
        {
            Assert((int)RobotDecision.Comply == 0 && (int)RobotDecision.Freeze == 1 && (int)RobotDecision.Flee == 2
                && (int)RobotDecision.Resist == 3 && (int)RobotDecision.Unknown == 4, "RobotDecision order must be Comply,Freeze,Flee,Resist,Unknown");
            Assert((int)BrainFieldType.String == 0 && (int)BrainFieldType.Number == 1 && (int)BrainFieldType.Boolean == 2,
                "BrainFieldType order must be String,Number,Boolean");

            var field = new BrainOutputField("action", "the reaction", BrainFieldType.String, new[] { "comply", "resist" });
            Assert(field.Name == "action" && field.Type == BrainFieldType.String, "BrainOutputField should keep name/type");
            Assert(field.AllowedStrings != null && field.AllowedStrings.Count == 2, "BrainOutputField should keep its allowed strings");

            var request = new BrainQueryRequest("hello", new[] { field });
            Assert(request.Prompt == "hello" && request.Outputs.Count == 1, "BrainQueryRequest should keep prompt and outputs");
            Assert(request.Usage == "robot-brain-query", "BrainQueryRequest.Usage should default");
            Assert(Math.Abs(request.Temperature - 0.7f) < 1e-6 && !request.UseReasoning, "BrainQueryRequest defaults: temp 0.7, no reasoning");

            var nullRequest = new BrainQueryRequest(null!, null!);
            Assert(nullRequest.Prompt == string.Empty && nullRequest.Outputs.Count == 0, "BrainQueryRequest should null-guard prompt/outputs");

            var unavailable = OperationResult<BrainQueryResult>.Failure(ModErrorCode.Unavailable, "offline");
            Assert(!unavailable.Succeeded && unavailable.ErrorCode == ModErrorCode.Unavailable,
                "expected brain failures use stable result codes");

            var ok = new BrainQueryResult(new Dictionary<string, string> { ["action"] = "comply" });
            Assert(ok.TryGet("action", out var action) && action == "comply", "BrainQueryResult.TryGet should return a present value");
            Assert(!ok.TryGet("missing", out _), "BrainQueryResult.TryGet should be false for a missing key");

            var query = typeof(IRobotBrainQueryService).GetMethod("QueryAsync");
            Assert(query != null && query.ReturnType ==
                typeof(System.Threading.Tasks.Task<OperationResult<BrainQueryResult>>),
                "IRobotBrainQueryService.QueryAsync returns an awaited stable result");
            Assert(typeof(IRobotBrainQueryService).GetProperty("IsAvailable") != null, "IRobotBrainQueryService should expose IsAvailable");
        }

        // The shop contract (catalog item + wallet + purchase arbiter) consumed by the TopiaForgeUi shop pane.
        // Behaviour lives in ShopTests; this pins the surface so it cannot regress silently.
        private static void TestShopContracts()
        {
            var item = new ShopItem("mod.item", "ITEM", "desc", 25);
            Assert(item.Category == string.Empty && item.MaxPurchases == 0,
                "ShopItem defaults: no category chip, unlimited purchases");

            Assert(typeof(IShopWallet).IsAssignableFrom(typeof(ShopWallet)), "ShopWallet implements IShopWallet");
            var balanceChanged = typeof(IShopWallet).GetEvent("BalanceChanged");
            Assert(balanceChanged != null && balanceChanged.EventHandlerType == typeof(Action<int>),
                "IShopWallet exposes BalanceChanged as Action<int>");
            var trySpend = typeof(IShopWallet).GetMethod("TrySpend");
            Assert(trySpend != null && trySpend.ReturnType == typeof(bool)
                && trySpend.GetParameters().Length == 1 && trySpend.GetParameters()[0].ParameterType == typeof(int),
                "IShopWallet exposes bool TrySpend(int)");
            Assert(typeof(IShopWallet).GetProperty("Balance")?.PropertyType == typeof(int),
                "IShopWallet exposes int Balance");

            // Pin the result set: shop UIs and mods switch on these, so a silent rename/reorder is breaking.
            Assert((int)ShopPurchaseResult.Purchased == 0 && (int)ShopPurchaseResult.InsufficientFunds == 1
                && (int)ShopPurchaseResult.SoldOut == 2 && (int)ShopPurchaseResult.Rejected == 3,
                "ShopPurchaseResult order must be Purchased, InsufficientFunds, SoldOut, Rejected");

            var tryPurchase = typeof(ShopTransactions).GetMethod("TryPurchase");
            Assert(tryPurchase != null && tryPurchase.ReturnType == typeof(ShopPurchaseResult)
                && tryPurchase.GetParameters().Length == 4,
                "ShopTransactions exposes TryPurchase(item, wallet, timesPurchased, canPurchase)");
        }

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


        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
