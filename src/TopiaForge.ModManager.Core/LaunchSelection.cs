using System;
using System.Runtime.Serialization;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Versioned durable selection; unresolved legacy JSON remains available for explicit repair.</summary>
    [DataContract]
    public sealed class LaunchSelection
    {
        private LaunchSelection(string kind, LaunchRequest? request = null, string? legacyJson = null)
        {
            SchemaVersion = 1;
            Kind = kind;
            storedRequest = request == null ? null : new StoredRequest(request);
            legacy = legacyJson;
        }
        [DataMember(Name = "schemaVersion", Order = 0)] public int SchemaVersion { get; private set; }
        [DataMember(Name = "kind", Order = 1)] public string Kind { get; private set; }
        [DataMember(Name = "request", Order = 2, EmitDefaultValue = false)] private StoredRequest? storedRequest;
        private readonly string? legacy;
        public LaunchRequest? Request => storedRequest?.ToRequest();
        public string? LegacyJson => legacy;
        public static LaunchSelection MainMenu() => new LaunchSelection("main-menu");
        public static LaunchSelection Target(LaunchRequest request) => new LaunchSelection("target", request ?? throw new ArgumentNullException(nameof(request)));
        public static LaunchSelection UnresolvedLegacy(string json)
        {
            JsonObjectMerge.ValidateObject(json);
            return new LaunchSelection("unresolved-legacy", legacyJson: json);
        }
        [DataContract]
        private sealed class StoredRequest
        {
            internal StoredRequest(LaunchRequest request)
            { TargetId = request.TargetId; WorldOverride = request.WorldOverride; TransitionOverride = request.TransitionOverride; }
            [DataMember(Name = "targetId")] public string TargetId { get; private set; }
            [DataMember(Name = "worldOverride", EmitDefaultValue = false)] public string? WorldOverride { get; private set; }
            [DataMember(Name = "transitionOverride", EmitDefaultValue = false)] public string? TransitionOverride { get; private set; }
            internal LaunchRequest ToRequest() => new LaunchRequest(TargetId, WorldOverride, TransitionOverride);
        }

    }
    public static partial class LaunchTransportJson
    {
        public static string WriteSelection(LaunchSelection selection)
        {
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            var json = JsonUtil.Serialize(selection);
            if (selection.LegacyJson != null) json = JsonObjectMerge.Merge(json,
                new System.Collections.Generic.Dictionary<string, string> { ["legacy"] = selection.LegacyJson });
            return Bounded(json, MaxDocumentBytes);
        }
        public static LaunchSelection ReadSelection(string json) => Read(json, MaxDocumentBytes, ParseSelection);
        private static LaunchSelection ParseSelection(string json)
        {
            var value = new Fields(json, "schemaVersion", "kind", "request", "legacy");
            value.Version(1);
            switch (value.String("kind"))
            {
                case "main-menu":
                    if (value.Has("request") || value.Has("legacy")) throw Invalid("Main-menu selection cannot contain target or legacy data.");
                    return LaunchSelection.MainMenu();
                case "target":
                    if (value.Has("legacy")) throw Invalid("Target selection cannot contain legacy data.");
                    var request = new Fields(value.Required("request"), "targetId", "worldOverride", "transitionOverride");
                    return LaunchSelection.Target(new LaunchRequest(request.String("targetId"), request.OptionalString("worldOverride"), request.OptionalString("transitionOverride")));
                case "unresolved-legacy":
                    if (value.Has("request")) throw Invalid("Unresolved legacy selection cannot contain an inferred target.");
                    return LaunchSelection.UnresolvedLegacy(value.Required("legacy"));
                default: throw Invalid("Unknown durable launch selection kind.");
            }
        }
    }
}
