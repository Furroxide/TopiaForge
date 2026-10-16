using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.SandboxAcceptance.Native
{
    // Step postconditions computed only from independently captured facts. Semantics mirror the verifier's
    // independent recomputation; measured values stay in the transcript for that recomputation.
    internal sealed partial class SandboxNativeScenarios
    {
        private bool Observe(string action, Dictionary<string, object?> facts)
        {
            switch (action)
            {
                case "open": case "reopen": return ObserveOpen(action, facts);
                case "hide": case "hide-f5": case "hide-close": return ObserveHidden(facts);
                case "move-player": return ObserveMovement(facts, expectMoved: true, expectVisible: false, expectResources: true);
                case "move-while-visible": return ObserveMovement(facts, expectMoved: false, expectVisible: true, expectResources: false);
                case "spawn-prop": case "spawn-catalog": case "spawn-character": case "duplicate": return ObserveSpawn(action, facts);
                case "remove":
                    var remaining = ObjectIds(facts); var previous = ObjectIds(prior);
                    return previous.Length == remaining.Length + 1 && remaining.All(previous.Contains);
                case "select-borrowed":
                    var id = Text(facts, "borrowedRosterId");
                    return id.Length > 0 && Widgets(facts).Any(w => Text(w, "nodeId") == "roster-list/" + id && Boolean(w, "focused"));
                case "edit-transform": case "edit-rotation": case "edit-scale": return ObserveTransformEdit(action, facts);
                case "edit-personality":
                    var bp = Map(Map(prior, "borrowedRobot"), "brain"); var ap = Map(Map(facts, "borrowedRobot"), "brain");
                    if (Text(ap, "hackedPersonalityFingerprint") == "unavailable") throw new MissingObservation("personality-fields-unavailable");
                    return Text(bp, "hackedPersonalityFingerprint") != Text(ap, "hackedPersonalityFingerprint") && Number(ap, "hackedPersonalityId") != 0;
                case "edit-brain":
                    var bb = Map(Map(prior, "borrowedRobot"), "brain"); var ab = Map(Map(facts, "borrowedRobot"), "brain");
                    return new[] { "state", "initialState", "llmDisabled", "behaviorTrees" }
                        .Any(key => bb.TryGetValue(key, out var before) && before != null && ab.TryGetValue(key, out var after) && after != null && !Same(before, after));
                case "run-graph":
                    return Objects(facts).Length >= Objects(prior).Length + 2
                        && Number(facts, "interactionCount") > Number(prior, "interactionCount")
                        && Number(facts, "graphPlayingAudioCount") > 0 && Number(facts, "conversationCount") > Number(prior, "conversationCount");
                case "stop-graph":
                    if (preGraph == null) throw new MissingObservation("graph-baseline-missing");
                    var stopped = SameObjects(preGraph, facts) && ResourcesEqual(preGraph, facts, includeUi: false)
                        && Number(facts, "graphPlayingAudioCount") == Number(preGraph, "graphPlayingAudioCount");
                    // In graph-rollback c1 the unrelated control cue must survive the graph stop.
                    return stopped && (scenario != "graph-rollback" || cycle != 1 || Boolean(facts, "controlCuePlaying"));
                case "unregister-source": return ObserveUnregisterSource(facts);
                case "end-session": return ObserveEndSession(facts);
                case "stop-world-session": case "toggle-during-transition": case "toggle-in-menu": return ObserveLifecycle(action, facts);
                case "observe-refusal":
                    return Text(facts, "mutationSafetyState") == "Unavailable" && !Boolean(facts, "persistenceIsolationAvailable")
                        && SandboxWidgets(facts).Any(w => Boolean(w, "visible") && Text(w, "text").Contains("Sandbox isolation active."));
                default: return ObserveExtended(action, facts);
            }
        }
        private bool ObserveOpen(string action, Dictionary<string, object?> facts)
        {
            var open = Visible(facts) && Text(facts, "activeHostId") == NativeSandboxObservations.SandboxOwner && SameSession(facts)
                && Number(facts, "creatorSessionCount") == Number(baseline, "creatorSessionCount") + 1;
            if (!open || action != "reopen" || preGraph == null) return open;
            // Reopening over a running graph must show the run, its audio and any borrowed edit still alive.
            if (!Boolean(facts, "projectRunning") || Number(facts, "graphPlayingAudioCount") <= 0) return false;
            return !selectingBorrowed || !SameTransform(Map(baseline, "borrowedRobot"), Map(facts, "borrowedRobot"), 0.0001);
        }
        private bool ObserveHidden(Dictionary<string, object?> facts) =>
            !Visible(facts) && Hud(facts, "SESSION ACTIVE") && SameSession(facts) && SameObjects(prior, facts)
            && Number(Map(facts, "ui"), "cursorLeaseCount") == Number(Map(baseline, "ui"), "cursorLeaseCount");
        private bool ObserveMovement(Dictionary<string, object?> facts, bool expectMoved, bool expectVisible, bool expectResources)
        {
            // Compared with the prior step so a restarted session (lifecycle-routes c2) keeps its new identity.
            return Visible(facts) == expectVisible && PositionMoved(prior, facts) == expectMoved && SameObjects(prior, facts)
                && Text(facts, "worldSessionId") == Text(prior, "worldSessionId") && (!expectResources || ResourcesEqual(prior, facts, includeUi: false));
        }
        private bool ObserveSpawn(string action, Dictionary<string, object?> facts)
        {
            var before = ObjectIds(prior); var after = Objects(facts);
            var added = after.Where(o => !before.Contains(Number(o, "instanceId"))).ToArray();
            if (added.Length != 1 || after.Length != before.Length + 1 || !Boolean(added[0], "alive") || Number(added[0], "sceneHandle") == 0) return false;
            if (action == "duplicate")
            {
                // The duplicate sits at (+1, 0, +1) from its source within 0.01.
                var position = Transform(added[0]);
                if (!Objects(prior).Any(source =>
                {
                    var origin = Transform(source);
                    return Near(position[0] - origin[0], 1, 0.01) && Near(position[1] - origin[1], 0, 0.01) && Near(position[2] - origin[2], 1, 0.01);
                })) return false;
            }
            if (action == "spawn-catalog")
            {
                // The new roster row must be the rendered selection.
                var previouslySelected = new HashSet<string>(Widgets(prior).Where(w => Boolean(w, "selected")).Select(w => Text(w, "nodeId")), StringComparer.Ordinal);
                if (!Widgets(facts).Any(w => Text(w, "surfaceId") == WindowSurface && Text(w, "nodeId").StartsWith("roster-list/", StringComparison.Ordinal)
                    && Boolean(w, "selected") && !previouslySelected.Contains(Text(w, "nodeId")))) return false;
            }
            lastAddedInstanceId = Number(added[0], "instanceId");
            return true;
        }
        private bool ObserveTransformEdit(string action, Dictionary<string, object?> facts)
        {
            var before = selectingBorrowed ? new[] { Map(prior, "borrowedRobot") } : Objects(prior);
            var after = selectingBorrowed ? new[] { Map(facts, "borrowedRobot") } : Objects(facts);
            return before.Any(a => after.Any(b => Number(a, "instanceId") == Number(b, "instanceId") && TransformEdited(action, Transform(a), Transform(b))));
        }
        private static bool TransformEdited(string action, double[] before, double[] after)
        {
            switch (action)
            {
                case "edit-transform": return Near(after[0] - before[0], 0, 0.001) && Near(after[1] - before[1], 1, 0.001) && Near(after[2] - before[2], 0, 0.001);
                case "edit-rotation": return Near(after[3], 0, 0.001) && Near(after[4], 0.7071068, 0.001) && Near(after[5], 0, 0.001) && Near(after[6], 0.7071068, 0.001);
                default: return Near(after[7], 1.25, 0.001) && Near(after[8], 1.25, 0.001) && Near(after[9], 1.25, 0.001);
            }
        }
        private bool ObserveUnregisterSource(Dictionary<string, object?> facts)
        {
            if (Rows(facts, "catalogIds").Cast<string>().Any(id => id.StartsWith("dev.topiaforge.sandbox-acceptance:", StringComparison.Ordinal))) return false;
            // The unavailable native vehicle source stays listed as a product limitation.
            var vehicles = Maps(facts, "catalogSources").Where(s => Text(s, "id") == "robotopia.vehicles").ToArray();
            if (vehicles.Length != 1 || Text(vehicles[0], "state") != "Unavailable") return false;
            if (Maps(facts, "nativeProps").Length != 0 || Number(facts, "ownedObjectCount") != 0) return false;
            if (scenario == "source-unload" && cycle == 2) return !Boolean(facts, "projectRunning");
            if (scenario != "source-unload") return true;
            var control = Number(facts, "controlRobotInstanceId");
            return control != 0 && ObjectIds(facts).Contains(control);
        }
        private bool ObserveEndSession(Dictionary<string, object?> facts)
        {
            if (Visible(facts) || !Hud(facts, "NO ACTIVE CREATOR SESSION") || !CachedUiReleased(facts) || !ResourcesEqual(baseline, facts, includeUi: false)) return false;
            if (scenario == "borrowed-robot" && cycle == 3)
                return facts.TryGetValue("borrowedRobot", out var destroyed) && destroyed == null && SameObjects(baseline, facts)
                    && Number(facts, "robotEditLeaseCount") == Number(baseline, "robotEditLeaseCount");
            if (scenario == "source-unload" && cycle == 1)
            {
                // The unrelated control robot outlives source teardown and the session end.
                var control = Number(facts, "controlRobotInstanceId");
                var expected = ObjectIds(baseline).Concat(new[] { control }).Distinct().OrderBy(v => v).ToArray();
                return control != 0 && expected.SequenceEqual(ObjectIds(facts)) && RestoredBorrowed(baseline, facts);
            }
            if (!SameObjects(baseline, facts)) return false;
            if (scenario == "borrowed-robot" && cycle == 2) return ObserveExternalRestoration(facts);
            // No audio host may still carry the graph cue name after the session ended.
            if (scenario == "graph-rollback" && cycle == 1 && Maps(facts, "graphAudioSources").Length != 0) return false;
            return RestoredBorrowed(baseline, facts);
        }
        // Borrowed-robot c2: End Session keeps the externally written transform, restores the personality and warns.
        private bool ObserveExternalRestoration(Dictionary<string, object?> facts)
        {
            if (externalWrite == null) throw new MissingObservation("external-write-baseline-missing");
            var expected = Map(externalWrite, "borrowedRobot"); var actual = Map(facts, "borrowedRobot"); var original = Map(baseline, "borrowedRobot");
            if (Number(actual, "instanceId") != Number(original, "instanceId") || !SameTransform(expected, actual, 0.01)) return false;
            if (Near(Transform(actual)[0], Transform(original)[0], 0.01) || !Same(Required(original, "brain"), Required(actual, "brain"))) return false;
            return Maps(facts, "toasts").Any(t => Boolean(t, "visible") && Text(t, "style") == "Warning"
                && (Text(t, "text").Contains("outside Creator Tools") || Text(t, "text").Contains("restoration warnings")));
        }
        private bool ObserveLifecycle(string action, Dictionary<string, object?> facts)
        {
            var phase = Text(facts, "sessionPhase");
            bool Quiet() => !Visible(facts) && Number(facts, "ownedObjectCount") == 0 && Maps(facts, "nativeProps").Length == 0
                && Number(facts, "creatorSessionCount") == Number(baseline, "creatorSessionCount") && Number(facts, "graphPlayingAudioCount") == 0;
            switch (action)
            {
                case "stop-world-session":
                    if (cycle == 2)
                        // RestartAsync: the original session has left Running; the completed transition is the next step's postcondition.
                        return !Visible(facts) && (phase != "Running" || !SameSession(facts));
                    return Quiet() && phase == "Idle" && Text(facts, "worldSessionId").Length == 0
                        && facts.TryGetValue("controllerInstanceId", out var idle) && idle == null;
                case "toggle-during-transition":
                    return Quiet() && phase == "Running" && Text(facts, "targetId") == SandboxAcceptanceMod.SandboxTargetId && !SameSession(facts)
                        && Text(facts, "activeHostId").Length == 0 && facts.TryGetValue("controllerInstanceId", out var controller) && controller is int fresh
                        && !(baseline.TryGetValue("controllerInstanceId", out var previous) && previous is int old && old == fresh);
                default:
                    return Quiet() && phase == "Idle" && Text(facts, "activeHostId").Length == 0;
            }
        }
    }
}
