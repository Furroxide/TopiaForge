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
        /// <summary>Converts a root-level editor position; use AuthoredEntity.WorldTransform for a parented record.</summary>
        Vec3 ToWorld(Vec3 editorPosition);
        /// <summary>Converts a root-level editor transform; no authored parent chain is inferred.</summary>
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
        private readonly AuthoredAffine worldTransform;
        internal AuthoredEntity(AuthoredRecord record, IEntity live, bool assetResolved, Func<bool> alive, AuthoredAffine worldTransform)
        {
            Id = record.Id; SceneId = record.SceneId; Name = record.Name; ParentId = record.ParentId;
            ModelId = record.ModelId; PrefabUri = record.PrefabUri; EditorTransform = record.Transform;
            Components = record.Components; this.live = live; this.alive = alive; AssetResolved = assetResolved;
            this.worldTransform = worldTransform;
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
        /// <summary>Gets the exact parent-local editor transform, or root-level editor transform when ParentId is null.</summary>
        public MapTransform EditorTransform { get; }
        /// <summary>Gets the imported authored world pose, including all authored parents and the import root, even without a live marker.</summary>
        /// <remarks>Captured at indexing; does not track later physics or root movement. Position uses the full affine chain.
        /// Rotation is the normalized product of converted quaternions. Scale is lossy basis length, with an odd reflection
        /// assigned to X; this TRS cannot represent shear. Use IEntityService.TryGetTransform for current live movement.</remarks>
        /// <exception cref="InvalidOperationException">The map's world session has ended, or the composed pose has a zero scale axis or non-finite values
        /// that TransformState cannot represent. Such authored records remain indexed; inspect EditorTransform and the parent chain.</exception>
        public TransformState WorldTransform => alive() ? worldTransform.Pose : throw new InvalidOperationException("This map's world session has ended.");
        /// <summary>Tries to get the same captured authored world pose as WorldTransform, even without a live marker.</summary>
        /// <param name="transform">The captured pose on success; Identity on failure, which must not be used for placement.</param>
        /// <returns>False after the map session ends or when collapsed scale or non-finite composed values prevent representation.</returns>
        /// <remarks>Does not track later physics or root movement. Unexpected lifetime callback failures propagate.</remarks>
        public bool TryGetWorldTransform(out TransformState transform)
        {
            transform = TransformState.Identity;
            return alive() && worldTransform.TryGetPose(out transform);
        }
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
