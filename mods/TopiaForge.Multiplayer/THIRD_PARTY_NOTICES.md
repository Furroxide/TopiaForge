# Third Party Notices

`TopiaForge.Multiplayer.dll` compiles a subset of the BouncyCastle C# cryptography library, which supplies
X25519, Ed25519 and ChaCha20-Poly1305 for the multiplayer transport. The source lives in
`ThirdParty/BouncyCastle/` of this mod's source tree, under the MIT licence reproduced below.

- Project: BouncyCastle C# (bc-csharp)
- Version/tag: 2.7.0 (`release-2.7.0`, tag object `6dae2bb926060ff44e1aca77e61daa7942baebe9`, commit
  `4007498b13582d90ee1eda5d9920c324428b98b3`)
- Upstream: https://github.com/bcgit/bc-csharp/tree/release-2.7.0/crypto/src
- License: MIT

## Changes made to every file

- The `Org.BouncyCastle` namespace prefix became `TopiaForge.Multiplayer.ThirdParty.BouncyCastle`, so this copy
  cannot collide with another BouncyCastle loaded in the same game.
- Top-level public types became internal.
- UTF-8 without a byte-order mark, and LF line endings.
- The `NETSTANDARD2_1_OR_GREATER` symbol became `BOUNCYCASTLE_NETSTANDARD2_1_PATHS`, which nothing defines.
  BouncyCastle builds for netstandard2.0 but never netstandard2.1, and those paths call APIs only newer .NET
  has, so this netstandard2.1 build compiles the same code as BouncyCastle's own netstandard2.0 build.

## Further changes

| File | Change |
| --- | --- |
| `crypto/macs/Poly1305.cs` | Dropped the import of, and a doc link to, the unvendored `Poly1305KeyGenerator`. |
| `crypto/modes/ChaCha20Poly1305.cs` | `Init` takes only `ParametersWithIV`; the `AeadParameters` branch is gone, as the provider passes associated data per message. |
| `math/ec/rfc7748/X25519.cs` | Removed both `GeneratePrivateKey(SecureRandom, ...)` overloads; keys come from the provider's `RandomNumberGenerator`. |
| `math/ec/rfc8032/Ed25519.cs` | Removed the four `GeneratePrivateKey(SecureRandom, ...)` overloads, for the same reason. |
| `math/raw/Mod.cs` | Removed both `Random(SecureRandom, ...)` overloads; `Platform.Is64BitProcess` became `Environment.Is64BitProcess`. |
| `math/raw/Nat.cs` | Removed the `BigInteger` conversions and the `Xor` and `Zero` helpers, which need the unvendored `BigInteger` and `Nat512`. |
| `math/raw/Nat256.cs` | Removed `ToBigInteger` and `ToBigInteger64`. |
| `util/Arrays.cs` | Removed the `BigInteger[]` overloads of `CopyOf` and `CopyOfRange`; `Objects.Equals` became `object.Equals`, and `Objects.GetHashCode` a local null-safe `ObjectHash`. |

## Vendored files

Each file is `crypto/src/<path>` upstream. The hash is the upstream git blob, so a copy can be checked against
the tag before these changes were applied.

| Path | Upstream blob |
| --- | --- |
| `crypto/Check.cs` | `42996c9201a48d568b2e869a2c5baf8111c5ee2f` |
| `crypto/CryptoException.cs` | `96fa587bd6e74ff819b3f6b0349a9d2f9773e76c` |
| `crypto/DataLengthException.cs` | `e262e1bc823226c81ab8419b3a6d9b7fd547f3d4` |
| `crypto/IBlockCipher.cs` | `450611b3c9268c95605a9242b175f10c1296e4c9` |
| `crypto/ICipherParameters.cs` | `fff0941c7e7d80de77576d9ffe8c1dc3f6e3ade1` |
| `crypto/IDigest.cs` | `c7057235313414b407f1f8c387966a135ba3c0fc` |
| `crypto/IMac.cs` | `aa539b96893ba97d1cb8095642af4ddab3b13ac2` |
| `crypto/IStreamCipher.cs` | `0408b33c9ad5c4ecc9b032c4fb2298833240fd7b` |
| `crypto/InvalidCipherTextException.cs` | `4a0ba5a6915fe390c5d37a1059d6a5c43d54855e` |
| `crypto/MaxBytesExceededException.cs` | `f4a461bb1a06b514b31e1ec5939fbea4fd20ba36` |
| `crypto/OutputLengthException.cs` | `394e12b3a6b1197cfb51074f8764fa9ebec2633e` |
| `crypto/digests/LongDigest.cs` | `df48c48890fef4ae3809965c26906c420b49a535` |
| `crypto/digests/Sha512Digest.cs` | `3b70fdc98ed9bb899121dc4cab3a7ec31b616ae8` |
| `crypto/engines/ChaCha7539Engine.cs` | `69cb02f600e2f5d092851fee6d929f21c4a0f506` |
| `crypto/engines/ChaChaEngine.cs` | `a21b34c06761cff867bd36c79dcf2839a4ddd275` |
| `crypto/engines/Salsa20Engine.cs` | `5cf9a3e1dba60aa2c3021968b0241ccf90c09882` |
| `crypto/macs/Poly1305.cs` | `d0cbd4d0ff995908dd1add1d4ec071bdd9564929` |
| `crypto/modes/ChaCha20Poly1305.cs` | `9c5f35c025beedc912a03f4240a90cc8814e4f5d` |
| `crypto/modes/IAeadCipher.cs` | `597d3a7b5be277dacd6c2e48aac17bb9889c31f4` |
| `crypto/parameters/KeyParameter.cs` | `0199b3ffd25babdebf0362e1758f0da0d9ca3839` |
| `crypto/parameters/ParametersWithIV.cs` | `0eb42cba2f0ff1bd0a76091ae8c8a25bf717f258` |
| `crypto/util/Pack.cs` | `4d671c599690883a5d91fb2271c964b4aebd64f1` |
| `math/ec/rfc7748/X25519.cs` | `aa9ba1db08e77dd965a5cc5504e738ec3ab96696` |
| `math/ec/rfc7748/X25519Field.cs` | `979cf20e044f9f74b61dd71ae75d41fa8cb49016` |
| `math/ec/rfc8032/Codec.cs` | `cf1b69b275cd2a7faad9cd310e4cf1faba8c0865` |
| `math/ec/rfc8032/Ed25519.cs` | `916d6adc4b98b3b4a2ae2ad20e85350ee41a0a5f` |
| `math/ec/rfc8032/Scalar25519.cs` | `08ab80607d7ea7987e16a89710f2f8f043c0ab8f` |
| `math/ec/rfc8032/ScalarUtilities.cs` | `7b8cf1d80239355eccbaf6cc7d935e264296711b` |
| `math/ec/rfc8032/Wnaf.cs` | `20993403168c9eb19b64cf1357003f58b72886f3` |
| `math/raw/Bits.cs` | `3efc890d6dd2412e587bf4ce6fc30c1bccc088ae` |
| `math/raw/Interleave.cs` | `7fd8951819f2e129c8b08e82f98544bd9c9e468c` |
| `math/raw/Mod.cs` | `a128a0817e9ddca797b012e73e91e5ef5a756585` |
| `math/raw/Nat.cs` | `71056e0f3062683d3437b4842466fd7bfe78a48e` |
| `math/raw/Nat256.cs` | `b92ccc8cf3f9d8c70e64a48415f53b2d844bff20` |
| `util/Arrays.cs` | `235fcdfa49d9243997ef08caf562bef2e0f17c68` |
| `util/Bytes.cs` | `f6e401ae05ab197f01203d6eb07df2dbbe851cca` |
| `util/IMemoable.cs` | `cc8a2e55b0cbc3a56466a2d752b5a94d536fb7b1` |
| `util/Integers.cs` | `72002b95ea8339fb68498d3e43b9b49d37031e55` |
| `util/Longs.cs` | `67ef4464fb8cf8ad9625501e26f6419933d06742` |
| `util/Strings.cs` | `f6d82eec9feeef8e0abeabfd8cf6646c84aa5d7e` |

## License

```text
MIT License (https://opensource.org/licenses/MIT)

Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org).
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sub license, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions: The above copyright notice and this
permission notice shall be included in all copies or substantial portions of the Software.

**THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.**
```
