using System;
using System.Collections.Generic;
using Robotopia.Mods;

namespace Robotopia.RobotKit
{
    /// <summary>
    /// Last-resort diagnostics for static reflection bridges that cannot accept a logger on every hot-path call.
    /// Each compatibility failure is reported once so a renamed game symbol remains diagnosable without producing
    /// per-frame log storms.
    /// </summary>
    internal static class RobotKitDiagnostics
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static IModLogger? logger;

        public static void Configure(IModLogger value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            lock (Gate)
            {
                logger = value;
                Reported.Clear();
            }
        }

        public static void Clear(IModLogger value)
        {
            lock (Gate)
            {
                if (ReferenceEquals(logger, value))
                {
                    logger = null;
                    Reported.Clear();
                }
            }
        }

        public static void ReportOnce(string operation, Exception exception)
        {
            IModLogger? sink;
            lock (Gate)
            {
                if (!Reported.Add(operation))
                {
                    return;
                }

                sink = logger;
            }

            var message = "RobotKit compatibility fallback in " + operation + ": " + exception.Message;
            if (sink != null)
            {
                try
                {
                    sink.Debug(message);
                    return;
                }
                catch (Exception loggingFailure)
                {
                    message += "; diagnostic logger also failed: " + loggingFailure.Message;
                }
            }

            try
            {
                Console.Error.WriteLine(message);
            }
            catch
            {
                // No independent sink remains. Compatibility fallbacks must not break the game lifecycle.
            }
        }
    }
}
