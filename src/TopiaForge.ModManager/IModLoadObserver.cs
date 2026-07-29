namespace TopiaForge.ModManager
{
    /// <summary>Receives crash-safe boundaries around each mod's load callback.</summary>
    internal interface IModLoadObserver
    {
        /// <summary>Runs immediately before the mod's OnLoad callback begins.</summary>
        void OnLoading(string modId);

        /// <summary>Runs immediately after the callback returns or throws.</summary>
        void OnLoadCompleted(string modId, bool succeeded);
    }
}
