using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.Mods.Internal
{
    internal sealed class AuthoredMarker
    {
        internal AuthoredMarker(string id, IEntity entity, bool assetResolved)
        { Id = id; Entity = entity; AssetResolved = assetResolved; }
        internal string Id { get; }
        internal IEntity Entity { get; }
        internal bool AssetResolved { get; }
    }

    internal sealed class DeadAuthoredEntity : IEntity
    {
        internal static readonly DeadAuthoredEntity Instance = new DeadAuthoredEntity();
        public string Id => string.Empty;
        public string Name => string.Empty;
        public bool IsAlive => false;
        public Vec3 Position => Vec3.Zero;
    }

    internal sealed partial class AuthoredMapIndex : IAuthoredMap, IDisposable
    {
        private sealed class Life { internal bool Alive = true; }
        private readonly Life life;
        private readonly Dictionary<string, AuthoredEntity> entities;
        private readonly Dictionary<string, AuthoredEntity> live;
        private readonly Dictionary<string, IReadOnlyList<AuthoredEntity>> models;
        private readonly Func<Vec3, Vec3> position;
        private readonly Func<MapTransform, TransformState> transform;
        private readonly Func<bool> nativeAlive;
        private readonly Func<string, OperationResult<byte[]>>? readAnchors;
        internal AuthoredMapIndex(string mapId, AuthoredScene scene, MapRevision revision, IEnumerable<AuthoredMarker> markers,
            Func<Vec3, Vec3> position, Func<MapTransform, TransformState> transform, Func<bool>? nativeAlive = null,
            Func<string, OperationResult<byte[]>>? readAnchors = null)
        {
            if (!(AuthoredText.IsId(mapId) || mapId.StartsWith("file:", StringComparison.Ordinal) && AuthoredText.PackagePath(mapId.Substring(5))))
                throw new ArgumentException("A map id or package-relative file identity is required.", nameof(mapId));
            MapId = mapId; SceneId = scene.Id; SceneName = scene.Name; Environment = scene.Environment; Revision = revision;
            this.position = position; this.transform = transform; this.nativeAlive = nativeAlive ?? (() => true);
            this.readAnchors = readAnchors; life = new Life();
            entities = new Dictionary<string, AuthoredEntity>(StringComparer.Ordinal);
            live = new Dictionary<string, AuthoredEntity>(StringComparer.Ordinal);
            var joined = new Dictionary<string, AuthoredMarker>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var marker in markers)
            {
                if (!AuthoredText.IsId(marker.Id) || marker.Entity == null) { UnknownMarkers++; continue; }
                if (joined.ContainsKey(marker.Id)) duplicates.Add(marker.Id); else joined.Add(marker.Id, marker);
            }
            DuplicateMarkers = duplicates.Count;
            var ordered = new List<AuthoredEntity>();
            foreach (var record in scene.Records)
            {
                joined.TryGetValue(record.Id, out var marker);
                var usable = marker != null && !duplicates.Contains(record.Id) && marker.Entity.IsAlive;
                if (!usable) MissingMarkers++;
                var entity = new AuthoredEntity(record, usable ? marker!.Entity : DeadAuthoredEntity.Instance,
                    usable && marker!.AssetResolved, () => IsAlive);
                entities.Add(record.Id, entity); ordered.Add(entity);
                if (usable && !live.ContainsKey(marker!.Entity.Id)) live.Add(marker.Entity.Id, entity);
            }
            UnknownMarkers += joined.Keys.Count(id => !entities.ContainsKey(id));
            Entities = ordered.AsReadOnly();
            models = ordered.Where(e => e.ModelId != null).GroupBy(e => e.ModelId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<AuthoredEntity>)g.ToList().AsReadOnly(), StringComparer.Ordinal);
        }
        private AuthoredMapIndex(AuthoredMapIndex source, Func<string, OperationResult<byte[]>> reader)
        {
            MapId = source.MapId; SceneId = source.SceneId; SceneName = source.SceneName; Environment = source.Environment;
            Revision = source.Revision; Entities = source.Entities; life = source.life; entities = source.entities; live = source.live;
            models = source.models; position = source.position; transform = source.transform; nativeAlive = source.nativeAlive; readAnchors = reader; ViewSource = source.ViewSource;
            MissingMarkers = source.MissingMarkers; UnknownMarkers = source.UnknownMarkers; DuplicateMarkers = source.DuplicateMarkers;
        }
        internal IInternalMapViewSource? ViewSource { get; set; }
        internal AuthoredMapIndex ForPackage(Func<string, OperationResult<byte[]>> reader) => new AuthoredMapIndex(this, reader);
        internal int MissingMarkers { get; }
        internal int UnknownMarkers { get; }
        internal int DuplicateMarkers { get; }
        public string MapId { get; }
        public string SceneId { get; }
        public string SceneName { get; }
        public string Environment { get; }
        public MapRevision Revision { get; }
        public bool IsAlive => life.Alive && nativeAlive();
        public IReadOnlyList<AuthoredEntity> Entities { get; }
        public bool TryGetEntity(string entityId, out AuthoredEntity? entity)
        {
            entity = null;
            return IsAlive && entityId != null && entities.TryGetValue(entityId, out entity);
        }
        public AuthoredEntity Entity(string entityId)
        {
            AuthoredText.RequireId(entityId, "entityId");
            RequireAlive();
            if (TryGetEntity(entityId, out var entity)) return entity!;
            var similar = entities.Keys.OrderByDescending(id => Prefix(id, entityId)).ThenBy(id => id, StringComparer.Ordinal).Take(3);
            throw new InvalidOperationException("The map has no entity '" + entityId + "'. Similar ids: " + string.Join(", ", similar) +
                ". Re-survey with topiaforge world survey.");
        }
        private static int Prefix(string left, string right)
        { var i = 0; while (i < left.Length && i < right.Length && left[i] == right[i]) i++; return i; }
        public bool TryGetAuthored(IEntity entity, out AuthoredEntity? authored)
        {
            authored = null;
            if (!IsAlive || entity == null || !entity.IsAlive || !live.TryGetValue(entity.Id, out var found) || !ReferenceEquals(found.Entity, entity)) return false;
            authored = found; return true;
        }
        public IReadOnlyList<AuthoredEntity> FindByModel(string modelId) => IsAlive && modelId != null && models.TryGetValue(modelId, out var found)
            ? found : Array.Empty<AuthoredEntity>();
        public Vec3 ToWorld(Vec3 editorPosition)
        { RequireAlive(); if (!editorPosition.IsFinite) throw new ArgumentException("Editor position must be finite."); return position(editorPosition); }
        public TransformState ToWorld(MapTransform editorTransform)
        {
            RequireAlive();
            if (!editorTransform.Position.IsFinite || !editorTransform.Scale.IsFinite ||
                !(editorTransform.EulerDegrees.HasValue || editorTransform.QuaternionRotation.HasValue)) throw new ArgumentException("An editor transform is required.");
            return transform(editorTransform);
        }
        private void RequireAlive() { if (!IsAlive) throw new InvalidOperationException("This map's world session has ended."); }
        public void Dispose() { life.Alive = false; }
    }
}
