using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    internal sealed partial class UnityWorldRuntimeBackend : IWorldRuntimeBackend
    {
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const int DiscoveryLimit = 4096;
        private readonly UnityEntityRegistry entities;
        internal UnityWorldRuntimeBackend(UnityEntityRegistry entities) { this.entities = entities; }

        public OperationResult<IReadOnlyList<NativeWorldEntry>> Discover(NativeWorldSource source, int maximumResults)
        {
            UnityMainThreadGuard.AssertCurrent();
            try
            {
                if (maximumResults < 1 || maximumResults > DiscoveryLimit || !Enum.IsDefined(typeof(NativeWorldSource), source))
                    return Failure<IReadOnlyList<NativeWorldEntry>>(ModErrorCode.InvalidArgument, "Discovery requires a known source and a limit between 1 and 4096.");
                var entries = source == NativeWorldSource.CuratedLevels
                    ? CuratedEntries().Select(entry => entry.Descriptor).ToList() : BuildEntries();
                var duplicate = entries.GroupBy(entry => entry.SourceKey, StringComparer.Ordinal).FirstOrDefault(group => group.Count() != 1);
                if (duplicate != null)
                    return Failure<IReadOnlyList<NativeWorldEntry>>(ModErrorCode.Conflict, "Native discovery contains competing identities: " + duplicate.Key);
                return OperationResult<IReadOnlyList<NativeWorldEntry>>.Success(entries.OrderBy(entry => entry.SourceKey, StringComparer.Ordinal)
                    .Take(maximumResults).ToArray());
            }
            catch (Exception error) { return Failure<IReadOnlyList<NativeWorldEntry>>(ModErrorCode.Unavailable, Unwrap(error).Message); }
        }

        public OperationResult<IWorldNativeLoad> CreateLoad(NativeWorldLoadRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            try
            {
                object? checkpoint = null;
                string scenePath;
                if (request.Kind == NativeWorldLoadKind.Curated)
                {
                    if (request.Transition != WorldLoadTransition.SceneReplacement)
                        return Failure<IWorldNativeLoad>(ModErrorCode.InvalidArgument, "Curated checkpoints require scene replacement.");
                    var matches = CuratedEntries().Where(entry => entry.Descriptor.SourceKey == request.Key).ToArray();
                    if (matches.Length != 1)
                        return Failure<IWorldNativeLoad>(matches.Length == 0 ? ModErrorCode.NotFound : ModErrorCode.Conflict,
                            "The selected curated checkpoint is missing or ambiguous.");
                    checkpoint = matches[0].Checkpoint; scenePath = matches[0].Descriptor.SceneName;
                }
                else scenePath = request.Kind == NativeWorldLoadKind.OpenSandbox ? "UgcPlay" : request.Key!;
                if (request.Transition == WorldLoadTransition.AdditiveArena)
                {
                    var active = SceneManager.GetActiveScene();
                    if (!active.IsValid() || !active.isLoaded || GameScenes.IsNonGameplayScene(active.name))
                        return Failure<IWorldNativeLoad>(ModErrorCode.Unavailable, "An additive arena requires an existing loaded gameplay scene.");
                    if (request.Kind == NativeWorldLoadKind.Scene && !Matches(active, scenePath))
                        return Failure<IWorldNativeLoad>(ModErrorCode.Conflict, "The declared scene is not the active additive scene.");
                }
                else
                {
                    if (!Application.CanStreamedLevelBeLoaded(scenePath))
                        return Failure<IWorldNativeLoad>(ModErrorCode.NotFound, "The native scene is unavailable: " + scenePath);
                    if (!scenePath.Contains("/", StringComparison.Ordinal) && !scenePath.Contains("\\", StringComparison.Ordinal))
                    {
                        var names = BuildEntries().Where(entry => entry.SceneName == scenePath).ToArray();
                        if (names.Length > 1) return Failure<IWorldNativeLoad>(ModErrorCode.Conflict, "The native scene name identifies multiple build scenes.");
                    }
                }
                return OperationResult<IWorldNativeLoad>.Success(new NativeLoad(entities, request, scenePath, checkpoint));
            }
            catch (Exception error) { return Failure<IWorldNativeLoad>(ModErrorCode.Unavailable, Unwrap(error).Message); }
        }

        private static List<NativeWorldEntry> BuildEntries()
        {
            var count = SceneManager.sceneCountInBuildSettings;
            if (count > DiscoveryLimit) throw new InvalidOperationException("Native build-scene discovery exceeds its inspection limit.");
            var found = new List<NativeWorldEntry>();
            for (var index = 0; index < count; index++)
            {
                var path = SceneUtility.GetScenePathByBuildIndex(index);
                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name) || GameScenes.IsNonGameplayScene(name)) continue;
                found.Add(new NativeWorldEntry(NativeWorldSource.BuildScenes, path, name, "First-party Robotopia scene.", name));
            }
            return found;
        }

        private static List<CuratedEntry> CuratedEntries()
        {
            var type = Type.GetType("GlobalAssetsMap, GameCode", false);
            var catalog = type?.GetProperty("LevelEntryPoints", PublicStatic)?.GetValue(null)
                ?? throw new InvalidOperationException("The game's curated level catalog is unavailable.");
            var entries = catalog.GetType().GetField("entries", AnyInstance)?.GetValue(catalog) as Array
                ?? throw new InvalidOperationException("The game's curated level entries are unavailable.");
            if (entries.Length > DiscoveryLimit) throw new InvalidOperationException("Curated discovery exceeds its inspection limit.");
            var found = new List<CuratedEntry>();
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                var checkpoint = entry.GetType().GetField("checkpointAsset", AnyInstance)?.GetValue(entry);
                var scene = checkpoint?.GetType().GetProperty("SceneName", AnyInstance)?.GetValue(checkpoint) as string;
                if (checkpoint == null || string.IsNullOrWhiteSpace(scene) || GameScenes.IsNonGameplayScene(scene!)) continue;
                if (!(checkpoint is UnityEngine.Object asset) || asset == null || string.IsNullOrWhiteSpace(asset.name))
                    throw new InvalidOperationException("A curated checkpoint lacks a stable asset identity.");
                var key = scene + ":" + checkpoint.GetType().FullName + ":" + asset.name;
                var name = entry.GetType().GetField("displayName", AnyInstance)?.GetValue(entry) as string;
                var description = entry.GetType().GetField("description", AnyInstance)?.GetValue(entry) as string ?? string.Empty;
                found.Add(new CuratedEntry(new NativeWorldEntry(NativeWorldSource.CuratedLevels, key,
                    string.IsNullOrWhiteSpace(name) ? scene! : name!, description, scene!), checkpoint));
            }
            return found;
        }
        private sealed class CuratedEntry
        {
            internal CuratedEntry(NativeWorldEntry descriptor, object checkpoint) { Descriptor = descriptor; Checkpoint = checkpoint; }
            internal NativeWorldEntry Descriptor { get; }
            internal object Checkpoint { get; }
        }
        private static bool Matches(Scene scene, string nameOrPath) =>
            string.Equals(scene.path, nameOrPath, StringComparison.Ordinal) ||
            (!nameOrPath.Contains("/", StringComparison.Ordinal) && !nameOrPath.Contains("\\", StringComparison.Ordinal)
                && string.Equals(scene.name, nameOrPath, StringComparison.Ordinal));
        private static Exception Unwrap(Exception error) => error is TargetInvocationException invocation ? invocation.InnerException ?? invocation : error;
        private static OperationResult<T> Failure<T>(ModErrorCode code, string message) where T : notnull => OperationResult<T>.Failure(code, message);
    }
}
