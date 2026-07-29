using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    // Exercises the Unity-free V1 contracts and specialist module data types. No GameCode/UnityEngine involved.
    internal static partial class SdkSurfaceTests
    {
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

    }
}
