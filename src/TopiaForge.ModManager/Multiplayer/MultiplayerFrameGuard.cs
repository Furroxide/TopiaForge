using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The in-game multiplayer interface's boundary with Unity's frame. The overlay runs the sessions presenter, the
    /// prompts, toasts and connecting overlay, the MULTIPLAYER tab, and the game's answers to them through
    /// <see cref="Run(string, Action)"/>. A failure in any of them is caught here, so it never escapes into Unity's frame,
    /// where the game's crash reporter would take it, and the rest of the plugin's frame runs on.
    /// <para>
    /// Each distinct failure, by its stage, its type, its redacted message and the method that threw it, is logged once,
    /// with its stack, through the manager's log and <see cref="MultiplayerDiagnosticRedactor"/>, so no address reaches
    /// the log, the diagnostics bundle or a crash report from here. At most <see cref="MaximumLogged"/> are logged, then one
    /// line says the rest are not, so a failure whose text changes every frame cannot fill the log. A stage that does not
    /// fail allocates nothing. Main thread only.
    /// </para>
    /// </summary>
    internal sealed class MultiplayerFrameGuard
    {
        internal const int MaximumLogged = 16;
        internal const string Prefix = "Multiplayer interface: ";
        internal const string Silenced = Prefix + "further failures are not logged.";

        private readonly Action<string> logError;
        private readonly HashSet<string> logged = new HashSet<string>(StringComparer.Ordinal);
        private bool silenced;

        /// <param name="logError">The manager's log, as an error line; the text it receives is already redacted.</param>
        internal MultiplayerFrameGuard(Action<string> logError)
        {
            this.logError = logError ?? throw new ArgumentNullException(nameof(logError));
        }

        /// <summary>Gets how many failures were caught, logged or not.</summary>
        internal int Failures { get; private set; }

        /// <summary>Runs one stage; true when it finished, false when it failed and the failure was caught.</summary>
        internal bool Run(string stage, Action work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            try
            {
                work();
                return true;
            }
            catch (Exception exception)
            {
                Report(stage, exception);
                return false;
            }
        }

        /// <summary>Records a failure caught elsewhere on the interface's boundary, as the guarded stages do.</summary>
        internal void Report(string stage, Exception exception)
        {
            Failures++;
            try
            {
                if (silenced) return;
                var stack = exception.StackTrace;
                var thrownAt = MultiplayerDiagnosticRedactor.Redact(FirstFrame(stack));
                var failure = Prefix + stage + " failed and was skipped (" + exception.GetType().Name + ": " +
                              MultiplayerDiagnosticRedactor.Redact(exception.Message) + ")" + (thrownAt.Length == 0 ? "." : ", at " + thrownAt + ".");
                if (logged.Contains(failure)) return;
                if (logged.Count >= MaximumLogged)
                {
                    silenced = true;
                    logError(Silenced);
                    return;
                }

                logged.Add(failure);
                logError(string.IsNullOrEmpty(stack) ? failure : failure + Environment.NewLine + MultiplayerDiagnosticRedactor.Redact(stack));
            }
            catch (Exception)
            {
                // Reporting is the last line: a log that cannot take the line must not break the frame it guards.
            }
        }

        /// <summary>The innermost frame of a stack trace, where the failure was thrown, without its file and line.</summary>
        private static string FirstFrame(string? stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return string.Empty;
            var end = stackTrace!.IndexOf('\n');
            var first = (end < 0 ? stackTrace : stackTrace.Substring(0, end)).Trim();
            if (first.StartsWith("at ", StringComparison.Ordinal)) first = first.Substring(3);
            var file = first.IndexOf(" in ", StringComparison.Ordinal);
            return file < 0 ? first : first.Substring(0, file);
        }
    }
}
