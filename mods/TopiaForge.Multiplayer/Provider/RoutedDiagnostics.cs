using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// Where the provider's diagnostics go: to the attached runtime, which redacts them before its log and diagnostics
    /// bundle, or to the provider's own log before one attaches. Diagnostics never carry network addresses either way.
    /// </summary>
    internal sealed class RoutedDiagnostics : IEngineDiagnostics
    {
        private readonly IModLogger? logger;

        internal RoutedDiagnostics(IModLogger? logger)
        {
            this.logger = logger;
        }

        /// <summary>Gets or sets the attached runtime, which then receives every diagnostic.</summary>
        internal IInternalMultiplayerRuntimeHost? Runtime { get; set; }

        public void Report(EngineDiagnostic diagnostic)
        {
            if (diagnostic == null) throw new ArgumentNullException(nameof(diagnostic));
            Report(new InternalMultiplayerDiagnostic(
                diagnostic.Exception != null ? InternalDiagnosticSeverity.Error : InternalDiagnosticSeverity.Warning,
                Code(diagnostic.Kind),
                diagnostic.Message,
                diagnostic.Participant,
                diagnostic.Exception));
        }

        internal void Report(InternalMultiplayerDiagnostic diagnostic)
        {
            var runtime = Runtime;
            if (runtime != null)
            {
                try
                {
                    runtime.ReportDiagnostic(diagnostic);
                    return;
                }
                catch (Exception)
                {
                    // A runtime that cannot take a diagnostic loses it to the provider's own log instead.
                }
            }

            if (logger == null) return;
            var text = "Multiplayer: " + diagnostic.Message + (diagnostic.Participant.HasValue ? " (participant " + diagnostic.Participant.Value + ")" : string.Empty);
            if (diagnostic.Exception != null) logger.Error(diagnostic.Exception, text);
            else if (diagnostic.Severity == InternalDiagnosticSeverity.Info) logger.Info(text);
            else logger.Warn(text);
        }

        /// <summary>
        /// Gets the code for an engine diagnostic. Every code ends in a hyphenated part, since the runtime's redactor reads
        /// a dotted name ending in a single word as a host name.
        /// </summary>
        internal static string Code(EngineDiagnosticKind kind)
        {
            switch (kind)
            {
                case EngineDiagnosticKind.ProtocolViolation:
                    return "engine.protocol-violation";
                case EngineDiagnosticKind.CallbackFailed:
                    return "engine.callback-failed";
                case EngineDiagnosticKind.CodecFailed:
                    return "engine.codec-failed";
                case EngineDiagnosticKind.VoiceAbuse:
                    return "engine.voice-abuse";
                default:
                    return "engine.other-diagnostic";
            }
        }
    }
}
