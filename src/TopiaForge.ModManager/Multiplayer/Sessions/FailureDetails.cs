using System.Globalization;
using System.Text;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// What "Copy details" puts on the clipboard for a failed join: the failure's name, the step, this game's own words, the
    /// host's admission code and problem lines, what the host said, and the game and TopiaForge versions. It never holds an
    /// address: the target and anything the player typed are not part of it, and every line is redacted besides, the host's
    /// words most of all.
    /// </summary>
    internal static class FailureDetails
    {
        internal const int MaximumProblems = 16;

        private static readonly string[] AdmissionCodes =
        {
            "other", "full", "protocol", "password-required", "password-wrong", "too-many-attempts", "banned", "content", "not-ready"
        };

        internal static string Build(
            InternalLiveFailure failure,
            ConnectingStep step,
            string stepLabel,
            InternalLiveStatus status,
            string gameBuild,
            string runtimeVersion)
        {
            var builder = new StringBuilder(256);
            builder.Append("TopiaForge multiplayer join").AppendLine();
            builder.Append("Failure: ").Append(failure.ToString()).Append(" (").Append(((int)failure).ToString(CultureInfo.InvariantCulture)).Append(')').AppendLine();
            builder.Append("Step: ").Append(Line(stepLabel)).Append(" (").Append(((int)step).ToString(CultureInfo.InvariantCulture)).Append(')').AppendLine();
            if (status.JoinDetail.Length > 0) builder.Append("Detail: ").Append(Line(status.JoinDetail)).AppendLine();
            if (failure == InternalLiveFailure.Rejected)
            {
                var code = (int)status.RejectionCode;
                builder.Append("Admission: ").Append(code >= 0 && code < AdmissionCodes.Length ? AdmissionCodes[code] : "other").AppendLine();
            }

            if (status.ContentProblems.Count > 0)
            {
                builder.Append(status.ContentProblemsFromHost ? "Problems the host reported:" : "Problems this game found:").AppendLine();
                for (var index = 0; index < status.ContentProblems.Count && index < MaximumProblems; index++)
                {
                    builder.Append("- ").Append(Line(status.ContentProblems[index])).AppendLine();
                }
            }

            var hostSays = HostText.HostSays(status.HostMessage);
            if (hostSays.Length > 0) builder.Append(hostSays).AppendLine();
            builder.Append("Game build: ").Append(Line(gameBuild)).AppendLine();
            builder.Append("TopiaForge: ").Append(Line(runtimeVersion)).AppendLine();
            return builder.ToString();
        }

        private static string Line(string text) => HostText.ForDisplay(text, HostText.LineChars);
    }
}
