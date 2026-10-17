using System;
using TopiaForge.ModManager;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// Where the provider's diagnostics go: to the attached runtime, which redacts them before its log and diagnostics
    /// bundle, or to the provider's own log when no runtime is attached or the runtime fails to take one. That log gets the
    /// line the runtime would have written, from the runtime's own redactor, so no path logs an address.
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
            if (diagnostic == null) throw new ArgumentNullException(nameof(diagnostic));
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

            LogLocally(diagnostic);
        }

        /// <summary>
        /// Writes what no runtime took to the provider's own log, redacted exactly as the runtime redacts. The failure goes in
        /// as redacted text, never as the exception itself, whose message, inner failures and stack the log would print as
        /// they are. A log that throws loses the line: diagnostics never break the frame.
        /// </summary>
        private void LogLocally(InternalMultiplayerDiagnostic diagnostic)
        {
            if (logger == null) return;
            var text = MultiplayerDiagnosticRedactor.Describe(diagnostic, out _);
            try
            {
                switch (diagnostic.Severity)
                {
                    case InternalDiagnosticSeverity.Info:
                        logger.Info(text);
                        break;
                    case InternalDiagnosticSeverity.Warning:
                        logger.Warn(text);
                        break;
                    default:
                        logger.Error(text);
                        break;
                }
            }
            catch (Exception)
            {
                // Best effort, as the runtime's own log is.
            }
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
                case EngineDiagnosticKind.VoiceFailed:
                    return "engine.voice-failed";
                default:
                    return "engine.other-diagnostic";
            }
        }
    }
}
