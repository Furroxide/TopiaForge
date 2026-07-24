using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TopiaForge.CreatorContent
{
    [DataContract]
    internal sealed class StoredProject
    {
        [DataMember(Name = "schemaVersion", Order = 1)] public int SchemaVersion { get; set; }
        [DataMember(Name = "id", Order = 2)] public string? Id { get; set; }
        [DataMember(Name = "displayName", Order = 3)] public string? DisplayName { get; set; }
        [DataMember(Name = "description", Order = 4)] public string? Description { get; set; }
        [DataMember(Name = "scope", Order = 5)] public int Scope { get; set; }
        [DataMember(Name = "worldId", Order = 6)] public string? WorldId { get; set; }
        [DataMember(Name = "sceneName", Order = 7)] public string? SceneName { get; set; }
        [DataMember(Name = "modifiedAtUtc", Order = 8)] public string? ModifiedAtUtc { get; set; }
        [DataMember(Name = "entities", Order = 9)] public List<StoredEntity>? Entities { get; set; }
        [DataMember(Name = "nativeBindings", Order = 10)] public List<StoredNativeBinding>? NativeBindings { get; set; }
        [DataMember(Name = "personas", Order = 11)] public List<StoredPersona>? Personas { get; set; }
        [DataMember(Name = "nodes", Order = 12)] public List<StoredNode>? Nodes { get; set; }
        [DataMember(Name = "edges", Order = 13)] public List<StoredEdge>? Edges { get; set; }
        [DataMember(Name = "origin", Order = 14)] public int Origin { get; set; }
    }

    [DataContract]
    internal sealed class StoredEntity
    {
        [DataMember(Name = "id", Order = 1)] public string? Id { get; set; }
        [DataMember(Name = "displayName", Order = 2)] public string? DisplayName { get; set; }
        [DataMember(Name = "contentId", Order = 3)] public string? ContentId { get; set; }
        [DataMember(Name = "expectedSourceVersion", Order = 4)] public string? ExpectedSourceVersion { get; set; }
        [DataMember(Name = "transform", Order = 5)] public StoredTransform? Transform { get; set; }
        [DataMember(Name = "spawnOnStart", Order = 6)] public bool SpawnOnStart { get; set; }
    }

    [DataContract]
    internal sealed class StoredNativeBinding
    {
        [DataMember(Name = "id", Order = 1)] public string? Id { get; set; }
        [DataMember(Name = "displayName", Order = 2)] public string? DisplayName { get; set; }
        [DataMember(Name = "sceneName", Order = 3)] public string? SceneName { get; set; }
        [DataMember(Name = "nameContains", Order = 4)] public string? NameContains { get; set; }
        [DataMember(Name = "expectedPosition", Order = 5)] public StoredVector3? ExpectedPosition { get; set; }
        [DataMember(Name = "searchRadius", Order = 6)] public float SearchRadius { get; set; }
        [DataMember(Name = "adapterId", Order = 7)] public string? AdapterId { get; set; }
    }

    [DataContract]
    internal sealed class StoredPersona
    {
        [DataMember(Name = "id", Order = 1)] public string? Id { get; set; }
        [DataMember(Name = "displayName", Order = 2)] public string? DisplayName { get; set; }
        [DataMember(Name = "systemFrame", Order = 3)] public string? SystemFrame { get; set; }
        [DataMember(Name = "replyGuidance", Order = 4)] public string? ReplyGuidance { get; set; }
        [DataMember(Name = "facts", Order = 5)] public Dictionary<string, string>? Facts { get; set; }
    }

    [DataContract]
    internal sealed class StoredNode
    {
        [DataMember(Name = "id", Order = 1)] public string? Id { get; set; }
        [DataMember(Name = "kind", Order = 2)] public int Kind { get; set; }
        [DataMember(Name = "x", Order = 3)] public float X { get; set; }
        [DataMember(Name = "y", Order = 4)] public float Y { get; set; }
        [DataMember(Name = "parameters", Order = 5)] public Dictionary<string, string>? Parameters { get; set; }
    }

    [DataContract]
    internal sealed class StoredEdge
    {
        [DataMember(Name = "fromNodeId", Order = 1)] public string? FromNodeId { get; set; }
        [DataMember(Name = "fromPort", Order = 2)] public string? FromPort { get; set; }
        [DataMember(Name = "toNodeId", Order = 3)] public string? ToNodeId { get; set; }
        [DataMember(Name = "toPort", Order = 4)] public string? ToPort { get; set; }
    }

    [DataContract]
    internal sealed class StoredTransform
    {
        [DataMember(Name = "position", Order = 1)] public StoredVector3? Position { get; set; }
        [DataMember(Name = "rotation", Order = 2)] public StoredQuaternion? Rotation { get; set; }
        [DataMember(Name = "scale", Order = 3)] public StoredVector3? Scale { get; set; }
    }

    [DataContract]
    internal sealed class StoredVector3
    {
        [DataMember(Name = "x", Order = 1)] public float X { get; set; }
        [DataMember(Name = "y", Order = 2)] public float Y { get; set; }
        [DataMember(Name = "z", Order = 3)] public float Z { get; set; }
    }

    [DataContract]
    internal sealed class StoredQuaternion
    {
        [DataMember(Name = "x", Order = 1)] public float X { get; set; }
        [DataMember(Name = "y", Order = 2)] public float Y { get; set; }
        [DataMember(Name = "z", Order = 3)] public float Z { get; set; }
        [DataMember(Name = "w", Order = 4)] public float W { get; set; }
    }

    [DataContract]
    internal sealed class StoredProjectIndex
    {
        [DataMember(Name = "schemaVersion", Order = 1)] public int SchemaVersion { get; set; }
        [DataMember(Name = "projects", Order = 2)] public List<StoredProjectSummary>? Projects { get; set; }
    }

    [DataContract]
    internal sealed class StoredProjectSummary
    {
        [DataMember(Name = "id", Order = 1)] public string? Id { get; set; }
        [DataMember(Name = "displayName", Order = 2)] public string? DisplayName { get; set; }
        [DataMember(Name = "scope", Order = 3)] public int Scope { get; set; }
        [DataMember(Name = "modifiedAtUtc", Order = 4)] public string? ModifiedAtUtc { get; set; }
    }
}
