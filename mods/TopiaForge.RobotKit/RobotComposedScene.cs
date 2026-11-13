using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.RobotKit
{
    // Clean-room, guarded bindings to the native entity builder. No active scene import or prefab cloning.
    internal sealed class RobotComposedScene
    {
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private readonly object config, project, entity;
        private readonly RobotComposedCurrentness currentness;
        private readonly Type builderType, ownerType;
        private readonly MethodInfo build, addOwner, destroyAssets;

        private RobotComposedScene(Func<WorldSessionSnapshot?> world, WorldSessionSnapshot state,
            Component host, object config, object sceneRoot, Transform mapRoot, object project, object entity)
        {
            this.config = config; this.project = project; this.entity = entity;
            currentness = new RobotComposedCurrentness(world, state, host, config, sceneRoot, mapRoot);
            builderType = Type.GetType("UgcSceneBuilder, GameCode", true)!; ownerType = Type.GetType("UgcRuntimeAssetOwner, GameCode", true)!;
            build = RequireMethod(builderType, "BuildEntity", false, typeof(GameObject), project.GetType(), entity.GetType(), typeof(Transform));
            addOwner = RequireMethod(builderType, "GetOrAddAssetOwner", true, ownerType, typeof(Transform));
            destroyAssets = RequireMethod(ownerType, "DestroyOwnedAssets", false, typeof(void));
            Require(builderType.GetConstructor(new[] { config.GetType() }) != null);
        }

        internal static RobotPrefabCandidate? Discover(Func<WorldSessionSnapshot?>? world) => Discover(world, out _);

        internal static RobotPrefabCandidate? Discover(Func<WorldSessionSnapshot?>? world, out bool notPresent)
        {
            notPresent = false;
            if (world == null) return null;
            WorldSessionSnapshot? state = null;
            try
            {
                state = world();
                if (state == null || !CanSpawn(state.Phase) || state.Session == null || state.World?.Map?.IsAlive != true) return null;
                var scene = default(Scene);
                for (var index = 0; index < SceneManager.sceneCount; index++)
                {
                    var candidate = SceneManager.GetSceneAt(index);
                    if (candidate.handle == state.World.Scene.InstanceId) { scene = candidate; break; }
                }
                if (!scene.IsValid() || !scene.isLoaded) return Refuse(state, RobotComposedRefusal.SceneUnavailable);
                var hosts = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(Type.GetType("UgcImportHostSceneController, GameCode", true)!, true)).ToArray();
                if (hosts.Length != 1) return Refuse(state, RobotComposedRefusal.HostCount);
                var host = hosts[0];
                var config = ReadProperty(host, "RuntimeAssetConfig", Type.GetType("UgcRuntimeAssetConfig, GameCode", true)!);
                var sceneRoot = ReadProperty(host, "SceneRoot", Type.GetType("UgcSceneRoot, GameCode", true)!);
                if (config == null || sceneRoot == null) return Refuse(state, RobotComposedRefusal.ContextUnverified);
                var mapRoot = ReadProperty(sceneRoot, "ActiveImportRoot", typeof(Transform)) as Transform;
                if (mapRoot is null) return Refuse(state, RobotComposedRefusal.ContextUnverified);
                if (mapRoot == null || mapRoot.gameObject.scene.handle != scene.handle) return Refuse(state, RobotComposedRefusal.MapRoot);
                var map = Property(config, "PrefabSceneMap", Type.GetType("UgcPrefabSceneMap, GameCode", true)!);
                var entries = Property(map, "Entries", Type.GetType("UgcPrefabSceneMap+Entry, GameCode", true)!.MakeArrayType()) as Array;
                if (entries == null || entries.Length > 256) return Refuse(state, RobotComposedRefusal.EntryBudget);
                RobotComposedScene? selected = null;
                foreach (var entry in entries)
                {
                    if (entry == null || !RobotComposedSceneShape.IsMunicipalPath(GameReflection.GetFieldValue(entry, "scenePath") as string)) continue;
                    if (!(GameReflection.GetFieldValue(entry, "sceneJson") is string json)
                        || !RobotComposedSceneShape.TryRead(json, out var data)) return Refuse(state, RobotComposedRefusal.SceneShape);
                    // Re-serialize parsed data: the native serializer receives plain data and explicit safe settings.
                    var sceneId = data!.TryGetValue("id", out var id) ? id as string : null;
                    if (string.IsNullOrEmpty(sceneId)) return Refuse(state, RobotComposedRefusal.SceneIdentity);
                    var envelope = new Dictionary<string, object?>
                    {
                        ["scenes"] = new Dictionary<string, object?> { [sceneId!] = data },
                        ["assets"] = new Dictionary<string, object?>(),
                        ["local-assets"] = new Dictionary<string, object?>()
                    };
                    var project = DeserializeProject(MiniJson.Serialize(envelope));
                    // Check the native alias and serializer result as well as the input envelope. This
                    // synchronous factory owns no asset catalogue and must not acquire one through defaults.
                    if (!(GameReflection.GetFieldValue(project, "assets") is IDictionary assets) || assets.Count != 0
                        || !(GameReflection.GetFieldValue(project, "localAssets") is IDictionary localAssets) || localAssets.Count != 0)
                        return Refuse(state, RobotComposedRefusal.ProjectCatalogShape);
                    var nativeScenes = GameReflection.GetFieldValue(project, "scenes") as IDictionary;
                    var nativeScene = nativeScenes?[sceneId!];
                    if (nativeScene == null || nativeScene.GetType() != Type.GetType("UgcExportScene, GameCode", true)!)
                        return Refuse(state, RobotComposedRefusal.EntityShape);
                    var entities = GameReflection.GetFieldValue(nativeScene, "entities") as IDictionary;
                    if (entities?.Count != 1) return Refuse(state, RobotComposedRefusal.EntityShape);
                    var entity = entities.Values.Cast<object>().Single();
                    if (entity == null || entity.GetType() != Type.GetType("UgcExportEntity, GameCode", true)!)
                        return Refuse(state, RobotComposedRefusal.EntityShape);
                    if (!ReferencesResolve(config, map, project, entity, out var referenceRefusal)) return Refuse(state, referenceRefusal);
                    // Ambiguous catalog identities are unavailable rather than chosen by ordering.
                    if (selected != null) return Refuse(state, RobotComposedRefusal.AmbiguousIdentity);
                    selected = new RobotComposedScene(world, state, host, config, sceneRoot, mapRoot, project, entity);
                }
                notPresent = selected == null;
                return selected == null ? Refuse(state, RobotComposedRefusal.NotPresent) : new RobotPrefabCandidate("municipal", "MuniciPal", selected.Create, selected.AdmitsSpawns);
            }
            catch (Exception)
            {
                return Refuse(state, RobotComposedRefusal.NativeBinding);
            }
        }

        private static RobotPrefabCandidate? Refuse(WorldSessionSnapshot? state, RobotComposedRefusal reason)
        {
            RobotKitDiagnostics.ReportComposedDiscovery(state, reason);
            return null;
        }

        private GameObject Create(Transform incubator)
        {
            currentness.RequireVerified();
            var parent = new GameObject("RobotKit Composed Agent");
            parent.SetActive(false);
            parent.transform.SetParent(incubator, false);
            var lease = parent.AddComponent<RobotComposedLifetime>();
            lease.Initialize(ownerType, destroyAssets);
            lease.Current = IsCurrent;
            try
            {
                // Force native ownership onto this brand-new subtree, never the provider or active map root.
                var owner = addOwner.Invoke(null, new object[] { parent.transform }) as Component;
                Require(owner != null && owner.transform == parent.transform);
                var builder = Activator.CreateInstance(builderType, config);
                var actor = build.Invoke(builder, new[] { project, entity, parent.transform }) as GameObject;
                Require(actor != null && actor.transform.IsChildOf(parent.transform) && !actor.activeInHierarchy);
                lease.Actor = actor;
                currentness.RequireVerified();
                Require(GameReflection.HasComponent(actor!, "RobotBody")
                    && GameReflection.HasComponent(actor!, "LLMAgent") && GameReflection.HasComponent(actor!, "AgentHead")
                    && GameReflection.HasComponent(actor!, "LocomotionController"));
                actor!.SetActive(false);
                return actor;
            }
            catch { lease.Dispose(); throw; }
        }

        private bool IsCurrent()
        {
            return currentness.IsCurrent();
        }

        private bool AdmitsSpawns() => currentness.AdmitsSpawns();

        private static bool CanSpawn(WorldSessionPhase phase) => phase == WorldSessionPhase.StartingMode || phase == WorldSessionPhase.Running;

        private static bool ReferencesResolve(object config, object sceneMap, object project, object entity,
            out RobotComposedRefusal refusal)
        {
            refusal = RobotComposedRefusal.EntityReferencesShape;
            var components = GameReflection.GetFieldValue(entity, "components");
            if (components == null || components.GetType() != Type.GetType("UgcExportEntityComponents, GameCode", true)!
                || !(GameReflection.GetFieldValue(components, "humanoidBodyParts") is IDictionary parts)) return false;
            // This synchronous factory has not prepared catalog lore for its empty project.
            // A part or voice resolver cannot attest personality/about data, even for equal strings.
            var preparedCatalogAssets = new HashSet<string>(StringComparer.Ordinal);
            var builder = Type.GetType("UgcSceneBuilder, GameCode", true)!;
            string Literal(string name)
            {
                var field = builder.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
                Require(field != null && field.IsLiteral && field.FieldType == typeof(string));
                return (string)field!.GetRawConstantValue()!;
            }
            var plastic = Literal("PlasticMaterialField"); var metal = Literal("MetalMaterialField");
            var partMap = Property(config, "RobotPartMap", Type.GetType("UgcRobotPartMap, GameCode", true)!);
            var partType = Type.GetType("ComposerPartType, GameCode", true)!;
            var resolve = RequireMethod(partMap.GetType(), "TryResolve", false, typeof(bool), typeof(string), partType.MakeByRefType(), typeof(GameObject).MakeByRefType());
            refusal = RobotComposedRefusal.BodyPartUnavailable;
            foreach (DictionaryEntry part in parts)
            {
                if (!(part.Key is string slot) || !(part.Value is string uri)) return false;
                // Preserve native symbolic materials unchanged; BuildEntity owns their interpretation.
                if (slot == plastic || slot == metal) continue;
                var names = Enum.GetNames(partType).Where(n => string.Equals(n, slot, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (names.Length != 1) return false;
                if (string.IsNullOrEmpty(uri)) continue;
                var args = new object?[] { uri, null, null };
                if (!(resolve.Invoke(partMap, args) is true) || !Equals(args[1], Enum.Parse(partType, names[0]))
                    || !(args[2] is GameObject prefab) || prefab == null || prefab.scene.IsValid()) return false;
            }
            refusal = RobotComposedRefusal.EntityReferencesShape;
            var agent = GameReflection.GetFieldValue(components, "agent");
            if (agent == null || agent.GetType() != Type.GetType("UgcAgentComponent, GameCode", true)!) return false;
            var voice = GameReflection.GetFieldValue(agent, "voiceset") as string;
            if (!string.IsNullOrEmpty(voice))
            {
                refusal = RobotComposedRefusal.VoiceUnavailable;
                var voiceMap = Property(config, "VoiceMap", Type.GetType("UgcVoiceMap, GameCode", true)!);
                var args = new object?[] { voice, null };
                var method = RequireMethod(voiceMap.GetType(), "TryResolve", false, typeof(bool), typeof(string), Type.GetType("OvertoneVoiceAsset, GameCode", true)!.MakeByRefType());
                if (!(method.Invoke(voiceMap, args) is true) || !(args[1] is UnityEngine.Object asset) || asset == null) return false;
            }
            var collector = Type.GetType("UgcCatalogLoreLoader, GameCode", true)!.GetMethod("CollectReferencedAssetUris",
                BindingFlags.NonPublic | BindingFlags.Static, null, new[] { project.GetType(), sceneMap.GetType() }, null);
            Require(collector != null && collector.ReturnType == typeof(IReadOnlyList<string>));
            var assets = collector!.Invoke(null, new[] { project, sceneMap }) as IReadOnlyList<string>;
            return RobotComposedReferences.AllAssetsResolved(assets, preparedCatalogAssets, out refusal);
        }

        private static object DeserializeProject(string json)
        {
            var settingsType = Type.GetType("Newtonsoft.Json.JsonSerializerSettings, Newtonsoft.Json", true)!;
            var settings = Activator.CreateInstance(settingsType)!;
            foreach (var pair in new[] { ("TypeNameHandling", "None"), ("MetadataPropertyHandling", "Ignore"), ("MissingMemberHandling", "Error") })
            {
                var property = settingsType.GetProperty(pair.Item1)!;
                property.SetValue(settings, Enum.Parse(property.PropertyType, pair.Item2));
            }
            settingsType.GetProperty("MaxDepth")!.SetValue(settings, (int?)16);
            var convert = Type.GetType("Newtonsoft.Json.JsonConvert, Newtonsoft.Json", true)!;
            return RequireMethod(convert, "DeserializeObject", true, typeof(object), typeof(string), typeof(Type), settingsType)
                .Invoke(null, new[] { json, (object)Type.GetType("UgcExportProject, GameCode", true)!, settings }) ?? throw new InvalidOperationException();
        }

        private static object Property(object instance, string name, Type type) =>
            ReadProperty(instance, name, type) ?? throw new InvalidOperationException();

        private static object? ReadProperty(object instance, string name, Type type)
        {
            var property = instance.GetType().GetProperty(name, PublicInstance);
            Require(property != null && property.PropertyType == type && property.GetIndexParameters().Length == 0);
            return property!.GetValue(instance);
        }
        private static MethodInfo RequireMethod(Type type, string name, bool isStatic, Type returns, params Type[] parameters)
        {
            var method = type.GetMethod(name, BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance), null, parameters, null);
            Require(method != null && method.ReturnType == returns);
            return method!;
        }
        private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Native composed robot context is unavailable."); }
    }
}
