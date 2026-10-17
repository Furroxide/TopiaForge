using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>A game the player can start and host, from the launch targets the enabled packages declare.</summary>
    internal sealed class HostTargetChoice
    {
        internal HostTargetChoice(string label, LaunchRequest request, string? gamemode, string? world)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Gamemode = gamemode;
            World = world;
        }

        internal string Label { get; }

        internal LaunchRequest Request { get; }

        /// <summary>Gets the gamemode's name for browsing players, at most 48 printable characters, or null.</summary>
        internal string? Gamemode { get; }

        /// <summary>Gets the world's name for browsing players, at most 48 printable characters, or null.</summary>
        internal string? World { get; }
    }

    /// <summary>
    /// What the multiplayer screens need from the runtime and the game around them. ModManager implements it over its
    /// runtime, the world session and Unity; tests implement it with fakes. Main thread only; every member that is read
    /// each frame allocates nothing.
    /// </summary>
    internal interface IMultiplayerSessionEnvironment
    {
        /// <summary>Gets the multiplayer provider's control, or null when the provider is not loaded.</summary>
        IInternalMultiplayerControl? Control { get; }

        /// <summary>Gets the provider's session as mods see it, for the participant list; null without one.</summary>
        MultiplayerSessionSnapshot? SessionSnapshot { get; }

        /// <summary>Gets how far this game's packages are identified, from the runtime host. It never blocks.</summary>
        InternalContentReadiness ContentReadiness { get; }

        void RetryContentIdentification();

        /// <summary>Gets or sets the name this player hosts and joins as.</summary>
        string DisplayName { get; set; }

        /// <summary>Gets the name of an enabled mod that refuses multiplayer, or null when there is none.</summary>
        string? StandaloneModName { get; }

        /// <summary>Gets the local game's phase: idle at the main menu, running in a game.</summary>
        WorldSessionPhase GamePhase { get; }

        /// <summary>Gets the running game's description, such as "Sandbox · Open Sandbox"; empty while idle.</summary>
        string CurrentGameText { get; }

        /// <summary>Gets the running game's gamemode and world names for browsing players, or nulls.</summary>
        HostTargetChoice? CurrentGame { get; }

        /// <summary>Gets the games the player may start and host, read again on each call.</summary>
        IReadOnlyList<HostTargetChoice> ReadHostTargets();

        /// <summary>Starts <paramref name="choice"/>; <paramref name="done"/> runs on the main thread once it runs or fails.</summary>
        void Launch(HostTargetChoice choice, Action<bool, string> done);

        /// <summary>Ends the running game; <paramref name="done"/> runs on the main thread once it is back at the menu or failed.</summary>
        void ReturnToMainMenu(Action<bool, string> done);

        /// <summary>Hands a <c>topiaforge://</c> link to the system, which opens the launcher.</summary>
        void OpenUrl(string url);

        void CopyToClipboard(string text);

        string GameBuild { get; }

        string RuntimeVersion { get; }

        /// <summary>Gets the current time, for bookmarks.</summary>
        long UnixSeconds { get; }
    }
}
