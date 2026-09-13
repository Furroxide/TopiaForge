using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.SandboxAcceptance.Native
{
    // Protocol v2 facts. A value that cannot be measured is null together with an unavailable reason; the
    // verifier maps that to an unavailable scenario rather than a zero.
    internal sealed partial class NativeSandboxObservations
    {
        internal const string GraphPropPrompt = "ACCEPTANCE";
        private const string AudioHostPrefix = "TopiaForge.Audio.";

        private static object? Toasts(List<string> unavailable)
        {
            TopiaForgeUiDiagnosticSnapshot snapshot;
            try { snapshot = TopiaForgeUiDiagnostics.Capture(TopiaForgeToasts.DiagnosticsOwnerId); }
            catch (InvalidOperationException) { return Missing(unavailable, "toast-diagnostics-unavailable"); }
            var rows = snapshot.Widgets.Where(w => w.SurfaceId == TopiaForgeUiDiagnosticFormat.ToastSurface)
                .Select(w => (object)NativeFactShapes.Toast(w.NodeId, w.Text, w.Style, w.Visible)).ToArray();
            return rows.Length > NativeFactShapes.ListBound ? Missing(unavailable, "toast-bound-exceeded") : rows;
        }

        private static object? CatalogSources(ICreatorContentService? content, List<string> unavailable)
        {
            if (content == null) return Missing(unavailable, "catalog-sources-unavailable");
            var sources = content.Catalog.Sources;
            if (sources.Count > NativeFactShapes.ListBound) return Missing(unavailable, "catalog-source-bound-exceeded");
            return sources.Select(s => (object)NativeFactShapes.CatalogSource(s.SourceId, s.DisplayName, s.State.ToString(), s.EntryCount)).ToArray();
        }

        // Every live AudioSource host named TopiaForge.Audio.* (the production pool renames idle hosts to
        // TopiaForge.Audio.Pooled, which still matches this measured prefix).
        private static object? AudioSourceIds(AudioSource[] audio, List<string> unavailable)
        {
            var ids = audio.Where(IsFrameworkAudio).Select(a => a.GetInstanceID()).OrderBy(id => id).ToArray();
            return ids.Length > NativeFactShapes.ListBound ? Missing(unavailable, "audio-source-bound-exceeded") : ids;
        }

        private static object AudioSources(AudioSource[] audio) => audio.Where(IsFrameworkAudio).OrderBy(a => a.GetInstanceID())
            .Take(NativeFactShapes.ListBound).Select(a => (object)new Dictionary<string, object?>
            { ["instanceId"] = a.GetInstanceID(), ["name"] = a.gameObject.name, ["playing"] = a.isPlaying }).ToArray();

        private static bool IsFrameworkAudio(AudioSource source) => source != null && source.gameObject.name.StartsWith(AudioHostPrefix, StringComparison.Ordinal);

        private static object FocusedInteraction(List<string> unavailable)
        {
            if (!NativeRuntimeReflection.TryGetInteractTarget(out var target))
            {
                NativeOwnerObservations.AddOnce(unavailable, "focused-interaction-unavailable");
                return NativeFactShapes.FocusedInteraction(false, null);
            }
            return NativeFactShapes.FocusedInteraction(true, NativeRuntimeReflection.InteractableInstanceId(target));
        }

        // Camera ray versus the graph prop registered with the ACCEPTANCE prompt. An absent prop is a measured
        // state (available: false without a reason); an unreadable camera or interact target is unavailable.
        private object AimToGraphProp(List<InteractionObservation> interactions, List<string> unavailable)
        {
            if (!context.LocalPlayer.TryGetSnapshot(out var player) || player == null)
            { NativeOwnerObservations.AddOnce(unavailable, "actual-player-aim-unavailable"); return NativeFactShapes.AimUnavailable(); }
            if (!NativeRuntimeReflection.TryGetInteractTarget(out var focusedTarget))
            { NativeOwnerObservations.AddOnce(unavailable, "focused-interaction-unavailable"); return NativeFactShapes.AimUnavailable(); }
            var origin = new Vector3(player.AimRay.Origin.X, player.AimRay.Origin.Y, player.AimRay.Origin.Z);
            var targets = interactions.Where(i => i.Active && i.Prompt == GraphPropPrompt && i.Bridge != null).ToArray();
            if (targets.Length == 0) return NativeFactShapes.AimUnavailable();
            var nearest = targets.OrderBy(t => (t.Bridge.transform.position - origin).sqrMagnitude).First();
            var toTarget = nearest.Bridge.transform.position - origin;
            var forward = player.AimRay.Direction;
            var (yaw, pitch) = NativeFactShapes.AimAngles(forward.X, forward.Y, forward.Z, toTarget.x, toTarget.y, toTarget.z);
            return NativeFactShapes.AimToGraphProp(yaw, pitch, toTarget.magnitude, ReferenceEquals(focusedTarget, nearest.Bridge));
        }

        private object? ControlRobotInstanceId(List<string> unavailable)
        {
            var robot = fixture.ControlRobot;
            if (robot == null) return 0;
            var native = NativeOwnerObservations.Resolve(context, robot);
            return native != null ? native.GetInstanceID() : Missing(unavailable, "control-robot-native-identity-unavailable");
        }
    }
}
