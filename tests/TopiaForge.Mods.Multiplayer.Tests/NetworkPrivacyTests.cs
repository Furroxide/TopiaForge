extern alias provider;

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Addresses stay inside the network layer. No other part of the provider, the engine or the SDK names a socket type,
/// nothing mods can see has an address-shaped member, and what the network layer reports outward names a reason, never
/// an address.
/// </summary>
internal static class NetworkPrivacyTests
{
    // Whole names only: the composition root may name the network layer's own types, such as SocketNetworkFactory.
    private static readonly Regex SocketNames = new(@"\bSystem\.Net\b|\bIPAddress\b|\bIPEndPoint\b|\bEndPoint\b|\bSocket\b|\bDns\.", RegexOptions.CultureInvariant);

    internal static void OnlyTheNetworkLayerNamesSockets()
    {
        var root = NetworkFixtures.RepositoryRoot();
        var offenders = new List<string>();
        var provider = Path.Combine(root, "mods", "TopiaForge.Multiplayer");
        // The network layer: the transport, the live flows, the NAT layer, and the TopiaForge service's client.
        var allowed = new[] { "Network", "Transport", "Nat", "ControlPlane", "ThirdParty", "bin", "obj" };
        foreach (var file in Directory.EnumerateFiles(provider, "*.cs", SearchOption.AllDirectories))
        {
            if (!allowed.Contains(Path.GetRelativePath(provider, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0])) Scan(root, file, offenders);
        }

        foreach (var project in new[] { "TopiaForge.Multiplayer.Engine", "TopiaForge.Mods.Multiplayer" })
        {
            var tree = Path.Combine(root, "src", project);
            foreach (var file in Directory.EnumerateFiles(tree, "*.cs", SearchOption.AllDirectories))
            {
                var top = Path.GetRelativePath(tree, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (top != "bin" && top != "obj") Scan(root, file, offenders);
            }
        }

        That(offenders.Count == 0, "only the provider's network layer names socket types (" + string.Join(", ", offenders) + ")");
    }

    internal static void WhatModsSeeHasNoAddress()
    {
        var offenders = new List<string>();
        foreach (var type in typeof(MultiplayerSessionSnapshot).Assembly.GetExportedTypes())
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsAddressShaped(member.Name) || MemberTypes(member).Any(IsSocketType)) offenders.Add(type.Name + "." + member.Name);
            }
        }

        That(offenders.Count == 0, "no SDK member is shaped like an address (" + string.Join(", ", offenders) + ")");
    }

    internal static void TheNetworkLayerReportsReasonsNotAddresses()
    {
        That(NetworkEvent.Failed(new SocketException((int)SocketError.HostUnreachable)).Fault == "SocketException",
            "a network fault names only its exception type, never the message a socket wrote");

        // Nothing listens on this port: the fetch fails and says why, without the address it tried.
        int closed;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            closed = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        var fetch = DescriptorClient.FetchAsync(new IPEndPoint(IPAddress.Loopback, closed), null, TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
        That(fetch.Outcome != DescriptorFetchOutcome.Fetched && fetch.Detail.Length > 0 && !Mentions(fetch.Detail, "127.0.0.1", closed),
            "a failed fetch names a reason, not the address (" + fetch.Detail + ")");

        var resolver = new NetworkTargetResolver(() => null, new KnownHosts(), () => { }, () => 0);
        var missing = resolver.ResolveAsync(JoinTarget.Direct("no-such-host.invalid", 27250), true, null, CancellationToken.None).GetAwaiter().GetResult();
        That(missing.Failure != LiveFailure.None && !missing.Detail.Contains("no-such-host", StringComparison.OrdinalIgnoreCase),
            "an unknown host name is reported without the name (" + missing.Detail + ")");
    }

    internal static void ARelayOnlyResolveContactsOnlyTheLan()
    {
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        SignedServerDescriptor Signed(int index) => SignedServerDescriptor.Sign(
            NetworkDescriptorTests.Build(fixture.GetProperty("valid")[index].GetProperty("descriptor")),
            NetworkFixtures.Hex(fixture.GetProperty("keys")[index].Text("signingSeed")));
        var first = Signed(0);
        var second = Signed(1);
        var serving = first;
        var contacted = new List<IPEndPoint>();
        var known = new KnownHosts();
        var saves = 0;
        var resolver = new NetworkTargetResolver(() => null, known, () => saves++, () => 1_000, (endpoint, _, _) =>
        {
            lock (contacted) contacted.Add(endpoint);
            return Task.FromResult(new DescriptorFetch(DescriptorFetchOutcome.Fetched, serving, string.Empty));
        });
        ResolvedTarget Resolve(string host, bool allowDirect, string? trusted = null) =>
            resolver.ResolveAsync(JoinTarget.Direct(host, 27250), allowDirect, trusted, CancellationToken.None).GetAwaiter().GetResult();

        That(Resolve("203.0.113.5", false).Failure == LiveFailure.DirectNotAllowed && contacted.Count == 0,
            "a relay-only player never contacts an address beyond the LAN, not even for its descriptor");

        // A name is looked up by its own name servers, which would learn something of this player; a relay-only player
        // resolves only names that stay on this machine or the local network.
        foreach (var name in new[] { "play.example.org", "beacon.attacker.example", "xlocal", "local", "localhost.example" })
        {
            That(Resolve(name, false).Failure == LiveFailure.DirectNotAllowed && contacted.Count == 0, "a relay-only player does not look up " + name);
        }

        var lan = Resolve("192.168.1.20", false);
        That(lan.Failure == LiveFailure.None && contacted.Count == 1 && lan.Path!.Kind == provider::TopiaForge.Multiplayer.Transport.TransportPathKind.Lan,
            "but still reaches one on the LAN");
        var direct = Resolve("203.0.113.5", true);
        That(direct.Failure == LiveFailure.None && contacted.Count == 2 && direct.Path!.Kind == provider::TopiaForge.Multiplayer.Transport.TransportPathKind.Direct,
            "and one beyond it once direct play is allowed");

        // Trust on first use: an address is remembered with the server it served, and another server there later is a
        // changed identity unless the player trusts it.
        var savesBefore = saves;
        That(known.Check("203.0.113.5", 27250, first.Descriptor.ServerId) == KnownHostCheck.Match && savesBefore >= 2, "each new address is remembered and saved");
        serving = second;
        var changed = Resolve("203.0.113.5", true);
        That(changed.Failure == LiveFailure.HostIdentityChanged && changed.ServedServerId == second.Descriptor.ServerId,
            "another server at a remembered address is a changed identity, naming the server it serves now");
        That(Resolve("203.0.113.5", true, trusted: first.Descriptor.ServerId).Failure == LiveFailure.HostIdentityChanged,
            "trusting some other server does not let this one in");
        That(Resolve("203.0.113.5", true, trusted: second.Descriptor.ServerId).Failure == LiveFailure.None &&
             known.Check("203.0.113.5", 27250, second.Descriptor.ServerId) == KnownHostCheck.Match,
            "only trusting exactly the server it serves now replaces the record");

        // Names that stay on this machine are still looked up for a relay-only player.
        var before = contacted.Count;
        That(Resolve("localhost", false).Failure != LiveFailure.DirectNotAllowed && contacted.Count > before, "localhost resolves on this machine");
    }

    private static void Scan(string root, string file, List<string> offenders)
    {
        var match = SocketNames.Match(File.ReadAllText(file));
        if (match.Success) offenders.Add(Path.GetRelativePath(root, file) + " (" + match.Value + ")");
    }

    private static bool IsAddressShaped(string name) =>
        name.Contains("Address", StringComparison.Ordinal) || name.Contains("EndPoint", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Port", StringComparison.Ordinal) || name.EndsWith("Ip", StringComparison.Ordinal) || name.Contains("Hostname", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
    {
        PropertyInfo property => new[] { property.PropertyType },
        FieldInfo field => new[] { field.FieldType },
        MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
        ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
        EventInfo handler => handler.EventHandlerType == null ? Array.Empty<Type>() : new[] { handler.EventHandlerType },
        _ => Array.Empty<Type>(),
    };

    private static bool IsSocketType(Type type)
    {
        if (type.HasElementType) return IsSocketType(type.GetElementType()!);
        if (type.IsGenericType && type.GetGenericArguments().Any(IsSocketType)) return true;
        return type.Namespace != null && (type.Namespace == "System.Net" || type.Namespace.StartsWith("System.Net.", StringComparison.Ordinal));
    }

    private static bool Mentions(string text, string address, int port) =>
        text.Contains(address, StringComparison.Ordinal) || text.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
