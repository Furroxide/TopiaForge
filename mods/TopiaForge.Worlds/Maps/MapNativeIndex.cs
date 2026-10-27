using System;
using System.Collections.Generic;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.Worlds.Maps
{
    // The importer-marker join is isolated until native qualification confirms prefab expansion behavior.
    internal static class MapNativeIndex
    {
        internal static AuthoredMapIndex Create(Transform? root, MapDocument document, string sceneId, string mapId, IModContext context)
        {
            if (root == null || !(context is IInternalAuthoredMapContext bridge)) throw Unavailable();
            var type = Type.GetType("UgcImportedEntity, GameCode", false);
            var id = type?.GetProperty("EntityId", BindingFlags.Public | BindingFlags.Instance);
            var source = type?.GetProperty("SourceAssetId", BindingFlags.Public | BindingFlags.Instance);
            var unresolved = type?.GetProperty("UnresolvedAsset", BindingFlags.Public | BindingFlags.Instance);
            if (type == null || id?.PropertyType != typeof(string) || source?.PropertyType != typeof(string) || unresolved?.PropertyType != typeof(bool))
                throw Unavailable();
            var components = root.GetComponentsInChildren(type, true);
            var token = Guid.NewGuid().ToString("N");
            // Install identity anchors first so a child collider and its authored parent receive the same SDK identity.
            foreach (var component in components)
            {
                var entityId = id.GetValue(component) as string;
                if (!AuthoredText.IsId(entityId)) continue;
                var anchor = component.gameObject.GetComponent<MapEntityIdentity>() ?? component.gameObject.AddComponent<MapEntityIdentity>();
                anchor.Identity = "map:" + token + ":" + entityId;
            }
            var markers = new List<AuthoredMarker>();
            foreach (var component in components)
            {
                var entityId = id.GetValue(component) as string ?? string.Empty;
                _ = source.GetValue(component) as string;
                var wrapped = bridge.WrapMapEntity(component.gameObject);
                if (wrapped.TryGetValue(out var entity)) markers.Add(new AuthoredMarker(entityId, entity, !(bool)unresolved.GetValue(component)!));
            }
            var conversion = new MapNativeCoordinates(root);
            var map = new AuthoredMapIndex(mapId, document.Authored.Scenes[sceneId],
                new MapRevision(document.Sha256, document.Packed.Length, document.Modified), markers,
                conversion.Position, conversion.Transform, () => root != null, bridge.ReadAnchorFile);
            context.Logger.Info("Map index: records=" + map.Entities.Count + "; missing markers=" + map.MissingMarkers +
                "; unknown markers=" + map.UnknownMarkers + "; duplicate marker ids=" + map.DuplicateMarkers + ".");
            return map;
        }
        private static MapProblem Unavailable() => new MapProblem(ModErrorCode.Unavailable,
            "This game build cannot index authored map entities yet. Update TopiaForge.");
    }

    internal sealed class MapEntityIdentity : MonoBehaviour, IRuntimeEntityIdentityAnchor
    {
        internal string Identity = string.Empty;
        public string RuntimeEntityId => Identity;
    }
}
