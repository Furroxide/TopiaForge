extern alias provider;

using System.Security.Cryptography;
using System.Text;
using provider::TopiaForge.Multiplayer.ControlPlane;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport.Crypto;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>One host session the fake service holds, as its HostSession object would.</summary>
internal sealed class FakeHostSession
{
    internal string ServerId { get; init; } = string.Empty;
    internal string Token { get; set; } = string.Empty;
    internal byte[] Descriptor { get; set; } = Array.Empty<byte>();
    internal SignedServerDescriptor Signed { get; set; } = null!;
    internal bool RelayOnly { get; set; }
    internal int Epoch { get; init; }
    internal long ExpiresAt { get; set; }
    internal long HeartbeatAt { get; set; }
    internal int Players { get; set; }
    internal bool Ended { get; set; }
    internal Dictionary<string, long> Invites { get; } = new(StringComparer.Ordinal);
    internal string Visibility => Signed.Descriptor.Visibility == ServerVisibility.Public ? "public" : "invite";
}

/// <summary>A join ticket the fake service issued: what its MAC would carry.</summary>
internal sealed record FakeTicket(string ServerId, int Epoch, string? Invite, bool RelayOnly, string PeerId, long ExpiresAt);

/// <summary>
/// The TopiaForge service for the live flows' tests, in memory and on the virtual clock, behind the same transport
/// interface the game's HTTPS client implements (ControlPlaneApi.md). It holds status flags and the kill switch,
/// challenges bound to a key and good once, registrations whose proof it checks under the descriptor's own key,
/// heartbeats no closer than five seconds and listings that lapse after their lifetime, descriptor updates, ending,
/// invites, one 404 for every miss, join tickets that carry the joiner's privacy, and relay credentials minted on the
/// NAT simulator's TURN server. Every answer is canonical JSON in the shapes the shared fixtures record, and its error
/// answers are the service's catalogue, byte for byte (<see cref="ControlPlaneFakeTests"/> holds it to the recordings).
/// Signaling runs in the NAT tests' fake room, which it closes when a session ends. Knobs make it degraded or hostile.
/// </summary>
internal sealed partial class FakeControlPlane : IControlPlaneTransport
{
    internal const int HeartbeatSeconds = 30;
    internal const int ListingTtlSeconds = 90;

    private static readonly byte[] RegistrationLabel = Encoding.ASCII.GetBytes("TopiaForge host registration v1\n");

    private readonly NatScenario scenario;
    private readonly Random random;
    private readonly Dictionary<string, FakeHostSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string SigningKey, long ExpiresAt)> challenges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeTicket> tickets = new(StringComparer.Ordinal);
    private int epochs;

    internal FakeControlPlane(NatScenario scenario, int seed)
    {
        this.scenario = scenario;
        random = new Random(seed);
    }

    internal bool Hosting { get; set; } = true;
    internal bool Invites { get; set; } = true;
    internal bool Relay { get; set; } = true;
    internal bool Signaling { get; set; } = true;
    internal bool KillSwitch { get; set; }

    /// <summary>Gets the server ids an operator holds out; their registrations answer <c>server-blocked</c>.</summary>
    internal HashSet<string> Blocked { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets a filter that answers a request in the service's place, as a broken or hostile service would.</summary>
    internal Func<ControlPlaneRequest, ControlPlaneResponse?>? Hostile { get; set; }

    /// <summary>Gets or sets how many relay credentials the service mints before it refuses the rest with a rate limit.</summary>
    internal int MintAllowance { get; set; } = int.MaxValue;

    /// <summary>Gets or sets the text every error answer carries instead of the catalogue's, to prove no one reads it.</summary>
    internal string? ErrorText { get; set; }

    /// <summary>Gets every request the service took, in order.</summary>
    internal List<ControlPlaneRequest> Requests { get; } = new();

    /// <summary>Gets the answer the service gave last.</summary>
    internal ControlPlaneResponse? LastAnswer { get; private set; }

    internal IReadOnlyDictionary<string, FakeHostSession> Sessions => sessions;

    internal int Mints { get; private set; }

    internal long Now => scenario.Network.Now;

    public Task<ControlPlaneResponse> SendAsync(ControlPlaneRequest request, CancellationToken cancellation)
    {
        lock (sessions)
        {
            Requests.Add(request);
            if (cancellation.IsCancellationRequested) return Task.FromResult(ControlPlaneResponse.Failed(ControlPlaneTransportFailure.Cancelled));
            Expire();
            var answer = Hostile?.Invoke(request) ?? Handle(request);
            LastAnswer = answer;
            return Task.FromResult(answer);
        }
    }

    /// <summary>Runs the service's alarms: a session whose heartbeats stopped for its lifetime ends, as HostSession's alarm ends it.</summary>
    internal void Tick()
    {
        lock (sessions) Expire();
    }

    /// <summary>Counts the requests to a path that start with <paramref name="method"/> and end with <paramref name="suffix"/>.</summary>
    internal int Count(string method, string suffix) => Requests.Count(request => request.MethodText == method && request.Path.EndsWith(suffix, StringComparison.Ordinal));

    /// <summary>Gets whether a server is in the public directory now.</summary>
    internal bool IsListed(string serverId) => sessions.TryGetValue(serverId, out var session) && !session.Ended && session.Visibility == "public";

    /// <summary>Ends a session as an operator's delisting does.</summary>
    internal void Delist(string serverId)
    {
        lock (sessions)
        {
            if (sessions.TryGetValue(serverId, out var session)) End(session);
        }
    }

    private ControlPlaneResponse Handle(ControlPlaneRequest request)
    {
        var path = request.Path;
        var segments = path.Split('/');
        if (path == "/v1/status") return request.Method == ControlPlaneMethod.Get ? Status() : Error("method-not-allowed");
        if (path == "/v1/hosts/challenge") return Challenge(request);
        if (path == "/v1/hosts") return Register(request);
        if (path == "/v1/invites/resolve") return Resolve(request);
        if (path == "/v1/join-tickets") return Ticket(request);
        if (path == "/v1/turn/credentials") return Mint(request);
        if (segments.Length >= 4 && segments[2] == "servers" && segments.Length == 4) return Detail(segments[3]);
        if (segments.Length >= 4 && segments[2] == "hosts") return HostRequest(request, segments);
        return Error("not-found");
    }

    private ControlPlaneResponse Status() => Json(200, new JsonObjectNode()
        .Add("api", 1)
        .Add("features", new JsonObjectNode().Add("hosting", Hosting).Add("invites", Invites).Add("relay", Relay && !KillSwitch).Add("signaling", Signaling))
        .Add("hosting", new JsonObjectNode().Add("heartbeatSeconds", HeartbeatSeconds).Add("listingTtlSeconds", ListingTtlSeconds))
        .Add("killSwitch", KillSwitch)
        .Add("relay", new JsonObjectNode().Add("credentialTtlSeconds", scenario.TtlSeconds))
        .Add("schema", ControlPlaneAnswers.StatusSchema));

    private ControlPlaneResponse Challenge(ControlPlaneRequest request)
    {
        if (!TryBody(request, out var body, "signingKey") || !WireText.TryParseBase64Url(Text(body, "signingKey"), 32, out _)) return Error("invalid-request");
        if (!Hosting) return Error("feature-disabled");
        var challenge = Random(37);
        challenges[challenge] = (Text(body, "signingKey"), Now + 120_000_000);
        return Json(200, new JsonObjectNode().Add("challenge", challenge).Add("expiresAt", Seconds(Now + 120_000_000)));
    }

    private ControlPlaneResponse Register(ControlPlaneRequest request)
    {
        if (!TryBody(request, out var body, "challenge", "descriptor", "proof", "relayOnly")) return Error("invalid-request");
        if (!Hosting) return Error("feature-disabled");
        var text = Text(body, "descriptor");
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!SignedServerDescriptor.TryVerify(bytes, null, out var signed, out _)) return Error("invalid-descriptor");
        if (signed!.Descriptor.Visibility == ServerVisibility.Lan) return Error("unsupported-visibility");
        var challenge = Text(body, "challenge");
        var signingKey = WireText.ToBase64Url(signed.Descriptor.SigningKey);
        if (!challenges.TryGetValue(challenge, out var issued) || issued.SigningKey != signingKey || issued.ExpiresAt < Now) return Error("challenge-invalid");
        challenges.Remove(challenge);
        var relayOnly = body.TryGet("relayOnly", out var flag) && flag is JsonBooleanNode { Value: true };
        var unsigned = CanonicalJson.Serialize(new JsonObjectNode().Add("challenge", challenge).Add("descriptor", text).Add("relayOnly", relayOnly));
        if (!WireText.TryParseBase64Url(Text(body, "proof"), 64, out var proof) || !Ed25519.Verify(proof, signed.Descriptor.SigningKey, Concat(RegistrationLabel, unsigned)))
        {
            return Error("proof-invalid");
        }

        var serverId = signed.Descriptor.ServerId;
        if (Blocked.Contains(serverId)) return Error("server-blocked");
        if (!sessions.TryGetValue(serverId, out var session) || session.Ended)
        {
            session = new FakeHostSession { ServerId = serverId, Epoch = ++epochs };
            sessions[serverId] = session;
            session.Invites[NewCode()] = Now + (7L * 86_400 * 1_000_000);
        }

        // Registering again keeps a live session, its invites and tickets; the old token's host socket is replaced.
        session.Token = "tfh1_" + Random(32);
        session.Descriptor = bytes;
        session.Signed = signed;
        session.RelayOnly = relayOnly;
        session.ExpiresAt = Now + (ListingTtlSeconds * 1_000_000L);
        session.HeartbeatAt = Now;
        var first = session.Invites.OrderBy(pair => pair.Value).First();
        return Json(201, new JsonObjectNode()
            .Add("descriptorSha256", signed.Sha256)
            .Add("expiresAt", Seconds(session.ExpiresAt))
            .Add("heartbeatSeconds", HeartbeatSeconds)
            .Add("invite", Invite(first.Key, first.Value))
            .Add("listed", session.Visibility == "public")
            .Add("listingTtlSeconds", ListingTtlSeconds)
            .Add("serverId", serverId)
            .Add("sessionToken", session.Token));
    }

    private ControlPlaneResponse HostRequest(ControlPlaneRequest request, string[] segments)
    {
        if (!sessions.TryGetValue(segments[3], out var session) || session.Ended || request.Bearer != session.Token) return Error("unauthorized");
        var tail = string.Join("/", segments.Skip(4));
        switch (tail)
        {
            case "" when request.Method == ControlPlaneMethod.Delete:
                End(session);
                return ControlPlaneResponse.Answered(204, Array.Empty<byte>(), false, null);
            case "heartbeat":
                if (!TryBody(request, out var beat, "players")) return Error("invalid-request");
                if (Now - session.HeartbeatAt < 5_000_000) return Error("rate-limited", 5);
                session.HeartbeatAt = Now;
                session.Players = (int)((JsonIntegerNode)Member(beat, "players")).Value;
                session.ExpiresAt = Now + (ListingTtlSeconds * 1_000_000L);
                return Json(200, new JsonObjectNode().Add("expiresAt", Seconds(session.ExpiresAt)).Add("heartbeatSeconds", HeartbeatSeconds)
                    .Add("listed", session.Visibility == "public"));
            case "descriptor":
                if (!TryBody(request, out var update, "descriptor")) return Error("invalid-request");
                var bytes = Encoding.UTF8.GetBytes(Text(update, "descriptor"));
                if (!SignedServerDescriptor.TryVerify(bytes, session.ServerId, out var signed, out _)) return Error("invalid-descriptor");
                if (signed!.Descriptor.IssuedAt < session.Signed.Descriptor.IssuedAt) return Error("stale-descriptor");
                session.Descriptor = bytes;
                session.Signed = signed;
                return Json(200, new JsonObjectNode().Add("descriptorSha256", signed.Sha256).Add("expiresAt", Seconds(session.ExpiresAt))
                    .Add("listed", session.Visibility == "public"));
            case "invites":
                if (!TryBody(request, out var create, "ttlSeconds")) return Error("invalid-request");
                if (!Invites) return Error("feature-disabled");
                if (LiveInvites(session) >= 8) return Error("invite-limit");
                var code = NewCode();
                var expires = Now + (((JsonIntegerNode)Member(create, "ttlSeconds")).Value * 1_000_000L);
                session.Invites[code] = expires;
                return Json(201, new JsonObjectNode().Add("invite", Invite(code, expires)));
            case "invites/revoke":
                if (!TryBody(request, out var revoke, "code")) return Error("invalid-request");
                var revoked = Text(revoke, "code");
                if (!session.Invites.Remove(revoked)) return Error("not-found");
                Withdraw(session, revoked);
                return ControlPlaneResponse.Answered(204, Array.Empty<byte>(), false, null);
            default:
                return Error("not-found");
        }
    }

    /// <summary>Ends a session: its listing and invites go, its tickets stop holding, and its room closes every socket with 4410.</summary>
    private void End(FakeHostSession session)
    {
        if (session.Ended) return;
        session.Ended = true;
        session.Invites.Clear();
        scenario.Signaling.EndSession();
    }

    private void Expire()
    {
        foreach (var session in sessions.Values)
        {
            if (!session.Ended && session.ExpiresAt <= Now) End(session);
        }
    }

    private int LiveInvites(FakeHostSession session) => session.Invites.Count(pair => pair.Value > Now);

    private JsonObjectNode Invite(string code, long expiresAt) => new JsonObjectNode()
        .Add("code", code)
        .Add("display", InviteCode.Display(code))
        .Add("expiresAt", Seconds(expiresAt))
        .Add("link", "topiaforge://join/" + code);

    private string NewCode()
    {
        var code = new StringBuilder();
        for (var index = 0; index < InviteCode.Length; index++) code.Append(InviteCode.Alphabet[random.Next(32)]);
        return code.ToString();
    }

    private string Random(int bytes)
    {
        var value = new byte[bytes];
        random.NextBytes(value);
        return WireText.ToBase64Url(value);
    }

    private static long Seconds(long micros) => 1_969_660_800 + (micros / 1_000_000);

    private static byte[] Concat(byte[] first, byte[] second) => first.Concat(second).ToArray();

    /// <summary>Reads a body as the service does: canonical JSON with exactly the members the endpoint takes.</summary>
    private static bool TryBody(ControlPlaneRequest request, out JsonObjectNode body, params string[] members)
    {
        body = null!;
        if (request.Body == null || !CanonicalJson.TryParse(request.Body, 600 * 1024, out var node, out _) || node is not JsonObjectNode value || !value.HasExactly(members)) return false;
        body = value;
        return true;
    }

    private static JsonNode Member(JsonObjectNode value, string key) => value.TryGet(key, out var node) ? node : JsonNullNode.Instance;

    private static string Text(JsonObjectNode value, string key) => Member(value, key) is JsonStringNode text ? text.Value : string.Empty;

    private static ControlPlaneResponse Json(int status, JsonObjectNode body) => ControlPlaneResponse.Answered(status, CanonicalJson.Serialize(body), true, null);

    /// <summary>An error answer from the service's own catalogue: its status and its fixed message, unless a test replaces the text.</summary>
    internal ControlPlaneResponse Error(string code, int? retryAfter = null)
    {
        var (status, message) = ErrorCatalogue[code];
        var body = new JsonObjectNode().Add("error", new JsonObjectNode().Add("code", code).Add("message", ErrorText ?? message));
        return ControlPlaneResponse.Answered(status, CanonicalJson.Serialize(body), true, retryAfter);
    }

    internal static readonly Dictionary<string, (int Status, string Message)> ErrorCatalogue = new(StringComparer.Ordinal)
    {
        ["invalid-request"] = (400, "The request is malformed."),
        ["invalid-descriptor"] = (400, "The server descriptor is invalid."),
        ["unsupported-visibility"] = (400, "LAN sessions are not registered with the control plane."),
        ["challenge-invalid"] = (401, "The challenge is invalid, used or expired."),
        ["proof-invalid"] = (401, "The proof does not verify."),
        ["unauthorized"] = (401, "Valid credentials are required."),
        ["server-blocked"] = (403, "This server may not register at the moment."),
        ["ticket-limit"] = (403, "This ticket may not mint more relay credentials."),
        ["not-found"] = (404, "Not found."),
        ["method-not-allowed"] = (405, "The method is not allowed here."),
        ["stale-descriptor"] = (409, "A newer descriptor is already registered."),
        ["invite-limit"] = (409, "The server has as many invites as it may hold."),
        ["rate-limited"] = (429, "Too many requests."),
        ["internal"] = (500, "The service failed."),
        ["upstream-failed"] = (502, "The relay provider did not answer."),
        ["feature-disabled"] = (503, "This feature is turned off."),
        ["relay-unavailable"] = (503, "Relay credentials are unavailable."),
        ["service-unavailable"] = (503, "The service is not configured.")
    };

    private static string PeerIdOf(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(ticket))).ToLowerInvariant().Substring(0, 16);
}
