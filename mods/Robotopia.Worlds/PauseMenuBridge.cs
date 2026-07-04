using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Robotopia.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Worlds
{
    /// <summary>
    /// Session-scoped bridge into the game's vanilla pause menu (<c>PlayerController.pauseUI</c>). While a
    /// world session is active it rewires the vanilla exit/quit buttons so leaving the world first ends the
    /// session cleanly (consulting an optional gamemode interceptor), and hosts gamemode-registered actions
    /// as extra buttons cloned from the game's own. Everything is defensive reflection in the
    /// <see cref="GameLevelBridge"/> style: a missing symbol or unrecognized UI logs once and degrades to
    /// doing nothing — the provider's scene-load session teardown remains the correctness backstop.
    /// </summary>
    internal sealed class PauseMenuBridge : IWorldPauseMenuService, IDisposable
    {
        private const float PollIntervalSeconds = 0.5f;
        private const string ActionNamePrefix = "RobotopiaPauseAction:";
        private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Label heuristics for the vanilla buttons that leave the world (exit-to-menu / quit-to-desktop) and
        // for buttons that must never be treated as an exit even though their label may contain a keyword.
        private static readonly string[] ExitKeywords = { "menu", "exit", "quit" };
        private static readonly string[] NeverExitKeywords = { "resume", "continue", "back", "restart", "options", "settings" };

        private readonly WorldsService service;
        private readonly IModLogger logger;
        private readonly bool enabled;
        private readonly Type? playerControllerType;
        private readonly List<ActionRegistration> actions = new List<ActionRegistration>();
        private readonly List<RewiredButton> rewired = new List<RewiredButton>();

        private Func<WorldPauseExitContext, WorldPauseExitDecision>? exitInterceptor;
        private Component? pauseRoot;
        private bool pauseWasActive;
        private float pollTimer;
        private bool resolveFailureLogged;
        private bool disposed;

        public PauseMenuBridge(WorldsService service, IModLogger logger, bool enabled)
        {
            this.service = service;
            this.logger = logger;
            this.enabled = enabled;
            playerControllerType = Type.GetType("PlayerController, GameCode", throwOnError: false);
            service.SessionEnded += OnSessionEnded;
        }

        public bool IsAvailable { get; private set; }

        public IDisposable RegisterAction(WorldPauseAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            var registration = new ActionRegistration(this, action);
            actions.RemoveAll(item => string.Equals(item.Action.Id, action.Id, StringComparison.OrdinalIgnoreCase));
            actions.Add(registration);
            // Injected on the next rewire pass; if the pause menu is open right now, refresh immediately.
            if (pauseWasActive)
            {
                TryRewire();
            }

            return registration;
        }

        public void SetExitInterceptor(Func<WorldPauseExitContext, WorldPauseExitDecision>? interceptor)
        {
            exitInterceptor = interceptor;
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

        // --- resolution -----------------------------------------------------------------------------------

        private void ResolvePauseRoot()
        {
            try
            {
                var player = ResolvePlayerInstance();
                if (player == null)
                {
                    return;
                }

                var value = playerControllerType?.GetField("pauseUI", AnyInstance)?.GetValue(player);
                pauseRoot = AsComponent(value);
                if (pauseRoot != null)
                {
                    IsAvailable = true;
                    logger.Debug("Worlds pause bridge resolved the game's pause UI ('" + pauseRoot.gameObject.name + "').");
                }
                else if (!resolveFailureLogged)
                {
                    resolveFailureLogged = true;
                    logger.Warn("Worlds pause bridge could not resolve PlayerController.pauseUI; vanilla pause "
                        + "interception is disabled (session teardown still happens on menu load).");
                }
            }
            catch (Exception ex)
            {
                if (!resolveFailureLogged)
                {
                    resolveFailureLogged = true;
                    logger.Warn("Worlds pause bridge failed to resolve the pause UI: " + ex.Message);
                }
            }
        }

        private object? ResolvePlayerInstance()
        {
            if (playerControllerType == null)
            {
                return null;
            }

            var instance = playerControllerType.GetField("_instance", AnyStatic)?.GetValue(null);
            if (instance is UnityEngine.Object unityInstance && unityInstance != null)
            {
                return instance;
            }

            var findPlayer = playerControllerType.GetMethod("FindPlayer", AnyStatic, null, Type.EmptyTypes, null);
            return findPlayer?.Invoke(null, null);
        }

        // The pauseUI field's declared type (GlobalButtonRoles) is a game type we deliberately do not bind to
        // member-by-member: treat the value as a Unity Component when it is one, otherwise scan its fields
        // generically for the first live Component/GameObject to use as the panel root.
        private static Component? AsComponent(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case Component component when component != null:
                    return component;
                case GameObject go when go != null:
                    return go.transform;
            }

            foreach (var field in value.GetType().GetFields(AnyInstance))
            {
                var inner = field.GetValue(value);
                if (inner is Component innerComponent && innerComponent != null)
                {
                    return innerComponent;
                }

                if (inner is GameObject innerGo && innerGo != null)
                {
                    return innerGo.transform;
                }
            }

            return null;
        }

        // --- rewiring -------------------------------------------------------------------------------------

        private void TryRewire()
        {
            try
            {
                rewired.RemoveAll(item => item.Button == null);

                var buttons = pauseRoot!.GetComponentsInChildren<Button>(true)
                    .Where(button => button != null && !button.gameObject.name.StartsWith(ActionNamePrefix, StringComparison.Ordinal))
                    .ToArray();

                Button? exitTemplate = null;
                foreach (var button in buttons)
                {
                    if (!IsExitButton(button))
                    {
                        continue;
                    }

                    exitTemplate ??= button;
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

                if (exitTemplate != null)
                {
                    InjectActions(exitTemplate);
                }
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds pause bridge rewire pass failed: " + ex.Message);
            }
        }

        private static bool IsExitButton(Button button)
        {
            var label = GetLabel(button);
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            if (NeverExitKeywords.Any(keyword => label.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return false;
            }

            return ExitKeywords.Any(keyword => label.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
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

            foreach (var registration in actions)
            {
                registration.DestroyClone();
            }
        }

        // --- gamemode actions -----------------------------------------------------------------------------

        private void InjectActions(Button template)
        {
            var rank = 0;
            foreach (var registration in actions.OrderBy(item => item.Action.Order))
            {
                rank++;
                if (registration.Clone != null)
                {
                    continue;
                }

                try
                {
                    registration.Clone = BuildActionButton(template, registration.Action, rank);
                }
                catch (Exception ex)
                {
                    logger.Debug("Worlds pause bridge could not inject action '" + registration.Action.Id + "': " + ex.Message);
                }
            }
        }

        private GameObject? BuildActionButton(Button template, WorldPauseAction action, int rank)
        {
            var clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
            clone.name = ActionNamePrefix + action.Id;
            clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + rank);

            // The template can carry game behaviours (localizers, sfx hooks, the game's own click handler
            // component) that would misbehave on a fake button — keep only the visual/interaction essentials.
            foreach (var component in clone.GetComponentsInChildren<Component>(true))
            {
                if (component == null
                    || component is RectTransform
                    || component is CanvasRenderer
                    || component is Button
                    || component is Graphic // Image + Text (+ TMP_Text derives from Graphic via MaskableGraphic)
                    || component.GetType().Name.StartsWith("TextMeshPro", StringComparison.Ordinal))
                {
                    continue;
                }

                UnityEngine.Object.Destroy(component);
            }

            SetLabel(clone, action.Label);

            var button = clone.GetComponent<Button>();
            if (button == null)
            {
                UnityEngine.Object.Destroy(clone);
                return null;
            }

            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() =>
            {
                try
                {
                    action.Callback();
                }
                catch (Exception ex)
                {
                    logger.Warn("Worlds pause action '" + action.Id + "' failed: " + ex.Message);
                }

                if (action.ClosePauseMenu)
                {
                    ClosePauseMenu();
                }
            });

            // If the parent lays children out for us the clone already slots in; otherwise stack it under the
            // template so it does not overlap.
            if (template.transform.parent != null && template.transform.parent.GetComponent<LayoutGroup>() == null
                && clone.transform is RectTransform rect && template.transform is RectTransform templateRect)
            {
                rect.anchoredPosition = templateRect.anchoredPosition
                    - new Vector2(0f, (templateRect.sizeDelta.y + 8f) * rank);
            }

            logger.Info("Worlds pause bridge added gamemode action '" + action.Label + "' to the pause menu.");
            return clone;
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

        // --- label helpers (uGUI Text directly; TMP via reflection so no TMP assembly reference) ------------

        private static string GetLabel(Component buttonRoot)
        {
            var text = buttonRoot.GetComponentInChildren<Text>(true);
            if (text != null && !string.IsNullOrWhiteSpace(text.text))
            {
                return text.text;
            }

            var tmp = FindTmpText(buttonRoot);
            return tmp.HasValue
                ? tmp.Value.property.GetValue(tmp.Value.component) as string ?? string.Empty
                : string.Empty;
        }

        private static void SetLabel(GameObject buttonRoot, string label)
        {
            var text = buttonRoot.GetComponentInChildren<Text>(true);
            if (text != null)
            {
                text.text = label;
                return;
            }

            var tmp = FindTmpText(buttonRoot.transform);
            if (tmp.HasValue)
            {
                tmp.Value.property.SetValue(tmp.Value.component, label);
            }
        }

        private static (Component component, PropertyInfo property)? FindTmpText(Component root)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    continue;
                }

                var type = component.GetType();
                if (!type.Name.StartsWith("TextMeshPro", StringComparison.Ordinal) && type.Name != "TMP_Text")
                {
                    continue;
                }

                var property = type.GetProperty("text", AnyInstance);
                if (property != null)
                {
                    return (component, property);
                }
            }

            return null;
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

        private sealed class ActionRegistration : IDisposable
        {
            private readonly PauseMenuBridge owner;

            public ActionRegistration(PauseMenuBridge owner, WorldPauseAction action)
            {
                this.owner = owner;
                Action = action;
            }

            public WorldPauseAction Action { get; }
            public GameObject? Clone { get; set; }

            public void DestroyClone()
            {
                if (Clone != null)
                {
                    UnityEngine.Object.Destroy(Clone);
                    Clone = null;
                }
            }

            public void Dispose()
            {
                DestroyClone();
                owner.actions.Remove(this);
            }
        }
    }
}
