using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The multiplayer screens' environment in the game: the runtime's multiplayer composition root, the world session,
    /// the launch targets, and Unity for links and the clipboard. What a frame reads is cached until its source changes, so
    /// a settled frame allocates nothing.
    /// </summary>
    internal sealed class PluginMultiplayerEnvironment : IMultiplayerSessionEnvironment
    {
        private readonly TopiaForgeModManagerPlugin plugin;
        private IInternalMultiplayerControl? sessionOwner;
        private IMultiplayerSession? session;
        private WorldSessionSnapshot? shownWorld;
        private string currentGameText = string.Empty;
        private HostTargetChoice? currentGame;
        private int standalonePackages = -1;
        private string? standaloneName;

        internal PluginMultiplayerEnvironment(TopiaForgeModManagerPlugin plugin)
        {
            this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        }

        public IInternalMultiplayerControl? Control => plugin.Multiplayer?.Control;

        public MultiplayerSessionSnapshot? SessionSnapshot
        {
            get
            {
                var control = Control;
                if (!ReferenceEquals(control, sessionOwner))
                {
                    sessionOwner = control;
                    session = control == null ? null : plugin.GetMultiplayerSession();
                }

                return session?.Snapshot;
            }
        }

        public InternalContentReadiness ContentReadiness => plugin.Multiplayer?.Host.ContentReadiness ?? InternalContentReadiness.Ready;

        public void RetryContentIdentification() => plugin.Multiplayer?.Host.Inventory.RetryIdentification();

        public string DisplayName
        {
            get => plugin.Multiplayer?.Host.PreferredDisplayName ?? MultiplayerRuntimeHost.DefaultDisplayName;
            set
            {
                var host = plugin.Multiplayer?.Host;
                if (host != null) host.PreferredDisplayName = value;
            }
        }

        public string? StandaloneModName
        {
            get
            {
                var packages = plugin.Multiplayer?.Host.Inventory.Packages;
                if (packages == null) return null;
                if (packages.Count == standalonePackages) return standaloneName;
                standalonePackages = packages.Count;
                standaloneName = null;
                foreach (var package in packages)
                {
                    if (package.Manifest.EffectiveMultiplayerMode == ModMultiplayerEffectiveMode.Standalone)
                    {
                        standaloneName = package.Name;
                        break;
                    }
                }

                return standaloneName;
            }
        }

        public WorldSessionPhase GamePhase => plugin.GetSessionService()?.Current.Phase ?? WorldSessionPhase.Idle;

        public string CurrentGameText
        {
            get
            {
                RefreshCurrentGame();
                return currentGameText;
            }
        }

        public HostTargetChoice? CurrentGame
        {
            get
            {
                RefreshCurrentGame();
                return currentGame;
            }
        }

        public string GameBuild => plugin.Multiplayer?.Host.GameBuild ?? "unknown";

        public string RuntimeVersion => plugin.Multiplayer?.Host.RuntimeVersion ?? TopiaForgeVersions.LoaderVersion;

        public long UnixSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public IReadOnlyList<HostTargetChoice> ReadHostTargets()
        {
            var choices = new List<HostTargetChoice>();
            foreach (var preview in plugin.GetLaunchPreviews())
            {
                foreach (var choice in preview.Choices)
                {
                    choices.Add(new HostTargetChoice(preview.Title + " · " + choice.WorldId, choice.Request, preview.Title, choice.WorldId));
                }
            }

            return choices.AsReadOnly();
        }

        public async void Launch(HostTargetChoice choice, Action<bool, string> done)
        {
            bool ok;
            string message;
            try
            {
                var request = choice.Request;
                (ok, message) = await plugin.LaunchTarget(request.TargetId, request.WorldOverride, request.TransitionOverride);
            }
            catch (Exception exception)
            {
                ok = false;
                message = "The game could not start (" + exception.GetType().Name + ").";
            }

            done(ok, message);
        }

        public async void ReturnToMainMenu(Action<bool, string> done)
        {
            bool ok;
            string message;
            try
            {
                (ok, message) = await plugin.ReturnToMainMenu();
            }
            catch (Exception exception)
            {
                ok = false;
                message = "The game could not return to the menu (" + exception.GetType().Name + ").";
            }

            done(ok, message);
        }

        public void OpenUrl(string url) => Application.OpenURL(url);

        public void CopyToClipboard(string text) => GUIUtility.systemCopyBuffer = text;

        private void RefreshCurrentGame()
        {
            var world = plugin.GetSessionService()?.Current;
            if (ReferenceEquals(world, shownWorld)) return;
            shownWorld = world;
            var running = world?.Session;
            if (world == null || running == null || world.Phase != WorldSessionPhase.Running)
            {
                currentGameText = string.Empty;
                currentGame = null;
                return;
            }

            var title = running.TargetId;
            try
            {
                foreach (var preview in plugin.GetLaunchPreviews())
                {
                    if (string.Equals(preview.Id, running.TargetId, StringComparison.OrdinalIgnoreCase)) title = preview.Title;
                }
            }
            catch (Exception)
            {
                // Without previews the target's id names the game.
            }

            currentGameText = "This game: " + title + " · " + running.WorldId;
            currentGame = new HostTargetChoice(currentGameText, new LaunchRequest(running.TargetId, running.WorldId), title, running.WorldId);
        }
    }
}
