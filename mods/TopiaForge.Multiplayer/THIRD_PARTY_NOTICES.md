# Third Party Notices

`TopiaForge.Multiplayer.dll` compiles source from two third-party projects. Each keeps its own licence, reproduced
below with every change this copy makes and each file's upstream blob hash.

| Project | Supplies | Licence | Source in this mod |
| --- | --- | --- | --- |
| BouncyCastle C# 2.7.0 | X25519, Ed25519 and ChaCha20-Poly1305 for the transport | MIT | `ThirdParty/BouncyCastle/` |
| Concentus 2.2.2 | The Opus voice codec and the Speex resampler | BSD-3-Clause | `ThirdParty/Concentus/` |

## BouncyCastle C#

`TopiaForge.Multiplayer.dll` compiles a subset of the BouncyCastle C# cryptography library, which supplies
X25519, Ed25519 and ChaCha20-Poly1305 for the multiplayer transport. The source lives in
`ThirdParty/BouncyCastle/` of this mod's source tree, under the MIT licence reproduced below.

- Project: BouncyCastle C# (bc-csharp)
- Version/tag: 2.7.0 (`release-2.7.0`, tag object `6dae2bb926060ff44e1aca77e61daa7942baebe9`, commit
  `4007498b13582d90ee1eda5d9920c324428b98b3`)
- Upstream: https://github.com/bcgit/bc-csharp/tree/release-2.7.0/crypto/src
- License: MIT

### Changes made to every file

- The `Org.BouncyCastle` namespace prefix became `TopiaForge.Multiplayer.ThirdParty.BouncyCastle`, so this copy
  cannot collide with another BouncyCastle loaded in the same game.
- Top-level public types became internal.
- UTF-8 without a byte-order mark, and LF line endings.
- The `NETSTANDARD2_1_OR_GREATER` symbol became `BOUNCYCASTLE_NETSTANDARD2_1_PATHS`, which nothing defines.
  BouncyCastle builds for netstandard2.0 but never netstandard2.1, and those paths call APIs only newer .NET
  has, so this netstandard2.1 build compiles the same code as BouncyCastle's own netstandard2.0 build.

### Further changes

| File | Change |
| --- | --- |
| `crypto/macs/Poly1305.cs` | Dropped the import of, and a doc link to, the unvendored `Poly1305KeyGenerator`. Added an internal `Wipe` that zeroes the one-time key and the accumulator. |
| `crypto/modes/ChaCha20Poly1305.cs` | `Init` takes only `ParametersWithIV`; the `AeadParameters` branch is gone, as the provider passes associated data per message. Added an internal `Wipe` that zeroes the key copy, the ChaCha20 engine state and keystream, the buffers and the Poly1305 state. |
| `crypto/parameters/KeyParameter.cs` | Added an internal `Wipe` that zeroes the key copy. |
| `math/ec/rfc7748/X25519.cs` | Removed both `GeneratePrivateKey(SecureRandom, ...)` overloads; keys come from the provider's `RandomNumberGenerator`. |
| `math/ec/rfc8032/Ed25519.cs` | Removed the four `GeneratePrivateKey(SecureRandom, ...)` overloads, for the same reason. |
| `math/raw/Mod.cs` | Removed both `Random(SecureRandom, ...)` overloads; `Platform.Is64BitProcess` became `Environment.Is64BitProcess`. |
| `math/raw/Nat.cs` | Removed the `BigInteger` conversions and the `Xor` and `Zero` helpers, which need the unvendored `BigInteger` and `Nat512`. |
| `math/raw/Nat256.cs` | Removed `ToBigInteger` and `ToBigInteger64`. |
| `util/Arrays.cs` | Removed the `BigInteger[]` overloads of `CopyOf` and `CopyOfRange`; `Objects.Equals` became `object.Equals`, and `Objects.GetHashCode` a local null-safe `ObjectHash`. |

### Vendored files

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

### License

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

## Concentus

`TopiaForge.Multiplayer.dll` compiles a subset of Concentus, a C# port of the Opus reference library (libopus
1.1.2, fixed point) and of the Speex resampler, for voice chat. The source lives in `ThirdParty/Concentus/` of
this mod's source tree, under the BSD-style licences reproduced below.

- Project: Concentus (lostromb/concentus)
- Version: 2.2.2. The NuGet package `Concentus` 2.2.2 names repository commit
  `6c2328dc19044601e33a9c11628b8d60e1f3011c` in its metadata; the files were fetched from GitHub at that commit.
- Upstream: https://github.com/lostromb/concentus/tree/6c2328dc19044601e33a9c11628b8d60e1f3011c/CSharp/Concentus
- License: BSD-3-Clause

### Changes made to every file

- The `Concentus` namespace prefix became `TopiaForge.Multiplayer.ThirdParty.Concentus`, so this copy cannot collide
  with another Concentus loaded in the same game.
- Top-level public types became internal.
- UTF-8 without a byte-order mark, and LF line endings.

### Left out

- Native-library interop: the 13 files under `Native/`, and `OpusCodecFactory.cs` and
  `ResamplerFactory.cs`, whose only addition over the managed constructors is choosing a native libopus.
- Code no part of the encoder, decoder or resampler needs: `IOpusMultiStreamDecoder.cs`, `IOpusMultiStreamEncoder.cs`,
  `Opus/Enums/OpusControl.cs`, `Opus/OpusCompare.cs`, `Opus/OpusMultistream.cs`, `Opus/Structs/ChannelLayout.cs`,
  `Opus/Structs/OpusMSDecoder.cs`, `Opus/Structs/OpusMSEncoder.cs`, `Opus/Structs/VorbisLayout.cs`,
  `Silk/Structs/TOCStruct.cs`.
- `AssemblyInfo.cs`, which grants upstream's own test projects access to internals, and `Concentus.csproj`.

### Further changes

The game's Mono runtime ships no `System.Numerics.Vectors`, so every `Vector<T>` path is gone; the scalar code each
one duplicated remains.

| File | Change |
| --- | --- |
| `Celt/CeltPitchXCorr.cs` | Removed the `System.Numerics` import and the `Vector<T>` branch of `pitch_xcorr`, keeping its scalar loop. |
| `Celt/Kernels.cs` | Removed the `System.Numerics` import and `xcorr_kernel_vector`, the `Vector<T>` kernel that branch called. |
| `Common/Inlines.cs` | Defines `PARITY`, which upstream's `Concentus.csproj` defines for every build. It selects the bit-exact fixed-point arithmetic over floating-point approximations. |
| `Common/SpeexResampler.cs` | Removed the `System.Numerics` import, `resampler_basic_direct_single_SIMD` (built only for .NET 6 and later, on `Vector<T>`), and the code that chose it, so the scalar resampler always runs. |
| `Opus/CodecHelpers.cs` | Defines `PARITY`, for the same reason. |
| `Opus/Structs/OpusDecoder.cs` | `opus_decode_frame` resets `data_ptr` when it drops a payload of one byte or less, as libopus does by setting `data` to null. Upstream kept the offset, so once a decoder had decoded audio, every discontinuous-transmission (TOC-only) packet threw `ArgumentOutOfRangeException`. |
| `Opus/Structs/OpusEncoder.cs` | Removed the unused import of the `Native` namespace, which is not vendored. |

### Vendored files

Each file is `CSharp/Concentus/<path>` upstream. The hash is the upstream git blob at the commit above, so a copy
can be checked against upstream before these changes were applied.

| Path | Upstream blob |
| --- | --- |
| `Celt/Bands.cs` | `c7e5a7e87ca694f342fcaa23decb63c9d3706355` |
| `Celt/CWRS.cs` | `ec78576c4c8efdf6bfeeac9383b7e9c48479388a` |
| `Celt/CeltCommon.cs` | `4eb89c3ac838b55f0164d8ba5fbaf9233b8f1a34` |
| `Celt/CeltConstants.cs` | `9b49ad8b58a5d49acc19090817b798faab654e94` |
| `Celt/CeltLPC.cs` | `42751113ba07dd2f1287a3444cfe81182b875de0` |
| `Celt/CeltPitchXCorr.cs` | `9f6327d45483f3638d5edc74eab087e9a0179bf6` |
| `Celt/Enums/Spread.cs` | `cf7418df710bd342a15581a0dc31830d8ce14374` |
| `Celt/Kernels.cs` | `ac591aab4d82441080421915aa8c88d71c55d940` |
| `Celt/KissFFT.cs` | `f60d6f863affd3162ad0daf714cd103b7e7f06f2` |
| `Celt/Laplace.cs` | `f7ab4f8c46d2b196273d28f43319090f6783b3ad` |
| `Celt/MDCT.cs` | `a89194cfab35209fb307f03441484e2fd63794c0` |
| `Celt/Pitch.cs` | `0574c977625d5acbbd62317b2011bdd8e721d6d0` |
| `Celt/QuantizeBands.cs` | `0c7ffca6b899b10b2c5c83be94f19a721b14e4f9` |
| `Celt/Rate.cs` | `e10ee19cd59646ecf92bc6b5595e9934d1e4b255` |
| `Celt/Structs/AnalysisInfo.cs` | `e35465a1be5d732758806c570de5f32d5b5068cb` |
| `Celt/Structs/CELTDecoder.cs` | `ec97502e6f1952edc86ba3cad16638cfcc1c987a` |
| `Celt/Structs/CELTMode.cs` | `d2b513d0aae83b97f61c6369ae1cf13e4b1a6893` |
| `Celt/Structs/CeltEncoder.cs` | `41efeb4c945ec1cd1747908ca88fe663834308e4` |
| `Celt/Structs/FFTState.cs` | `e1b79c7334d66ad81e4306d2f0e88eee291490aa` |
| `Celt/Structs/MDCTLookup.cs` | `4d29155d73026ff2e5d95247293588fb673f6ccf` |
| `Celt/Structs/PulseCache.cs` | `8f6e90260cf58cc07debddc78b9d33243bde5b71` |
| `Celt/Tables.cs` | `33036dde9573fe595635de5334e4d8604c02afd4` |
| `Celt/VQ.cs` | `ca26bd5c4346f7a40678017c16268d23537da1d0` |
| `Common/Autocorrelation.cs` | `f87bc0efe6bf6dcaffb0508b77fad4782f135e6b` |
| `Common/CPlusPlus/Arrays.cs` | `186bc19a75b38f4141b9997040351e7e1f4571b1` |
| `Common/CPlusPlus/BoxedValue.cs` | `d2e1068f0e3bf696e4d41efe87b5bb21be3493d1` |
| `Common/CPlusPlus/Pointer.cs` | `0fe6eb7eb0f7a510322195a37d12cbceae70157a` |
| `Common/EntropyCoder.cs` | `b130b4abf23e2eb47ed6fbef557c950d7262a07f` |
| `Common/Inlines.cs` | `b652d5e09a24637bb7841c896ebb7395d13ddf82` |
| `Common/SpeexResampler.cs` | `91c2e522e56769b713be01934523ca0b86abd6af` |
| `IOpusDecoder.cs` | `bdce39cc5c7fd37f73b4653c34e6e8c8916bf243` |
| `IOpusEncoder.cs` | `0e30cb3d83247af3d35fdf5df95d5ba30c3d27c9` |
| `IResampler.cs` | `c821d347dc8115c3c84bf03ccdc93784575dfe2d` |
| `Opus/Analysis.cs` | `ee2efea73957a7876cce0050856ba6a50620de3e` |
| `Opus/CodecHelpers.cs` | `184525a9b8cf0ed6ce92473d16ffad069f2d6c7c` |
| `Opus/Downmix.cs` | `bcb24b5665fa0c7a2b14fbf2485b72fdff52ba46` |
| `Opus/Enums/OpusApplication.cs` | `176edcc5c860e3d18f2cafd166d8c8f0a20ad63c` |
| `Opus/Enums/OpusBandwidth.cs` | `cbd19f03ad5cd3f5a61d33952019755b52bb042e` |
| `Opus/Enums/OpusError.cs` | `0e2cc239c07585f1c6a61a6e22c35ce71ac73034` |
| `Opus/Enums/OpusFramesize.cs` | `fb44a4f7c4b83944f8bc261419e58c0e16881835` |
| `Opus/Enums/OpusMode.cs` | `a330d988ba2977cda6e4ef8a7319101e4ca534e5` |
| `Opus/Enums/OpusSignal.cs` | `5e27027c59145c1fb5563be81bb65340a3186cc2` |
| `Opus/MultiLayerPerceptron.cs` | `cc954bd4f0f13d638b5d6f6b247ea4ad4e5ed39e` |
| `Opus/OpusConstants.cs` | `ee568dbf1e1be9061feb3e9b08c171465dfb7718` |
| `Opus/OpusException.cs` | `1347ea602be65daac7d4f7ea62732f3e7a907e34` |
| `Opus/Structs/MLP.cs` | `02b953ba4940c4ec4d7e199b05a79735e56b33bc` |
| `Opus/Structs/OpusDecoder.cs` | `ab4cd2f652e2be31dbfda8b75403fcc6a2a3c7ad` |
| `Opus/Structs/OpusEncoder.cs` | `7b1488197ef7564f438f5b99b760ff561432e767` |
| `Opus/Structs/OpusPacketInfo.cs` | `278a483a56ef2e4f7ab295b478c89a5aaa53cb20` |
| `Opus/Structs/OpusRepacketizer.cs` | `25a5ad3e772514e4f94335b7115f42701a818e66` |
| `Opus/Structs/StereoWidthState.cs` | `1ff516f93d7c8afde4240a5b4c5764599aab048f` |
| `Opus/Structs/TonalityAnalysisState.cs` | `9f70bd8aaecaaab912f5c22ce2b7a3c524c1a45b` |
| `Opus/Tables.cs` | `7092b0cfe5668298cf59ffa4350b9be9f86b9ec3` |
| `Silk/ApplySineWindow.cs` | `ca954fe0412ff3c025e2ee4647c38963839e6dbc` |
| `Silk/BWExpander.cs` | `071a5d08e1cc072eb16d3c3cb7a966af3e9963ea` |
| `Silk/BurgModified.cs` | `9bacd343b198ce3b30106094d2a3e2feaa22131c` |
| `Silk/CNG.cs` | `e8599ba24c0e785e48f7b7825019a262b33032d6` |
| `Silk/CodeSigns.cs` | `3266b54f88be38b38944897fe07fbcc2cefdf4a9` |
| `Silk/CorrelateMatrix.cs` | `74fec1649df85d95c1c19a7204f3245e850771fa` |
| `Silk/DecodeAPI.cs` | `157ade02882b2e92addf3303a6701a3cc0823652` |
| `Silk/DecodeCore.cs` | `4292fb139bd5d921d8ed26c89b52bd11a69a9138` |
| `Silk/DecodeIndices.cs` | `011804959ed21773a2c9bedf88a45272de816884` |
| `Silk/DecodeParameters.cs` | `9edcdc8891cb2d989b89f04627abf87ebd35996d` |
| `Silk/DecodePitch.cs` | `e9f52dd1ce9c670fdcaf2fd31b5a85a7962fa06c` |
| `Silk/DecodePulses.cs` | `9a100f7c7d7930b8c4e78e47b4401db9f5c6f69f` |
| `Silk/EncodeAPI.cs` | `41715155687d20b2099e8a4d66e968971d5f4ee7` |
| `Silk/EncodeIndices.cs` | `57352500bc438de6be9f221a60dadb65bb035474` |
| `Silk/EncodePulses.cs` | `2d8934e09bd1e8506d4834a070f0b91b79536ce6` |
| `Silk/Enums/DecoderAPIFlag.cs` | `618cc4933d761f218f24a8c134b294475bf34edc` |
| `Silk/Enums/SilkError.cs` | `513b7138f7966529b5091f088054e9bd22640e7b` |
| `Silk/Filters.cs` | `8802afdbd8201a8589aeb49c2521d924fe902f8a` |
| `Silk/FindLPC.cs` | `e064e8e23d7bb42154088b361945324a4a3df93e` |
| `Silk/FindLTP.cs` | `3a4580c149cedee6e330ae4bb1b12563fd5d9258` |
| `Silk/FindPitchLags.cs` | `d15fd9c006ebddb2be882158892fd6390bbf18a9` |
| `Silk/FindPredCoefs.cs` | `51130ada010b691168c6b795f454b9866df8c6c3` |
| `Silk/GainQuantization.cs` | `1578f118f93d8dd302b7416407921225f78560cf` |
| `Silk/HPVariableCutoff.cs` | `77d32e870a6b889ca5a09bfa384c0b34c678cf72` |
| `Silk/K2A.cs` | `1eacdace788ed837d2db80fffe3f32cfc919fb64` |
| `Silk/LPCInversePredGain.cs` | `1eb2ec9ee7dd966ce401fe28661a7e9447009c32` |
| `Silk/LTPAnalysisFilter.cs` | `bfaa5c2fc92c72665a3df2e8f783317e4a556c0c` |
| `Silk/LTPScaleControl.cs` | `75777fdfdd2296541df0c96730ed12d9a409f2d9` |
| `Silk/LinearAlgebra.cs` | `172c6ae749451a83844bfe4131a8d7fd73412a88` |
| `Silk/NLSF.cs` | `9b03612c0cf72532a38afb8ac1a0ab8937f3c7ab` |
| `Silk/NoiseShapeAnalysis.cs` | `53504dfe98ef89769b996fa0c760e683f6809882` |
| `Silk/PLC.cs` | `5aa13a5617628879314a7064718f6c5330c0faad` |
| `Silk/PitchAnalysisCore.cs` | `7944f09ab6bbd4da5bb502628e5aa761ca7afe63` |
| `Silk/ProcessGains.cs` | `5d08424b4a137322a2a7b6fab626e18458e513d5` |
| `Silk/QuantizeLTPGains.cs` | `dfcbb2d36bd970ca6ef81ef4ddd0f526219574f2` |
| `Silk/RegularizeCorrelations.cs` | `33c5d9eb2c681e7cde17b615b47f3e201cb59c9f` |
| `Silk/Resampler.cs` | `35423b0cb6245fd79e1107b2201f2ff717d5c6b8` |
| `Silk/ResidualEnergy.cs` | `e768e9bbca9ca2346f163cd9fa3f01b28d6b1cda` |
| `Silk/Schur.cs` | `ed499284dab2e2d5a19b59011f6202c5322f3e8e` |
| `Silk/ShellCoder.cs` | `a08fda7194590e1204604887cd91c4a27e504f82` |
| `Silk/Sigmoid.cs` | `4e813be2aa9485e4f0d1161d268c582ffc675c07` |
| `Silk/SilkConstants.cs` | `400c07a2bcc3cb6bd1933c439d619fc6062cbcfe` |
| `Silk/Sort.cs` | `65d862dc3eb521143b2098b2936ae0a09274f102` |
| `Silk/Stereo.cs` | `18deb2c08b8a19fa60382ed15f3c889bb12f9b11` |
| `Silk/Structs/CNGState.cs` | `0684d50345dda8008984e3062469dc88cef3da04` |
| `Silk/Structs/DecControlState.cs` | `11b2fe3de4d9a88cc8baa1227c3a8f1e141c6ab9` |
| `Silk/Structs/EncControlState.cs` | `15c7069dcf3482e9352ab58a9ac0fcc7165a6b98` |
| `Silk/Structs/NLSFCodebook.cs` | `e67028f48f97dacee5eccb4755ed586327c1e1cf` |
| `Silk/Structs/PLCStruct.cs` | `a408e5aa1f631b9ac6ea0b7b914ebdf27e56deb5` |
| `Silk/Structs/SideInfoIndices.cs` | `eb998756797c2b00c1e3dea64de39e0b7876d8d6` |
| `Silk/Structs/SilkChannelDecoder.cs` | `56632632949f6146869fb530883db357cadd7878` |
| `Silk/Structs/SilkChannelEncoder.cs` | `4a5fcf489fd55d5f0dd9bb73434a689a64774097` |
| `Silk/Structs/SilkDecoder.cs` | `33a8906b785681fb6c5aa6f825d0a098984f80a7` |
| `Silk/Structs/SilkDecoderControl.cs` | `fd9aef6eb2c5bf4fcf2bc0ba4819935db5d64377` |
| `Silk/Structs/SilkEncoder.cs` | `b9438bf6ac190bdc9224535ab86e35939ec95e2a` |
| `Silk/Structs/SilkEncoderControl.cs` | `e9f7a8879489d2ad003dae6ef989d5350f39061b` |
| `Silk/Structs/SilkLPState.cs` | `26e03a77ce3820c386c1e49b3e00bb9e6f9095ba` |
| `Silk/Structs/SilkNSQState.cs` | `8b3eaa6f7e72622cf64fa5d773b5ee670b80959f` |
| `Silk/Structs/SilkPrefilterState.cs` | `21d6e363fe6ca545a00eb122080e320e8d0c71f7` |
| `Silk/Structs/SilkResamplerState.cs` | `e5833615a98f27509fbb1b30fa1edaa40ab9a7e4` |
| `Silk/Structs/SilkShapeState.cs` | `b20ca6134468f8750b2ab6ac66ce8a65b4daf3fb` |
| `Silk/Structs/SilkVADState.cs` | `6854cdee2ae728d958baac3779315438b5d983ae` |
| `Silk/Structs/StereoDecodeState.cs` | `80373bc748c5101be1a9a9b5bd8b4f9d72577284` |
| `Silk/Structs/StereoEncodeState.cs` | `b15f8c5d8d31e1864672fafe05bed462a3df2562` |
| `Silk/SumSqrShift.cs` | `07593463b17b0cb31c8765a3e2e4d336fcc07627` |
| `Silk/Tables.cs` | `cf8409b754e55831bc2c86a7f43018f8fc7f42da` |
| `Silk/TuningParameters.cs` | `1d11cb13c0489add2b70ce0575b64c5999e105f8` |
| `Silk/VQ_WMat_EC.cs` | `bd5c5738162feea04084058d1a2d7a1180e431c3` |
| `Silk/VoiceActivityDetection.cs` | `5f7bb4379dbd29eb28fe721f45cf3f94d3a15b6d` |

### Notices in the vendored files

Every vendored file opens with copyright lines and one of two licence texts. Each group below quotes a set of
copyright lines word for word as its files carry them, without their indentation and blank lines; the licence
text that follows them in those files comes after the groups.

#### Opus licence, carried by the codec files

```text
Copyright (c) 2007-2008 CSIRO
Copyright (c) 2007-2011 Xiph.Org Foundation
Originally written by Jean-Marc Valin, Gregory Maxwell, Koen Vos,
Timothy B. Terriberry, and the Opus open-source contributors
Ported to C# by Logan Stromberg
```

Carried by `Celt/Bands.cs`, `Celt/CWRS.cs`, `Celt/CeltCommon.cs`, `Celt/CeltConstants.cs`, `Celt/CeltLPC.cs`,
`Celt/CeltPitchXCorr.cs`, `Celt/Enums/Spread.cs`, `Celt/Kernels.cs`, `Celt/Laplace.cs`, `Celt/Pitch.cs`,
`Celt/QuantizeBands.cs`, `Celt/Rate.cs`, `Celt/Structs/AnalysisInfo.cs`, `Celt/Structs/CELTDecoder.cs`,
`Celt/Structs/CELTMode.cs`, `Celt/Structs/CeltEncoder.cs`, `Celt/Structs/PulseCache.cs`, `Celt/Tables.cs`,
`Celt/VQ.cs`, `Opus/Analysis.cs`, `Opus/CodecHelpers.cs`, `Opus/Downmix.cs`, `Opus/Enums/OpusApplication.cs`,
`Opus/Enums/OpusBandwidth.cs`, `Opus/Enums/OpusError.cs`, `Opus/Enums/OpusFramesize.cs`, `Opus/Enums/OpusMode.cs`,
`Opus/Enums/OpusSignal.cs`, `Opus/OpusConstants.cs`, `Opus/Structs/OpusDecoder.cs`, `Opus/Structs/OpusEncoder.cs`,
`Opus/Structs/OpusPacketInfo.cs`, `Opus/Structs/OpusRepacketizer.cs`, `Opus/Structs/StereoWidthState.cs`,
`Opus/Structs/TonalityAnalysisState.cs`, `Opus/Tables.cs`.

```text
Copyright (c) 2007-2008 CSIRO
Copyright (c) 2007-2011 Xiph.Org Foundation
Copyright (c) 2003-2004, Mark Borgerding
Modified from KISS-FFT by Jean-Marc Valin
Ported to C# by Logan Stromberg
```

Carried by `Celt/KissFFT.cs`.

```text
Copyright (c) 2003-2004, Mark Borgerding
Copyright (c) 2007-2008 CSIRO
Copyright (c) 2007-2011 Xiph.Org Foundation
Modified from KISS-FFT by Jean-Marc Valin
Ported to C# by Logan Stromberg
```

Carried by `Celt/MDCT.cs`, `Celt/Structs/FFTState.cs`, `Celt/Structs/MDCTLookup.cs`.

```text
Copyright (c) 2006-2011 Skype Limited. All Rights Reserved
Ported to C# by Logan Stromberg
```

Carried by `Common/Autocorrelation.cs`, `Silk/ApplySineWindow.cs`, `Silk/BWExpander.cs`, `Silk/BurgModified.cs`,
`Silk/CNG.cs`, `Silk/CodeSigns.cs`, `Silk/CorrelateMatrix.cs`, `Silk/DecodeAPI.cs`, `Silk/DecodeCore.cs`,
`Silk/DecodeIndices.cs`, `Silk/DecodeParameters.cs`, `Silk/DecodePitch.cs`, `Silk/DecodePulses.cs`,
`Silk/EncodeAPI.cs`, `Silk/EncodeIndices.cs`, `Silk/EncodePulses.cs`, `Silk/Enums/DecoderAPIFlag.cs`,
`Silk/Enums/SilkError.cs`, `Silk/Filters.cs`, `Silk/FindLPC.cs`, `Silk/FindLTP.cs`, `Silk/FindPitchLags.cs`,
`Silk/FindPredCoefs.cs`, `Silk/GainQuantization.cs`, `Silk/HPVariableCutoff.cs`, `Silk/K2A.cs`,
`Silk/LPCInversePredGain.cs`, `Silk/LTPAnalysisFilter.cs`, `Silk/LTPScaleControl.cs`, `Silk/LinearAlgebra.cs`,
`Silk/NLSF.cs`, `Silk/NoiseShapeAnalysis.cs`, `Silk/PLC.cs`, `Silk/PitchAnalysisCore.cs`, `Silk/ProcessGains.cs`,
`Silk/QuantizeLTPGains.cs`, `Silk/RegularizeCorrelations.cs`, `Silk/Resampler.cs`, `Silk/ResidualEnergy.cs`,
`Silk/Schur.cs`, `Silk/ShellCoder.cs`, `Silk/Sigmoid.cs`, `Silk/SilkConstants.cs`, `Silk/Sort.cs`, `Silk/Stereo.cs`,
`Silk/Structs/CNGState.cs`, `Silk/Structs/DecControlState.cs`, `Silk/Structs/EncControlState.cs`,
`Silk/Structs/NLSFCodebook.cs`, `Silk/Structs/PLCStruct.cs`, `Silk/Structs/SideInfoIndices.cs`,
`Silk/Structs/SilkChannelDecoder.cs`, `Silk/Structs/SilkChannelEncoder.cs`, `Silk/Structs/SilkDecoder.cs`,
`Silk/Structs/SilkDecoderControl.cs`, `Silk/Structs/SilkEncoder.cs`, `Silk/Structs/SilkEncoderControl.cs`,
`Silk/Structs/SilkLPState.cs`, `Silk/Structs/SilkNSQState.cs`, `Silk/Structs/SilkPrefilterState.cs`,
`Silk/Structs/SilkResamplerState.cs`, `Silk/Structs/SilkShapeState.cs`, `Silk/Structs/SilkVADState.cs`,
`Silk/Structs/StereoDecodeState.cs`, `Silk/Structs/StereoEncodeState.cs`, `Silk/SumSqrShift.cs`, `Silk/Tables.cs`,
`Silk/TuningParameters.cs`, `Silk/VQ_WMat_EC.cs`, `Silk/VoiceActivityDetection.cs`.

```text
Copyright (c) 2016 Logan Stromberg
```

Carried by `Common/CPlusPlus/Arrays.cs`, `Common/CPlusPlus/BoxedValue.cs`, `Common/CPlusPlus/Pointer.cs`,
`Opus/OpusException.cs`.

```text
Copyright (c) 2001-2011 Timothy B. Terriberry
Ported to C# by Logan Stromberg
```

Carried by `Common/EntropyCoder.cs`.

```text
Copyright (c) 2006-2011 Skype Limited.
Copyright (c) 2007-2008 CSIRO
Copyright (c) 2007-2011 Xiph.Org Foundation
Ported to C# by Logan Stromberg
```

Carried by `Common/Inlines.cs`.

```text
Copyright (c) 2024 Logan Stromberg
```

Carried by `IOpusDecoder.cs`, `IOpusEncoder.cs`.

```text
Copyright (c) 2008-2011 Octasic Inc.
Originally written by Jean-Marc Valin
Ported to C# by Logan Stromberg
```

Carried by `Opus/MultiLayerPerceptron.cs`, `Opus/Structs/MLP.cs`.

The licence text after those lines:

```text
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of Internet Society, IETF or IETF Trust, nor the
names of specific contributors, may be used to endorse or promote
products derived from this software without specific prior written
permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

#### Speex licence, carried by the resampler files

```text
Copyright (C) 2007-2008 Jean-Marc Valin
Copyright (C) 2008      Thorvald Natvig
Ported to C# by Logan Stromberg
File: Resampler.cs
Arbitrary resampling code
```

Carried by `Common/SpeexResampler.cs`.

```text
Copyright (C) 2007-2008 Jean-Marc Valin
Copyright (C) 2008      Thorvald Natvig
Ported to C# by Logan Stromberg
```

Carried by `IResampler.cs`.

The licence text after those lines:

```text
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

1. Redistributions of source code must retain the above copyright notice,
this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

3. The name of the author may not be used to endorse or promote products
derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY DIRECT,
INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

### License

Upstream's `LICENSE` file, blob `62d21b6b7a3b7bc98dba02ee6aafc9db865da8b4`, also copied unchanged to `ThirdParty/Concentus/LICENSE`.
The NuGet package carries the same text.

```text
Copyright (c) by various holding parties, including (but not limited to): 
Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation,
Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.


This repository and its redistributable packages contain independently compiled
versions of the Opus C reference library, which is maintained by Xiph.org and the
Opus open-source contributors. The source code for these libraries is freely available
at https://gitlab.xiph.org/xiph/opus/-/tags/v1.5.2, and all binaries are being
redistributed to you under the same terms of the general Opus license dictated above.
```
