using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// Session-scoped bridge into the game's vanilla pause menu (<c>PlayerController.pauseUI</c>). While a
    /// world session is active it rewires the vanilla exit/quit buttons so leaving the world first ends the
    /// session cleanly (consulting an optional gamemode interceptor). Gamemode actions are hosted in a TopiaForgeUi
    /// companion window rather than cloning the game's private UI hierarchy. Everything is defensive reflection in the
    /// <see cref="GameLevelBridge"/> style: a missing symbol or unrecognized UI logs once and degrades to
    /// doing nothing — the provider's scene-load session teardown remains the correctness backstop.
    /// </summary>
    internal sealed partial class PauseMenuBridge : IWorldPauseMenuService,
        IOwnerBoundExtensionFactory, IDisposable
    {
        private const float PollIntervalSeconds = 0.5f;
        private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly WorldsService service;
        private readonly IModLogger logger;
        private readonly bool enabled;
        private readonly Type? playerControllerType;
        private readonly List<ActionRegistration> actions = new List<ActionRegistration>();
        private readonly List<RewiredButton> rewired = new List<RewiredButton>();
        private readonly PauseActionOverlay actionOverlay;

        private Func<WorldPauseExitContext, WorldPauseExitDecision>? exitInterceptor;
        private Component? pauseRoot;
        private bool pauseWasActive;
        private float pollTimer;
        private bool resolveFailureLogged;
        private bool disposed;

        public PauseMenuBridge(WorldsService service, IModLogger logger, UiHost ui, bool enabled)
        {
            this.service = service;
            this.logger = logger;
            this.enabled = enabled;
            actionOverlay = new PauseActionOverlay(ui, logger, ClosePauseMenu);
            playerControllerType = Type.GetType("PlayerController, GameCode", throwOnError: false);
            service.SessionEnded += OnSessionEnded;
        }

        public bool IsAvailable { get; private set; }

        public OperationResult<IDisposable> RegisterAction(WorldPauseAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (disposed)
            {
                logger.Warn("Worlds ignored a pause action registered after the pause service was disposed.");
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.InvalidState,
                    "Pause service is disposed.");
            }

            var previous = actions.FirstOrDefault(item =>
                string.Equals(item.Action.Id, action.Id, StringComparison.OrdinalIgnoreCase));
            previous?.Dispose();

            var registration = new ActionRegistration(this, action);
            actions.Add(registration);
            RefreshActionOverlay();

            return OperationResult<IDisposable>.Success(registration);
        }

        public OperationResult<IDisposable> InterceptExit(
            Func<WorldPauseExitContext, WorldPauseExitDecision> interceptor)
        {
            if (interceptor == null)
            {
                throw new ArgumentNullException(nameof(interceptor));
            }

            if (disposed)
            {
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.InvalidState,
                    "Pause service is disposed.");
            }

            exitInterceptor = interceptor;
            return OperationResult<IDisposable>.Success(new ExitInterceptorLease(this, interceptor));
        }

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string ownerModId,
            IModLifetime lifetime)
        {
            if (contractType != typeof(IWorldPauseMenuService))
            {
                throw new ArgumentException("Unsupported Worlds pause extension contract.", nameof(contractType));
            }

            return new OwnerFacade(this, ownerModId, lifetime);
        }

        /// <summary>Main-thread pump (throttled). Watches for the pause UI opening during a session.</summary>
        public void Update(float deltaTime)
        {
            if (disposed || !enabled)
            {
                return;
            }

            if (service.CurrentSession == null)
            {
                pauseWasActive = false;
                return;
            }

            pollTimer -= deltaTime;
            if (pollTimer > 0f)
            {
                return;
            }

            pollTimer = PollIntervalSeconds;

            try
            {
                if (pauseRoot == null)
                {
                    ResolvePauseRoot();
                }

                if (pauseRoot == null)
                {
                    pauseWasActive = false;
                    return;
                }

                var active = pauseRoot.gameObject.activeInHierarchy;
                if (active)
                {
                    // Rewire on every poll while open: idempotent per button, and it re-captures buttons if
                    // the game rebuilt the panel (same presence-check discipline as MenuButtonInjector).
                    TryRewire();
                    if (!pauseWasActive)
                    {
                        actionOverlay.Show();
                    }
                }
                else if (pauseWasActive)
                {
                    actionOverlay.Hide();
                }

                pauseWasActive = active;
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds pause bridge update failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            service.SessionEnded -= OnSessionEnded;
            RestoreAll();
            foreach (var registration in actions.ToArray())
            {
                registration.Dispose();
            }

            actionOverlay.Dispose();
        }

        private void OnSessionEnded(WorldSessionEnd end)
        {
            // The session's pause customizations must not outlive it (the menu scene has its own UI).
            RestoreAll();
            exitInterceptor = null;
            pauseRoot = null;
            pauseWasActive = false;
            IsAvailable = false;
        }

        // --- rewiring -------------------------------------------------------------------------------------

        private void TryRewire()
        {
            try
            {
                rewired.RemoveAll(item => item.Button == null);

                var buttons = pauseRoot!.GetComponentsInChildren<Button>(true)
                    .Where(button => button != null)
                    .ToArray();

                foreach (var button in buttons)
                {
                    if (!IsExitButton(button))
                    {
                        continue;
                    }

                    if (rewired.Any(item => item.Button == button))
                    {
                        continue;
                    }

                    var original = button.onClick;
                    button.onClick = new Button.ButtonClickedEvent();
                    button.onClick.AddListener(() => OnVanillaExitClicked(original));
                    rewired.Add(new RewiredButton(button, original));
                    logger.Info("Worlds pause bridge rewired vanilla pause button '" + GetLabel(button) + "'.");
                }
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds pause bridge rewire pass failed: " + ex.Message);
            }
        }

        private void OnVanillaExitClicked(Button.ButtonClickedEvent original)
        {
            var session = service.CurrentSession;
            if (session == null)
            {
                // No live session to protect — behave exactly like the vanilla button.
                original.Invoke();
                return;
            }

            var decision = WorldPauseExitDecision.EndSessionAndExit;
            var interceptor = exitInterceptor;
            if (interceptor != null)
            {
                try
                {
                    decision = interceptor(new WorldPauseExitContext(session));
                }
                catch (Exception ex)
                {
                    // A throwing gamemode hook must never eat the vanilla button.
                    logger.Warn("Worlds pause exit interceptor failed; ending the session and exiting: " + ex.Message);
                    decision = WorldPauseExitDecision.EndSessionAndExit;
                }
            }

            switch (decision)
            {
                case WorldPauseExitDecision.Block:
                    return;
                case WorldPauseExitDecision.ExitWithoutEnding:
                    original.Invoke();
                    return;
                default:
                    service.EndSession(WorldSessionEndReason.MenuReached);
                    original.Invoke();
                    return;
            }
        }

        private void RestoreAll()
        {
            foreach (var item in rewired)
            {
                if (item.Button != null)
                {
                    item.Button.onClick = item.Original;
                }
            }

            rewired.Clear();
            actionOverlay.Reset();
        }

        // --- gamemode actions -----------------------------------------------------------------------------

        private void RefreshActionOverlay()
        {
            actionOverlay.SetActions(actions
                .OrderBy(item => item.Action.Order)
                .Select(item => item.Action)
                .ToArray());
            if (pauseWasActive)
            {
                actionOverlay.Show();
            }
        }

        private void RemoveAction(ActionRegistration registration)
        {
            if (actions.Remove(registration))
            {
                RefreshActionOverlay();
            }
        }

        private void RemoveExitInterceptor(Func<WorldPauseExitContext, WorldPauseExitDecision> interceptor)
        {
            if (ReferenceEquals(exitInterceptor, interceptor))
            {
                exitInterceptor = null;
            }
        }

        private void ClosePauseMenu()
        {
            try
            {
                var player = ResolvePlayerInstance();
                var exitPause = playerControllerType?.GetMethod("ExitPause", AnyInstance, null, Type.EmptyTypes, null);
                if (player != null && exitPause != null)
                {
                    exitPause.Invoke(player, null);
                }
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds pause bridge could not close the pause menu: " + ex.Message);
            }
        }

        private sealed class RewiredButton
        {
            public RewiredButton(Button button, Button.ButtonClickedEvent original)
            {
                Button = button;
                Original = original;
            }

            public Button Button { get; }
            public Button.ButtonClickedEvent Original { get; }
        }

    }
}
