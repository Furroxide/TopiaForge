extern alias provider;

using System.Text;
using Aead = provider::TopiaForge.Multiplayer.Transport.Crypto.ChaCha20Poly1305;
using CipherState = provider::TopiaForge.Multiplayer.Transport.Crypto.CipherState;
using NoiseHandshake = provider::TopiaForge.Multiplayer.Transport.Crypto.NoiseHandshakeXX;
using ReplayWindow = provider::TopiaForge.Multiplayer.Transport.Crypto.ReplayWindow;
using X25519 = provider::TopiaForge.Multiplayer.Transport.Crypto.X25519;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The Noise XX handshake, its cipher states and the datagram replay window. The fixed-key transcript comes from an
/// independent Noise implementation over OpenSSL primitives, so the provider must match it byte for byte.
/// </summary>
internal static class NoiseHandshakeTests
{
    private static readonly byte[] Prologue = Encoding.ASCII.GetBytes("TopiaForge Noise XX reference vector");

    internal static void NoiseXXMatchesAnIndependentTranscript()
    {
        var initiatorStatic = Hex("483ac9dccb949b68b5403dc4e275aa970eb9d806dcbef2ffe4e48871d4ceecee");
        var initiatorEphemeral = Hex("620be318ccfe9b1685ba2954a24c1b57f2cec0e57e5e2e0ae7c831e91d14309f");
        var responderStatic = Hex("3b89c555c37e6ed93a5a7a6226b02a3a276fa974d6a963b0ccd654b04365d89a");
        var responderEphemeral = Hex("4f59b20a2e70ef843c89814a5b15c851882fccafd2ffbde0f825b60d0f90f5ae");
        using var initiator = new NoiseHandshake(true, initiatorStatic, Prologue, () => initiatorEphemeral);
        using var responder = new NoiseHandshake(false, responderStatic, Prologue, () => responderEphemeral);

        var expected = new[]
        {
            "e91cb54a921625d460fb12619c852bb40a21b4e55077827fd19a9bca6607cd5c6669727374207061796c6f6164",
            "83b5a6ba9c40fbdba070e69b84ff3d88191fba6b8c604849de918e5a5743ef3ca9d1a673f0c595821f5a97f1470cd6e39ae60f084bdc3d" +
            "f3771d0154a1553cac24d2125b3f455b5633129d50d7506e078faf73f3b619ca719267800f3a97b0c37e5ff67784abfa31dbf9c5d8cdef",
            "0f4028e303a90b0eb2d45ffd55881714f5fd9386f893aa723d9f2faf7f176975192428bd4d5915413eaeed212412fc391920bf74d0a1ff" +
            "8bd2d50236abfcad8c22c55290efca6ece69c097be12"
        };
        var payloads = new[] { "first payload", "second payload", "third payload" };
        var writers = new[] { initiator, responder, initiator };
        var readers = new[] { responder, initiator, responder };
        for (var index = 0; index < 3; index++)
        {
            var message = writers[index].WriteMessage(Encoding.ASCII.GetBytes(payloads[index]));
            AssertHex(message, expected[index], "handshake message " + (index + 1));
            Assert(readers[index].TryReadMessage(message, out var payload) && Encoding.ASCII.GetString(payload) == payloads[index],
                "handshake message " + (index + 1) + " reads back its payload");
        }

        Assert(initiator.IsComplete && responder.IsComplete, "both sides complete after three messages");
        AssertHex(initiator.HandshakeHash, "b2a1cab6f35b5aea0b4489088ed8afaddfb617b2ba0e9bd9eb127c96eb16ea16", "the initiator's handshake hash");
        Assert(responder.HandshakeHash.SequenceEqual(initiator.HandshakeHash), "both sides bind the same transcript");
        Assert(initiator.RemoteStaticKey.SequenceEqual(X25519.DerivePublicKey(responderStatic)) &&
               responder.RemoteStaticKey.SequenceEqual(X25519.DerivePublicKey(initiatorStatic)),
            "each side learns the other's static key");

        var initiatorKeys = initiator.Split();
        var responderKeys = responder.Split();
        var transport = new[]
        {
            (initiatorKeys.Send, responderKeys.Receive, "initiator to responder 0",
                "924b65f57f3d86bfecad8d0e0fc25c611bcb2608597501506178a174296794e3ec490a4618eac09f"),
            (responderKeys.Send, initiatorKeys.Receive, "responder to initiator 0",
                "d0791d19da6608c7978215ab868b27778627838bc6fdbe7996befd8cbac0f71c9e2733fd4591c840"),
            (initiatorKeys.Send, responderKeys.Receive, "initiator to responder 1",
                "e53b1efb510464636fe0eb5fd15206a5a923179bf09da6007b2dc544c8ac16e1718eca486b07d080")
        };
        foreach (var (sender, receiver, text, ciphertext) in transport)
        {
            var sealedMessage = sender.EncryptWithAd(Array.Empty<byte>(), Encoding.ASCII.GetBytes(text));
            AssertHex(sealedMessage, ciphertext, "transport message '" + text + "'");
            Assert(receiver.TryDecryptWithAd(Array.Empty<byte>(), sealedMessage, out var opened) && Encoding.ASCII.GetString(opened) == text,
                "transport message '" + text + "' opens");
        }
    }

    /// <summary>
    /// The Noise_XX_25519_ChaChaPoly_SHA256 case of the Cacophony test vectors: `vectors/cacophony.txt` in
    /// haskell-cryptography/cacophony at commit 8ee9d41e34a1a596cfa3ab12aa4069ff87dc1247 (blob b8a271ed, Unlicense).
    /// After the handshake, messages keep alternating: responder, initiator, responder.
    /// </summary>
    internal static void NoiseXXMatchesTheCacophonyVectors()
    {
        var prologue = Hex("4a6f686e2047616c74");
        var initiatorStatic = Hex("e61ef9919cde45dd5f82166404bd08e38bceb5dfdfded0a34c8df7ed542214d1");
        var initiatorEphemeral = Hex("893e28b9dc6ca8d611ab664754b8ceb7bac5117349a4439a6b0569da977c464a");
        var responderStatic = Hex("4a3acbfdb163dec651dfa3194dece676d437029c62a408b4c5ea9114246e4893");
        var responderEphemeral = Hex("bbdb4cdbd309f1a1f2e1456967fe288cadd6f712d65dc7b7793d5e63da6b375b");
        var messages = new[]
        {
            ("4c756477696720766f6e204d69736573",
                "ca35def5ae56cec33dc2036731ab14896bc4c75dbb07a61f879f8e3afa4c79444c756477696720766f6e204d69736573"),
            ("4d757272617920526f746862617264",
                "95ebc60d2b1fa672c1f46a8aa265ef51bfe38e7ccb39ec5be34069f14480884381cbad1f276e038c48378ffce2b65285e08d" +
                "6b68aaa3629a5a8639392490e5b9bd5269c2f1e4f488ed8831161f19b7815528f8982ffe09be9b5c412f8a0db50f8814c719" +
                "4e83f23dbd8d162c9326ad"),
            ("462e20412e20486179656b",
                "c7195ffacac1307ff99046f219750fc47693e23c3cb08b89c2af808b444850a80ae475b9df0f169ae80a89be0865b57f58c9" +
                "fea0d4ec82a286427402f113e4b6ae769a1d95941d49b25030"),
            ("4361726c204d656e676572",
                "96763ed773f8e47bb3712f0e29b3060ffc956ffc146cee53d5e1df"),
            ("4a65616e2d426170746973746520536179",
                "3e40f15f6f3a46ae446b253bf8b1d9ffb6ed9b174d272328ff91a7e2e5c79c07f5"),
            ("457567656e2042f6686d20766f6e2042617765726b",
                "eb3f3515110702e047a6c9da4478b6ead94873c11c0f2d710ddb3f09fce024b3a58502ae3f")
        };
        using var initiator = new NoiseHandshake(true, initiatorStatic, prologue, () => initiatorEphemeral);
        using var responder = new NoiseHandshake(false, responderStatic, prologue, () => responderEphemeral);
        for (var index = 0; index < 3; index++)
        {
            var (writer, reader) = index % 2 == 0 ? (initiator, responder) : (responder, initiator);
            var (payload, ciphertext) = messages[index];
            var message = writer.WriteMessage(Hex(payload));
            AssertHex(message, ciphertext, "Cacophony handshake message " + (index + 1));
            Assert(reader.TryReadMessage(message, out var received) && received.SequenceEqual(Hex(payload)),
                "Cacophony handshake message " + (index + 1) + " reads back its payload");
        }

        AssertHex(initiator.HandshakeHash, "c8e5f64e846193be2a834104c2a009868d6c9f3bd3c186299888b488b2f1f58e", "the Cacophony handshake hash");
        var initiatorKeys = initiator.Split();
        var responderKeys = responder.Split();
        for (var index = 3; index < messages.Length; index++)
        {
            var (sender, receiver) = index % 2 == 0 ? (initiatorKeys.Send, responderKeys.Receive) : (responderKeys.Send, initiatorKeys.Receive);
            var (payload, ciphertext) = messages[index];
            var sealedMessage = sender.EncryptWithAd(Array.Empty<byte>(), Hex(payload));
            AssertHex(sealedMessage, ciphertext, "Cacophony transport message " + (index + 1));
            Assert(receiver.TryDecryptWithAd(Array.Empty<byte>(), sealedMessage, out var opened) && opened.SequenceEqual(Hex(payload)),
                "Cacophony transport message " + (index + 1) + " opens");
        }
    }

    internal static void NoiseXXRejectsTamperingAndMismatches()
    {
        Assert(FailsAt(2, (index, message) => index == 2 ? Flip(message, 40) : message), "an altered encrypted static fails message 2");
        Assert(FailsAt(3, (index, message) => index == 3 ? Flip(message, 10) : message), "an altered message 3 fails");
        Assert(FailsAt(2, (index, message) => index == 1 ? Flip(message, 3) : message),
            "an altered ephemeral in message 1 surfaces as a failure of message 2");
        Assert(FailsAt(2, (_, message) => message, responderPrologue: Encoding.ASCII.GetBytes("another prologue")),
            "a prologue mismatch fails the first authenticated message");
        Assert(FailsAt(2, (index, message) => index == 2 ? message.Take(message.Length - 1).ToArray() : message),
            "a truncated message fails");

        using var initiator = new NoiseHandshake(true, X25519.GeneratePrivateKey(), Prologue);
        using var responder = new NoiseHandshake(false, X25519.GeneratePrivateKey(), Prologue);
        AssertThrows<InvalidOperationException>(() => responder.WriteMessage(Array.Empty<byte>()), "the responder cannot write first");
        AssertThrows<InvalidOperationException>(() => initiator.TryReadMessage(new byte[48], out _), "the initiator cannot read first");
        AssertThrows<InvalidOperationException>(() => initiator.Split(), "keys exist only after the handshake");

        // A low-order ephemeral key is caught when the responder mixes it into its first Diffie-Hellman.
        using var lowOrderTarget = new NoiseHandshake(false, X25519.GeneratePrivateKey(), Prologue);
        Assert(lowOrderTarget.TryReadMessage(new byte[32], out _), "message 1 itself carries no authentication");
        AssertThrows<InvalidOperationException>(() => lowOrderTarget.WriteMessage(Array.Empty<byte>()), "a low-order ephemeral key ends the handshake");
        Assert(!lowOrderTarget.IsComplete && !lowOrderTarget.IsMyTurn, "nothing more can be written or read");

        var oversized = new byte[NoiseHandshake.MaximumMessageSize];
        AssertThrows<ArgumentException>(() => initiator.WriteMessage(oversized), "a handshake message stays within 65,535 bytes");
        Assert(initiator.IsMyTurn, "an oversized payload is refused before the transcript changes");
    }

    internal static void CipherStateFollowsTheNoiseRules()
    {
        var keyless = new CipherState();
        Assert(keyless.EncryptWithAd(Array.Empty<byte>(), new byte[] { 1, 2 }).SequenceEqual(new byte[] { 1, 2 }),
            "without a key a cipher state passes data through");

        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var sender = new CipherState();
        var receiver = new CipherState();
        sender.InitializeKey(key);
        receiver.InitializeKey(key);
        var associatedData = Encoding.ASCII.GetBytes("ad");
        var first = sender.EncryptWithAd(associatedData, Encoding.ASCII.GetBytes("same"));
        var second = sender.EncryptWithAd(associatedData, Encoding.ASCII.GetBytes("same"));
        Assert(!first.SequenceEqual(second) && sender.Nonce == 2, "each message takes the next nonce");
        Assert(!receiver.TryDecryptWithAd(associatedData, second, out _) && receiver.Nonce == 0,
            "an out-of-order message fails without moving the implicit nonce");
        Assert(receiver.TryDecryptWithAd(associatedData, first, out _) && receiver.TryDecryptWithAd(associatedData, second, out _),
            "in order, both messages open");

        var datagram = new byte[3 + CipherState.TagSize];
        sender.Seal(41, associatedData, new byte[] { 7, 8, 9 }, 0, 3, datagram, 0);
        var opened = new byte[3];
        Assert(receiver.TryOpen(41, associatedData, datagram, 0, datagram.Length, opened, 0, out _) && opened.SequenceEqual(new byte[] { 7, 8, 9 }),
            "a datagram opens under its explicit nonce");
        Assert(!receiver.TryOpen(42, associatedData, datagram, 0, datagram.Length, new byte[3], 0, out _), "another nonce fails");
        AssertThrows<InvalidOperationException>(
            () => sender.Seal(CipherState.ReservedNonce, associatedData, new byte[1], 0, 1, new byte[1 + CipherState.TagSize], 0),
            "the reserved nonce never seals a message");
        Assert(!receiver.TryOpen(CipherState.ReservedNonce, associatedData, datagram, 0, datagram.Length, new byte[3], 0, out _),
            "the reserved nonce never opens one");

        // REKEY: the new key is the first 32 bytes of 32 zeros sealed under the reserved nonce.
        var reference = new byte[32 + Aead.TagSize];
        new Aead(key).Seal(Hex("00000000ffffffffffffffff"), Array.Empty<byte>(), new byte[32], 0, 32, reference, 0);
        var expectedKey = reference.Take(32).ToArray();
        sender.Rekey();
        receiver.Rekey();
        var probe = new CipherState();
        probe.InitializeKey(expectedKey);
        var afterRekey = sender.EncryptWithAd(associatedData, Encoding.ASCII.GetBytes("rekeyed"));
        Assert(sender.Nonce == 3 && receiver.TryDecryptWithAd(associatedData, afterRekey, out _),
            "both sides rekey in step and the implicit nonce carries on");
        var probeOutput = new byte[7];
        Assert(probe.TryOpen(2, associatedData, afterRekey, 0, afterRekey.Length, probeOutput, 0, out _),
            "the rekeyed key is the one Noise defines");
    }

    internal static void ReplayWindowAcceptsEachCounterOnce()
    {
        var window = new ReplayWindow();
        Assert(window.Accept(0) && !window.Accept(0), "a counter is accepted once");
        Assert(window.Accept(5) && window.Accept(3) && window.Accept(4) && !window.Accept(3), "late counters within the window are accepted once");
        Assert(window.CanAccept(1) && window.CanAccept(1), "checking does not consume a counter");

        Assert(window.Accept(ReplayWindow.Size + 5), "the window advances");
        Assert(!window.Accept(5) && !window.CanAccept(4), "counters a full window behind are too old, accepted or not");
        Assert(window.Accept(6) && !window.Accept(6), "a counter just inside the window is still accepted once");

        Assert(window.Accept(1_000_000) && window.Accept(999_999) && !window.Accept(1_000_000) && window.Highest == 1_000_000,
            "a large jump forgets everything older");

        // Every counter of a shuffled run is accepted exactly once, and its replay is refused.
        var shuffled = new ReplayWindow();
        var random = new Random(7);
        var counters = Enumerable.Range(0, 4000).Select(value => (ulong)value).ToArray();
        for (var index = 0; index < counters.Length; index++)
        {
            var swap = Math.Min(counters.Length - 1, index + random.Next(0, 64));
            (counters[index], counters[swap]) = (counters[swap], counters[index]);
        }

        foreach (var counter in counters)
        {
            Assert(shuffled.Accept(counter) && !shuffled.Accept(counter), "counter " + counter + " is accepted once");
        }
    }

    /// <summary>Runs a handshake with fresh keys, letting <paramref name="alter"/> change message n (1-3) in flight.</summary>
    private static bool FailsAt(int messageNumber, Func<int, byte[], byte[]> alter, byte[]? responderPrologue = null)
    {
        using var initiator = new NoiseHandshake(true, X25519.GeneratePrivateKey(), Prologue);
        using var responder = new NoiseHandshake(false, X25519.GeneratePrivateKey(), responderPrologue ?? Prologue);
        var writers = new[] { initiator, responder, initiator };
        var readers = new[] { responder, initiator, responder };
        for (var index = 0; index < 3; index++)
        {
            var message = alter(index + 1, writers[index].WriteMessage(Encoding.ASCII.GetBytes("payload " + index)));
            if (!readers[index].TryReadMessage(message, out _))
            {
                return index + 1 == messageNumber && !readers[index].IsComplete && !readers[index].IsMyTurn;
            }
        }

        return false;
    }

    private static byte[] Flip(byte[] message, int index)
    {
        var copy = (byte[])message.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value);

    private static void AssertHex(byte[] actual, string expected, string what) =>
        Assert(Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase),
            what + " (got " + Convert.ToHexString(actual).ToLowerInvariant() + ")");

    private static void AssertThrows<T>(Action action, string what) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException("Assertion failed: expected " + typeof(T).Name + " because " + what + ".");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
