extern alias provider;

using System.Text;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Password proofs, player keys, the local identity and trust on first use.</summary>
internal static class NetworkIdentityTests
{
    internal static void SecretsMatchTheSharedFixtures()
    {
        var fixture = NetworkFixtures.Load("secrets/cases.json");
        foreach (var vector in fixture.GetProperty("pbkdf2").EnumerateArray())
        {
            var key = PasswordProof.Pbkdf2Sha256(
                NetworkFixtures.Utf8(vector.Text("password")),
                NetworkFixtures.Utf8(vector.Text("salt")),
                vector.GetProperty("iterations").GetInt32(),
                vector.GetProperty("length").GetInt32());
            That(Convert.ToHexString(key).ToLowerInvariant() == vector.Text("key"), "PBKDF2-HMAC-SHA256 matches " + vector.Text("source"));
        }

        var proofs = fixture.GetProperty("passwordProof");
        That(WireText.TryParseBase64Url(proofs.Text("hostTransportKey"), 32, out var hostKey), "the host key is canonical");
        var handshakeHash = NetworkFixtures.Hex(proofs.Text("handshakeHash"));
        foreach (var testCase in proofs.GetProperty("cases").EnumerateArray())
        {
            var key = PasswordProof.DeriveKey(testCase.Text("password"), hostKey);
            That(Convert.ToHexString(key).ToLowerInvariant() == testCase.Text("key"), testCase.Text("id") + ": the proof key matches");
            var proof = PasswordProof.Prove(key, handshakeHash);
            That(Convert.ToHexString(proof).ToLowerInvariant() == testCase.Text("proof") && PasswordProof.Verify(key, handshakeHash, proof),
                testCase.Text("id") + ": the proof matches and verifies");
        }

        var right = PasswordProof.DeriveKey("correct horse battery staple", hostKey);
        var wrong = PasswordProof.DeriveKey("correct horse battery stapler", hostKey);
        var otherHash = (byte[])handshakeHash.Clone();
        otherHash[0] ^= 1;
        That(!PasswordProof.Verify(right, handshakeHash, PasswordProof.Prove(wrong, handshakeHash)), "a wrong password fails");
        That(!PasswordProof.Verify(right, handshakeHash, PasswordProof.Prove(right, otherHash)), "a proof for another handshake fails");
        That(!PasswordProof.Verify(right, handshakeHash, null) && !PasswordProof.Verify(right, handshakeHash, new byte[31]), "a missing or short proof fails");
        Throws<ArgumentException>(() => PasswordProof.DeriveKey(new string('p', 65), hostKey), "passwords are at most 64 characters");
        Throws<ArgumentException>(() => PasswordProof.DeriveKey("tab\there", hostKey), "passwords hold no control characters");

        var players = fixture.GetProperty("playerKeys");
        var secret = NetworkFixtures.Hex(players.Text("hostSecret"));
        var keys = new HashSet<string>();
        foreach (var testCase in players.GetProperty("cases").EnumerateArray())
        {
            That(WireText.TryParseBase64Url(testCase.Text("clientTransportKey"), 32, out var client), "the client key is canonical");
            var derived = PlayerKeys.Derive(secret, client);
            That(derived == testCase.Text("playerKey"), "the player key matches its fixture");
            keys.Add(derived);
        }

        That(keys.Count == 2, "different players get different keys");
        var otherSecret = (byte[])secret.Clone();
        otherSecret[0] ^= 1;
        That(WireText.TryParseBase64Url(players.GetProperty("cases")[0].Text("clientTransportKey"), 32, out var first) &&
             PlayerKeys.Derive(otherSecret, first) != players.GetProperty("cases")[0].Text("playerKey"),
            "another host gives the same player another key");
        _ = new MultiplayerPlayerKey(PlayerKeys.Derive(secret, first));
    }

    internal static void IdentitiesPersistAndSurviveDamage()
    {
        var files = new MemoryFiles();
        var store = new IdentityStore(files, () => 1_800_000_000);
        var replaced = new List<string>();
        store.IdentityReplaced += replaced.Add;
        using var created = store.LoadOrCreateAsync(CancellationToken.None).GetAwaiter().GetResult();
        That(files.Contents.ContainsKey(IdentityStore.IdentityPath) && created.CreatedAt == 1_800_000_000, "the first load creates and saves an identity");
        using var loaded = store.LoadOrCreateAsync(CancellationToken.None).GetAwaiter().GetResult();
        That(loaded.Fingerprint == created.Fingerprint && loaded.ServerId == created.ServerId &&
             loaded.CopyTransportPrivateKey().SequenceEqual(created.CopyTransportPrivateKey()) && replaced.Count == 0,
            "the next load returns the same identity");
        That(created.Fingerprint == Convert.ToHexString(created.TransportPublicKey).ToLowerInvariant() &&
             created.ServerId == ServerDescriptor.ServerIdOf(created.SigningPublicKey), "the fingerprint and server id derive from the public keys");
        That(files.LastRead != null && files.LastRead.All(value => value == 0) && files.LastWritten.All(written => written.All(value => value == 0)),
            "every buffer that held the identity's secrets is zeroed once it is read or written");
        var exposed = created.TransportPublicKey;
        exposed[0] ^= 0xFF;
        var signing = created.SigningPublicKey;
        signing[0] ^= 0xFF;
        That(created.Fingerprint == Convert.ToHexString(created.TransportPublicKey).ToLowerInvariant() &&
             created.ServerId == ServerDescriptor.ServerIdOf(created.SigningPublicKey), "changing a returned key changes nothing inside the identity");

        files.Contents[IdentityStore.IdentityPath] = Encoding.UTF8.GetBytes("{\"schema\":\"topiaforge.identity/1\"}");
        using var fresh = store.LoadOrCreateAsync(CancellationToken.None).GetAwaiter().GetResult();
        That(fresh.Fingerprint != created.Fingerprint && replaced.Count == 1 && files.Contents.ContainsKey(IdentityStore.UnreadablePath),
            "an unreadable file is set aside and replaced");
        That(replaced[0].IndexOf(created.Fingerprint, StringComparison.Ordinal) < 0, "the notice names no key");
        using var rotated = store.RotateAsync(CancellationToken.None).GetAwaiter().GetResult();
        That(rotated.Fingerprint != fresh.Fingerprint && rotated.ServerId != fresh.ServerId, "rotation creates a new identity");

        var text = Encoding.UTF8.GetString(rotated.Serialize());
        That(LocalIdentity.TryParse(rotated.Serialize(), out var parsed, out _) && parsed!.Fingerprint == rotated.Fingerprint, "an identity round-trips");
        That(!LocalIdentity.TryParse(Encoding.UTF8.GetBytes(text.Replace("identity/1", "identity/2")), out _, out _), "another schema is refused");
        rotated.Dispose();
        Throws<ObjectDisposedException>(() => rotated.CopySigningSeed(), "a disposed identity gives up no secrets");
        That(rotated.Fingerprint.Length == 64, "its public values stay readable");
    }

    internal static void KnownHostsRememberServersNotAddresses()
    {
        var hosts = new KnownHosts();
        var first = new string('1', 64);
        var second = new string('2', 64);
        That(hosts.Check("192.168.1.20", 27250, first) == KnownHostCheck.Unknown, "an address starts unknown");
        hosts.Remember("192.168.1.20", 27250, first, 100);
        That(hosts.Check("192.168.1.20", 27250, first) == KnownHostCheck.Match, "the same server matches");
        That(hosts.Check("192.168.1.20", 27250, second) == KnownHostCheck.Mismatch, "another server at that address is a mismatch");
        That(hosts.Check("192.168.1.20", 27251, first) == KnownHostCheck.Unknown, "another port is another target");
        hosts.Remember("Play.Example.org", 27250, second, 101);
        That(hosts.Check("play.example.ORG", 27250, second) == KnownHostCheck.Match, "names compare after normalization");
        hosts.Remember("::ffff:10.0.0.7", 1, first, 102);
        That(hosts.Check("10.0.0.7", 1, first) == KnownHostCheck.Match, "a mapped address is its IPv4 address");

        var bytes = hosts.Serialize();
        var text = Encoding.UTF8.GetString(bytes);
        That(text.IndexOf("192.168", StringComparison.Ordinal) < 0 && text.IndexOf("example", StringComparison.OrdinalIgnoreCase) < 0 &&
             text.IndexOf("10.0.0.7", StringComparison.Ordinal) < 0, "the file holds no address or name");
        var reloaded = KnownHosts.Parse(bytes);
        That(reloaded.Count == 3 && reloaded.Check("192.168.1.20", 27250, first) == KnownHostCheck.Match, "the record reloads");
        That(reloaded.Forget("192.168.1.20", 27250) && reloaded.Check("192.168.1.20", 27250, second) == KnownHostCheck.Unknown, "a target can be forgotten");
        That(KnownHosts.Parse(Encoding.UTF8.GetBytes("not json")).Count == 0 && KnownHosts.Parse(null).Count == 0, "an unreadable file is an empty record");

        for (var index = 0; index < KnownHosts.MaximumEntries + 10; index++) hosts.Remember("10.1." + (index / 250) + "." + (index % 250 + 1), 27250, first, 200 + index);
        That(hosts.Count == KnownHosts.MaximumEntries && hosts.Check("10.1.1.10", 27250, first) == KnownHostCheck.Match &&
             hosts.Check("192.168.1.20", 27250, first) == KnownHostCheck.Unknown, "the oldest entries give way past the cap");
    }

    private sealed class MemoryFiles : INetworkDataFiles
    {
        internal Dictionary<string, byte[]> Contents { get; } = new();

        /// <summary>Gets the last buffer handed to the store, which the store must zero after use.</summary>
        internal byte[]? LastRead { get; private set; }

        /// <summary>Gets every buffer the store wrote from, which it must zero after writing.</summary>
        internal List<byte[]> LastWritten { get; } = new();

        public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken)
        {
            LastRead = Contents.TryGetValue(relativePath, out var bytes) ? bytes.ToArray() : null;
            return Task.FromResult(LastRead);
        }

        public Task WriteAsync(string relativePath, byte[] content, CancellationToken cancellationToken)
        {
            Contents[relativePath] = content.ToArray();
            if (relativePath == IdentityStore.IdentityPath) LastWritten.Add(content);
            return Task.CompletedTask;
        }
    }
}
