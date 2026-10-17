using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
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
    /// runtime, the world session and Unity; tests implement it over a real <see cref="MultiplayerRuntime"/> and fakes.
    /// Main thread only; every member that is read each frame allocates nothing.
    /// </summary>
    /// <remarks>
    /// Going online and leaving pass through the runtime's realm (<see cref="MultiplayerRuntime"/>, section 10 of
    /// docs/internal/MultiplayerRuntimeIntegration.md), never the control's own hosting and joining: its rules read the
    /// session orchestrator's <c>RealmPhase</c>, which counts an idle session that is still changing, as while the main
    /// menu loads, as busy. The screens keep no realm verdict of their own.
    /// </remarks>
    internal interface IMultiplayerSessionEnvironment
    {
        /// <summary>
        /// Gets the multiplayer provider's control, or null when the provider is not loaded. The screens read its status,
        /// listings and checks through it, and moderate through it; they host, join and leave only through the realm below.
        /// </summary>
        IInternalMultiplayerControl? Control { get; }

        /// <summary>Gets this game's realm as of the runtime's last pump: offline, a listen host, or joined to a host.</summary>
        NetRealm Realm { get; }

        /// <summary>
        /// Asks the runtime's realm rules whether this game may move to <paramref name="target"/> now, without acting
        /// (<see cref="MultiplayerRuntime.EvaluateRealmChange"/>). It allocates nothing, so a frame may ask.
        /// </summary>
        NetRealmTransition EvaluateRealmChange(NetRealm target);

        /// <summary>Opens this game to other players through the realm rules (<see cref="MultiplayerRuntime.OpenToMultiplayer"/>).</summary>
        MultiplayerRealmChange OpenToMultiplayer(InternalHostSettings settings);

        /// <summary>Joins <paramref name="target"/> through the realm rules (<see cref="MultiplayerRuntime.Join"/>).</summary>
        MultiplayerRealmChange Join(IInternalJoinTarget target, InternalJoinSettings settings);

        /// <summary>Leaves whatever this game does online, or a join on its way (<see cref="MultiplayerRuntime.Leave"/>); never refused.</summary>
        MultiplayerRealmChange Leave();

        /// <summary>Gets the names of the loaded mods that refuse multiplayer, sorted. It allocates; read it only to say why.</summary>
        IReadOnlyList<string> StandaloneMods { get; }

        /// <summary>Gets the provider's session as mods see it, for the participant list; null without one.</summary>
        MultiplayerSessionSnapshot? SessionSnapshot { get; }

        /// <summary>Gets how far this game's packages are identified, from the runtime host. It never blocks.</summary>
        InternalContentReadiness ContentReadiness { get; }

        void RetryContentIdentification();

        /// <summary>Gets or sets the name this player hosts and joins as.</summary>
        string DisplayName { get; set; }

        /// <summary>Gets the running game's description, such as "Sandbox · Open Sandbox"; empty while idle.</summary>
        string CurrentGameText { get; }

        /// <summary>Gets the running game's gamemode and world names for browsing players, or nulls.</summary>
        HostTargetChoice? CurrentGame { get; }

        /// <summary>Gets the games the player may start at the main menu and host (<see cref="HostTargets"/>), read again on each call.</summary>
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
