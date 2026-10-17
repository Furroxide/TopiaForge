using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// The session channels in manager staging: session-handoff (game to launcher), session-join (launcher to game), and the
    /// multiplayer preferences projection (launcher to game) with its change journal (game to launcher). Sorted JSON, at
    /// most 64 KiB each. None carries an address.
    /// </summary>
    public static partial class LaunchTransportJson
    {
        public const int MaxChannelBytes = 64 * 1024;

        public static string WriteSessionHandoff(SessionHandoff handoff) => Bounded(Sorted(
            ("handoffId", Text(handoff.HandoffId)), ("requestId", Text(handoff.RequestId)),
            ("schemaVersion", Number(SessionHandoff.SchemaVersion)), ("target", HandoffTargetJson(handoff.Target))), MaxChannelBytes);

        public static SessionHandoff ReadSessionHandoff(string json) => Read(json, MaxChannelBytes, text =>
        {
            var value = new Fields(text, "handoffId", "requestId", "schemaVersion", "target");
            value.Version(SessionHandoff.SchemaVersion);
            var target = value.Required("target");
            var kind = new Fields(target, allowed: null).String("kind");
            var parsed = kind switch
            {
                "invite" => LaunchHandoffTarget.Invite(new Fields(target, "inviteCode", "kind").String("inviteCode")),
                "listing" => ListingTarget(new Fields(target, "kind", "serverId", "source")),
                "current" => CurrentTarget(new Fields(target, "kind")),
                _ => throw Invalid("Unknown handoff target kind."),
            };
            return new SessionHandoff(value.String("requestId"), value.String("handoffId"), parsed);
        });

        public static string WriteSessionJoin(SessionJoinRequest join) => Bounded(Sorted(
            ("joinId", Text(join.JoinId)), ("packageSetDigest", Text(join.PackageSetDigest)), ("requestId", Text(join.RequestId)),
            ("schemaVersion", Number(SessionJoinRequest.SchemaVersion)), ("session", SessionJson(join.Session))), MaxChannelBytes);

        public static SessionJoinRequest ReadSessionJoin(string json) => Read(json, MaxChannelBytes, text =>
        {
            var value = new Fields(text, "joinId", "packageSetDigest", "requestId", "schemaVersion", "session");
            value.Version(SessionJoinRequest.SchemaVersion);
            return new SessionJoinRequest(value.String("requestId"), value.String("joinId"), value.String("packageSetDigest"),
                ParseSession(value.Required("session")));
        });

        public static string WritePreferences(MultiplayerPreferencesProjection projection) => Bounded(Sorted(
            ("displayName", Text(projection.DisplayName)), ("hosting", HostingJson(projection.Hosting)),
            ("journalApplied", Number(projection.JournalApplied)),
            ("journalEpoch", projection.JournalEpoch == null ? null : Text(projection.JournalEpoch)),
            ("network", Sorted(("lan", Boolean(projection.LanDiscovery)), ("upnp", Boolean(projection.Upnp)))),
            ("privacy", Sorted(("disclosure", Number(projection.Disclosure)), ("mode", Text("relay-only")))),
            ("revision", Number(projection.Revision)), ("schemaVersion", Number(MultiplayerPreferencesProjection.SchemaVersion)),
            ("serviceOrigin", projection.ServiceOrigin == null ? null : Text(projection.ServiceOrigin)),
            ("voice", Sorted(("consent", Number(projection.VoiceConsent)), ("enabled", Boolean(projection.VoiceEnabled))))), MaxChannelBytes);

        public static MultiplayerPreferencesProjection ReadPreferences(string json) => Read(json, MaxChannelBytes, text =>
        {
            var value = new Fields(text, "displayName", "hosting", "journalApplied", "journalEpoch", "network", "privacy", "revision",
                "schemaVersion", "serviceOrigin", "voice");
            value.Version(MultiplayerPreferencesProjection.SchemaVersion);
            var privacy = new Fields(value.Required("privacy"), "disclosure", "mode");
            if (privacy.String("mode") != "relay-only") throw Invalid("Direct play is never a stored default.");
            var voice = new Fields(value.Required("voice"), "consent", "enabled");
            var network = new Fields(value.Required("network"), "lan", "upnp");
            return new MultiplayerPreferencesProjection(value.Integer("revision"), value.Integer("journalApplied"), value.String("displayName"),
                privacy.Integer("disclosure"), voice.Integer("consent"), voice.Bool("enabled"), network.Bool("lan"), network.Bool("upnp"),
                ParseHosting(value.Required("hosting")), value.OptionalString("serviceOrigin"), value.OptionalString("journalEpoch"));
        });

        public static string WritePreferencesJournal(MultiplayerPreferencesJournal journal) => Bounded(Sorted(
            ("changes", ArrayOf(journal.Changes.Select(ChangeJson))), ("epoch", Text(journal.Epoch)),
            ("schemaVersion", Number(MultiplayerPreferencesJournal.SchemaVersion))), MaxChannelBytes);

        public static MultiplayerPreferencesJournal ReadPreferencesJournal(string json) => Read(json, MaxChannelBytes, text =>
        {
            var value = new Fields(text, "changes", "epoch", "schemaVersion");
            value.Version(MultiplayerPreferencesJournal.SchemaVersion);
            var changes = Values(value.Required("changes"));
            if (changes.Count > MultiplayerPreferencesJournal.MaximumChanges) throw Invalid("The journal holds at most 64 changes.");
            return new MultiplayerPreferencesJournal(value.String("epoch"), changes.Select(ParseChange).ToArray());
        });

        private static string HandoffTargetJson(LaunchHandoffTarget target) => Sorted(
            ("inviteCode", target.InviteCode == null ? null : Text(target.InviteCode)), ("kind", Text(target.Kind)),
            ("serverId", target.ServerId == null ? null : Text(target.ServerId)), ("source", target.Source == null ? null : Text(target.Source)));

        private static LaunchHandoffTarget ListingTarget(Fields value) => LaunchHandoffTarget.Listing(value.String("serverId"), value.String("source"));

        private static LaunchHandoffTarget CurrentTarget(Fields value) => value.String("kind") == "current"
            ? LaunchHandoffTarget.Current() : throw Invalid("Unknown handoff target kind.");

        private static string HostingJson(MultiplayerHostingDefaults hosting) => Sorted(
            ("maxPlayers", Number(hosting.MaxPlayers)), ("name", Text(hosting.Name)), ("port", Number(hosting.Port)),
            ("visibility", Text(hosting.Visibility)), ("voice", Boolean(hosting.Voice)));

        private static MultiplayerHostingDefaults ParseHosting(string json)
        {
            var value = new Fields(json, "maxPlayers", "name", "port", "visibility", "voice");
            return new MultiplayerHostingDefaults(value.String("name"), value.Integer("maxPlayers"), value.String("visibility"),
                value.Integer("port"), value.Bool("voice"));
        }

        private static string ChangeJson(MultiplayerPreferenceChange change) => change.Kind switch
        {
            "displayName" => Sorted(("displayName", Text(change.NewDisplayName!)), ("sequence", Number(change.Sequence))),
            "disclosure" => Sorted(("disclosure", Number(change.NewDisclosure)), ("sequence", Number(change.Sequence))),
            "voice" => Sorted(("sequence", Number(change.Sequence)),
                ("voice", Sorted(("consent", Number(change.NewVoiceConsent)), ("enabled", Boolean(change.NewVoiceEnabled))))),
            "network" => Sorted(("network", Sorted(("lan", Boolean(change.NewLanDiscovery)), ("upnp", Boolean(change.NewUpnp)))),
                ("sequence", Number(change.Sequence))),
            _ => Sorted(("hosting", HostingJson(change.NewHosting!)), ("sequence", Number(change.Sequence))),
        };

        private static MultiplayerPreferenceChange ParseChange(string json)
        {
            var value = new Fields(json, allowed: null);
            var changed = value.Values.Keys.Where(key => key != "sequence").ToArray();
            if (changed.Length != 1 || !value.Has("sequence")) throw Invalid("A change sets exactly one preference.");
            var sequence = value.Integer("sequence");
            switch (changed[0])
            {
                case "displayName":
                    return MultiplayerPreferenceChange.DisplayName(sequence, value.String("displayName"));
                case "disclosure":
                    return MultiplayerPreferenceChange.Disclosure(sequence, value.Integer("disclosure"));
                case "voice":
                    var voice = new Fields(value.Required("voice"), "consent", "enabled");
                    return MultiplayerPreferenceChange.Voice(sequence, voice.Integer("consent"), voice.Bool("enabled"));
                case "network":
                    var network = new Fields(value.Required("network"), "lan", "upnp");
                    return MultiplayerPreferenceChange.Network(sequence, network.Bool("lan"), network.Bool("upnp"));
                case "hosting":
                    return MultiplayerPreferenceChange.Hosting(sequence, ParseHosting(value.Required("hosting")));
                default:
                    throw Invalid("Unknown preference change.");
            }
        }
    }
}
