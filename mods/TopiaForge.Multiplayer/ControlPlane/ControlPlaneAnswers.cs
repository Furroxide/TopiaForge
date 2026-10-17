using System;
using System.Text;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>
    /// Strict readers for the service's successful answers. Each answer must be canonical JSON, as the service writes
    /// every one, with every member this client uses present and valid: types, ranges, the exact shapes of ids, codes and
    /// links, and agreement with what was asked, such as the server id a registration or a ticket names. Members the
    /// client does not know are ignored, as the API allows. A reader returns null for anything else, and nothing it reads
    /// is ever logged.
    /// </summary>
    internal static class ControlPlaneAnswers
    {
        internal const string StatusSchema = "topiaforge.control-plane-status/1";
        internal const string ListingSchema = "topiaforge.server-listing/1";

        /// <summary>The latest Unix time a service value may name: the end of year 9999, far inside the safe integers.</summary>
        private const long LatestTime = 253_402_300_799;

        internal static ServiceStatus? Status(byte[] body) => Read(body, ControlPlaneClient.SmallAnswerBytes, root =>
        {
            if (Text(root, "schema") != StatusSchema || Integer(root, "api", 1, 1) != 1) return null;
            var features = Object(root, "features");
            var hosting = Object(root, "hosting");
            var relay = Object(root, "relay");
            var heartbeat = (int)Integer(hosting, "heartbeatSeconds", 5, 300);
            var ttl = (int)Integer(hosting, "listingTtlSeconds", heartbeat + 1, 3_600);
            return new ServiceStatus(Flag(features, "hosting"), Flag(features, "invites"), Flag(features, "relay"), Flag(features, "signaling"),
                Flag(root, "killSwitch"), heartbeat, ttl, (int)Integer(relay, "credentialTtlSeconds", 60, 86_400));
        });

        internal static HostChallenge? Challenge(byte[] body) => Read(body, ControlPlaneClient.SmallAnswerBytes, root =>
            new HostChallenge(Credential(root, "challenge"), Time(root, "expiresAt")));

        internal static HostRegistration? Registration(byte[] body, string serverId, string descriptorSha256) =>
            Read(body, ControlPlaneClient.SmallAnswerBytes, root =>
            {
                if (Text(root, "serverId") != serverId || Text(root, "descriptorSha256") != descriptorSha256) return null;
                var heartbeat = (int)Integer(root, "heartbeatSeconds", 5, 300);
                return new HostRegistration(serverId, Credential(root, "sessionToken"), descriptorSha256, Time(root, "expiresAt"), heartbeat,
                    (int)Integer(root, "listingTtlSeconds", heartbeat + 1, 3_600), Flag(root, "listed"), Invite(Object(root, "invite")));
            });

        internal static HostRenewal? Heartbeat(byte[] body) => Read(body, ControlPlaneClient.SmallAnswerBytes, root =>
            new HostRenewal(Time(root, "expiresAt"), Flag(root, "listed"), (int)Integer(root, "heartbeatSeconds", 5, 300), null));

        internal static HostRenewal? DescriptorUpdate(byte[] body, string descriptorSha256) => Read(body, ControlPlaneClient.SmallAnswerBytes, root =>
            Text(root, "descriptorSha256") != descriptorSha256 ? null : new HostRenewal(Time(root, "expiresAt"), Flag(root, "listed"), null, descriptorSha256));

        internal static ServiceInvite? CreatedInvite(byte[] body) => Read(body, ControlPlaneClient.SmallAnswerBytes, root => Invite(Object(root, "invite")));

        /// <summary>
        /// Reads a server answer, an invite's resolution or a public server's detail. The descriptor must verify for the
        /// server id, and its exact served bytes must hash to the answer's <c>descriptorSha256</c>, which the listing names too.
        /// <paramref name="serverId"/> is the id asked for, or null when an invite named it.
        /// </summary>
        internal static ServiceServer? Server(byte[] body, string? serverId) => Read(body, ControlPlaneClient.ServerAnswerBytes, root =>
        {
            var id = serverId ?? Text(root, "serverId");
            if (!WireText.IsHex(id, 32) || (serverId != null && root.TryGet("serverId", out var named) && !(named is JsonStringNode text && text.Value == serverId))) return null;
            var sha = Text(root, "descriptorSha256");
            var bytes = Encoding.UTF8.GetBytes(Text(root, "descriptor"));
            if (!SignedServerDescriptor.TryVerify(bytes, id, out var descriptor, out _) || descriptor!.Sha256 != sha) return null;
            var listing = Object(root, "listing");
            if (Text(listing, "schema") != ListingSchema || Text(listing, "serverId") != id || Text(listing, "descriptorSha256") != sha) return null;
            var visibility = Text(listing, "visibility");
            if (visibility != "public" && visibility != "invite") return null;
            return new ServiceServer(id, descriptor, Flag(listing, "relayOnly"), (int)Integer(listing, "players", 0, descriptor.Descriptor.MaxPlayers));
        });

        internal static ServiceJoinTicket? Ticket(byte[] body, string serverId, bool relayOnly) => Read(body, ControlPlaneClient.SmallAnswerBytes, root =>
        {
            // The ticket carries the joiner's privacy choice under the service's MAC; one that says otherwise is not this join's.
            if (Text(root, "serverId") != serverId || Flag(root, "relayOnly") != relayOnly) return null;
            return new ServiceJoinTicket(Credential(root, "ticket"), serverId, Time(root, "expiresAt"), relayOnly, Flag(root, "hostRelayOnly"));
        });

        internal static RelayCredentials? Credentials(byte[] body) => Read(body, IceServerSet.MaximumBytes, root =>
            IceServerSet.TryParse(body, out var servers) ? new RelayCredentials(servers, Time(root, "expiresAt")) : null);

        private static T? Read<T>(byte[] body, int maximum, Func<JsonObjectNode, T?> read) where T : class
        {
            if (body == null || body.Length == 0 || body.Length > maximum) return null;
            if (!CanonicalJson.TryParse(body, maximum, out var node, out _) || !(node is JsonObjectNode root)) return null;
            try
            {
                return read(root);
            }
            catch (FormatException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static ServiceInvite Invite(JsonObjectNode invite)
        {
            var code = Text(invite, "code");
            if (!InviteCode.IsCanonical(code)) throw new FormatException("An invite code is canonical.");
            var made = new ServiceInvite(code, Time(invite, "expiresAt"));
            if (Text(invite, "display") != made.Display || Text(invite, "link") != made.Link) throw new FormatException("An invite's forms agree.");
            return made;
        }

        private static JsonObjectNode Object(JsonObjectNode value, string key) =>
            value.TryGet(key, out var node) && node is JsonObjectNode inner ? inner : throw new FormatException("An object is expected.");

        private static string Text(JsonObjectNode value, string key) => JsonRead.String(value, key);

        private static bool Flag(JsonObjectNode value, string key) => JsonRead.Boolean(value, key);

        private static long Integer(JsonObjectNode value, string key, long minimum, long maximum)
        {
            var number = JsonRead.Integer(value, key);
            return number >= minimum && number <= maximum ? number : throw new FormatException("A number is out of range.");
        }

        private static long Time(JsonObjectNode value, string key) => Integer(value, key, 0, LatestTime);

        /// <summary>A token, ticket or challenge: opaque, but held to the base64url alphabet and 256 characters, as every one is.</summary>
        private static string Credential(JsonObjectNode value, string key)
        {
            var text = Text(value, key);
            return ControlPlaneRequest.IsCredential(text) ? text : throw new FormatException("A credential is base64url text.");
        }
    }
}
