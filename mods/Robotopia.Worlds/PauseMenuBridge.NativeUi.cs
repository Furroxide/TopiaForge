using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Worlds
{
    /// <summary>Read-only discovery helpers for the game's private pause UI hierarchy.</summary>
    internal sealed partial class PauseMenuBridge
    {
        // Label heuristics for the vanilla buttons that leave the world (exit-to-menu / quit-to-desktop) and
        // for buttons that must never be treated as an exit even though their label may contain a keyword.
        private static readonly string[] ExitKeywords = { "menu", "exit", "quit" };
        private static readonly string[] NeverExitKeywords =
            { "resume", "continue", "back", "restart", "options", "settings" };

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
    }
}
