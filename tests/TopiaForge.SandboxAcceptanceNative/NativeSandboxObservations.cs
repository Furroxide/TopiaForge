using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.SandboxAcceptance.Native
{
    internal sealed partial class NativeSandboxObservations
    {
        internal const string SandboxOwner = "io.github.furroxide.topiaforge.sandbox";
        internal const string GraphAudioHost = "TopiaForge.Audio.sandbox-acceptance-graph";
        private readonly IModContext context;
        private readonly NativeFixtureObjects objects;
        private readonly ISandboxAcceptanceFixture fixture;
        internal NativeSandboxObservations(IModContext context, NativeFixtureObjects objects, ISandboxAcceptanceFixture fixture)
        { this.context = context; this.objects = objects; this.fixture = fixture; }

        internal Dictionary<string, object?> Capture(SandboxFixtureSnapshot safe, List<string> unavailable)
        {
            var ui = TopiaForgeUiDiagnostics.Capture(SandboxOwner);
            var widgets = ui.Widgets.Select(w => (object)NativeFactShapes.Widget(w.SurfaceId, w.NodeId, w.Kind, w.Text, w.Style,
                w.X, w.Y, w.Width, w.Height, w.Visible, w.Enabled, w.Focused, w.Clipped, w.HighContrast, w.ReducedMotion, w.UiScale,
                w.MotionIntensity, w.Selected, w.Value, w.Foreground, w.Background)).ToArray();
            context.Extensions.TryGet<ICreatorContentService>(out var content);
            context.Extensions.TryGet<ICreatorToolHostService>(out var router);
            context.Extensions.TryGet<IRobotSceneEditorService>(out var editor);
            context.Extensions.TryGet<IRobotConversationService>(out var conversations);
            context.Extensions.TryGet<IRobotAgentService>(out var robots);
            context.Extensions.TryGet<IWorldSessionService>(out var worlds);
            var audio = Resources.FindObjectsOfTypeAll<AudioSource>();
            if (audio.Length > 4096) throw new InvalidOperationException("Native audio observation bound exceeded.");
            var graphAudio = audio.Where(a => a != null && a.gameObject.name == GraphAudioHost).ToArray();
            var interactions = NativeOwnerObservations.Interactions(unavailable);
            var controller = NativeRuntimeReflection.Controller(worlds, unavailable);
            var workbench = NativeRuntimeReflection.Workbench(controller, unavailable);
            var result = new Dictionary<string, object?>
            {
                ["worldSessionId"] = safe.WorldSessionId, ["sessionPhase"] = safe.SessionPhase, ["sessionSequence"] = safe.SessionSequence, ["targetId"] = safe.TargetId,
                ["gamemodeId"] = safe.GamemodeId, ["worldId"] = safe.WorldId, ["activeHostId"] = safe.ActiveHostId,
                ["catalogIds"] = safe.CatalogIds,
                ["catalog"] = content?.Catalog.Entries.Select(e => (object)new Dictionary<string, object?> { ["rowId"] = "content:" + e.ContentId, ["contentId"] = e.ContentId, ["displayName"] = e.DisplayName, ["kind"] = e.Kind.ToString(), ["transformCapabilities"] = (int)e.TransformCapabilities }).ToArray() ?? Array.Empty<object>(),
                ["robotCatalog"] = robots?.RobotTypes.Select(t => (object)new Dictionary<string, object?> { ["rowId"] = "robotkit:" + t.Id, ["displayName"] = t.DisplayName, ["kind"] = "Robot", ["transformCapabilities"] = 7 }).ToArray() ?? Array.Empty<object>(),
                ["catalogSources"] = CatalogSources(content, unavailable),
                ["sandboxContent"] = NativeOwnerObservations.Content(context, content, unavailable),
                ["nativeRobots"] = NativeOwnerObservations.Robots(context, unavailable),
                ["interactions"] = interactions.Select(i => (object)i.Row()).ToArray(), ["interactionCount"] = interactions.Count(i => i.Active),
                ["focusedInteraction"] = FocusedInteraction(unavailable), ["aimToGraphProp"] = AimToGraphProp(interactions, unavailable),
                ["playerControlLeaseCount"] = NativeOwnerObservations.PlayerControls(context, unavailable), ["robotTypes"] = safe.RobotTypes, ["playerPosition"] = safe.PlayerPosition,
                ["playerAim"] = safe.PlayerAim.Length == 3 ? safe.PlayerAim : Missing(unavailable, "actual-player-aim-unavailable"),
                ["fixtureObjects"] = safe.Objects.Select(o => (object)new Dictionary<string, object?> { ["id"] = o.Id, ["contentId"] = o.ContentId,
                    ["alive"] = o.Alive, ["transform"] = o.Transform }).ToArray(),
                ["ownedObjectCount"] = safe.Objects.Count(o => o.Alive), ["createdObjectCount"] = safe.CreatedObjects,
                ["disposedObjectCount"] = safe.DisposedObjects, ["cleanupErrors"] = safe.CleanupErrors,
                ["nativeProps"] = objects.ObserveProps(), ["nativeCleanupPendingObjects"] = objects.CleanupPendingObjects, ["borrowedRosterId"] = objects.BorrowedRosterId,
                ["borrowedRobot"] = Borrowed(unavailable), ["borrowedRobotDestroyed"] = objects.BorrowedDestroyed,
                ["mutationSafetyState"] = safe.MutationSafetyState, ["persistenceIsolationAvailable"] = safe.PersistenceIsolationAvailable,
                ["creatorSessionCount"] = Count(content, "TopiaForge.CreatorContent.CreatorContentService", "sessions", unavailable),
                ["creatorEditLeaseCount"] = Count(content, "TopiaForge.CreatorContent.CreatorContentService", "activeSceneEditEntities", unavailable),
                ["robotEditLeaseCount"] = Count(editor, "TopiaForge.RobotKit.RobotSceneEditorService", "leases", unavailable),
                ["conversationCount"] = Count(conversations, "TopiaForge.RobotKit.RobotConversationService", "active", unavailable),
                ["routingHostCount"] = Count(router, "TopiaForge.CreatorContent.CreatorToolHostRouter", "registrations", unavailable),
                ["graphPlayingAudioCount"] = graphAudio.Count(a => a.isPlaying),
                ["graphAudioSources"] = graphAudio.Select(a => (object)new Dictionary<string, object?> { ["instanceId"] = a.GetInstanceID(),
                    ["playing"] = a.isPlaying, ["clipId"] = a.clip != null ? a.clip.GetInstanceID() : 0, ["samplePosition"] = a.timeSamples }).ToArray(),
                ["audioSourceIds"] = AudioSourceIds(audio, unavailable), ["audioSources"] = AudioSources(audio),
                ["personalityAssetIds"] = NativeRuntimeReflection.PersonalityAssetIds(unavailable),
                ["toasts"] = Toasts(unavailable), ["accessibility"] = NativeAccessibility.Observe(),
                ["competingHost"] = NativeFactShapes.CompetingHost(safe.CompetingHostRegistered, safe.CompetingHostCanOpenCalls, safe.CompetingHostOpenCalls, safe.CompetingHostCloseCalls),
                ["controlCuePlaying"] = safe.ControlCuePlaying, ["controlRobotInstanceId"] = ControlRobotInstanceId(unavailable),
                ["controllerInstanceId"] = NativeRuntimeReflection.ControllerInstanceId(controller),
                ["projectRunning"] = NativeRuntimeReflection.ProjectRunning(workbench, unavailable), ["undoDepth"] = NativeRuntimeReflection.UndoDepth(workbench, unavailable),
                ["ui"] = new Dictionary<string, object?> { ["width"] = ui.Width, ["height"] = ui.Height, ["hostCount"] = ui.HostCount,
                    ["ownerCanvasCount"] = ui.OwnerCanvasCount, ["totalCanvasCount"] = ui.TotalCanvasCount,
                    ["themeSubscriberCount"] = ui.ThemeSubscriberCount, ["cursorLeaseCount"] = ui.CursorLeaseCount,
                    ["dismissScopeCount"] = ui.DismissScopeCount, ["widgets"] = widgets }
            };
            if (objects.Borrowed != null && editor?.Targets.Any(t => t.IsNativeSceneObject && ("robot-native:" + t.Id) == objects.BorrowedRosterId) != true)
                unavailable.Add("borrowed-native-target-not-discovered");
            return result;
        }
        private static object? Missing(List<string> unavailable, string reason) { NativeOwnerObservations.AddOnce(unavailable, reason); return null; }
        private static int? Count(object? service, string expectedType, string field, List<string> unavailable)
        {
            try
            {
                if (service?.GetType().FullName == expectedType + "+OwnerFacade")
                    service = service.GetType().GetField("service", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(service);
                if (service?.GetType().FullName != expectedType) throw new InvalidOperationException();
                var value = service.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(service);
                var property = value?.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                if (!(property?.GetValue(value) is int count) || count < 0 || count > 65536) throw new InvalidOperationException();
                return count;
            }
            catch { unavailable.Add("native-counter-unavailable:" + expectedType + "." + field); return null; }
        }
        private object? Borrowed(List<string> unavailable)
        {
            var robot = objects.Borrowed;
            if (robot == null) return null;
            var components = robot.GetComponentsInChildren<Component>(true);
            var agent = components.FirstOrDefault(c => c != null && c.GetType().Name == "LLMAgent");
            if (agent == null) { unavailable.Add("borrowed-robot-brain-unavailable"); return null; }
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var state = new Dictionary<string, object?>();
            foreach (var field in new[] { "initialState", "state", "llmDisabled" })
            {
                var value = agent.GetType().GetField(field, flags)?.GetValue(agent);
                if (value == null) unavailable.Add("borrowed-brain-field-unavailable:" + field);
                state[field] = value?.ToString();
            }
            state["behaviorTrees"] = components.OfType<Behaviour>().Where(c => c.GetType().Name == "BehaviorTree")
                .Select(c => (object)new Dictionary<string, object?> { ["instanceId"] = c.GetInstanceID(), ["enabled"] = c.enabled }).ToArray();
            var personality = agent.GetType().GetProperty("HackedPersonality", flags)?.GetValue(agent) as UnityEngine.Object;
            state["hackedPersonalityId"] = personality != null ? personality.GetInstanceID() : 0;
            state["hackedPersonalityFingerprint"] = Personality(personality, unavailable);
            return new Dictionary<string, object?> { ["instanceId"] = robot.GetInstanceID(), ["sceneHandle"] = robot.scene.handle,
                ["transform"] = NativeFixtureObjects.Transform(robot.transform), ["brain"] = state };
        }
        private static string Personality(UnityEngine.Object? personality, List<string> unavailable)
        {
            if (personality == null) return "none";
            if (personality.GetType().Name != "PersonalityAsset") { unavailable.Add("unexpected-personality-type"); return "unavailable"; }
            var values = new Dictionary<string, object?> { ["identity"] = personality.GetInstanceID(), ["name"] = personality.name };
            var fields = personality.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (fields.Length > 64) { unavailable.Add("personality-field-bound-exceeded"); return "unavailable"; }
            foreach (var field in fields)
            {
                var value = field.GetValue(personality);
                if (value == null || value is string || value is bool || value is int || value is float) values[field.Name] = value;
                else if (value is UnityEngine.Object obj) values[field.Name] = obj.GetInstanceID();
                else if (value is TextAsset[] text) values[field.Name] = text.Length <= 32 ? text.Select(a => a == null ? "" : a.text).ToArray() : throw new InvalidOperationException("Personality text bound exceeded.");
                else unavailable.Add("personality-field-not-observable:" + field.Name);
            }
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(SandboxWireCodec.Serialize(values)).Select(b => b.ToString("x2")));
        }
    }
}
