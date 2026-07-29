using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal static class CreatorProjectCodec
    {
        public static string EncodeProject(CreatorEventProject project) => Serialize(ToStored(project));

        public static CreatorEventProject DecodeProject(string json)
        {
            var stored = Deserialize<StoredProject>(json);
            return new CreatorEventProject(
                stored.SchemaVersion,
                Required(stored.Id, "project id"),
                Required(stored.DisplayName, "project display name"),
                stored.Description ?? string.Empty,
                (CreatorProjectScope)stored.Scope,
                stored.WorldId ?? string.Empty,
                stored.SceneName ?? string.Empty,
                ParseDate(stored.ModifiedAtUtc),
                (stored.Entities ?? new List<StoredEntity>()).Select(FromStored),
                (stored.NativeBindings ?? new List<StoredNativeBinding>()).Select(FromStored),
                (stored.Personas ?? new List<StoredPersona>()).Select(FromStored),
                (stored.Nodes ?? new List<StoredNode>()).Select(FromStored),
                (stored.Edges ?? new List<StoredEdge>()).Select(FromStored),
                (CreatorProjectOrigin)stored.Origin);
        }

        public static string EncodeIndex(IEnumerable<CreatorProjectSummary> projects)
        {
            return Serialize(new StoredProjectIndex
            {
                SchemaVersion = CreatorProjectValidator.CurrentSchemaVersion,
                Projects = projects.Select(project => new StoredProjectSummary
                {
                    Id = project.Id,
                    DisplayName = project.DisplayName,
                    Scope = (int)project.Scope,
                    ModifiedAtUtc = Date(project.ModifiedAtUtc)
                }).ToList()
            });
        }

        public static IReadOnlyList<CreatorProjectSummary> DecodeIndex(string json)
        {
            var index = Deserialize<StoredProjectIndex>(json);
            if (index.SchemaVersion != CreatorProjectValidator.CurrentSchemaVersion)
            {
                throw new InvalidDataException("Unsupported creator project index schema.");
            }
            return (index.Projects ?? new List<StoredProjectSummary>())
                .Select(project => new CreatorProjectSummary(
                    Required(project.Id, "project id"),
                    Required(project.DisplayName, "project display name"),
                    (CreatorProjectScope)project.Scope,
                    ParseDate(project.ModifiedAtUtc)))
                .ToArray();
        }

        private static StoredProject ToStored(CreatorEventProject project)
        {
            return new StoredProject
            {
                SchemaVersion = project.SchemaVersion,
                Id = project.Id,
                DisplayName = project.DisplayName,
                Description = project.Description,
                Scope = (int)project.Scope,
                WorldId = project.WorldId,
                SceneName = project.SceneName,
                ModifiedAtUtc = Date(project.ModifiedAtUtc),
                Origin = (int)project.Origin,
                Entities = project.Entities.Select(entity => new StoredEntity
                {
                    Id = entity.Id,
                    DisplayName = entity.DisplayName,
                    ContentId = entity.ContentId,
                    ExpectedSourceVersion = entity.ExpectedSourceVersion,
                    Transform = ToStored(entity.Transform),
                    SpawnOnStart = entity.SpawnOnStart
                }).ToList(),
                NativeBindings = project.NativeBindings.Select(binding => new StoredNativeBinding
                {
                    Id = binding.Id,
                    DisplayName = binding.DisplayName,
                    SceneName = binding.SceneName,
                    NameContains = binding.NameContains,
                    ExpectedPosition = ToStored(binding.ExpectedPosition),
                    SearchRadius = binding.SearchRadius,
                    AdapterId = binding.AdapterId
                }).ToList(),
                Personas = project.Personas.Select(persona => new StoredPersona
                {
                    Id = persona.Id,
                    DisplayName = persona.DisplayName,
                    SystemFrame = persona.SystemFrame,
                    ReplyGuidance = persona.ReplyGuidance,
                    Facts = new Dictionary<string, string>(persona.Facts, StringComparer.Ordinal)
                }).ToList(),
                Nodes = project.Nodes.Select(node => new StoredNode
                {
                    Id = node.Id,
                    Kind = (int)node.Kind,
                    X = node.EditorPosition.X,
                    Y = node.EditorPosition.Y,
                    Parameters = new Dictionary<string, string>(node.Parameters, StringComparer.Ordinal)
                }).ToList(),
                Edges = project.Edges.Select(edge => new StoredEdge
                {
                    FromNodeId = edge.FromNodeId,
                    FromPort = edge.FromPort,
                    ToNodeId = edge.ToNodeId,
                    ToPort = edge.ToPort
                }).ToList()
            };
        }

        private static CreatorProjectEntity FromStored(StoredEntity entity) => new CreatorProjectEntity(
            Required(entity.Id, "entity id"),
            Required(entity.DisplayName, "entity display name"),
            Required(entity.ContentId, "entity content id"),
            entity.ExpectedSourceVersion ?? string.Empty,
            FromStored(entity.Transform),
            entity.SpawnOnStart);

        private static CreatorNativeBinding FromStored(StoredNativeBinding binding) => new CreatorNativeBinding(
            Required(binding.Id, "native binding id"),
            Required(binding.DisplayName, "native binding display name"),
            Required(binding.SceneName, "native binding scene"),
            Required(binding.NameContains, "native binding name fragment"),
            FromStored(binding.ExpectedPosition),
            binding.SearchRadius,
            binding.AdapterId ?? string.Empty);

        private static CreatorPersona FromStored(StoredPersona persona) => new CreatorPersona(
            Required(persona.Id, "persona id"),
            Required(persona.DisplayName, "persona display name"),
            persona.SystemFrame ?? string.Empty,
            persona.ReplyGuidance ?? string.Empty,
            persona.Facts ?? new Dictionary<string, string>(StringComparer.Ordinal));

        private static CreatorGraphNode FromStored(StoredNode node) => new CreatorGraphNode(
            Required(node.Id, "node id"),
            (CreatorGraphNodeKind)node.Kind,
            new Vec2(node.X, node.Y),
            node.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal));

        private static CreatorGraphEdge FromStored(StoredEdge edge) => new CreatorGraphEdge(
            Required(edge.FromNodeId, "edge source node"),
            Required(edge.FromPort, "edge source port"),
            Required(edge.ToNodeId, "edge target node"),
            Required(edge.ToPort, "edge target port"));

        private static StoredTransform ToStored(TransformState transform) => new StoredTransform
        {
            Position = ToStored(transform.Position),
            Rotation = new StoredQuaternion
            {
                X = transform.Rotation.X,
                Y = transform.Rotation.Y,
                Z = transform.Rotation.Z,
                W = transform.Rotation.W
            },
            Scale = ToStored(transform.Scale)
        };

        private static StoredVector3 ToStored(Vec3 vector) => new StoredVector3
        {
            X = vector.X,
            Y = vector.Y,
            Z = vector.Z
        };

        private static TransformState FromStored(StoredTransform? transform)
        {
            if (transform?.Position == null || transform.Rotation == null || transform.Scale == null)
            {
                throw new InvalidDataException("A complete transform is required.");
            }
            return new TransformState(
                FromStored(transform.Position),
                new Quat(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W),
                FromStored(transform.Scale));
        }

        private static Vec3 FromStored(StoredVector3? vector)
        {
            if (vector == null) throw new InvalidDataException("A vector is required.");
            return new Vec3(vector.X, vector.Y, vector.Z);
        }

        private static string Serialize<T>(T value)
        {
            using var stream = new MemoryStream();
            Serializer<T>().WriteObject(stream, value);
            return new UTF8Encoding(false, true).GetString(stream.ToArray());
        }

        private static T Deserialize<T>(string json) where T : class
        {
            using var stream = new MemoryStream(new UTF8Encoding(false, true).GetBytes(json ?? string.Empty));
            return Serializer<T>().ReadObject(stream) as T
                ?? throw new InvalidDataException("The creator project document was empty.");
        }

        private static DataContractJsonSerializer Serializer<T>() => new DataContractJsonSerializer(
            typeof(T),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        private static string Required(string? value, string label) =>
            !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("Missing " + label + ".");

        private static string Date(DateTimeOffset value) => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        private static DateTimeOffset ParseDate(string? value) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : throw new InvalidDataException("The project timestamp is invalid.");
    }
}
