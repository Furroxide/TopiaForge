using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

/// v2 atoms. Each records its measured event before the shared "input" event, even
/// when it fails, so the independent verifier can recompute what the OS received.
internal sealed partial class BrokerDriver
{
    private async Task ScrollIntoView(JsonElement atom, string surface, string node, JsonElement actionFacts, CancellationToken cancellation)
    {
        var target = atom.TryGetProperty("itemIdFromFact", out _) ? node + "/" + Text(actionFacts, Text(atom, "itemIdFromFact")) : node;
        var container = Text(atom, "containerId");
        var samples = new List<object>(); var attempts = 0; var visible = false; ScrollProbe? probe = null;
        void Sample(bool clipped, string direction, ScrollProbe? point) => samples.Add(new { observedFrame = Frame, clipped, direction, probeX = point?.X, probeY = point?.Y });
        try
        {
            while (true)
            {
                var widget = FindWidget(surface, target) ?? throw new InvalidOperationException("Scroll target is absent from the measured surface.");
                var clipped = widget.GetProperty("clipped").GetBoolean();
                if (!clipped) { Sample(clipped, "", null); visible = true; break; }
                if (attempts == DriverVocabulary.ScrollAttempts) { Sample(clipped, "", null); throw new InvalidOperationException("Scroll target never became visible within the declared attempts."); }
                var host = FindWidget(surface, container) ?? throw new InvalidOperationException("Scroll container is absent from the measured surface.");
                if (!host.GetProperty("visible").GetBoolean()) throw new InvalidOperationException("Scroll container is hidden.");
                var direction = ScrollDirection(Number(widget, "y"), Number(widget, "height"), Number(host, "y"), Number(host, "height"));
                // The wheel lands outside nested scroll views and virtual lists, which would otherwise consume it.
                probe = ScrollProbePlanner.Plan(MeasuredRect.Of(host), NestedRects(surface, container));
                Sample(clipped, direction, probe);
                attempts++;
                await input.Wheel(probe.X, probe.Y, DriverVocabulary.ScrollTicksPerAttempt, direction == "down", cancellation);
                await Barrier(2, cancellation);
            }
        }
        finally
        {
            transcript.Add(scenario, cycle, stepIndex, step, "scroll", new { nodeId = target, containerId = container, attempts, ticksPerAttempt = DriverVocabulary.ScrollTicksPerAttempt, ticks = attempts * DriverVocabulary.ScrollTicksPerAttempt, probeX = probe?.X, probeY = probe?.Y, samples, visible });
        }
    }
    /// Visible nested scroll views and virtual lists on the surface, excluding the container itself.
    private IReadOnlyList<MeasuredRect> NestedRects(string surface, string container)
    {
        var widgets = Facts.GetProperty("ui").GetProperty("widgets");
        if (widgets.GetArrayLength() > 2048) throw new InvalidDataException("Widget observation bound exceeded.");
        return widgets.EnumerateArray()
            .Where(w => w.GetProperty("surfaceId").GetString() == surface && w.GetProperty("nodeId").GetString() != container && w.GetProperty("visible").GetBoolean()
                && ScrollProbePlanner.NestedKinds.Contains(w.GetProperty("kind").GetString()))
            .Select(MeasuredRect.Of).ToList();
    }
    /// Bottom-left origin: content below the container centre needs a downward wheel (toward the user).
    internal static string ScrollDirection(double targetY, double targetHeight, double containerY, double containerHeight)
    {
        if (!double.IsFinite(targetY) || !double.IsFinite(targetHeight) || !double.IsFinite(containerY) || !double.IsFinite(containerHeight)) throw new InvalidDataException("Native geometry unavailable.");
        return targetY + targetHeight / 2 < containerY + containerHeight / 2 ? "down" : "up";
    }
    private async Task MouseMove(JsonElement atom, CancellationToken cancellation)
    {
        var dx = DriverVocabulary.SignedInteger(atom, "dx", -DriverVocabulary.MouseMoveBound, DriverVocabulary.MouseMoveBound);
        var dy = DriverVocabulary.SignedInteger(atom, "dy", -DriverVocabulary.MouseMoveBound, DriverVocabulary.MouseMoveBound);
        await input.MoveRelative(dx, dy, cancellation);
        transcript.Add(scenario, cycle, stepIndex, step, "mouse-move", new { dx, dy });
    }
    private async Task KeyHold(JsonElement atom, CancellationToken cancellation)
    {
        var key = Text(atom, "key");
        var requested = BoundedJson.Integer(atom, "milliseconds", DriverVocabulary.KeyHoldMinimumMilliseconds, DriverVocabulary.KeyHoldMaximumMilliseconds);
        long? held = null;
        try { held = await input.Hold(key, requested, cancellation); }
        finally { transcript.Add(scenario, cycle, stepIndex, step, "key-hold", new { key, requestedMilliseconds = requested, heldMilliseconds = held, released = input.Released }); }
    }
    private async Task Aim(JsonElement atom, CancellationToken cancellation)
    {
        var fact = Text(atom, "fact");
        var gain = BoundedJson.Integer(atom, "gain", DriverVocabulary.AimGainMinimum, DriverVocabulary.AimGainMaximum);
        var limit = BoundedJson.Integer(atom, "maxIterations", 1, DriverVocabulary.AimIterationsMaximum);
        var samples = new List<object>(); var converged = false;
        try
        {
            for (var iteration = 1; iteration <= limit; iteration++)
            {
                await Observe("capture", cancellation);
                if (!Facts.TryGetProperty(fact, out var value) || value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Aim fact " + fact + " is not observed.");
                BoundedJson.Keys(value, "available", "yawDegrees", "pitchDegrees", "distance", "focused");
                if (!value.GetProperty("available").GetBoolean()) throw new InvalidOperationException("Aim fact " + fact + " is unavailable.");
                var yaw = Number(value, "yawDegrees"); var pitch = Number(value, "pitchDegrees"); var distance = Number(value, "distance");
                var focused = value.GetProperty("focused").GetBoolean();
                var (dx, dy, clamped) = focused ? (0, 0, false) : AimDelta(yaw, pitch, gain);
                samples.Add(new { iteration, observedFrame = Frame, yawDegrees = yaw, pitchDegrees = pitch, distance, focused, dx, dy, clamped });
                if (focused) { converged = true; break; }
                await input.MoveRelative(dx, dy, cancellation);
                await Barrier(2, cancellation);
            }
            if (!converged) throw new InvalidOperationException("Aim did not converge within " + limit + " iterations.");
        }
        finally
        {
            transcript.Add(scenario, cycle, stepIndex, step, "aim", new { fact, gain, maxIterations = limit, iterations = samples.Count, converged, samples });
        }
    }
    /// One closed-loop move: -yaw*gain, -pitch*gain, bounded to the relative move range (clamping is recorded).
    internal static (int Dx, int Dy, bool Clamped) AimDelta(double yawDegrees, double pitchDegrees, int gain)
    {
        if (!double.IsFinite(yawDegrees) || !double.IsFinite(pitchDegrees) || gain < DriverVocabulary.AimGainMinimum || gain > DriverVocabulary.AimGainMaximum) throw new InvalidDataException("Aim inputs are outside their bound.");
        var dx = Math.Round(-yawDegrees * gain); var dy = Math.Round(-pitchDegrees * gain);
        var clamped = Math.Abs(dx) > DriverVocabulary.MouseMoveBound || Math.Abs(dy) > DriverVocabulary.MouseMoveBound;
        return ((int)Math.Clamp(dx, -DriverVocabulary.MouseMoveBound, DriverVocabulary.MouseMoveBound), (int)Math.Clamp(dy, -DriverVocabulary.MouseMoveBound, DriverVocabulary.MouseMoveBound), clamped);
    }
    private static double Number(JsonElement value, string name)
    {
        var field = value.GetProperty(name);
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var number) || !double.IsFinite(number)) throw new InvalidDataException("Invalid number: " + name);
        return number;
    }
}
