using System;
using System.Collections.Generic;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods
{
    /// <summary>An imported map scene, valid while its world session owns the content.</summary>
    [RequiresTopiaForge("0.2.0")]
    public interface IAuthoredMap
    {
        /// <summary>Gets the published map id, or file: followed by its package-relative path.</summary>
        string MapId { get; }
        /// <summary>Gets the authored scene id.</summary>
        string SceneId { get; }
        /// <summary>Gets the scene's bounded, untrusted plain-text name.</summary>
        string SceneName { get; }
        /// <summary>Gets the authored environment preset key.</summary>
        string Environment { get; }
        /// <summary>Gets the exact imported content identity.</summary>
        MapRevision Revision { get; }
        /// <summary>Gets whether the session still owns this map.</summary>
        bool IsAlive { get; }
        /// <summary>Gets all authored records, parents before children, including records without live markers.</summary>
        IReadOnlyList<AuthoredEntity> Entities { get; }
        /// <summary>Finds an authored record by id; returns false after teardown.</summary>
        bool TryGetEntity(string entityId, out AuthoredEntity? entity);
        /// <summary>Gets a record or throws with similar ids and a survey remedy.</summary>
        AuthoredEntity Entity(string entityId);
        /// <summary>Finds the authored record behind a live interaction or raycast entity.</summary>
        bool TryGetAuthored(IEntity entity, out AuthoredEntity? authored);
        /// <summary>Gets authored records using the given model id.</summary>
        IReadOnlyList<AuthoredEntity> FindByModel(string modelId);
        /// <summary>Converts an editor position through the game's coordinate adapter.</summary>
        Vec3 ToWorld(Vec3 editorPosition);
        /// <summary>Converts an editor transform through the game's coordinate adapter.</summary>
        TransformState ToWorld(MapTransform editorTransform);
        /// <summary>Resolves a bounded anchors file from the consuming gamemode's package.</summary>
        MapAnchorResolution ResolveAnchors(string packagePath);
        /// <summary>Resolves anchors by id, then by an unambiguous name, model and nearby position.</summary>
        MapAnchorResolution ResolveAnchors(IEnumerable<MapAnchor> anchors);
    }

    /// <summary>An authored record and the live entity built for it; names are plain text, never rich text.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class AuthoredEntity
    {
        private readonly IEntity live;
        private readonly Func<bool> alive;
        internal AuthoredEntity(AuthoredRecord record, IEntity live, bool assetResolved, Func<bool> alive)
        {
            Id = record.Id; SceneId = record.SceneId; Name = record.Name; ParentId = record.ParentId;
            ModelId = record.ModelId; PrefabUri = record.PrefabUri; EditorTransform = record.Transform;
            Components = record.Components; this.live = live; this.alive = alive; AssetResolved = assetResolved;
        }
        /// <summary>Gets the authored id, bounded to 64 ASCII characters.</summary>
        public string Id { get; }
        /// <summary>Gets the authored scene id.</summary>
        public string SceneId { get; }
        /// <summary>Gets the untrusted plain-text name, at most 128 characters.</summary>
        public string Name { get; }
        /// <summary>Gets the parent id, or null at the scene root.</summary>
        public string? ParentId { get; }
        /// <summary>Gets the model id, or null when none was authored.</summary>
        public string? ModelId { get; }
        /// <summary>Gets the authored prefab URI, or null.</summary>
        public string? PrefabUri { get; }
        /// <summary>Gets the exact editor transform.</summary>
        public MapTransform EditorTransform { get; }
        /// <summary>Gets the authored component flags.</summary>
        public AuthoredComponents Components { get; }
        /// <summary>Gets the live handle, or a dead handle for a missing marker or ended session.</summary>
        public IEntity Entity => alive() ? live : DeadAuthoredEntity.Instance;
        /// <summary>Gets whether the importer resolved the authored asset instead of using a placeholder.</summary>
        public bool AssetResolved { get; }
    }

    /// <summary>Access to a map world's authored content with a useful refusal for other worlds.</summary>
    [RequiresTopiaForge("0.2.0")]
    public static class WorldReadinessExtensions
    {
        /// <summary>Gets this session's map, or explains how to select a map world.</summary>
        public static IAuthoredMap RequireMap(this WorldReadiness world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            return world.Map ?? throw new InvalidOperationException(
                "This session's world is not a Robotopia map. Give the launch target a map world: topiaforge world add map <link>.");
        }
    }
}
