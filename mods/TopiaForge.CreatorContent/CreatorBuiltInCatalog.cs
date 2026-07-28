using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;
using UnityEngine;

namespace TopiaForge.CreatorContent
{
    /// <summary>
    /// Clean-room build-2309 catalog adapters. Discovery is deliberately limited to the game's equippable-item
    /// registry and the exact active UGC import host maps. It never walks Resources or clones live scene objects.
    /// </summary>
    internal sealed partial class CreatorBuiltInCatalog : IDisposable
    {
        private const string ItemsSourceId = "robotopia.items";
        private const string PropsSourceId = "robotopia.ugc-props";
        private const int MaximumEntriesPerSource = 1024;
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly object gate = new object();
        private readonly CreatorContentService service;
        private readonly IUnityInteropService interop;
        private readonly IModLogger logger;
        private readonly string gameVersion;
        private readonly Dictionary<string, RegistrationRecord> registrations =
            new Dictionary<string, RegistrationRecord>(StringComparer.OrdinalIgnoreCase);
        private bool disposed;

        public CreatorBuiltInCatalog(
            CreatorContentService service,
            IUnityInteropService interop,
            IRuntimeInfo runtime,
            IModLogger logger)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.interop = interop ?? throw new ArgumentNullException(nameof(interop));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            gameVersion = runtime != null && runtime.TryGetGameVersion(out var version)
                ? version.ToString()
                : "0.0.2309";
        }

        public OperationResult<bool> Refresh()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The built-in creator catalog is disposed.");
                }

                var discovery = Discover();
                Reconcile(discovery);
                service.UpdateBuiltInStatuses(FinalizeStatuses(discovery));
                return OperationResult<bool>.Success(true);
            }
        }

        public void Dispose()
        {
            RegistrationRecord[] current;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                current = registrations.Values.ToArray();
                registrations.Clear();
            }

            for (var index = current.Length - 1; index >= 0; index--)
            {
                SafeDispose(current[index].Registration);
            }
        }

        private Discovery Discover()
        {
            var result = new Discovery();
            DiscoverItems(result);
            DiscoverUgcProps(result);
            result.Statuses["robotopia.vehicles"] = new CreatorCatalogSourceStatus(
                "robotopia.vehicles",
                "Robotopia vehicles",
                CreatorCatalogSourceState.Unavailable,
                "No validated build-2309 vehicle registry adapter is available; arbitrary native scanning is disabled.",
                0);
            result.Statuses["robotopia.characters"] = new CreatorCatalogSourceStatus(
                "robotopia.characters",
                "Robotopia characters",
                CreatorCatalogSourceState.Unavailable,
                "Robot and character catalogs must be supplied by RobotKit or another explicit registered adapter.",
                0);
            return result;
        }

        private void DiscoverItems(Discovery result)
        {
            const string displayName = "Robotopia items";
            try
            {
                var registryType = Type.GetType("EquippableItemRegistry, GameCode", throwOnError: false);
                var entryType = Type.GetType("EquippableItemEntry, GameCode", throwOnError: false);
                var instanceProperty = registryType?.GetProperty("Instance", PublicStatic);
                var iterEntries = registryType?.GetMethod(
                    "IterEntries", PublicInstance, null, Type.EmptyTypes, null);
                var prefabField = entryType?.GetField("worldItemPrefab", PublicInstance);
                if (registryType == null || entryType == null || instanceProperty == null
                    || iterEntries == null || prefabField == null)
                {
                    result.Statuses[ItemsSourceId] = new CreatorCatalogSourceStatus(
                        ItemsSourceId,
                        displayName,
                        CreatorCatalogSourceState.Unavailable,
                        "The build-2309 equippable-item registry binding is missing or changed.",
                        0);
                    return;
                }

                var registry = instanceProperty.GetValue(null, null);
                if (registry == null)
                {
                    result.Statuses[ItemsSourceId] = new CreatorCatalogSourceStatus(
                        ItemsSourceId,
                        displayName,
                        CreatorCatalogSourceState.Degraded,
                        "The equippable-item registry is not active in the current game state.",
                        0);
                    return;
                }

                var entries = iterEntries.Invoke(registry, null) as IEnumerable;
                if (entries != null)
                {
                    foreach (var entry in entries)
                    {
                        if (result.Count(ItemsSourceId) >= MaximumEntriesPerSource) break;
                        if (entry == null || prefabField.GetValue(entry) is not GameObject prefab || prefab == null) continue;
                        var key = prefab.name ?? string.Empty;
                        result.Add(new Candidate(
                            ItemsSourceId,
                            MakeLocalId("item", key),
                            DisplayName(key, "Item"),
                            "Robotopia equippable item exposed by the build-2309 item registry.",
                            CreatorContentKind.Item,
                            prefab));
                    }
                }

                var count = result.Count(ItemsSourceId);
                result.Statuses[ItemsSourceId] = count == 0
                    ? new CreatorCatalogSourceStatus(
                        ItemsSourceId,
                        displayName,
                        CreatorCatalogSourceState.Degraded,
                        "The equippable-item registry was verified but contains no usable world-item prefabs.",
                        0)
                    : new CreatorCatalogSourceStatus(
                        ItemsSourceId,
                        displayName,
                        CreatorCatalogSourceState.Ready,
                        count >= MaximumEntriesPerSource ? "The catalog was capped at 1,024 entries." : string.Empty,
                        count);
            }
            catch (Exception exception)
            {
                result.Remove(ItemsSourceId);
                result.Statuses[ItemsSourceId] = new CreatorCatalogSourceStatus(
                    ItemsSourceId,
                    displayName,
                    CreatorCatalogSourceState.Unavailable,
                    "The equippable-item registry could not be read: " + exception.Message,
                    0);
            }
        }

        private void DiscoverUgcProps(Discovery result)
        {
            const string displayName = "Robotopia UGC props";
            try
            {
                var hostType = Type.GetType("UgcImportHostSceneController, GameCode", throwOnError: false);
                var environmentMapType = Type.GetType("UgcEnvironmentPrefabMap, GameCode", throwOnError: false);
                var runtimeConfigType = Type.GetType("UgcRuntimeAssetConfig, GameCode", throwOnError: false);
                var prefabMapType = Type.GetType("UgcPrefabAssetMap, GameCode", throwOnError: false);
                var environmentProperty = hostType?.GetProperty("EnvironmentPrefabMap", PublicInstance);
                var runtimeProperty = hostType?.GetProperty("RuntimeAssetConfig", PublicInstance);
                var exportedProperty = runtimeConfigType?.GetProperty("ExportedPrefabMap", PublicInstance);
                var environmentEntries = environmentMapType?.GetField("entries", PrivateInstance);
                var prefabEntries = prefabMapType?.GetField("entries", PrivateInstance);
                var environmentEntryType = environmentMapType?.GetNestedType("Entry", BindingFlags.Public | BindingFlags.NonPublic);
                var prefabEntryType = prefabMapType?.GetNestedType("Entry", BindingFlags.Public | BindingFlags.NonPublic);
                var presetKey = environmentEntryType?.GetField("presetKey", PublicInstance);
                var environmentPrefab = environmentEntryType?.GetField("prefab", PublicInstance);
                var assetUri = prefabEntryType?.GetField("assetUri", PublicInstance);
                var assetPrefab = prefabEntryType?.GetField("prefab", PublicInstance);
                var localPositionOffset = prefabEntryType?.GetField("localPositionOffset", PublicInstance);

                if (hostType == null || environmentMapType == null || runtimeConfigType == null || prefabMapType == null
                    || environmentProperty == null || runtimeProperty == null || exportedProperty == null
                    || environmentEntries == null || prefabEntries == null
                    || presetKey == null || environmentPrefab == null
                    || assetUri == null || assetPrefab == null || localPositionOffset == null)
                {
                    result.Statuses[PropsSourceId] = new CreatorCatalogSourceStatus(
                        PropsSourceId,
                        displayName,
                        CreatorCatalogSourceState.Unavailable,
                        "The curated build-2309 UGC prefab-map binding is missing or changed.",
                        0);
                    return;
                }

                var host = UnityEngine.Object.FindAnyObjectByType(hostType);
                if (host == null)
                {
                    result.Statuses[PropsSourceId] = new CreatorCatalogSourceStatus(
                        PropsSourceId,
                        displayName,
                        CreatorCatalogSourceState.Degraded,
                        "No active UGC import host exists in the current scene.",
                        0);
                    return;
                }

                var environmentMap = environmentProperty.GetValue(host, null);
                var runtimeConfig = runtimeProperty.GetValue(host, null);
                var exportedMap = runtimeConfig == null ? null : exportedProperty.GetValue(runtimeConfig, null);
                if (environmentMap == null || runtimeConfig == null || exportedMap == null)
                {
                    result.Statuses[PropsSourceId] = new CreatorCatalogSourceStatus(
                        PropsSourceId,
                        displayName,
                        CreatorCatalogSourceState.Degraded,
                        "The active UGC import host has not initialized all curated prefab maps.",
                        0);
                    return;
                }

                AddMapEntries(
                    result,
                    environmentEntries.GetValue(environmentMap) as Array,
                    presetKey,
                    environmentPrefab,
                    "environment",
                    "Robotopia UGC environment preset from the active import host.");
                AddMapEntries(
                    result,
                    prefabEntries.GetValue(exportedMap) as Array,
                    assetUri,
                    assetPrefab,
                    "asset",
                    "Robotopia UGC exported prefab from the active import host.");

                var count = result.Count(PropsSourceId);
                result.Statuses[PropsSourceId] = count == 0
                    ? new CreatorCatalogSourceStatus(
                        PropsSourceId,
                        displayName,
                        CreatorCatalogSourceState.Degraded,
                        "The active UGC import host was verified but its curated prefab maps are empty.",
                        0)
                    : new CreatorCatalogSourceStatus(
                        PropsSourceId,
                        displayName,
                        CreatorCatalogSourceState.Ready,
                        count >= MaximumEntriesPerSource ? "The catalog was capped at 1,024 entries." : string.Empty,
                        count);
            }
            catch (Exception exception)
            {
                result.Remove(PropsSourceId);
                result.Statuses[PropsSourceId] = new CreatorCatalogSourceStatus(
                    PropsSourceId,
                    displayName,
                    CreatorCatalogSourceState.Unavailable,
                    "The active UGC prefab maps could not be read: " + exception.Message,
                    0);
            }
        }

        private static void AddMapEntries(
            Discovery result,
            Array? entries,
            FieldInfo keyField,
            FieldInfo prefabField,
            string localPrefix,
            string description)
        {
            if (entries == null) return;
            foreach (var entry in entries)
            {
                if (result.Count(PropsSourceId) >= MaximumEntriesPerSource) return;
                if (entry == null || prefabField.GetValue(entry) is not GameObject prefab || prefab == null) continue;
                var key = keyField.GetValue(entry) as string;
                if (string.IsNullOrWhiteSpace(key)) key = prefab.name;
                result.Add(new Candidate(
                    PropsSourceId,
                    MakeLocalId(localPrefix, key ?? string.Empty),
                    DisplayName(prefab.name, key ?? "Prop"),
                    description,
                    CreatorContentKind.Prop,
                    prefab));
            }
        }

        private void Reconcile(Discovery discovery)
        {
            var desired = discovery.Candidates.ToDictionary(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var key in registrations.Keys.Where(key => !desired.ContainsKey(key)).ToArray())
            {
                var removed = registrations[key];
                registrations.Remove(key);
                SafeDispose(removed.Registration);
            }

            foreach (var candidate in discovery.Candidates)
            {
                if (registrations.TryGetValue(candidate.Key, out var current) && current.Matches(candidate))
                {
                    continue;
                }

                if (current != null)
                {
                    registrations.Remove(candidate.Key);
                    SafeDispose(current.Registration);
                }

                var request = new CreatorContentRegistrationRequest(
                    candidate.LocalId,
                    candidate.DisplayName,
                    candidate.Description,
                    candidate.Kind,
                    CreatorTransformCapabilities.All,
                    new NativePrefabFactory(candidate.Prefab, interop));
                var registered = service.RegisterBuiltIn(candidate.SourceId, gameVersion, request);
                if (registered.TryGetValue(out var registration))
                {
                    registrations[candidate.Key] = new RegistrationRecord(candidate, registration);
                }
                else
                {
                    discovery.Failures[candidate.SourceId] = discovery.FailureCount(candidate.SourceId) + 1;
                    logger.Warn("Creator Content skipped '" + candidate.DisplayName + "': " + registered.ErrorMessage);
                }
            }
        }

        private IReadOnlyList<CreatorCatalogSourceStatus> FinalizeStatuses(Discovery discovery)
        {
            var result = new List<CreatorCatalogSourceStatus>();
            foreach (var status in discovery.Statuses.Values)
            {
                var count = registrations.Values.Count(record =>
                    string.Equals(record.Candidate.SourceId, status.SourceId, StringComparison.OrdinalIgnoreCase));
                var failures = discovery.FailureCount(status.SourceId);
                var state = failures > 0 && status.State == CreatorCatalogSourceState.Ready
                    ? CreatorCatalogSourceState.Degraded
                    : status.State;
                var message = failures > 0
                    ? Append(status.Message, failures + " catalog entries could not be registered.")
                    : status.Message;
                result.Add(new CreatorCatalogSourceStatus(status.SourceId, status.DisplayName, state, message, count));
            }
            return result;
        }

        private void SafeDispose(IDisposable resource)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Creator Content could not release a built-in catalog registration.");
            }
        }
    }
}
