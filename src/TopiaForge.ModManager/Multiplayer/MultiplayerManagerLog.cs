using System;

namespace TopiaForge.ModManager
{
    /// <summary>The manager log as the multiplayer runtime writes to it. A failing log sink never breaks the frame.</summary>
    internal sealed class MultiplayerManagerLog : IMultiplayerLog
    {
        private readonly IModRuntimeLogger logger;

        internal MultiplayerManagerLog(IModRuntimeLogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Info(string message) => Write(logger.Info, message);

        public void Warn(string message) => Write(logger.Warn, message);

        public void Error(string message) => Write(logger.Error, message);

        private static void Write(Action<string> sink, string message)
        {
            try
            {
                sink(message);
            }
            catch
            {
                // Diagnostics are best effort; multiplayer keeps running when the log cannot take them.
            }
        }
    }
}
