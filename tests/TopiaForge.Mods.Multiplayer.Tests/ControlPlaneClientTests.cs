extern alias provider;

using System.Text;
using System.Text.Json;
using provider::TopiaForge.Multiplayer.ControlPlane;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The provider's control-plane client against the service's own recorded exchanges
/// (tests/fixtures/multiplayer/control-plane/exchanges.json): every request it makes goes out byte for byte as the
/// service recorded it, through the client and through the wire, and every recorded answer reads as the service meant
/// it. Answers that break the rules, successful or not, are refused or read by their code and status alone.
/// </summary>
internal static class ControlPlaneClientTests
{
    internal const string ServerId = "c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3";

    internal static void RequestsAndAnswersMatchTheRecordedExchanges()
    {
        var exchanges = ControlPlaneFixtures.Exchanges.GetProperty("exchanges").EnumerateArray().Select(item => item.Text("id")).ToList();
        var replayed = new HashSet<string>();
        var seed = NetworkFixtures.Hex(NetworkFixtures.Load("descriptor/cases.json").GetProperty("keys")[0].Text("signingSeed"));
        var token = ControlPlaneFixtures.Bearer("host-heartbeat")!;

        T Replay<T>(string id, Func<ControlPlaneClient, Task<ServiceResult<T>>> call, Func<ServiceResult<T>, bool> check) where T : class
        {
            var transport = new RecordingTransport(_ => ControlPlaneFixtures.Answer(id));
            var result = call(new ControlPlaneClient(transport)).GetAwaiter().GetResult();
            That(transport.Requests.Count == 1, id + ": one request");
            Matches(id, transport.Requests[0]);
            That(check(result), id + ": the answer reads as recorded (" + result + ")");
            replayed.Add(id);
            return result.Value!;
        }

        var status = Replay("status", client => client.GetStatusAsync(CancellationToken.None), result => result.Succeeded);
        That(status.Hosting && status.Invites && status.Relay && status.Signaling && !status.KillSwitch && status.RelayAvailable &&
             status.HeartbeatSeconds == 30 && status.ListingTtlSeconds == 90 && status.CredentialTtlSeconds == 600, "status: every flag and figure");

        var signingKey = WireText.TryParseBase64Url(JsonDocument.Parse(ControlPlaneFixtures.Request("host-challenge").Text("body")).RootElement.Text("signingKey"), 32, out var key) ? key : null;
        var challenge = Replay("host-challenge", client => client.RequestChallengeAsync(signingKey!, CancellationToken.None), result => result.Succeeded);
        That(challenge.ExpiresAt == 1969660920 && challenge.Challenge.StartsWith("AQAAAAB1ZqP4", StringComparison.Ordinal), "host-challenge: the challenge and its expiry");

        var descriptor = Descriptor("host-register");
        var registration = Replay("host-register", client => client.RegisterAsync(challenge, descriptor, false, seed, CancellationToken.None), result => result.Succeeded);
        That(registration.ServerId == ServerId && registration.SessionToken == token && registration.Listed && registration.HeartbeatSeconds == 30 &&
             registration.ListingTtlSeconds == 90 && registration.ExpiresAt == 1969660890 && registration.Invite.Code == "RJ9JMV1V09" &&
             registration.Invite.Display == "RJ9JM-V1V09" && registration.Invite.Link == "topiaforge://join/RJ9JMV1V09",
            "host-register: the proof is the recorded one, and the session token and first invite read back");

        Replay("host-heartbeat-too-soon", client => client.HeartbeatAsync(ServerId, token, 1, CancellationToken.None),
            result => result.Failure == ServiceFailure.RateLimited && result.RetryAfterSeconds == 5);
        var beat = Replay("host-heartbeat", client => client.HeartbeatAsync(ServerId, token, 2, CancellationToken.None), result => result.Succeeded);
        That(beat.ExpiresAt == 1969660920 && beat.Listed && beat.HeartbeatSeconds == 30, "host-heartbeat: the renewed expiry");

        var invite = Replay("invite-create", client => client.CreateInviteAsync(ServerId, token, 3600, CancellationToken.None), result => result.Succeeded);
        That(invite.Code == "F80BHRS9YW" && invite.ExpiresAt == 1969664430, "invite-create: the new invite");

        var detail = Replay("server-detail", client => client.GetServerAsync(ServerId, CancellationToken.None), result => result.Succeeded);
        That(detail.ServerId == ServerId && !detail.HostRelayOnly && detail.Players == 2 && detail.Descriptor.Sha256 == "918ec546e59e643a2dc3c6b6791d2c1c66945cd872f29b8e109ad13ed6a5ceae",
            "server-detail: the verified descriptor and the listing's privacy and players");

        var resolved = Replay("invite-resolve", client => client.ResolveInviteAsync("f80bh-RS9YW", CancellationToken.None), result => result.Succeeded);
        That(resolved.ServerId == ServerId && resolved.Descriptor.Descriptor.Name == "Sam's Sandbox", "invite-resolve: the code as typed resolves to the server");

        var ticket = Replay("join-ticket", client => client.CreateJoinTicketAsync(ServerId, null, true, CancellationToken.None), result => result.Succeeded);
        That(ticket.RelayOnly && !ticket.HostRelayOnly && ticket.ExpiresAt == 1969682430 && ticket.Ticket == ControlPlaneFixtures.Bearer("turn-credentials-joiner"),
            "join-ticket: a relay-only ticket for a host that is not");

        foreach (var (id, credential) in new[] { ("turn-credentials-joiner", ticket.Ticket), ("turn-credentials-host", token) })
        {
            var minted = Replay(id, client => client.MintRelayCredentialsAsync(ServerId, credential, CancellationToken.None), result => result.Succeeded);
            That(minted.ExpiresAt == 1969661430 && minted.Servers.HasRelay && minted.Servers.TtlSeconds == 600, id + ": credentials in the iceServers shape");
        }

        var updated = Descriptor("host-descriptor");
        Replay("host-descriptor", client => client.UpdateDescriptorAsync(ServerId, token, updated, CancellationToken.None),
            result => result.Succeeded && result.Value!.Listed && result.Value.DescriptorSha256 == updated.Sha256);
        Replay("invite-revoke", client => client.RevokeInviteAsync(ServerId, token, "F80BHRS9YW", CancellationToken.None), result => result.Succeeded);
        Replay("invite-resolve-miss", client => client.ResolveInviteAsync("F80BHRS9YW", CancellationToken.None), result => result.Failure == ServiceFailure.NotFound);
        Replay("error-unauthorized", client => client.HeartbeatAsync(ServerId, ControlPlaneFixtures.Bearer("error-unauthorized")!, 0, CancellationToken.None),
            result => result.Failure == ServiceFailure.Unauthorized);
        Replay("host-end", client => client.EndHostAsync(ServerId, token, CancellationToken.None), result => result.Succeeded);

        // The client never sends these; their recorded answers still read by code: a malformed body, a wrong method and an
        // unknown path. The host's own view and the server browser are the interface's, which this client does not use.
        foreach (var (id, failure) in new[] { ("error-malformed", ServiceFailure.InvalidRequest), ("error-method", ServiceFailure.MethodNotAllowed), ("error-unknown", ServiceFailure.NotFound) })
        {
            var client = new ControlPlaneClient(new RecordingTransport(_ => ControlPlaneFixtures.Answer(id)));
            That(client.ResolveInviteAsync("ABCDE12345", CancellationToken.None).GetAwaiter().GetResult().Failure == failure, id + ": reads as " + failure);
            replayed.Add(id);
        }

        replayed.Add("host-view");
        replayed.Add("servers-list");
        That(exchanges.All(replayed.Contains) && replayed.Count == exchanges.Count, "every recorded exchange is accounted for (" + string.Join(", ", exchanges.Where(id => !replayed.Contains(id))) + ")");
    }

    /// <summary>The same exchanges through the real wire: what the service's readers check arrives, and nothing else.</summary>
    internal static void TheWireCarriesTheRecordedRequests()
    {
        foreach (var id in new[] { "status", "host-challenge", "host-heartbeat-too-soon", "join-ticket", "turn-credentials-joiner", "host-descriptor", "invite-revoke", "host-end", "server-detail" })
        {
            var response = ControlPlaneFixtures.Response(id);
            var head = new StringBuilder("HTTP/1.1 " + response.GetProperty("status").GetInt32() + " Recorded\r\n");
            foreach (var header in response.GetProperty("headers").EnumerateObject()) head.Append(header.Name).Append(": ").Append(header.Value.GetString()).Append("\r\n");
            var body = response.OptionalText("body") ?? string.Empty;
            head.Append("content-length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n\r\n").Append(body);
            using var server = LoopbackHttpServer.Answering(Encoding.UTF8.GetBytes(head.ToString()));
            var request = ControlPlaneFixtures.Request(id);
            var method = request.Text("method") switch { "GET" => ControlPlaneMethod.Get, "POST" => ControlPlaneMethod.Post, "PUT" => ControlPlaneMethod.Put, _ => ControlPlaneMethod.Delete };
            var bound = id == "server-detail" ? ControlPlaneClient.ServerAnswerBytes : 64 * 1024;
            var transport = new HttpsControlPlaneTransport(ControlPlaneOrigin.Loopback(server.Port));
            var answer = transport.SendAsync(new ControlPlaneRequest(method, request.Text("path"), ControlPlaneFixtures.Bearer(id), ControlPlaneFixtures.Body(id), bound), CancellationToken.None)
                .GetAwaiter().GetResult();
            That(answer.Succeeded && answer.Status == response.GetProperty("status").GetInt32() && Encoding.UTF8.GetString(answer.Body) == body,
                id + ": the recorded answer comes back whole (" + answer + ")");
            That(server.Requests.TryDequeue(out var sent), id + ": the server read a request");
            That(sent!.RequestLine == request.Text("method") + " " + request.Text("path") + " HTTP/1.1", id + ": the request line (" + sent.RequestLine + ")");
            That(sent.Header("Host") == "127.0.0.1:" + server.Port && sent.Count("Host") == 1, id + ": one Host header naming the origin");
            That(sent.Header("Authorization") == (ControlPlaneFixtures.Bearer(id) == null ? null : "Bearer " + ControlPlaneFixtures.Bearer(id)), id + ": the credential only in its header");
            That(sent.Header("Transfer-Encoding") == null && sent.Header("Cookie") == null && sent.Header("Expect") == null && sent.Header("Proxy-Authorization") == null,
                id + ": no transfer coding, cookie, expectation or proxy credential");
            var expected = ControlPlaneFixtures.Body(id);
            if (expected == null)
            {
                That(sent.Body.Length == 0 && sent.Header("Content-Length") == null && sent.Header("Content-Type") == null, id + ": no body and no body framing");
            }
            else
            {
                That(sent.Body.SequenceEqual(expected) && sent.Header("Content-Length") == expected.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                     sent.Header("Content-Type") == "application/json", id + ": the body byte for byte, labelled as the service's reader wants");
            }

            if (answer.RetryAfterSeconds != null) That(id == "host-heartbeat-too-soon" && answer.RetryAfterSeconds == 5, id + ": a plain Retry-After is read");
        }
    }

    internal static void ErrorsReadByTheirCodeAndStatusOnly()
    {
        ServiceFailure Read(int status, string body, bool json = true) => ServiceErrors.Read(ControlPlaneResponse.Answered(status, Encoding.UTF8.GetBytes(body), json, null));

        That(Read(429, """{"error":{"code":"rate-limited","message":"Too many requests."}}""") == ServiceFailure.RateLimited, "a catalogued code with its own status");
        That(Read(503, """{"error":{"code":"relay-unavailable","message":"anything at all, 203.0.113.9"}}""") == ServiceFailure.RelayUnavailable,
            "the message is never read, so whatever it holds changes nothing");
        That(Read(400, """{"error":{"code":"rate-limited","message":"x"}}""") == ServiceFailure.Refused, "a code under another status is not believed");
        That(Read(503, """{"error":{"code":"not-found","message":"x"}}""") == ServiceFailure.Failed, "nor is one that claims a 404 under a 503");
        That(Read(418, """{"error":{"code":"teapot","message":"x"}}""") == ServiceFailure.Refused, "an unknown code falls back to its status class");
        That(Read(500, "<html>busy</html>", json: false) == ServiceFailure.Failed, "a page that is not JSON is the status alone");
        That(Read(429, "<html>error 1015: you are being rate limited</html>", json: false) == ServiceFailure.RateLimited,
            "a 429 is a rate limit whatever page the platform in front of the service writes");
        That(Read(429, """{"error":{"code":"not-found","message":"x"}}""") == ServiceFailure.RateLimited, "even one whose code claims something else");
        That(Read(404, """{ "error":{"code":"not-found","message":"x"}}""") == ServiceFailure.Refused, "and so is JSON that is not canonical");
        That(Read(404, """{"error":{"code":"not-found"}}""") == ServiceFailure.Refused, "or an error without its message");
        That(Read(404, "{\"error\":{\"code\":\"not-found\",\"message\":\"" + new string('x', 5000) + "\"}}") == ServiceFailure.Refused, "or one past its bound");
        That(Read(404, """{"error":{"code":"not-found","extra":1,"message":"x"},"more":true}""") == ServiceFailure.NotFound, "members the client does not know are ignored");
    }

    internal static void SuccessfulAnswersAreReadStrictly()
    {
        var token = ControlPlaneFixtures.Bearer("host-heartbeat")!;
        ServiceFailure Heartbeat(string body, bool json = true, int status = 200) =>
            new ControlPlaneClient(new RecordingTransport(_ => ControlPlaneResponse.Answered(status, Encoding.UTF8.GetBytes(body), json, null)))
                .HeartbeatAsync(ServerId, token, 1, CancellationToken.None).GetAwaiter().GetResult().Failure;

        That(Heartbeat("""{"expiresAt":1969660920,"heartbeatSeconds":30,"listed":true}""") == ServiceFailure.None, "the recorded shape reads");
        That(Heartbeat("""{"expiresAt":1969660920,"heartbeatSeconds":30,"later":{"x":1},"listed":true}""") == ServiceFailure.None, "with members added later ignored");
        foreach (var (about, body) in new[]
        {
            ("whitespace", """{"expiresAt":1969660920, "heartbeatSeconds":30,"listed":true}"""),
            ("member order", """{"listed":true,"expiresAt":1969660920,"heartbeatSeconds":30}"""),
            ("a missing member", """{"expiresAt":1969660920,"heartbeatSeconds":30}"""),
            ("a string for a flag", """{"expiresAt":1969660920,"heartbeatSeconds":30,"listed":"true"}"""),
            ("a fraction", """{"expiresAt":1969660920.5,"heartbeatSeconds":30,"listed":true}"""),
            ("a negative time", """{"expiresAt":-1,"heartbeatSeconds":30,"listed":true}"""),
            ("a heartbeat faster than the service allows", """{"expiresAt":1969660920,"heartbeatSeconds":1,"listed":true}"""),
            ("a heartbeat slower than any listing lasts", """{"expiresAt":1969660920,"heartbeatSeconds":100000,"listed":true}"""),
            ("an array", """[{"expiresAt":1969660920,"heartbeatSeconds":30,"listed":true}]"""),
            ("nothing", ""),
            ("an oversized body", "{\"expiresAt\":1969660920,\"heartbeatSeconds\":30,\"listed\":true,\"pad\":\"" + new string('a', 5000) + "\"}")
        })
        {
            That(Heartbeat(body) == ServiceFailure.InvalidResponse, "a heartbeat answer with " + about + " is refused");
        }

        That(Heartbeat("""{"expiresAt":1969660920,"heartbeatSeconds":30,"listed":true}""", json: false) == ServiceFailure.InvalidResponse, "a body not labelled JSON is refused");
        That(Heartbeat("""{"expiresAt":1969660920,"heartbeatSeconds":30,"listed":true}""", status: 201) == ServiceFailure.InvalidResponse, "and so is a success with another status");

        // Answers that name something other than what was asked are not this request's.
        var registerBody = ControlPlaneFixtures.Response("host-register").Text("body");
        var seed = NetworkFixtures.Hex(NetworkFixtures.Load("descriptor/cases.json").GetProperty("keys")[0].Text("signingSeed"));
        var challenge = new HostChallenge("AQAAAAB1ZqP4a8QNhigcvrIeP9PCvTJMOC23VpqoqEnDaEoiydNuPZA", 1969660920);
        ServiceFailure Register(string body) => new ControlPlaneClient(new RecordingTransport(_ => ControlPlaneResponse.Answered(201, Encoding.UTF8.GetBytes(body), true, null)))
            .RegisterAsync(challenge, Descriptor("host-register"), false, seed, CancellationToken.None).GetAwaiter().GetResult().Failure;
        That(Register(registerBody) == ServiceFailure.None, "the recorded registration reads");
        foreach (var (about, from, to) in new[]
        {
            ("another server", ServerId, new string('d', 64)),
            ("another descriptor", "918ec546e59e643a2dc3c6b6791d2c1c66945cd872f29b8e109ad13ed6a5ceae", new string('e', 64)),
            ("a token with a header break", "tfh1_CP64OMr4OHh0LqD7rolXdIPbWh3jO81Y6ipv6p3fGX4", "tfh1_CP64\\r\\nX-Evil: 1"),
            ("a code that is not canonical", "\"code\":\"RJ9JMV1V09\"", "\"code\":\"rj9jmv1v09\""),
            ("a display form that disagrees", "\"display\":\"RJ9JM-V1V09\"", "\"display\":\"RJ9JMV1V09\""),
            ("a link to somewhere else", "\"link\":\"topiaforge://join/RJ9JMV1V09\"", "\"link\":\"https://example.org/RJ9JMV1V09\"")
        })
        {
            That(Register(registerBody.Replace(from, to, StringComparison.Ordinal)) == ServiceFailure.InvalidResponse, "a registration naming " + about + " is refused");
        }

        // A server answer's descriptor must verify for the id asked for and hash to the answer's own pin.
        var detailBody = ControlPlaneFixtures.Response("server-detail").Text("body");
        ServiceFailure Detail(string body, string serverId = ServerId) => new ControlPlaneClient(new RecordingTransport(_ => ControlPlaneResponse.Answered(200, Encoding.UTF8.GetBytes(body), true, null)))
            .GetServerAsync(serverId, CancellationToken.None).GetAwaiter().GetResult().Failure;
        That(Detail(detailBody) == ServiceFailure.None, "the recorded detail reads");
        That(Detail(detailBody, new string('c', 64)) == ServiceFailure.InvalidResponse, "a descriptor for another server than the one asked is refused");
        That(Detail(detailBody.Replace("Sam's Sandbox", "Sam's Sandbox!", StringComparison.Ordinal)) == ServiceFailure.InvalidResponse, "a descriptor changed after signing is refused");
        That(Detail(detailBody.Replace("\"descriptorSha256\":\"918ec546", "\"descriptorSha256\":\"018ec546", StringComparison.Ordinal)) == ServiceFailure.InvalidResponse,
            "a pin that is not the served bytes' hash is refused");
        That(Detail(detailBody.Replace("\"relayOnly\":false", "\"relayOnly\":\"no\"", StringComparison.Ordinal)) == ServiceFailure.InvalidResponse, "a listing without its privacy flag is refused");
        That(Detail(detailBody.Replace("\"players\":2", "\"players\":9", StringComparison.Ordinal)) == ServiceFailure.InvalidResponse, "more players than the descriptor allows are refused");

        // A ticket must carry the privacy the joiner asked for: one that says otherwise would let the service treat it as direct.
        var ticketBody = ControlPlaneFixtures.Response("join-ticket").Text("body");
        ServiceFailure Ticket(string body, bool relayOnly) => new ControlPlaneClient(new RecordingTransport(_ => ControlPlaneResponse.Answered(201, Encoding.UTF8.GetBytes(body), true, null)))
            .CreateJoinTicketAsync(ServerId, null, relayOnly, CancellationToken.None).GetAwaiter().GetResult().Failure;
        That(Ticket(ticketBody, true) == ServiceFailure.None, "the recorded relay-only ticket reads");
        That(Ticket(ticketBody, false) == ServiceFailure.InvalidResponse, "a relay-only ticket for a join that allowed direct play is refused");
        That(Ticket(ticketBody.Replace("\"relayOnly\":true", "\"relayOnly\":false", StringComparison.Ordinal), true) == ServiceFailure.InvalidResponse,
            "and a direct ticket for a relay-only join is refused");
    }

    internal static SignedServerDescriptor Descriptor(string exchange)
    {
        var text = JsonDocument.Parse(ControlPlaneFixtures.Request(exchange).Text("body")).RootElement.Text("descriptor");
        That(SignedServerDescriptor.TryVerify(Encoding.UTF8.GetBytes(text), ServerId, out var descriptor, out _), exchange + ": the recorded descriptor verifies");
        return descriptor!;
    }

    private static void Matches(string id, ControlPlaneRequest sent)
    {
        var request = ControlPlaneFixtures.Request(id);
        That(sent.MethodText == request.Text("method") && sent.Path == request.Text("path"), id + ": the method and path (" + sent.MethodText + " " + sent.Path + ")");
        That(sent.Bearer == ControlPlaneFixtures.Bearer(id), id + ": the credential");
        var body = ControlPlaneFixtures.Body(id);
        That(body == null ? sent.Body == null : sent.Body != null && sent.Body.SequenceEqual(body),
            id + ": the body byte for byte (" + (sent.Body == null ? "none" : Encoding.UTF8.GetString(sent.Body)) + ")");
    }
}
