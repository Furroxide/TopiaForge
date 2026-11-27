extern alias provider;

using System.Net;
using System.Net.Sockets;
using System.Text;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// What a joining player reads in this game's own words stays this game's, whatever a host sends: a descriptor refusal
/// repeats no character the host chose, and a host whose runtime cannot decide yet says so, rather than blaming the
/// player's packages.
/// </summary>
internal static class HostTextTests
{
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');

    internal static void DescriptorRefusalsAreThisGamesOwnWords()
    {
        var cases = new[]
        {
            ("<x>", "The host refused the request."),
            ("1.2", "The host refused the request."),
            ("404", "The host is not serving a session."),
            ("429", "The host is busy; try again shortly."),
            ("503", "The host is busy; try again shortly.")
        };
        foreach (var (status, expected) in cases)
        {
            var fetched = FetchFrom("TFD1 " + status + "\n");
            That(fetched.Outcome == DescriptorFetchOutcome.Refused && fetched.Detail == expected && !fetched.Detail.Contains(status, StringComparison.Ordinal),
                "a host answering " + status + " is refused in this game's words (" + fetched.Detail + ")");
        }
    }

    internal static void HostsNotReadySaySoInACodeOfTheirOwn()
    {
        using var rig = new ControlRig(154, Sandbox);
        rig.Host.UseRuntime(new RuntimeAdmissionTests.ThrowingAdmission(rig.Host.Runtime));
        rig.StartHosting(new InternalHostSettings("Not yet", 4));
        var guest = rig.AddPeer("guest", Sandbox);
        That(guest.Control.Join(guest.Target(), new InternalJoinSettings()).Succeeded, "the guest asks to join");
        That(rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "and is turned away");
        var status = guest.Control.GetStatus();
        That(status.JoinFailure == InternalLiveFailure.Rejected && status.RejectionCode == InternalAdmissionCode.NotReady &&
             status.JoinDetail == "The host is not ready for players yet; try again shortly." && status.HostMessage == "The host is not ready for players yet.",
            "because the host is not ready, not because its packages differ (" + status.RejectionCode + ": " + status.JoinDetail + ")");
    }

    /// <summary>Fetches a descriptor from a host on loopback that answers <paramref name="answer"/> to any request.</summary>
    private static DescriptorFetch FetchFrom(string answer)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serving = Task.Run(() =>
            {
                using var client = listener.AcceptTcpClient();
                var stream = client.GetStream();
                var request = new byte[128];
                _ = stream.Read(request, 0, request.Length);
                var bytes = Encoding.ASCII.GetBytes(answer);
                stream.Write(bytes, 0, bytes.Length);
            });
            var fetched = DescriptorClient.FetchAsync(new IPEndPoint(IPAddress.Loopback, port), null, TimeSpan.FromSeconds(3), CancellationToken.None)
                .GetAwaiter().GetResult();
            serving.Wait(TimeSpan.FromSeconds(3));
            return fetched;
        }
        finally
        {
            listener.Stop();
        }
    }
}
