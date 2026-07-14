using System;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Logging seam for the kit. Adapts the owner's logger (a mod's IModLogger or the
    /// manager's file logger) via delegates so the kit depends on neither; falls back
    /// to Unity's Debug log with a [TopiaForgeUi] prefix.
    /// </summary>
    public static class TopiaForgeLog
    {
        private static Action<string>? infoSink;
        private static Action<string>? warnSink;
        private static Action<string>? errorSink;

        /// <summary>Routes kit logs into the host's logging (first registration wins).</summary>
        public static void UseSinks(Action<string> info, Action<string> warn, Action<string> error)
        {
            infoSink ??= info;
            warnSink ??= warn;
            errorSink ??= error;
        }

        public static void Info(string message)
        {
            if (infoSink != null)
            {
                infoSink(message);
            }
            else
            {
                Debug.Log("[TopiaForgeUi] " + message);
            }
        }

        public static void Warn(string message)
        {
            if (warnSink != null)
            {
                warnSink(message);
            }
            else
            {
                Debug.LogWarning("[TopiaForgeUi] " + message);
            }
        }

        public static void Error(string message)
        {
            if (errorSink != null)
            {
                errorSink(message);
            }
            else
            {
                Debug.LogError("[TopiaForgeUi] " + message);
            }
        }

        public static void Error(Exception exception, string message)
        {
            Error(message + " (" + exception.GetType().Name + ": " + exception.Message + ")");
        }

        internal static void Reset()
        {
            infoSink = null;
            warnSink = null;
            errorSink = null;
        }
    }
}
