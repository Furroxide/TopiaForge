using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.Worlds
{
    // One native adapter owns the pending engine verification. No native result is inferred from reflection.
    internal sealed class NativeMapViewSource : IInternalMapViewSource
    {
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly Component host;
        private readonly Transform root;
        private readonly object scene;
        private readonly WorldSceneIdentity identity;
        internal NativeMapViewSource(Component host, Transform root, object scene, WorldSceneIdentity identity)
        { this.host = host; this.root = root; this.scene = scene; this.identity = identity; }
        public IInternalWorldViewRenderer CreateRenderer(IModContext context) => new NativeWorldViewRenderer(identity, context, this);
        internal bool IsAlive => root != null && host != null;
        internal Transform Root => root;
        internal NativeEnvironment? BindEnvironment()
        {
            try
            {
                var mapType = Type.GetType("UgcEnvironmentPrefabMap, GameCode", false);
                var sceneType = Type.GetType("UgcExportScene, GameCode", false);
                var hostType = Type.GetType("UgcImportHostSceneController, GameCode", false);
                var applierType = Type.GetType("UgcSceneEnvironmentApplier, GameCode", false);
                var instanceType = Type.GetType("UgcSceneEnvironmentInstance, GameCode", false);
                var map = hostType?.GetProperty("EnvironmentPrefabMap", Instance)?.GetValue(host);
                var apply = applierType?.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { sceneType!, mapType!, typeof(Transform), typeof(UnityEngine.Object) }, null);
                var find = mapType?.GetMethod("TryGetPrefab", Instance, null,
                    new[] { typeof(string), typeof(GameObject).MakeByRefType() }, null);
                var environment = sceneType?.GetField("environment", Instance);
                if (map == null || apply == null || find == null || instanceType == null || environment?.FieldType != typeof(string)) return null;
                var keys = new List<string>();
                // Listing is optional: a missing list must not disable TryGetPrefab or applying a known key.
                try
                {
                    var entries = mapType!.GetField("entries", Instance)?.GetValue(map) as IEnumerable;
                    var entryType = Type.GetType("UgcEnvironmentPrefabMap+Entry, GameCode", false);
                    var keyField = entryType?.GetField("presetKey", Instance);
                    if (entries != null && keyField != null)
                        foreach (var entry in entries)
                        {
                            if (keys.Count >= 256) break;
                            if (keyField.GetValue(entry) is string key && ValidKey(key)) keys.Add(key);
                        }
                }
                catch { keys.Clear(); }
                return new NativeEnvironment(this, map, apply, find, environment,
                    environment.GetValue(scene) as string ?? "", keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray());
            }
            catch { return null; }
        }
        private static bool ValidKey(string value) => value.Length > 0 && value.Length <= 64 && !string.IsNullOrWhiteSpace(value)
            && !value.Any(char.IsControl);

        internal sealed class NativeEnvironment
        {
            private readonly NativeMapViewSource owner;
            private readonly object map;
            private readonly MethodInfo apply;
            private readonly MethodInfo find;
            private readonly FieldInfo environment;
            private readonly OwnedEnvironment<Component> ownership;
            internal NativeEnvironment(NativeMapViewSource owner, object map, MethodInfo apply, MethodInfo find,
                FieldInfo environment, string original, string[] keys)
            {
                this.owner = owner; this.map = map; this.apply = apply; this.find = find; this.environment = environment;
                Presets = Array.AsReadOnly(keys);
                var instance = Type.GetType("UgcSceneEnvironmentInstance, GameCode", true)!;
                ownership = new OwnedEnvironment<Component>(original,
                    () => owner.root.GetComponentsInChildren(instance, true).OrderBy(c => c.GetInstanceID()).ToArray(), Apply);
            }
            private void Apply(string key)
            {
                var descriptor = Activator.CreateInstance(owner.scene.GetType());
                environment.SetValue(descriptor, key);
                apply.Invoke(null, new object[] { descriptor!, map, owner.root, owner.host });
            }
            internal IReadOnlyList<string> Presets { get; }
            internal bool HasPreset(string key)
            {
                try { return owner.IsAlive && find.Invoke(map, new object?[] { key, null }) is bool found && found; }
                catch { return false; }
            }
            internal OperationResult<bool> Set(string? key)
            {
                if (!owner.IsAlive) return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The imported map has ended.");
                try
                {
                    // Copy only the selected environment into a fresh native descriptor. The imported document stays immutable.
                    ownership.Set(key);
                    return OperationResult<bool>.Success(true);
                }
                catch { return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The game could not apply the environment preset. Update TopiaForge."); }
            }
        }
    }
}
