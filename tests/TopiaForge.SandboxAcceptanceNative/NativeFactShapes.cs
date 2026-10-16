using System;
using System.Collections.Generic;
using System.Globalization;

namespace TopiaForge.SandboxAcceptance.Native
{
    // Unity-free fact shapes and measurement math so the offline harness pins every serialized key set and the
    // aim/contrast arithmetic without a game process. Values are measured by the Unity-bound observers.
    internal static class NativeFactShapes
    {
        internal static readonly string[] WidgetKeys =
        {
            "surfaceId", "nodeId", "kind", "text", "style", "x", "y", "width", "height", "visible", "enabled", "focused", "clipped",
            "highContrast", "reducedMotion", "uiScale", "motionIntensity", "selected", "value", "foreground", "background"
        };
        internal static readonly string[] AimKeys = { "available", "yawDegrees", "pitchDegrees", "distance", "focused" };
        internal const int ListBound = 512;

        internal static Dictionary<string, object?> Widget(string surfaceId, string nodeId, string kind, string text, string style,
            float x, float y, float width, float height, bool visible, bool enabled, bool focused, bool clipped,
            bool highContrast, bool reducedMotion, float uiScale, float motionIntensity, bool selected, string value, string foreground, string background)
            => new Dictionary<string, object?>
            {
                ["surfaceId"] = surfaceId, ["nodeId"] = nodeId, ["kind"] = kind, ["text"] = text, ["style"] = style,
                ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height, ["visible"] = visible, ["enabled"] = enabled,
                ["focused"] = focused, ["clipped"] = clipped, ["highContrast"] = highContrast, ["reducedMotion"] = reducedMotion,
                ["uiScale"] = uiScale, ["motionIntensity"] = motionIntensity, ["selected"] = selected, ["value"] = value ?? "",
                ["foreground"] = foreground ?? "", ["background"] = background ?? ""
            };

        internal static Dictionary<string, object?> Toast(string nodeId, string text, string style, bool visible)
            => new Dictionary<string, object?> { ["nodeId"] = nodeId, ["text"] = text, ["style"] = style, ["visible"] = visible };

        internal static Dictionary<string, object?> CatalogSource(string id, string displayName, string state, int entryCount)
            => new Dictionary<string, object?> { ["id"] = id, ["displayName"] = displayName, ["state"] = state, ["entryCount"] = entryCount };

        internal static Dictionary<string, object?> Accessibility(bool highContrast, float uiScale, bool reducedMotion, float motionIntensity)
            => new Dictionary<string, object?> { ["highContrast"] = highContrast, ["uiScale"] = uiScale, ["reducedMotion"] = reducedMotion, ["motionIntensity"] = motionIntensity };

        internal static Dictionary<string, object?> CompetingHost(bool registered, int canOpenCalls, int openCalls, int closeCalls)
            => new Dictionary<string, object?> { ["registered"] = registered, ["canOpenCalls"] = canOpenCalls, ["openCalls"] = openCalls, ["closeCalls"] = closeCalls };

        internal static Dictionary<string, object?> FocusedInteraction(bool available, int? entityInstanceId)
            => new Dictionary<string, object?> { ["available"] = available, ["entityInstanceId"] = entityInstanceId };

        /// <summary>Exactly the five broker-accepted keys; the broker refuses any extra key.</summary>
        internal static Dictionary<string, object?> AimToGraphProp(double yawDegrees, double pitchDegrees, double distance, bool focused)
            => new Dictionary<string, object?> { ["available"] = true, ["yawDegrees"] = yawDegrees, ["pitchDegrees"] = pitchDegrees, ["distance"] = distance, ["focused"] = focused };

        /// <summary>The same five keys with null measurements: no substitute zeros.</summary>
        internal static Dictionary<string, object?> AimUnavailable()
            => new Dictionary<string, object?> { ["available"] = false, ["yawDegrees"] = null, ["pitchDegrees"] = null, ["distance"] = null, ["focused"] = false };

        /// <summary>
        /// Camera correction angles toward a target. Yaw is positive when the target lies to the left of the forward
        /// direction and pitch is positive when it lies above it, so the broker's mouse move of (-yaw*gain, -pitch*gain)
        /// turns a non-inverted mouse-look camera toward the target. Forward and target vectors are world-space.
        /// </summary>
        internal static (double YawDegrees, double PitchDegrees) AimAngles(double fx, double fy, double fz, double tx, double ty, double tz)
        {
            var forwardHorizontal = Math.Sqrt(fx * fx + fz * fz);
            var targetHorizontal = Math.Sqrt(tx * tx + tz * tz);
            var yaw = 0d;
            if (forwardHorizontal > 1e-6 && targetHorizontal > 1e-6)
            {
                var hx = fx / forwardHorizontal; var hz = fz / forwardHorizontal;
                var localX = tx * hz - tz * hx;   // dot(target, right) with right = (hz, 0, -hx)
                var localZ = tx * hx + tz * hz;   // dot(target, horizontal forward)
                yaw = -Degrees(Math.Atan2(localX, localZ));
            }
            var pitch = Degrees(Math.Atan2(ty, targetHorizontal)) - Degrees(Math.Atan2(fy, forwardHorizontal));
            return (yaw, pitch);
        }

        private static double Degrees(double radians) => radians * 180d / Math.PI;

        /// <summary>WCAG 2 contrast ratio of two <c>#rrggbb</c> colours; 0 when either is empty or malformed.</summary>
        internal static double ContrastRatio(string? foreground, string? background)
        {
            if (!TryLuminance(foreground, out var first) || !TryLuminance(background, out var second)) return 0d;
            var lighter = Math.Max(first, second); var darker = Math.Min(first, second);
            return (lighter + 0.05d) / (darker + 0.05d);
        }

        internal static bool HasColour(string? value) => value != null && value.Length == 7 && value[0] == '#';

        private static bool TryLuminance(string? value, out double luminance)
        {
            luminance = 0d;
            if (!HasColour(value)) return false;
            var channels = new double[3];
            for (var index = 0; index < 3; index++)
            {
                if (!int.TryParse(value!.Substring(1 + index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var channel)) return false;
                var normalized = channel / 255d;
                channels[index] = normalized <= 0.03928d ? normalized / 12.92d : Math.Pow((normalized + 0.055d) / 1.055d, 2.4d);
            }
            luminance = 0.2126d * channels[0] + 0.7152d * channels[1] + 0.0722d * channels[2];
            return true;
        }
    }
}
