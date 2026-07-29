using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TopiaForge.Performance
{
    /// <summary>
    /// Clean-room reflection helpers for touching game (<c>GameCode</c>) types without a compile-time
    /// reference, so a future game update that renames a member degrades a single lever to an inert
    /// no-op instead of failing the whole mod to load. Everything is guarded; nothing throws.
    /// </summary>
    internal static class GameReflectionLite
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly Dictionary<string, Type?> TypeCache = new Dictionary<string, Type?>(StringComparer.Ordinal);

        /// <summary>Resolve a game type by its (namespace-qualified) name from the GameCode assembly.</summary>
        public static Type? GameType(string typeName)
        {
            if (TypeCache.TryGetValue(typeName, out var cached))
            {
                return cached;
            }

            Type? resolved = null;
            try
            {
                resolved = Type.GetType(typeName + ", GameCode", throwOnError: false);
            }
            catch
            {
                resolved = null;
            }

            TypeCache[typeName] = resolved;
            return resolved;
        }

        /// <summary>All active scene instances of a (typically MonoBehaviour) game type.</summary>
        public static UnityEngine.Object[] FindAll(Type type)
        {
            try
            {
                return UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None) ?? Array.Empty<UnityEngine.Object>();
            }
            catch
            {
                return Array.Empty<UnityEngine.Object>();
            }
        }

        /// <summary>First active scene instance of a game type, or null.</summary>
        public static UnityEngine.Object? FindFirst(Type type)
        {
            try
            {
                return UnityEngine.Object.FindFirstObjectByType(type);
            }
            catch
            {
                return null;
            }
        }

        public static bool TryGetField(object target, string name, out object? value)
        {
            value = null;
            try
            {
                var field = target.GetType().GetField(name, AllInstance);
                if (field == null)
                {
                    return false;
                }

                value = field.GetValue(target);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool SetField(object target, string name, object? value)
        {
            try
            {
                var field = target.GetType().GetField(name, AllInstance);
                if (field == null)
                {
                    return false;
                }

                field.SetValue(target, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool SetProperty(object target, string name, object? value)
        {
            try
            {
                var prop = target.GetType().GetProperty(name, AllInstance);
                if (prop == null || !prop.CanWrite)
                {
                    return false;
                }

                prop.SetValue(target, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool CallInstanceVoid(object target, string method, params object[] args)
        {
            try
            {
                var m = target.GetType().GetMethod(method, AllInstance, binder: null, ArgTypes(args), modifiers: null);
                if (m == null)
                {
                    return false;
                }

                m.Invoke(target, args);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool CallStaticVoid(Type type, string method, params object[] args)
        {
            try
            {
                var m = type.GetMethod(method, AllStatic, binder: null, ArgTypes(args), modifiers: null);
                if (m == null)
                {
                    return false;
                }

                m.Invoke(null, args);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Force the game's low quality level (1) and re-run its volume settings pass.</summary>
        public static void ForceLowQuality()
        {
            try
            {
                QualitySettings.SetQualityLevel(1);
            }
            catch
            {
                // Ignore.
            }

            var type = GameType("LevelSettingsApplier");
            if (type != null)
            {
                CallStaticVoid(type, "ApplySettings");
            }
        }

        /// <summary>
        /// Remove dictionary entries whose Unity key has been destroyed. The <c>(UnityEngine.Object)</c>
        /// cast is required so the comparison uses Unity's fake-null operator — a plain <c>key == null</c>
        /// on a generic type parameter would do reference equality and miss destroyed objects.
        /// </summary>
        public static void PruneDestroyed<TKey, TVal>(Dictionary<TKey, TVal> dict)
            where TKey : UnityEngine.Object
        {
            List<TKey>? dead = null;
            foreach (var key in dict.Keys)
            {
                if ((UnityEngine.Object)key == null)
                {
                    // Unity fake-null: the key is still a live managed reference, safe to add and remove.
                    (dead ??= new List<TKey>()).Add(key!);
                }
            }

            if (dead == null)
            {
                return;
            }

            foreach (var key in dead)
            {
                dict.Remove(key);
            }
        }

        private static Type[] ArgTypes(object[] args)
        {
            var types = new Type[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                types[i] = args[i]?.GetType() ?? typeof(object);
            }

            return types;
        }
    }
}
