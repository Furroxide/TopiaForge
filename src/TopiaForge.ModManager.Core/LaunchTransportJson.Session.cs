using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Session launches: request V5 and the progress and outcome V2 blocks. These documents are written as sorted JSON,
    /// every object's keys in ordinal order with no white space and only the minimal string escapes, so the game, the
    /// launcher and the fixture generator write the same bytes. Readers take any member order and spacing, but refuse
    /// unknown or repeated members, nulls and wrong types, as the V4 readers do.
    /// </summary>
    public static partial class LaunchTransportJson
    {
        public static string WriteProfileV5(ProfileLaunchConfigurationV5 profile) => Bounded(ProfileV5Json(profile), MaxDocumentBytes);
        public static ProfileLaunchConfigurationV5 ReadProfileV5(string json) => Read(json, MaxDocumentBytes, ParseProfileV5);

        /// <summary>Reads a one-shot request of either version, as the runtime consumes it.</summary>
        public static ConsumedLaunchRequest ReadLaunchRequest(string json) => Read(json, MaxDocumentBytes, text =>
        {
            var version = new Fields(text, allowed: null).Integer("schemaVersion");
            if (version != ProfileLaunchConfigurationV5.SchemaVersion) return new ConsumedLaunchRequest(ParseProfile(text), null);
            var session = ParseProfileV5(text);
            return new ConsumedLaunchRequest(session.Executable, session);
        });

        private static string ProfileV5Json(ProfileLaunchConfigurationV5 profile) => Sorted(
            ("command", Text(profile.Command)), ("digest", Text(profile.Digest)),
            ("enabledMods", ArrayOf(profile.EnabledMods.Select(Text))), ("inheritManagerModState", Boolean(profile.InheritManagerModState)),
            ("packages", ArrayOf(profile.Packages.Select(SortedPackage))), ("plan", profile.Plan == null ? null : SortedPlan(profile.Plan)),
            ("profileId", Text(profile.ProfileId)), ("profileRevision", Number(profile.ProfileRevision)), ("requestId", Text(profile.RequestId)),
            ("schemaVersion", Number(ProfileLaunchConfigurationV5.SchemaVersion)),
            ("selectedVersions", Sorted(profile.SelectedVersions.Select(pair => (pair.Key, (string?)Text(pair.Value))).ToArray())),
            ("session", SessionJson(profile.Session)));

        private static ProfileLaunchConfigurationV5 ParseProfileV5(string json)
        {
            var value = new Fields(json, "command", "digest", "enabledMods", "inheritManagerModState", "packages", "plan", "profileId",
                "profileRevision", "requestId", "schemaVersion", "selectedVersions", "session");
            value.Version(ProfileLaunchConfigurationV5.SchemaVersion);
            var versions = new Fields(value.Required("selectedVersions"), allowed: null);
            return new ProfileLaunchConfigurationV5(value.String("profileId"), value.Integer("profileRevision"), value.String("requestId"),
                value.String("command"), ParsePackages(value.Required("packages")), value.String("digest"), value.Bool("inheritManagerModState"),
                Strings(value.Required("enabledMods")), versions.Values.ToDictionary(pair => pair.Key, pair => String(pair.Value), StringComparer.Ordinal),
                ParseSession(value.Required("session")), value.Has("plan") ? ParsePlan(value.Required("plan")) : null);
        }

        internal static string SessionJson(LaunchSessionRequest session)
        {
            var transport = session.Transport;
            var consent = transport.DirectConsent;
            var host = session.Host;
            return Sorted(
                ("devInstance", session.DevInstance == null ? null
                    : Sorted(("count", Number(session.DevInstance.Count)), ("index", Number(session.DevInstance.Index)))),
                ("developerMode", Boolean(session.DeveloperMode)), ("displayName", Text(session.DisplayName)),
                ("host", host == null ? null : Sorted(("maxPlayers", Number(host.MaxPlayers)), ("name", Text(host.Name)),
                    ("passwordRequired", Boolean(host.PasswordRequired)), ("servePackages", Boolean(host.ServePackages)),
                    ("visibility", Text(host.Visibility)), ("voice", Boolean(host.Voice)))),
                ("intentId", Text(session.IntentId)), ("role", Text(session.Role)),
                ("serviceOrigin", session.ServiceOrigin == null ? null : Text(session.ServiceOrigin)),
                ("target", session.Target == null ? null : TargetJson(session.Target)),
                ("transport", Sorted(
                    ("directConsent", consent == null ? null : Sorted(("disclosure", Number(consent.Disclosure)), ("intentId", Text(consent.IntentId)))),
                    ("lan", Boolean(transport.Lan)), ("port", Number(transport.Port)), ("privacy", Text(transport.Privacy)),
                    ("upnp", Boolean(transport.Upnp)))));
        }

        private static string TargetJson(LaunchSessionTarget target) => Sorted(
            ("descriptorSha256", Text(target.DescriptorSha256)), ("host", target.Host == null ? null : Text(target.Host)),
            ("inviteCode", target.InviteCode == null ? null : Text(target.InviteCode)), ("kind", Text(target.Kind)),
            ("packageDigest", Text(target.PackageDigest)), ("port", target.Kind == "direct" ? Number(target.Port) : null),
            ("serverId", Text(target.ServerId)), ("source", target.Source == null ? null : Text(target.Source)));

        internal static LaunchSessionRequest ParseSession(string json)
        {
            var value = new Fields(json, "devInstance", "developerMode", "displayName", "host", "intentId", "role", "serviceOrigin", "target",
                "transport");
            var transport = new Fields(value.Required("transport"), "directConsent", "lan", "port", "privacy", "upnp");
            LaunchDirectConsent? consent = null;
            if (transport.Has("directConsent"))
            {
                var given = new Fields(transport.Required("directConsent"), "disclosure", "intentId");
                consent = new LaunchDirectConsent(given.String("intentId"), given.Integer("disclosure"));
            }

            LaunchHostOptions? host = null;
            if (value.Has("host"))
            {
                var options = new Fields(value.Required("host"), "maxPlayers", "name", "passwordRequired", "servePackages", "visibility", "voice");
                host = new LaunchHostOptions(options.String("name"), options.Integer("maxPlayers"), options.String("visibility"),
                    options.Bool("voice"), options.Bool("passwordRequired"), options.Bool("servePackages"));
            }

            LaunchDevInstance? peer = null;
            if (value.Has("devInstance"))
            {
                var slot = new Fields(value.Required("devInstance"), "count", "index");
                peer = new LaunchDevInstance(slot.Integer("index"), slot.Integer("count"));
            }

            return new LaunchSessionRequest(value.String("role"), value.String("intentId"), value.String("displayName"),
                new LaunchSessionTransport(transport.String("privacy"), consent, transport.Integer("port"), transport.Bool("upnp"), transport.Bool("lan")),
                value.OptionalString("serviceOrigin"), value.Has("target") ? ParseTarget(value.Required("target")) : null, host, peer,
                value.Bool("developerMode"));
        }

        private static LaunchSessionTarget ParseTarget(string json)
        {
            var kind = new Fields(json, allowed: null).String("kind");
            var pins = new[] { "descriptorSha256", "kind", "packageDigest", "serverId" };
            switch (kind)
            {
                case "invite":
                    var invite = new Fields(json, pins.Append("inviteCode").ToArray());
                    return LaunchSessionTarget.Invite(invite.String("inviteCode"), invite.String("serverId"), invite.String("descriptorSha256"),
                        invite.String("packageDigest"));
                case "listing":
                    var listing = new Fields(json, pins.Append("source").ToArray());
                    return LaunchSessionTarget.Listing(listing.String("source"), listing.String("serverId"), listing.String("descriptorSha256"),
                        listing.String("packageDigest"));
                case "direct":
                    var direct = new Fields(json, pins.Concat(new[] { "host", "port" }).ToArray());
                    return LaunchSessionTarget.Direct(direct.String("host"), direct.Integer("port"), direct.String("serverId"),
                        direct.String("descriptorSha256"), direct.String("packageDigest"));
                default:
                    throw Invalid("Unknown session target kind.");
            }
        }

        private static string SessionProgressJson(LaunchProgress progress)
        {
            var session = progress.Session!;
            return Sorted(("nativeBusy", progress.NativeBusy.HasValue ? Boolean(progress.NativeBusy.Value) : null), ("phase", Text(progress.Phase)),
                ("requestId", Text(progress.RequestId)), ("schemaVersion", Number(LaunchProgress.SessionSchemaVersion)),
                ("sequence", Number(progress.Sequence)),
                ("session", Sorted(("intentId", Text(session.IntentId)), ("inviteCode", session.InviteCode == null ? null : Text(session.InviteCode)),
                    ("maxPlayers", session.MaxPlayers.HasValue ? Number(session.MaxPlayers.Value) : null),
                    ("players", session.Players.HasValue ? Number(session.Players.Value) : null), ("role", Text(session.Role)),
                    ("serverId", session.ServerId == null ? null : Text(session.ServerId)), ("state", Text(session.State)))),
                ("sessionId", progress.SessionId == null ? null : Text(progress.SessionId)));
        }

        private static LaunchProgress ParseSessionProgress(string json)
        {
            var value = new Fields(json, "nativeBusy", "phase", "requestId", "schemaVersion", "sequence", "session", "sessionId");
            value.Version(LaunchProgress.SessionSchemaVersion);
            var block = new Fields(value.Required("session"), "intentId", "inviteCode", "maxPlayers", "players", "role", "serverId", "state");
            var session = new LaunchProgressSession(block.String("role"), block.String("intentId"), block.String("state"), block.OptionalString("serverId"),
                block.Has("players") ? block.Integer("players") : (int?)null, block.Has("maxPlayers") ? block.Integer("maxPlayers") : (int?)null,
                block.OptionalString("inviteCode"));
            return new LaunchProgress(value.String("requestId"), value.Integer("sequence"), value.String("phase"), value.OptionalString("sessionId"),
                value.OptionalBool("nativeBusy"), session);
        }

        private static string SessionOutcomeJson(LaunchOutcome outcome)
        {
            var session = outcome.Multiplayer!;
            var admission = session.Admission;
            return Sorted(("blocks", SortedBlocks(outcome.Blocks)), ("command", outcome.Command == null ? null : Text(outcome.Command)),
                ("error", outcome.Error == null ? null : Sorted(("code", Text(outcome.Error.Code)), ("message", Text(outcome.Error.Message)))),
                ("kind", Text(outcome.Kind)),
                ("multiplayer", Sorted(
                    ("admission", admission == null ? null
                        : Sorted(("code", Text(admission.Code)), ("problems", ArrayOf(admission.Problems.Select(Text))))),
                    ("detail", session.Detail == null ? null : Text(session.Detail)),
                    ("endReason", session.EndReason == null ? null : Text(session.EndReason)),
                    ("failure", session.Failure == null ? null : Text(session.Failure)), ("intentId", Text(session.IntentId)),
                    ("role", Text(session.Role)), ("serverId", session.ServerId == null ? null : Text(session.ServerId)))),
                ("phase", Text(outcome.Phase)), ("requestId", Text(outcome.RequestId)),
                ("schemaVersion", Number(LaunchOutcome.SessionSchemaVersion)), ("sequence", Number(outcome.Sequence)),
                ("sessionId", outcome.SessionId == null ? null : Text(outcome.SessionId)), ("status", Text(outcome.Status)));
        }

        private static LaunchOutcome ParseSessionOutcome(string json)
        {
            var value = new Fields(json, "blocks", "command", "error", "kind", "multiplayer", "phase", "requestId", "schemaVersion", "sequence",
                "sessionId", "status");
            value.Version(LaunchOutcome.SessionSchemaVersion);
            LaunchExecutionError? error = null;
            if (value.Has("error"))
            {
                var detail = new Fields(value.Required("error"), "code", "message");
                error = new LaunchExecutionError(detail.String("code"), detail.String("message"));
            }

            var block = new Fields(value.Required("multiplayer"), "admission", "detail", "endReason", "failure", "intentId", "role", "serverId");
            LaunchAdmissionReport? admission = null;
            if (block.Has("admission"))
            {
                var report = new Fields(block.Required("admission"), "code", "problems");
                admission = new LaunchAdmissionReport(report.String("code"), Strings(report.Required("problems")));
            }

            var multiplayer = new LaunchOutcomeMultiplayer(block.String("role"), block.String("intentId"), block.OptionalString("serverId"),
                block.OptionalString("failure"), block.OptionalString("detail"), admission, block.OptionalString("endReason"));
            return new LaunchOutcome(value.String("kind"), value.String("requestId"), value.Integer("sequence"), value.String("phase"),
                value.String("status"), ParseBlocks(value.Required("blocks")), value.OptionalString("sessionId"), value.OptionalString("command"),
                error, multiplayer);
        }

        private static int SchemaVersionOf(string json) => new Fields(json, allowed: null).Integer("schemaVersion");

        private static string SortedPackage(PackageIdentity package) => Sorted(("id", Text(package.Id)), ("version", Text(package.Version)));

        private static string SortedPlan(LaunchPlanDescriptor plan) => Sorted(
            ("digest", Text(plan.Digest)), ("gamemodeId", Text(plan.GamemodeId)), ("packages", ArrayOf(plan.Packages.Select(SortedPackage))),
            ("request", Sorted(("targetId", Text(plan.Request.TargetId)),
                ("transitionOverride", plan.Request.TransitionOverride == null ? null : Text(plan.Request.TransitionOverride)),
                ("worldOverride", plan.Request.WorldOverride == null ? null : Text(plan.Request.WorldOverride)))),
            ("targetId", Text(plan.TargetId)), ("transition", Text(plan.Transition)),
            ("worldFamilyId", plan.WorldFamilyId == null ? null : Text(plan.WorldFamilyId)), ("worldId", Text(plan.WorldId)));

        private static string SortedBlocks(IEnumerable<LaunchBlock> blocks) => ArrayOf(blocks.Select(block => Sorted(
            ("code", Text(CodeName(block.Code))), ("subject", Text(block.Subject)), ("subjectVersion", Text(block.SubjectVersion)))));

        /// <summary>An object with its present members in ordinal key order.</summary>
        internal static string Sorted(params (string Key, string? Value)[] members) => "{" + string.Join(",", members
            .Where(member => member.Value != null).OrderBy(member => member.Key, StringComparer.Ordinal)
            .Select(member => Text(member.Key) + ":" + member.Value)) + "}";

        internal static string ArrayOf(IEnumerable<string> values) => "[" + string.Join(",", LaunchContractValues.Copy(values)) + "]";

        /// <summary>A JSON string with only the quotation mark, the backslash and U+0000 to U+001F escaped.</summary>
        internal static string Text(string value)
        {
            var builder = new StringBuilder(value.Length + 2).Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < ' ') builder.Append("\\u00").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
                        else builder.Append(character);
                        break;
                }
            }

            return builder.Append('"').ToString();
        }
    }
}
