using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods.Testing
{
    /// <summary>A builder for deterministic authored-map tests, using the runtime's index and anchor rules.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class FakeAuthoredMap : IAuthoredMap, IDisposable
    {
        private readonly List<AuthoredRecord> records = new List<AuthoredRecord>();
        private readonly List<AuthoredMarker> markers = new List<AuthoredMarker>();
        private readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private AuthoredMapIndex? index;
        private bool disposed;
        /// <summary>Creates an empty synthetic map. Add parents before children, before reading the index.</summary>
        public FakeAuthoredMap(string mapId = "test-map", string sceneId = "scene", string sceneName = "Test map", string environment = "")
        {
            AuthoredText.RequireId(mapId, "mapId"); AuthoredText.RequireId(sceneId, "sceneId");
            AuthoredText.RequireText(sceneName, 128, "sceneName"); AuthoredText.RequireText(environment, 128, "environment");
            MapId = mapId; SceneId = sceneId; SceneName = sceneName; Environment = environment;
            Revision = new MapRevision(new string('0', 64), 1, string.Empty);
        }
        /// <summary>Adds an authored record and, optionally, its live marker; missing markers stay indexed.</summary>
        public FakeAuthoredMap AddEntity(string id, string name, string? modelId = null, MapTransform? editorTransform = null,
            string? parentId = null, AuthoredComponents components = AuthoredComponents.None, string? prefabUri = null,
            bool assetResolved = true, bool hasMarker = true)
        {
            if (index != null || disposed) throw new InvalidOperationException("Build the fake map before reading its index.");
            AuthoredText.RequireId(id, "id"); AuthoredText.RequireText(name, 128, "name");
            if (modelId != null) AuthoredText.RequireText(modelId, 256, "modelId");
            if (prefabUri != null) AuthoredText.RequireText(prefabUri, 512, "prefabUri");
            if (records.Any(r => r.Id == id) || parentId != null && !records.Any(r => r.Id == parentId))
                throw new ArgumentException("Entity ids must be unique and parents must be added first.");
            var value = editorTransform ?? MapTransform.Identity;
            records.Add(new AuthoredRecord(id, SceneId, name, parentId, modelId, prefabUri, value, components));
            if (hasMarker) markers.Add(new AuthoredMarker(id, new FakeEntity("fake:" + id, name, Position(value.Position))
            { Rotation = Convert(value).Rotation, Scale = value.Scale }, assetResolved));
            return this;
        }
        /// <summary>Supplies one package-relative anchors file without filesystem access.</summary>
        public FakeAuthoredMap WithAnchorFile(string packagePath, string json)
        {
            if (!AuthoredText.PackagePath(packagePath)) throw new ArgumentException("A package-relative path is required.");
            files[packagePath] = Encoding.UTF8.GetBytes(json ?? throw new ArgumentNullException(nameof(json)));
            return this;
        }
        /// <summary>Parses a synthetic project through the strict runtime reader and builds a live fake per record.</summary>
        public static FakeAuthoredMap FromProject(string json, string? sceneId = null, string mapId = "test-map")
        {
            var bytes = Encoding.UTF8.GetBytes(json ?? throw new ArgumentNullException(nameof(json)));
            var project = AuthoredProject.Read(bytes);
            if (sceneId == null && project.Scenes.Count != 1) throw new ArgumentException("Choose a scene for a multi-scene fixture.", nameof(sceneId));
            var key = sceneId ?? project.Scenes.Keys.Single();
            if (!project.Scenes.TryGetValue(key, out var scene)) throw new ArgumentException("The scene does not exist.", nameof(sceneId));
            var map = new FakeAuthoredMap(mapId, scene.Id, scene.Name, scene.Environment);
            using (var sha = SHA256.Create())
                map.Revision = new MapRevision(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant(), bytes.Length, project.Modified);
            foreach (var record in scene.Records)
                map.AddEntity(record.Id, record.Name, record.ModelId, record.Transform, record.ParentId, record.Components, record.PrefabUri);
            return map;
        }
        private AuthoredMapIndex Index => index ??= new AuthoredMapIndex(MapId,
            new AuthoredScene(SceneId, SceneName, Environment, records.AsReadOnly()), Revision, markers,
            Position, Convert, () => !disposed, path => files.TryGetValue(path, out var bytes)
                ? OperationResult<byte[]>.Success((byte[])bytes.Clone()) : OperationResult<byte[]>.Failure(ModErrorCode.NotFound, "Anchor file missing."));
        internal AuthoredMapIndex Inner => Index;
        private static Vec3 Position(Vec3 value) => new Vec3(-value.X, value.Y, value.Z);
        private static TransformState Convert(MapTransform value)
        {
            var rotation = value.QuaternionRotation ?? Euler(value.EulerDegrees ?? Vec3.Zero);
            return new TransformState(Position(value.Position), new Quat(rotation.X, -rotation.Y, -rotation.Z, rotation.W).Normalized, value.Scale);
        }
        private static Quat Euler(Vec3 degrees)
        {
            var x = degrees.X * Math.PI / 360; var y = degrees.Y * Math.PI / 360; var z = degrees.Z * Math.PI / 360;
            return new Quat(0, (float)Math.Sin(y), 0, (float)Math.Cos(y)) *
                new Quat((float)Math.Sin(x), 0, 0, (float)Math.Cos(x)) * new Quat(0, 0, (float)Math.Sin(z), (float)Math.Cos(z));
        }
        /// <inheritdoc/>
        public string MapId { get; }
        /// <inheritdoc/>
        public string SceneId { get; }
        /// <inheritdoc/>
        public string SceneName { get; }
        /// <inheritdoc/>
        public string Environment { get; }
        /// <inheritdoc/>
        public MapRevision Revision { get; private set; }
        /// <inheritdoc/>
        public bool IsAlive => !disposed;
        /// <inheritdoc/>
        public IReadOnlyList<AuthoredEntity> Entities => Index.Entities;
        /// <inheritdoc/>
        public bool TryGetEntity(string entityId, out AuthoredEntity? entity) => Index.TryGetEntity(entityId, out entity);
        /// <inheritdoc/>
        public AuthoredEntity Entity(string entityId) => Index.Entity(entityId);
        /// <inheritdoc/>
        public bool TryGetAuthored(IEntity entity, out AuthoredEntity? authored) => Index.TryGetAuthored(entity, out authored);
        /// <inheritdoc/>
        public IReadOnlyList<AuthoredEntity> FindByModel(string modelId) => Index.FindByModel(modelId);
        /// <inheritdoc/>
        public Vec3 ToWorld(Vec3 editorPosition) => Index.ToWorld(editorPosition);
        /// <inheritdoc/>
        public TransformState ToWorld(MapTransform editorTransform) => Index.ToWorld(editorTransform);
        /// <inheritdoc/>
        public MapAnchorResolution ResolveAnchors(string packagePath) => Index.ResolveAnchors(packagePath);
        /// <inheritdoc/>
        public MapAnchorResolution ResolveAnchors(IEnumerable<MapAnchor> anchors) => Index.ResolveAnchors(anchors);
        /// <summary>Ends the fake map's lifetime and destroys all its live fake entities.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; index?.Dispose();
            foreach (var marker in markers) (marker.Entity as FakeEntity)?.Destroy();
        }
    }
}
