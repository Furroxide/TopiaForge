using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.SandboxAcceptance.Native
{
    internal sealed partial class SandboxNativeScenarios
    {
        private bool Observe(string action, Dictionary<string, object?> facts)
        {
            var ui = Map(facts, "ui");
            bool Visible() => Maps(ui, "widgets").Any(w => Text(w, "surfaceId") == "sandbox-creator-window" && Boolean(w, "visible"));
            switch (action)
            {
                case "open": case "reopen":
                    return Visible() && Text(facts, "activeHostId") == NativeSandboxObservations.SandboxOwner
                        && Number(facts, "creatorSessionCount") == Number(baseline, "creatorSessionCount") + 1;
                case "hide":
                    return !Visible() && Hud(facts, "SESSION ACTIVE") && Text(facts, "worldSessionId") == Text(baseline, "worldSessionId")
                        && SameObjects(prior, facts) && Number(ui, "cursorLeaseCount") == Number(Map(baseline, "ui"), "cursorLeaseCount");
                case "move-player":
                    var oldPosition = Rows(prior, "playerPosition").Cast<float>().ToArray();
                    var newPosition = Rows(facts, "playerPosition").Cast<float>().ToArray();
                    if (oldPosition.Length != 3 || newPosition.Length != 3) throw new MissingObservation("actual-player-position-unavailable");
                    return !Visible() && Text(facts, "worldSessionId") == Text(baseline, "worldSessionId") && SameObjects(prior, facts)
                        && (Math.Abs(oldPosition[0] - newPosition[0]) > 0.005f || Math.Abs(oldPosition[2] - newPosition[2]) > 0.005f)
                        && ResourcesEqual(prior, facts, includeUi: false);
                case "spawn-prop": case "spawn-catalog": case "duplicate":
                    var added = Objects(facts).Where(o => !Objects(prior).Any(p => Number(p, "instanceId") == Number(o, "instanceId"))).ToArray();
                    return added.Length == 1 && Boolean(added[0], "alive") && Number(added[0], "sceneHandle") != 0;
                case "remove":
                    return Objects(prior).Length == Objects(facts).Length + 1;
                case "select-borrowed":
                    var id = Text(facts, "borrowedRosterId");
                    return id.Length > 0 && Maps(ui, "widgets").Any(w => Text(w, "nodeId") == "roster-list/" + id && Boolean(w, "focused"));
                case "edit-transform": case "edit-rotation": case "edit-scale":
                    var before = selectingBorrowed ? new[] { Map(prior, "borrowedRobot") } : Objects(prior);
                    var after = selectingBorrowed ? new[] { Map(facts, "borrowedRobot") } : Objects(facts);
                    var offset = action == "edit-transform" ? 0 : action == "edit-rotation" ? 3 : 7;
                    var length = action == "edit-rotation" ? 4 : 3;
                    return before.Any(a => after.Any(b => Number(a, "instanceId") == Number(b, "instanceId")
                        && !Same(Rows(a, "transform").Skip(offset).Take(length).ToArray(), Rows(b, "transform").Skip(offset).Take(length).ToArray())));
                case "edit-personality":
                    var bp = Map(Map(prior, "borrowedRobot"), "brain"); var ap = Map(Map(facts, "borrowedRobot"), "brain");
                    if (Text(ap, "hackedPersonalityFingerprint") == "unavailable") throw new MissingObservation("personality-fields-unavailable");
                    return Text(bp, "hackedPersonalityFingerprint") != Text(ap, "hackedPersonalityFingerprint") && Number(ap, "hackedPersonalityId") != 0;
                case "edit-brain":
                    var bb = Map(Map(prior, "borrowedRobot"), "brain"); var ab = Map(Map(facts, "borrowedRobot"), "brain");
                    return !Same(Required(bb, "state"), Required(ab, "state")) || !Same(Required(bb, "llmDisabled"), Required(ab, "llmDisabled"))
                        || !Same(Required(bb, "behaviorTrees"), Required(ab, "behaviorTrees"));
                case "run-graph":
                    return Objects(facts).Length >= Objects(prior).Length + 2
                        && Number(facts, "interactionCount") > Number(prior, "interactionCount")
                        && Number(facts, "graphPlayingAudioCount") > 0 && Number(facts, "conversationCount") > Number(prior, "conversationCount");
                case "stop-graph":
                    if (preGraph == null) throw new MissingObservation("graph-baseline-missing");
                    return SameObjects(preGraph, facts) && ResourcesEqual(preGraph, facts, includeUi: false)
                        && Number(facts, "graphPlayingAudioCount") == Number(preGraph, "graphPlayingAudioCount");
                case "unregister-source":
                    return !Rows(facts, "catalogIds").Cast<string>().Any(idValue => idValue.StartsWith("dev.topiaforge.sandbox-acceptance:", StringComparison.Ordinal))
                        && Maps(facts, "nativeProps").Length == 0 && Number(facts, "ownedObjectCount") == 0;
                case "end-session":
                    return !Visible() && Hud(facts, "NO ACTIVE CREATOR SESSION") && SameObjects(baseline, facts) && ResourcesEqual(baseline, facts, includeUi: false)
                        && CachedUiReleased(facts) && Same(Required(baseline, "borrowedRobot"), Required(facts, "borrowedRobot"));
                case "stop-world-session":
                    var stopped = cycle == 2 ? Text(facts, "sessionPhase") == "Running" && Text(facts, "worldSessionId") != Text(baseline, "worldSessionId")
                        : Text(facts, "sessionPhase") == "Idle" && Text(facts, "worldSessionId").Length == 0;
                    return stopped && !Visible() && Number(facts, "ownedObjectCount") == 0 && Maps(facts, "nativeProps").Length == 0
                        && Number(facts, "creatorSessionCount") == Number(baseline, "creatorSessionCount") && Number(facts, "graphPlayingAudioCount") == 0;
                case "observe-refusal":
                    return Text(facts, "mutationSafetyState") == "Unavailable" && !Boolean(facts, "persistenceIsolationAvailable");
                default: throw new MissingObservation("unsupported-native-postcondition:" + action);
            }
        }
        private static bool Hud(Dictionary<string, object?> facts, string text) => Maps(Map(facts, "ui"), "widgets")
            .Any(w => Text(w, "surfaceId") == "sandbox-creator-hud" && Boolean(w, "visible") && !Boolean(w, "clipped") && Text(w, "text").Contains(text));
        private bool CachedUiReleased(Dictionary<string, object?> facts)
        {
            if (opened == null) return false;
            foreach (var key in new[] { "hostCount", "ownerCanvasCount", "totalCanvasCount", "themeSubscriberCount" })
                if (Number(Map(opened, "ui"), key) != Number(Map(facts, "ui"), key)) return false;
            foreach (var key in new[] { "cursorLeaseCount", "dismissScopeCount" })
                if (Number(Map(baseline, "ui"), key) != Number(Map(facts, "ui"), key)) return false;
            return true;
        }
        private static Dictionary<string, object?>[] Objects(Dictionary<string, object?> facts) => Maps(facts, "sandboxContent")
            .Concat(Maps(facts, "nativeRobots")).Where(o => Boolean(o, "alive")).GroupBy(o => Number(o, "instanceId")).Select(g => g.First()).ToArray();
        private static bool SameObjects(Dictionary<string, object?> a, Dictionary<string, object?> b) =>
            Same(Objects(a).Select(o => Number(o, "instanceId")).OrderBy(v => v).ToArray(), Objects(b).Select(o => Number(o, "instanceId")).OrderBy(v => v).ToArray());
        private static bool ResourcesEqual(Dictionary<string, object?> a, Dictionary<string, object?> b, bool includeUi)
        {
            foreach (var key in new[] { "creatorSessionCount", "creatorEditLeaseCount", "robotEditLeaseCount", "conversationCount", "interactionCount", "playerControlLeaseCount", "routingHostCount" })
                if (Number(a, key) != Number(b, key)) return false;
            if (includeUi)
                foreach (var key in new[] { "hostCount", "ownerCanvasCount", "totalCanvasCount", "themeSubscriberCount", "cursorLeaseCount", "dismissScopeCount" })
                    if (Number(Map(a, "ui"), key) != Number(Map(b, "ui"), key)) return false;
            return true;
        }
    }
}
