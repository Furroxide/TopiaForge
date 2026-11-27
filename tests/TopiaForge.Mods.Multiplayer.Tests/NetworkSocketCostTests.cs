extern alias provider;

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// What a datagram costs the game's socket on the network thread, real sockets on the loopback interface: the IPv4 socket
/// a system without IPv6 gets allocates no more per datagram than the dual-mode one.
/// </summary>
internal static class NetworkSocketCostTests
{
    private const int Rounds = 100;

    /// <summary>
    /// A send on the IPv4 socket reads the plain IPv4 endpoint straight from the path and adds nothing to it and the
    /// runtime's own send, so it allocates no more than the dual-mode socket's endpoint in the stack's form and that send.
    /// A receive keys the plain source the IPv4 socket hears at no more cost than the mapped one a dual-mode socket hears.
    /// Where this system has IPv6, a send on the dual-mode socket adds nothing to its endpoint and the runtime's send either.
    /// </summary>
    internal static void TheIPv4SocketCostsNoMorePerDatagram()
    {
        var payload = new byte[] { 1 };
        var buffer = new byte[TransportOptions.MaximumDatagramBytes];
        using var receiver = new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6: true);
        using var ipv4 = new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6: true);
        using var runtime = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var plain = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);
        var mapped = new IPEndPoint(IPAddress.Loopback.MapToIPv6(), receiver.LocalPort);
        var path = UdpPaths.For(plain);

        var endpoint = AllocationPasses.Fewest(() => EndPoints(path));
        var runtimeSend = AllocationPasses.Fewest(() => Drained(RuntimeSends(runtime, plain, payload), receiver, buffer));
        var send = AllocationPasses.Fewest(() => Drained(Sends(ipv4, path, payload), receiver, buffer));
        That(send <= endpoint + runtimeSend,
            "a send on the IPv4 socket allocates no more than the dual-mode socket's endpoint and the runtime's send (" + send +
            " bytes for " + Rounds + " sends, against " + endpoint + " and " + runtimeSend + ")");

        var plainKey = AllocationPasses.Fewest(() => Keys(plain));
        var mappedKey = AllocationPasses.Fewest(() => Keys(mapped));
        That(plainKey <= mappedKey,
            "keying the plain source the IPv4 socket hears allocates no more than keying the mapped one a dual-mode socket hears (" +
            plainKey + " and " + mappedKey + " bytes for " + Rounds + ")");

        if (!Socket.OSSupportsIPv6) return;
        using var dual = new UdpSocketDriver(0, IPAddress.Loopback);
        using var dualRuntime = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        var dualRuntimeSend = AllocationPasses.Fewest(() => Drained(RuntimeSends(dualRuntime, mapped, payload), receiver, buffer));
        var dualSend = AllocationPasses.Fewest(() => Drained(Sends(dual, path, payload), receiver, buffer));
        That(!dual.IPv4Only && dualSend <= endpoint + dualRuntimeSend,
            "a send on the dual-mode socket allocates nothing beyond its endpoint and the runtime's send (" + dualSend + " bytes for " +
            Rounds + " sends, against " + endpoint + " and " + dualRuntimeSend + ")");
    }

    /// <summary>Reads <paramref name="path"/>'s endpoint in the stack's form, as the dual-mode socket sends to it; returns the bytes allocated.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long EndPoints(TransportPath path)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < Rounds; round++) UdpPaths.TryGetEndPoint(path, out _);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Sends straight through <paramref name="socket"/> to one endpoint; returns the bytes the runtime's sends allocated.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RuntimeSends(Socket socket, IPEndPoint to, byte[] payload)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < Rounds; round++) socket.SendTo(payload, 0, payload.Length, SocketFlags.None, to);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Sends through <paramref name="driver"/> on <paramref name="path"/>; returns the bytes the sends allocated.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Sends(UdpSocketDriver driver, TransportPath path, byte[] payload)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < Rounds; round++) driver.Send(path, payload, 0, payload.Length);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Keys <paramref name="source"/> as a received datagram's path is keyed; returns the bytes allocated.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Keys(IPEndPoint source)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < Rounds; round++) UdpPaths.For(source);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Empties <paramref name="receiver"/> after a measured pass, outside its measurement, and passes the pass's bytes on.</summary>
    private static long Drained(long allocated, UdpSocketDriver receiver, byte[] buffer)
    {
        Thread.Sleep(10);
        while (receiver.TryReceive(buffer, out _, out _))
        {
        }

        return allocated;
    }
}
