using System;
using System.Collections.Generic;
using System.Linq;
using Robotopia.Mods;

namespace Robotopia.ModManager.Tests
{
    // Exercises the Unity-free additions to Robotopia.Mods.Abstractions: the Vec3 struct and the IModContext
    // service-resolution extension methods. No GameCode/UnityEngine involved.
    internal static class SdkSurfaceTests
    {
        public static void Run()
        {
            TestVec3RoundTrip();
            TestVec3Equality();
            TestRequireServiceReturnsRegistered();
            TestRequireServiceThrowsWhenMissing();
            TestTryGetService();
            TestAssetContracts();
            TestPromptContracts();
            TestAssetAndPromptContextExtensions();
            TestRobotColor();
            TestRobotAgentSpawnRequestDefaults();
            TestRobotInteractionContracts();
            TestReachableSpawnRequestDefaults();
            TestRobotAgentEnums();
            TestRobotAgentSurface();
            TestBrainQueryContracts();
            TestConversationContracts();
            TestDialogueInputContracts();
            Console.WriteLine("All SDK surface tests passed.");
        }

        // The multi-turn conversation primitive (IRobotConversationService): request defaults + the pollable handle
        // surface, so the Unity-free contract cannot regress silently.
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

            var begin = typeof(IRobotConversationService).GetMethod("BeginConversation");
            Assert(begin != null && begin.ReturnType == typeof(IRobotConversation), "BeginConversation returns IRobotConversation");
            Assert(typeof(IRobotConversationService).GetProperty("IsAvailable") != null, "service exposes IsAvailable");
            foreach (var member in new[] { "IsThinking", "TurnReady", "Ended", "TurnCount", "LastReply", "LastDecision" })
            {
                Assert(typeof(IRobotConversation).GetProperty(member) != null, "IRobotConversation should expose " + member);
            }

            Assert(typeof(IRobotConversation).GetMethod("Submit") != null, "IRobotConversation should expose Submit");
            Assert(typeof(IRobotConversation).GetMethod("End") != null, "IRobotConversation should expose End");
        }

        // The player dialogue input (text + voice) contract surface.
        private static void TestDialogueInputContracts()
        {
            var begin = typeof(IPlayerDialogueInputService).GetMethod("BeginVoiceCapture");
            Assert(begin != null && begin.ReturnType == typeof(IVoiceCapture), "BeginVoiceCapture returns IVoiceCapture");
            Assert(typeof(IPlayerDialogueInputService).GetProperty("IsVoiceAvailable") != null, "service exposes IsVoiceAvailable");
            foreach (var member in new[] { "IsRecording", "IsComplete", "Found", "Text" })
            {
                Assert(typeof(IVoiceCapture).GetProperty(member) != null, "IVoiceCapture should expose " + member);
            }

            Assert(typeof(IVoiceCapture).GetMethod("Stop") != null && typeof(IVoiceCapture).GetMethod("Cancel") != null, "IVoiceCapture should expose Stop/Cancel");

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

        // The structured brain-query primitive (IRobotBrainQueryService): guard the enum order, the request/result
        // defaults, and the pollable-handle surface so the Unity-free contract cannot regress silently.
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

            var unavailable = BrainQueryResult.Unavailable;
            Assert(!unavailable.Available && !unavailable.Succeeded, "Unavailable result should be not-available, not-succeeded");
            Assert(unavailable.Values.Count == 0 && !unavailable.TryGet("x", out _), "Unavailable result should have empty values");

            var ok = new BrainQueryResult(true, true, new Dictionary<string, string> { ["action"] = "comply" }, null);
            Assert(ok.TryGet("action", out var action) && action == "comply", "BrainQueryResult.TryGet should return a present value");
            Assert(!ok.TryGet("missing", out _), "BrainQueryResult.TryGet should be false for a missing key");

            // Pollable-handle surface (Unity-free reflection, loads no UnityEngine).
            var serviceBegin = typeof(IRobotBrainQueryService).GetMethod("BeginQuery");
            Assert(serviceBegin != null && serviceBegin.ReturnType == typeof(IRobotBrainQuery), "IRobotBrainQueryService.BeginQuery should return IRobotBrainQuery");
            Assert(typeof(IRobotBrainQueryService).GetProperty("IsAvailable") != null, "IRobotBrainQueryService should expose IsAvailable");
            var complete = typeof(IRobotBrainQuery).GetProperty("IsComplete");
            Assert(complete != null && complete.PropertyType == typeof(bool), "IRobotBrainQuery should expose a bool IsComplete");
            var result = typeof(IRobotBrainQuery).GetProperty("Result");
            Assert(result != null && result.PropertyType == typeof(BrainQueryResult), "IRobotBrainQuery.Result should be a BrainQueryResult");
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

            var facing = new RobotAgentSpawnRequest(Vec3.Zero, new Vec3(0f, 0f, 1f)) { BrainMode = RobotBrainMode.Autonomous };
            Assert(facing.Facing.HasValue && facing.Facing.Value.Equals(new Vec3(0f, 0f, 1f)), "facing should round-trip when provided");
            Assert(facing.BrainMode == RobotBrainMode.Autonomous, "brain mode should be settable to Autonomous");
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
            var custom = new RobotCustomInteraction("Hack robot", _ => invoked = true)
            {
                Distance = 9f,
                ScreenRectExpansion = 0.2f,
                CanInteract = ctx => ctx.Distance < 9f
            };
            var customOptions = RobotInteractionOptions.Custom(custom);
            Assert(customOptions.NativeTalkMode == RobotNativeTalkMode.Disabled && ReferenceEquals(customOptions.CustomInteraction, custom),
                "Custom should disable native talk and keep the custom interaction");
            Assert(custom.Prompt == "Hack robot" && custom.Distance == 9f && Math.Abs(custom.ScreenRectExpansion - 0.2f) < 1e-6,
                "custom interaction should keep prompt, distance, and screen expansion");

            var context = new RobotInteractionContext(
                new FakeRobotAgent(),
                new object(),
                new Vec3(1f, 2f, 3f),
                new Vec3(1f, 2f, 7f),
                4f);
            Assert(context.Agent != null && context.Hand != null, "interaction context should keep agent and hand");
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

            var anchored = new ReachableSpawnRequest(Vec3.Zero)
            {
                ReachableFrom = new Vec3(1f, 0f, 2f),
                MinRadius = 5f,
                MaxRadius = 30f,
                MaxCandidates = 24
            };
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

        private static void TestRequireServiceReturnsRegistered()
        {
            var svc = new FakeService();
            var context = new FakeContext();
            context.Services[typeof(IFakeService)] = svc;
            Assert(ReferenceEquals(context.RequireService<IFakeService>(), svc), "RequireService should return the registered service");
        }

        private static void TestRequireServiceThrowsWhenMissing()
        {
            var context = new FakeContext();
            var threw = false;
            try
            {
                context.RequireService<IFakeService>();
            }
            catch (InvalidOperationException ex)
            {
                threw = ex.Message.Contains("IFakeService");
            }

            Assert(threw, "RequireService should throw an InvalidOperationException naming the missing service type");
        }

        private static void TestTryGetService()
        {
            var svc = new FakeService();
            var context = new FakeContext();

            Assert(!context.TryGetService<IFakeService>(out _), "TryGetService should be false when unregistered");

            context.Services[typeof(IFakeService)] = svc;
            Assert(context.TryGetService<IFakeService>(out var resolved) && ReferenceEquals(resolved, svc),
                "TryGetService should return true and the service when registered");
        }

        private static void TestAssetContracts()
        {
            var options = AssetBundleLoadOptions.Default;
            Assert(options.Cache && !options.Reload, "asset bundle options should cache by default without reload");

            var request = new AssetBundleLoadRequest("owner.mod", "pkg", "AssetBundles/main", options);
            Assert(request.OwnerModId == "owner.mod" && request.PackagePath == "pkg" && request.RelativePath == "AssetBundles/main",
                "asset bundle request should keep owner/package/relative path");

            var handle = new FakeAssetBundleHandle();
            var loadSuccess = AssetBundleLoadResult.Success(handle);
            Assert(loadSuccess.Ok && ReferenceEquals(loadSuccess.Bundle, handle) && loadSuccess.Error == string.Empty,
                "asset bundle load success should expose the handle");
            var loadFail = AssetBundleLoadResult.Fail("missing");
            Assert(!loadFail.Ok && loadFail.Bundle == null && loadFail.Error == "missing", "asset bundle load failure should expose the error");

            var asset = new object();
            var assetSuccess = AssetLoadResult.Success(asset);
            Assert(assetSuccess.Ok && ReferenceEquals(assetSuccess.Asset, asset), "asset load success should expose the asset");
            var typedAssetSuccess = AssetLoadResult<object>.Success(asset);
            Assert(typedAssetSuccess.Ok && ReferenceEquals(typedAssetSuccess.Asset, asset), "typed asset load success should expose the asset");

            var spawnSuccess = SpawnAssetResult.Success(asset);
            Assert(spawnSuccess.Ok && ReferenceEquals(spawnSuccess.Instance, asset), "spawn success should expose the instance");
            var typedSpawnSuccess = SpawnAssetResult<object>.Success(asset);
            Assert(typedSpawnSuccess.Ok && ReferenceEquals(typedSpawnSuccess.Instance, asset), "typed spawn success should expose the instance");
        }

        private static void TestPromptContracts()
        {
            var request = new PromptOverrideRequest("owner.mod", "robot.greeting", "replacement", 7, "why");
            Assert(request.OwnerModId == "owner.mod" && request.PromptId == "robot.greeting", "prompt request should keep owner and prompt id");
            Assert(request.ReplacementText == "replacement" && request.Priority == 7 && request.Description == "why",
                "prompt request should keep replacement metadata");

            var promptOverride = new PromptOverride("owner.mod", "robot.greeting", "replacement", 7, "why");
            var conflict = new PromptConflict("robot.greeting", new[] { promptOverride }, promptOverride);
            Assert(conflict.PromptId == "robot.greeting" && ReferenceEquals(conflict.EffectiveOverride, promptOverride),
                "prompt conflict should expose prompt id and effective override");
            Assert(conflict.Overrides.Count == 1 && ReferenceEquals(conflict.Overrides[0], promptOverride),
                "prompt conflict should keep overrides");
        }

        private static void TestAssetAndPromptContextExtensions()
        {
            var context = new FakeContext();
            var assetService = new FakeAssetBundleService();
            var promptRegistry = new FakePromptOverrideRegistry();
            context.Services[typeof(IAssetBundleService)] = assetService;
            context.Services[typeof(IPromptOverrideRegistry)] = promptRegistry;

            var load = context.LoadAssetBundle("AssetBundles/main");
            Assert(load.Ok && assetService.LastRequest != null, "context.LoadAssetBundle should call the asset service");
            Assert(assetService.LastRequest!.OwnerModId == context.ModId && assetService.LastRequest.PackagePath == context.Paths.PackagePath,
                "context.LoadAssetBundle should inject owner and package path");

            var asset = context.LoadAsset<object>(assetService.Handle, "prefab");
            Assert(asset.Ok && assetService.LastAssetName == "prefab", "context.LoadAsset should call the typed asset helper");

            var prefab = new object();
            var spawn = context.SpawnAsset(prefab);
            Assert(spawn.Ok && ReferenceEquals(assetService.LastPrefab, prefab), "context.SpawnAsset should call the typed spawn helper");

            var prompt = context.RegisterPromptOverride("robot.greeting", "hello", 3, "test");
            Assert(promptRegistry.LastRequest != null && promptRegistry.LastRequest.OwnerModId == context.ModId,
                "context.RegisterPromptOverride should inject the owner mod id");
            Assert(prompt.Override.Priority == 3 && prompt.Override.Description == "test", "prompt helper should keep priority and description");
        }

        private interface IFakeService
        {
        }

        private sealed class FakeService : IFakeService
        {
        }

        private sealed class FakeAssetBundleService : IAssetBundleService
        {
            public FakeAssetBundleHandle Handle { get; } = new FakeAssetBundleHandle();
            public AssetBundleLoadRequest? LastRequest { get; private set; }
            public string LastAssetName { get; private set; } = string.Empty;
            public object? LastPrefab { get; private set; }

            public AssetBundleLoadResult LoadBundle(AssetBundleLoadRequest request)
            {
                LastRequest = request;
                return AssetBundleLoadResult.Success(Handle);
            }

            public AssetLoadResult LoadAsset(IAssetBundleHandle bundle, string assetName, Type assetType)
            {
                LastAssetName = assetName;
                return AssetLoadResult.Success(new object());
            }

            public AssetLoadResult<T> LoadAsset<T>(IAssetBundleHandle bundle, string assetName) where T : class
            {
                LastAssetName = assetName;
                return AssetLoadResult<T>.Success(new object() as T ?? throw new InvalidOperationException("Unexpected test type."));
            }

            public SpawnAssetResult SpawnAsset(object prefab)
            {
                LastPrefab = prefab;
                return SpawnAssetResult.Success(prefab);
            }

            public SpawnAssetResult<T> SpawnAsset<T>(T prefab) where T : class
            {
                LastPrefab = prefab;
                return SpawnAssetResult<T>.Success(prefab);
            }

            public IReadOnlyList<string> GetAllAssetNames(IAssetBundleHandle bundle)
            {
                return new[] { "prefab" };
            }

            public void UnloadOwner(string ownerModId, bool unloadAllLoadedObjects = false)
            {
            }
        }

        private sealed class FakeAssetBundleHandle : IAssetBundleHandle
        {
            public string FullPath => "pkg/AssetBundles/main";
            public object Bundle { get; } = new object();
            public IReadOnlyList<string> OwnerModIds => new[] { "test.mod" };
            public bool IsLoaded => true;
        }

        private sealed class FakePromptOverrideRegistry : IPromptOverrideRegistry
        {
            private readonly List<PromptOverride> overrides = new List<PromptOverride>();

            public PromptOverrideRequest? LastRequest { get; private set; }
            public IReadOnlyList<PromptOverride> Overrides => overrides;

            public IPromptOverrideHandle Register(PromptOverrideRequest request)
            {
                LastRequest = request;
                var promptOverride = new PromptOverride(
                    request.OwnerModId,
                    request.PromptId,
                    request.ReplacementText,
                    request.Priority,
                    request.Description);
                overrides.Add(promptOverride);
                return new FakePromptOverrideHandle(promptOverride);
            }

            public bool TryGetEffectiveOverride(string promptId, out PromptOverride? promptOverride)
            {
                promptOverride = overrides.FirstOrDefault(o => o.PromptId == promptId);
                return promptOverride != null;
            }

            public IReadOnlyList<PromptConflict> GetConflicts()
            {
                return Array.Empty<PromptConflict>();
            }

            public void UnregisterOwner(string ownerModId)
            {
                overrides.RemoveAll(o => o.ModId == ownerModId);
            }
        }

        private sealed class FakePromptOverrideHandle : IPromptOverrideHandle
        {
            public FakePromptOverrideHandle(PromptOverride promptOverride)
            {
                Override = promptOverride;
            }

            public PromptOverride Override { get; }
            public bool IsDisposed { get; private set; }

            public void Dispose()
            {
                IsDisposed = true;
            }
        }

        private sealed class FakeRobotAgent : IRobotAgent
        {
            public string Id => "fake";
            public object GameObject { get; } = new object();
            public bool IsAlive => true;
            public Vec3 Position => Vec3.Zero;
            public Vec3 HeadPosition => Vec3.Zero;
            public RobotBrainMode BrainMode => RobotBrainMode.Dormant;
            public bool IsMoving => false;
            public bool HasReachedTarget => false;
            public float MoveSpeed { get; set; }
            public float TurnSpeed { get; set; }
            public float StopDistance { get; set; }
            public RobotGait Gait { get; set; }
            public void MoveTo(Vec3 position) { }
            public void Chase(object targetGameObject) { }
            public void Stop() { }
            public void SetTint(RobotColor color) { }
            public void SetEmote(string emojiShortcode) { }
            public void SetName(string name) { }
            public void SetScale(float scale) { }
            public void SetInteraction(RobotInteractionOptions options) { }
            public bool ApplyDamage(float amount, RobotDamageType type, string source) => false;
            public void Kill(RobotDamageType type, string source) { }
            public void Ragdoll() { }
            public void Knockback(Vec3 impulse) { }
            public void Despawn() { }
        }

        // Minimal IModContext for testing the service-resolution extensions; only GetService is exercised.
        private sealed class FakeContext : IModContext
        {
            public Dictionary<Type, object> Services { get; } = new Dictionary<Type, object>();

            public string ModId => "test.mod";
            public string ModName => "Test";
            public Version Version => new Version(1, 0, 0);
            public ModPaths Paths => new ModPaths("pkg", "cfg", "data");
            public IModLogger Logger => new NullLogger();

            public event Action<float>? Update;
            public event Action<string>? SceneLoaded;

            public T LoadConfig<T>(T defaultValue) where T : class => defaultValue;

            public void SaveConfig<T>(T config) where T : class
            {
            }

            public T? GetService<T>() where T : class
            {
                return Services.TryGetValue(typeof(T), out var service) ? (T)service : null;
            }

            // Keep the compiler from warning the events are unused without changing the public surface.
            public void RaiseForCoverage()
            {
                Update?.Invoke(0f);
                SceneLoaded?.Invoke(string.Empty);
            }
        }

        private sealed class NullLogger : IModLogger
        {
            public void Debug(string message)
            {
            }

            public void Info(string message)
            {
            }

            public void Warn(string message)
            {
            }

            public void Error(string message)
            {
            }

            public void Error(Exception exception, string message)
            {
            }
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
