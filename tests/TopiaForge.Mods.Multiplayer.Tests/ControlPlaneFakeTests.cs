extern alias provider;

using System.Text;
using System.Text.Json;
using provider::TopiaForge.Multiplayer.ControlPlane;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Holds the fake service the flows' tests run against to the service's own recordings
/// (tests/fixtures/multiplayer/control-plane/exchanges.json): its error answers are the catalogue's bytes, its status is
/// the recorded one, and every other answer has exactly the members the recorded answer has, at every depth.
/// </summary>
internal static class ControlPlaneFakeTests
{
    internal static void TheFakeServiceAnswersAsTheRecordingsDo()
    {
        var scenario = new NatScenario(7_501);
        var fake = new FakeControlPlane(scenario, 7_501);

        // Errors: the same status, body and Retry-After as the service recorded.
        foreach (var (id, code, retry) in new[]
        {
            ("host-heartbeat-too-soon", "rate-limited", (int?)5), ("invite-resolve-miss", "not-found", null), ("error-unauthorized", "unauthorized", null),
            ("error-malformed", "invalid-request", null), ("error-method", "method-not-allowed", null), ("error-unknown", "not-found", null)
        })
        {
            var recorded = ControlPlaneFixtures.Answer(id);
            var made = fake.Error(code, retry);
            That(made.Status == recorded.Status && made.Body.SequenceEqual(recorded.Body) && made.RetryAfterSeconds == recorded.RetryAfterSeconds && made.IsJson,
                id + ": the fake's error is the recorded one, byte for byte");
        }

        // The recorded host's own keys and descriptor, through the real client.
        var client = new ControlPlaneClient(fake);
        var seed = NetworkFixtures.Hex(NetworkFixtures.Load("descriptor/cases.json").GetProperty("keys")[0].Text("signingSeed"));
        var descriptor = ControlPlaneClientTests.Descriptor("host-register");
        var serverId = ControlPlaneClientTests.ServerId;
        Answer(fake, client.GetStatusAsync(CancellationToken.None), "status", exact: true);
        var challenge = Answer(fake, client.RequestChallengeAsync(descriptor.Descriptor.SigningKey, CancellationToken.None), "host-challenge");
        var registration = Answer(fake, client.RegisterAsync(challenge, descriptor, false, seed, CancellationToken.None), "host-register");
        scenario.Network.RunFor(30_000_000);
        Answer(fake, client.HeartbeatAsync(serverId, registration.SessionToken, 2, CancellationToken.None), "host-heartbeat");
        var invite = Answer(fake, client.CreateInviteAsync(serverId, registration.SessionToken, 3600, CancellationToken.None), "invite-create");
        Answer(fake, client.GetServerAsync(serverId, CancellationToken.None), "server-detail");
        Answer(fake, client.ResolveInviteAsync(invite.Display.ToLowerInvariant(), CancellationToken.None), "invite-resolve");
        var ticket = Answer(fake, client.CreateJoinTicketAsync(serverId, null, true, CancellationToken.None), "join-ticket");
        Answer(fake, client.MintRelayCredentialsAsync(serverId, ticket.Ticket, CancellationToken.None), "turn-credentials-joiner");
        Answer(fake, client.MintRelayCredentialsAsync(serverId, registration.SessionToken, CancellationToken.None), "turn-credentials-host");
        Answer(fake, client.UpdateDescriptorAsync(serverId, registration.SessionToken, ControlPlaneClientTests.Descriptor("host-descriptor"), CancellationToken.None), "host-descriptor");
        That(client.RevokeInviteAsync(serverId, registration.SessionToken, invite.Code, CancellationToken.None).GetAwaiter().GetResult().Succeeded, "invite-revoke: withdrawn");
        That(client.ResolveInviteAsync(invite.Code, CancellationToken.None).GetAwaiter().GetResult().Failure == ServiceFailure.NotFound, "invite-resolve-miss: a withdrawn code misses");
        That(client.EndHostAsync(serverId, registration.SessionToken, CancellationToken.None).GetAwaiter().GetResult().Succeeded, "host-end: ended");
        That(client.HeartbeatAsync(serverId, registration.SessionToken, 0, CancellationToken.None).GetAwaiter().GetResult().Failure == ServiceFailure.Unauthorized,
            "error-unauthorized: an ended session's token opens nothing");
    }

    /// <summary>Checks the fake's last answer against the recorded one: equal bytes when <paramref name="exact"/>, otherwise the same members.</summary>
    private static T Answer<T>(FakeControlPlane fake, Task<ServiceResult<T>> call, string id, bool exact = false) where T : class
    {
        var result = call.GetAwaiter().GetResult();
        That(result.Succeeded, id + ": the fake's answer reads through the client (" + result + ")");
        var answer = fake.LastAnswer!;
        var recorded = ControlPlaneFixtures.Response(id);
        var recordedBody = recorded.Text("body");
        That(answer.Status == recorded.GetProperty("status").GetInt32() && answer.IsJson, id + ": the fake answers with the recorded status");
        if (exact)
        {
            That(Encoding.UTF8.GetString(answer.Body) == recordedBody, id + ": the fake answers exactly as recorded");
            return result.Value!;
        }

        var made = Members(JsonDocument.Parse(answer.Body).RootElement, string.Empty);
        var expected = Members(JsonDocument.Parse(recordedBody).RootElement, string.Empty);
        That(made.SetEquals(expected), id + ": the fake's answer has the recorded members (" + string.Join(", ", made.Except(expected).Concat(expected.Except(made))) + ")");
        return result.Value!;
    }

    private static HashSet<string> Members(JsonElement element, string prefix)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var path = prefix + "." + property.Name;
                members.Add(path);
                members.UnionWith(Members(property.Value, path));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) members.UnionWith(Members(item, prefix + "[]"));
        }

        return members;
    }
}
