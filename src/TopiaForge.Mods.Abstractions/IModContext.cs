namespace TopiaForge.Mods
{
    /// <summary>Provides owner-scoped SDK services and runtime metadata to a loaded mod.</summary>
    public interface IModContext
    {
        /// <summary>Gets the complete identity declared by this mod's manifest.</summary>
        ModIdentity Identity { get; }

        /// <summary>Gets information about the game and TopiaForge runtime hosting this mod.</summary>
        IRuntimeInfo Runtime { get; }

        /// <summary>Gets the owner-scoped logger.</summary>
        IModLogger Logger { get; }

        /// <summary>Gets the runtime-owned lifetime for subscriptions, registrations, and resources.</summary>
        IModLifetime Lifetime { get; }

        /// <summary>Gets owner-scoped, automatically tracked runtime event subscriptions.</summary>
        IModEvents Events { get; }

        /// <summary>Gets process-local content-based access to package and persistent data files.</summary>
        IModFiles Files { get; }

        /// <summary>Gets process-local validated, versioned typed configuration persistence.</summary>
        IModConfigService Config { get; }

        /// <summary>Gets installation-local typed key-value persistence scoped to this mod.</summary>
        ILocalModStorageService LocalStorage { get; }

        /// <summary>Gets process-local, owner-scoped named input actions.</summary>
        IInputService Input { get; }

        /// <summary>Gets process-local game-loop timing samples.</summary>
        IGameTime Time { get; }

        /// <summary>Gets lifetime-cancelled main-thread scheduling.</summary>
        IModScheduler Scheduler { get; }

        /// <summary>Gets the process-local player and center-screen aim state.</summary>
        ILocalPlayerService LocalPlayer { get; }

        /// <summary>Gets typed scene state and loading.</summary>
        ISceneService Scenes { get; }

        /// <summary>Gets safe operations over opaque world entities.</summary>
        IEntityService Entities { get; }

        /// <summary>Gets world physics queries.</summary>
        IPhysicsService Physics { get; }

        /// <summary>Gets process-local, owner-scoped interaction registration and focus state.</summary>
        IInteractionService Interactions { get; }

        /// <summary>Gets process-local held-item, give, and drop operations.</summary>
        IItemService Items { get; }

        /// <summary>Gets safe package asset loading and prefab spawning.</summary>
        IAssetService Assets { get; }

        /// <summary>Gets process-local framework audio-cue playback.</summary>
        IAudioService Audio { get; }

        /// <summary>Gets process-local TopiaForgeUi-backed HUD, window, modal, and toast operations.</summary>
        IUiService Ui { get; }

        /// <summary>Gets owner-scoped localization catalogs and lookup.</summary>
        ILocalizationService Localization { get; }

        /// <summary>Gets owner-scoped command registration and invocation.</summary>
        ICommandService Commands { get; }

        /// <summary>Gets structured, bounded diagnostics.</summary>
        IDiagnosticsService Diagnostics { get; }

        /// <summary>Gets dependency-scoped typed extension providers.</summary>
        IExtensionService Extensions { get; }
    }

    /// <summary>Writes messages to attributed manager and per-mod logs.</summary>
    public interface IModLogger
    {
        /// <summary>Writes verbose developer information.</summary>
        void Debug(string message);

        /// <summary>Writes ordinary operational information.</summary>
        void Info(string message);

        /// <summary>Writes a recoverable warning.</summary>
        void Warn(string message);

        /// <summary>Writes an error message.</summary>
        void Error(string message);

        /// <summary>Writes an error with its complete exception chain.</summary>
        void Error(System.Exception exception, string message);
    }
}
