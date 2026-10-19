namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// The launcher that started this process asks it to quit, as the game's own Quit button would, before it restarts the
    /// game or installs what a join needs (<c>launcher-quit-&lt;request key&gt;.json</c>, launcher to game). Only the process
    /// launched as <see cref="RequestId"/> acts on it, so a launcher can never ask a game it did not start to quit.
    /// </summary>
    public sealed class LauncherQuitRequest
    {
        public const int SchemaVersion = 1;

        public LauncherQuitRequest(string requestId, string quitId)
        {
            RequestId = LaunchContractValues.Token(requestId, nameof(requestId));
            QuitId = LaunchContractValues.Token(quitId, nameof(quitId));
        }

        /// <summary>Gets the launch request the process to quit was started with.</summary>
        public string RequestId { get; }

        /// <summary>Gets this request's own id, so the launcher withdraws exactly the request it wrote.</summary>
        public string QuitId { get; }
    }
}
