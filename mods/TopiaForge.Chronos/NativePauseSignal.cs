using System;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Chronos
{
    /// <summary>
    /// Read-only, clean-room probe for Robotopia's native pause UI. No GameCode compile dependency is taken: the
    /// player and its private pauseUI field are resolved defensively at runtime. Missing or changed bindings simply
    /// fall back to TimeScaleOwnership's observed-zero policy.
    /// </summary>
    internal sealed class NativePauseSignal : IDisposable
    {
        private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly IModLogger logger;

        private Type? playerControllerType;
        private FieldInfo? playerInstanceField;
        private MethodInfo? findPlayerMethod;
        private FieldInfo? pauseUiField;
        private Component? pauseRoot;
        private bool metadataAttempted;
        private bool resolutionFailureLogged;
        private bool disposed;

        public NativePauseSignal(IModLogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsPaused()
        {
            if (disposed)
            {
                return false;
            }

            try
            {
                if (pauseRoot == null && !TryResolvePauseRoot())
                {
                    return false;
                }

                return pauseRoot != null && pauseRoot.gameObject.activeInHierarchy;
            }
            catch (Exception ex)
            {
                pauseRoot = null;
                LogResolutionFailureOnce(ex.Message);
                return false;
            }
        }

        public void ResetScene()
        {
            pauseRoot = null;
            resolutionFailureLogged = false;
        }

        /// <summary>
        /// Returns the exact active pause root already resolved by <see cref="IsPaused"/>. The caller can observe
        /// this Unity object through an unload handoff without re-running reflection or conflating lookup failure
        /// with a closed overlay.
        /// </summary>
        public Component? CaptureActiveRoot()
        {
            if (disposed || pauseRoot == null)
            {
                return null;
            }

            try
            {
                return pauseRoot.gameObject.activeInHierarchy ? pauseRoot : null;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pauseRoot = null;
            playerControllerType = null;
            playerInstanceField = null;
            findPlayerMethod = null;
            pauseUiField = null;
            metadataAttempted = false;
        }

        private bool TryResolvePauseRoot()
        {
            if (!TryResolveMetadata())
            {
                return false;
            }

            var player = ResolvePlayer();
            if (player == null)
            {
                return false;
            }

            var pauseUi = pauseUiField?.GetValue(player);
            pauseRoot = AsComponent(pauseUi);
            return pauseRoot != null;
        }

        private bool TryResolveMetadata()
        {
            if (metadataAttempted)
            {
                return playerControllerType != null
                    && pauseUiField != null
                    && (playerInstanceField != null || findPlayerMethod != null);
            }

            metadataAttempted = true;
            playerControllerType = FindType("PlayerController");
            if (playerControllerType == null)
            {
                LogResolutionFailureOnce("PlayerController type was not found");
                return false;
            }

            playerInstanceField = playerControllerType.GetField("_instance", AnyStatic);
            findPlayerMethod = playerControllerType.GetMethod(
                "FindPlayer",
                AnyStatic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            pauseUiField = playerControllerType.GetField("pauseUI", AnyInstance);
            if (pauseUiField == null || (playerInstanceField == null && findPlayerMethod == null))
            {
                LogResolutionFailureOnce("PlayerController pauseUI/player lookup members were not found");
                return false;
            }

            return true;
        }

        private object? ResolvePlayer()
        {
            var instance = playerInstanceField?.GetValue(null);
            if (IsLiveUnityObject(instance))
            {
                return instance;
            }

            instance = findPlayerMethod?.Invoke(null, Array.Empty<object>());
            return IsLiveUnityObject(instance) ? instance : null;
        }

        // pauseUI's private declared type is intentionally treated as opaque. It may itself be a Unity object, or a
        // small role container whose fields reference the active panel/buttons. Any child component reports the same
        // activeInHierarchy state, so the first live Unity object is a sufficient and allocation-free cached signal.
        private static Component? AsComponent(object? value)
        {
            switch (value)
            {
                case Component component when component != null:
                    return component;
                case GameObject gameObject when gameObject != null:
                    return gameObject.transform;
                case null:
                    return null;
            }

            foreach (var field in value.GetType().GetFields(AnyInstance))
            {
                var nested = field.GetValue(value);
                if (nested is Component nestedComponent && nestedComponent != null)
                {
                    return nestedComponent;
                }

                if (nested is GameObject nestedObject && nestedObject != null)
                {
                    return nestedObject.transform;
                }
            }

            return null;
        }

        private static bool IsLiveUnityObject(object? value)
        {
            return value is UnityEngine.Object unityObject && unityObject != null;
        }

        private static Type? FindType(string name)
        {
            var gameCodeType = Type.GetType(name + ", GameCode", throwOnError: false);
            if (gameCodeType != null)
            {
                return gameCodeType;
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var index = 0; index < assemblies.Length; index++)
            {
                try
                {
                    var type = assemblies[index].GetType(name, throwOnError: false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    // Dynamic/incomplete assemblies can reject reflection. Continue to the next assembly.
                }
            }

            return null;
        }

        private void LogResolutionFailureOnce(string message)
        {
            if (resolutionFailureLogged)
            {
                return;
            }

            resolutionFailureLogged = true;
            logger.Debug("Chronos native-pause signal unavailable (" + message
                + "); observed zero-timescale ownership remains active as a fallback.");
        }
    }
}
