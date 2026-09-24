using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private string robotNameText = "Creator Robot";
        private string robotTintText = "White";
        private string robotScaleText = "1";

        private UiNode BuildRobotAppearanceContent(CreatorRosterEntry? entry)
        {
            var enabled = entry?.Robot != null && entry.Owned && CanMutate;
            var colors = new[]
            {
                new UiChoice("White", "White"),
                new UiChoice("Red", "Red"),
                new UiChoice("Orange", "Orange"),
                new UiChoice("Yellow", "Yellow"),
                new UiChoice("Green", "Green"),
                new UiChoice("Cyan", "Cyan"),
                new UiChoice("Blue", "Blue"),
                new UiChoice("Purple", "Purple"),
                new UiChoice("Pink", "Pink")
            };
            return new UiColumn(
                new UiText("ROBOT APPEARANCE", UiTextStyle.Heading),
                new UiText(
                    enabled ? "Name, tint, and uniform scale apply only to tool-owned RobotKit robots." : "Select a tool-owned RobotKit robot for reversible session appearance controls.",
                    UiTextStyle.Caption),
                new UiRow(
                    new UiTextInput("robot-name", "Name", robotNameText, value => robotNameText = value, maximumLength: 64, enabled: enabled),
                    new UiDropdown("robot-tint", "Tint", colors, robotTintText, value => robotTintText = value, enabled)),
                new UiRow(
                    new UiTextInput("robot-scale", "Uniform scale", robotScaleText, value => robotScaleText = value, maximumLength: 16, enabled: enabled),
                    new UiButton("apply-robot-appearance", "Apply appearance", () => Execute(ApplyRobotAppearance), UiButtonStyle.Secondary, enabled)));
        }

        private void LoadRobotAppearanceDraft(CreatorRosterEntry? entry)
        {
            if (entry?.Robot == null) return;
            robotNameText = entry.DisplayName;
            if (TryGetTransform(entry, out var transform)) robotScaleText = Format(transform.Scale.X);
        }

        private OperationResult<string> ApplyRobotAppearance()
        {
            var allowed = EnsureMutationAllowed();
            if (!allowed.Succeeded) return OperationResult<string>.Failure(allowed.ErrorCode, allowed.ErrorMessage);
            var entry = SelectedRoster();
            if (entry?.Robot == null || !entry.Owned)
            {
                return OperationResult<string>.Failure(ModErrorCode.Conflict, "Only tool-owned RobotKit robots permit name, tint, and scale overrides.");
            }
            var name = robotNameText.Trim();
            if (name.Length < 1 || name.Length > 64)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, "Robot name must contain 1 to 64 characters.");
            }
            if (!TryFloat(robotScaleText, out var scale) || scale < 0.25f || scale > 4f)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, "Robot scale must be between 0.25 and 4.");
            }
            if (!TryRobotColor(robotTintText, out var color))
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidArgument, "Choose a supported robot tint.");
            }
            var named = entry.Robot.SetName(name);
            if (!named.Succeeded) return OperationResult<string>.Failure(named.ErrorCode, named.ErrorMessage);
            var tinted = entry.Robot.SetTint(color);
            if (!tinted.Succeeded) return OperationResult<string>.Failure(tinted.ErrorCode, tinted.ErrorMessage);
            var scaled = entry.Robot.SetScale(scale);
            if (!scaled.Succeeded) return OperationResult<string>.Failure(scaled.ErrorCode, scaled.ErrorMessage);
            entry.DisplayName = name;
            return OperationResult<string>.Success(name + " appearance updated.");
        }

        private static bool TryRobotColor(string value, out RobotColor color)
        {
            switch ((value ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "WHITE": color = new RobotColor(1f, 1f, 1f); return true;
                case "RED": color = new RobotColor(1f, 0.2f, 0.2f); return true;
                case "ORANGE": color = new RobotColor(1f, 0.5f, 0.1f); return true;
                case "YELLOW": color = new RobotColor(1f, 0.9f, 0.2f); return true;
                case "GREEN": color = new RobotColor(0.2f, 0.9f, 0.3f); return true;
                case "CYAN": color = new RobotColor(0.2f, 0.9f, 1f); return true;
                case "BLUE": color = new RobotColor(0.2f, 0.4f, 1f); return true;
                case "PURPLE": color = new RobotColor(0.6f, 0.3f, 1f); return true;
                case "PINK": color = new RobotColor(1f, 0.3f, 0.7f); return true;
                default: color = RobotColor.White; return false;
            }
        }
    }
}
