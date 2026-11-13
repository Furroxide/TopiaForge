using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        private static readonly ConditionalWeakTable<WorldSceneIdentity, HashSet<(string SessionId, int Reason, bool Retirement)>>
            ComposedReports = new ConditionalWeakTable<WorldSceneIdentity, HashSet<(string, int, bool)>>();
        private static readonly ConditionalWeakTable<WorldSceneIdentity, Dictionary<string, RobotComposedRefusal>> LatestDiscovery =
            new ConditionalWeakTable<WorldSceneIdentity, Dictionary<string, RobotComposedRefusal>>();

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
                ComposedReports.Clear();
                LatestDiscovery.Clear();
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
                    ComposedReports.Clear();
                    LatestDiscovery.Clear();
                }
            }
        }

        public static void ReportComposedDiscovery(WorldSessionSnapshot? state, RobotComposedRefusal reason)
        {
            if (state?.Session != null && state.World != null)
                lock (Gate) LatestDiscovery.GetOrCreateValue(state.World.Scene)[state.Session.SessionId] = reason;
            var sink = TakeComposedReport(state, (int)reason, retirement: false);
            if (sink != null)
                ReportComposed(sink, (reason == RobotComposedRefusal.CharacterUnprepared
                    ? "RobotKit composed discovery admitted the body only: " : "RobotKit composed discovery refused: ") + reason + ".");
        }

        // The latest composed discovery result for this world and session, so a refused spawn can say why.
        public static RobotComposedRefusal? LastComposedDiscovery(Func<WorldSessionSnapshot?>? world)
        {
            WorldSessionSnapshot? state;
            try { state = world?.Invoke(); }
            catch { return null; }
            if (state?.Session == null || state.World == null) return null;
            lock (Gate)
                return LatestDiscovery.TryGetValue(state.World.Scene, out var sessions)
                    && sessions.TryGetValue(state.Session.SessionId, out var reason) ? reason : (RobotComposedRefusal?)null;
        }

        public static void ReportComposedRetirement(WorldSessionSnapshot state, RobotComposedRetirement reason)
        {
            var sink = TakeComposedReport(state, (int)reason, retirement: true);
            if (sink != null) ReportComposed(sink, "RobotKit composed context retired: " + reason + ".");
        }

        private static IModLogger? TakeComposedReport(WorldSessionSnapshot? state, int reason, bool retirement)
        {
            // An old actor can retire after discovery in a newer world. Keep separate budgets without rooting scenes.
            if (state?.Session == null || state.World == null) return null;
            lock (Gate)
            {
                return logger != null && ComposedReports.GetOrCreateValue(state.World.Scene).Add((state.Session.SessionId, reason, retirement))
                    ? logger : null;
            }
        }

        private static void ReportComposed(IModLogger sink, string message)
        {
            try { sink.Info(message); }
            catch { /* A diagnostic cannot break discovery or retirement. */ }
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
