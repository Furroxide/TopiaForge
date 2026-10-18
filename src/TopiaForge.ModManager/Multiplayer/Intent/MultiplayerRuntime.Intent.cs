namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerRuntime
    {
        /// <summary>
        /// Gets the intent tracking (WS3.4): the record of the player intent the current code acts on, its entry points, and
        /// the realm epoch it follows. The realm reports each change to it at the pump that shows it.
        /// </summary>
        internal MultiplayerIntents Intents { get; } = new MultiplayerIntents();
    }
}
