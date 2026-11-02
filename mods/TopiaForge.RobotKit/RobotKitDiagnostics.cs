using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.RobotKit
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
        private static object? discoveryWorld;
        private static string? discoverySession;
        private static readonly HashSet<RobotComposedRefusal> discoveryReasons = new HashSet<RobotComposedRefusal>();

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
                discoveryWorld = null; discoverySession = null; discoveryReasons.Clear();
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
                    discoveryWorld = null; discoverySession = null; discoveryReasons.Clear();
                }
            }
        }

        public static void ReportComposedDiscovery(WorldSessionSnapshot? state, RobotComposedRefusal reason)
        {
            // Session/world identities are compared only in memory; diagnostics contain static codes only.
            if (state?.Session == null || state.World == null) return;
            IModLogger? sink;
            lock (Gate)
            {
                if (!ReferenceEquals(discoveryWorld, state.World.Scene) || discoverySession != state.Session.SessionId)
                {
                    discoveryWorld = state.World.Scene;
                    discoverySession = state.Session.SessionId;
                    discoveryReasons.Clear();
                }
                sink = logger;
                if (sink == null || !discoveryReasons.Add(reason)) return;
            }
            try { sink.Info("RobotKit composed discovery refused: " + reason + "."); }
            catch { /* A diagnostic cannot break discovery. */ }
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

            var message = "RobotKit compatibility fallback in " + operation + ": " + RobotKitLog.ExceptionType(exception);
            if (sink != null)
            {
                try
                {
                    sink.Debug(message);
                    return;
                }
                catch (Exception loggingFailure)
                {
                    message += "; diagnostic logger also failed: " + RobotKitLog.ExceptionType(loggingFailure);
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
