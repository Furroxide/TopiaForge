extern alias provider;

using System.Text;
using Aead = provider::TopiaForge.Multiplayer.Transport.Crypto.ChaCha20Poly1305;
using Ed25519 = provider::TopiaForge.Multiplayer.Transport.Crypto.Ed25519;
using Hkdf = provider::TopiaForge.Multiplayer.Transport.Crypto.Hkdf;
using X25519 = provider::TopiaForge.Multiplayer.Transport.Crypto.X25519;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The provider's transport primitives against published test vectors: RFC 7748 (X25519), RFC 8032 (Ed25519),
/// RFC 8439 (ChaCha20-Poly1305) and RFC 5869 (HKDF-SHA256). Each vector was also checked against OpenSSL.
/// </summary>
internal static class TransportCryptoVectorTests
{
    internal static void X25519MatchesRfc7748()
    {
        // Section 5.2: two scalar multiplications, then the iterated function.
        AssertHex(X25519.ScalarMultiply(
                Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
                Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c")),
            "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552", "section 5.2, first vector");
        AssertHex(X25519.ScalarMultiply(
                Hex("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d"),
                Hex("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493")),
            "95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957", "section 5.2, second vector");
        var k = Hex("0900000000000000000000000000000000000000000000000000000000000000");
        var u = (byte[])k.Clone();
        for (var iteration = 1; iteration <= 1000; iteration++)
        {
            var next = X25519.ScalarMultiply(k, u);
            u = k;
            k = next;
            if (iteration == 1) AssertHex(k, "422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079", "one iteration");
        }

        AssertHex(k, "684cf59ba83309552800ef566f2f4d3c1c3887c49360e3875f2eb94d99532c51", "1,000 iterations");

        // Section 6.1: the Diffie-Hellman example.
        var alicePrivate = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bobPrivate = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var alicePublic = X25519.DerivePublicKey(alicePrivate);
        var bobPublic = X25519.DerivePublicKey(bobPrivate);
        AssertHex(alicePublic, "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a", "Alice's public key");
        AssertHex(bobPublic, "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f", "Bob's public key");
        var aliceShared = new byte[X25519.KeySize];
        var bobShared = new byte[X25519.KeySize];
        Assert(X25519.TryAgree(alicePrivate, bobPublic, aliceShared) && X25519.TryAgree(bobPrivate, alicePublic, bobShared),
            "both sides agree");
        AssertHex(aliceShared, "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742", "Alice's shared secret");
        AssertHex(bobShared, "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742", "Bob's shared secret");

        // Section 6.1 also requires refusing the all-zero secret a low-order point produces.
        foreach (var lowOrder in new[] { new byte[32], Hex("0100000000000000000000000000000000000000000000000000000000000000") })
        {
            Assert(!X25519.TryAgree(alicePrivate, lowOrder, new byte[X25519.KeySize]), "a low-order point is refused");
        }

        var ours = X25519.GeneratePrivateKey();
        var theirs = X25519.GeneratePrivateKey();
        var oursShared = new byte[X25519.KeySize];
        var theirsShared = new byte[X25519.KeySize];
        Assert(X25519.TryAgree(ours, X25519.DerivePublicKey(theirs), oursShared) &&
               X25519.TryAgree(theirs, X25519.DerivePublicKey(ours), theirsShared) &&
               oursShared.SequenceEqual(theirsShared) && !ours.SequenceEqual(theirs),
            "freshly generated keys agree on one secret");
    }

    internal static void Ed25519MatchesRfc8032()
    {
        var vectors = new[]
        {
            ("TEST 1", "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
                "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
                "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
            ("TEST 2", "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
                "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
                "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
            ("TEST 3", "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
                "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82",
                "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"),
            ("TEST SHA(abc)", "833fe62409237b9d62ec77587520911e9a759cec1d19755b7da901b96dca3d42",
                "ec172b93ad5e563bf4932c70e1245034c35467ef2efd4d64ebf819683467e2bf",
                "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
                "dc2a4459e7369633a52b1bf277839a00201009a3efbf3ecb69bea2186c26b58909351fc9ac90b3ecfdfbc7c66431e0303dca179c138ac17ad9bef1177331a704")
        };
        foreach (var (name, secret, publicKey, message, signature) in vectors)
        {
            var secretKey = Hex(secret);
            var messageBytes = Hex(message);
            AssertHex(Ed25519.DerivePublicKey(secretKey), publicKey, name + " public key");
            var signed = Ed25519.Sign(secretKey, messageBytes);
            AssertHex(signed, signature, name + " signature");
            Assert(Ed25519.Verify(signed, Hex(publicKey), messageBytes), name + " verifies");
            var forged = (byte[])signed.Clone();
            forged[10] ^= 1;
            Assert(!Ed25519.Verify(forged, Hex(publicKey), messageBytes), name + " rejects an altered signature");
            Assert(!Ed25519.Verify(signed, Hex(publicKey), messageBytes.Concat(new byte[] { 0 }).ToArray()),
                name + " rejects an altered message");
        }

        var key = Ed25519.GenerateSecretKey();
        var statement = Encoding.UTF8.GetBytes("a host descriptor");
        var proof = Ed25519.Sign(key, statement);
        Assert(Ed25519.Verify(proof, Ed25519.DerivePublicKey(key), statement), "a fresh key signs and verifies");
        Assert(!Ed25519.Verify(proof, Ed25519.DerivePublicKey(Ed25519.GenerateSecretKey()), statement), "another key's signature fails");
        Assert(!Ed25519.Verify(proof.Take(63).ToArray(), Ed25519.DerivePublicKey(key), statement), "a short signature fails");
    }

    internal static void ChaCha20Poly1305MatchesRfc8439()
    {
        // Section 2.8.2.
        var aead = new Aead(Hex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f"));
        var nonce = Hex("070000004041424344454647");
        var associatedData = Hex("50515253c0c1c2c3c4c5c6c7");
        var plaintext = Encoding.ASCII.GetBytes(
            "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var sealedMessage = new byte[plaintext.Length + Aead.TagSize];
        Assert(aead.Seal(nonce, associatedData, plaintext, 0, plaintext.Length, sealedMessage, 0) == sealedMessage.Length, "seal length");
        AssertHex(sealedMessage,
            "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d63dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905" +
            "d6a5b67ecd3b3692ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc3ff4def08e4b7a9de576d26586cec64b6116" +
            "1ae10b594f09e26a7e902ecbd0600691",
            "section 2.8.2 ciphertext and tag");

        var opened = new byte[plaintext.Length];
        Assert(aead.TryOpen(nonce, associatedData, sealedMessage, 0, sealedMessage.Length, opened, 0, out var written) &&
               written == plaintext.Length && opened.SequenceEqual(plaintext),
            "the sealed message opens to the plaintext");

        var tampered = (byte[])sealedMessage.Clone();
        tampered[5] ^= 0x80;
        var spoiled = new byte[plaintext.Length];
        Assert(!aead.TryOpen(nonce, associatedData, tampered, 0, tampered.Length, spoiled, 0, out _) && spoiled.All(value => value == 0),
            "an altered ciphertext fails and leaves no plaintext behind");
        Assert(!aead.TryOpen(nonce, Hex("50515253c0c1c2c3c4c5c6c8"), sealedMessage, 0, sealedMessage.Length, new byte[plaintext.Length], 0, out _),
            "altered associated data fails");
        Assert(!aead.TryOpen(nonce, associatedData, sealedMessage, 0, Aead.TagSize - 1, new byte[1], 0, out _),
            "input shorter than a tag fails");
    }

    internal static void HkdfMatchesRfc5869()
    {
        var inputKeyMaterial = Enumerable.Repeat((byte)0x0b, 22).ToArray();

        // Appendix A, test case 1.
        var prk = Hkdf.Extract(Hex("000102030405060708090a0b0c"), inputKeyMaterial);
        AssertHex(prk, "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5", "case 1 PRK");
        AssertHex(Hkdf.Expand(prk, Hex("f0f1f2f3f4f5f6f7f8f9"), 42),
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", "case 1 OKM");

        // Appendix A, test case 3: no salt and no info, the shape Noise uses.
        var emptyPrk = Hkdf.Extract(Array.Empty<byte>(), inputKeyMaterial);
        AssertHex(emptyPrk, "19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04", "case 3 PRK");
        AssertHex(Hkdf.Expand(emptyPrk, Array.Empty<byte>(), 42),
            "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8", "case 3 OKM");

        var chainingKey = Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var first = new byte[Hkdf.HashSize];
        var second = new byte[Hkdf.HashSize];
        Hkdf.DeriveTwo(chainingKey, inputKeyMaterial, first, second);
        var expanded = Hkdf.Expand(Hkdf.Extract(chainingKey, inputKeyMaterial), Array.Empty<byte>(), 2 * Hkdf.HashSize);
        Assert(first.SequenceEqual(expanded.Take(32)) && second.SequenceEqual(expanded.Skip(32)),
            "the Noise two-output form is RFC 5869 with the chaining key as salt and empty info");
    }

    internal static byte[] Hex(string value) => Convert.FromHexString(value);

    private static void AssertHex(byte[] actual, string expected, string what) =>
        Assert(Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase),
            what + " (got " + Convert.ToHexString(actual).ToLowerInvariant() + ")");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
