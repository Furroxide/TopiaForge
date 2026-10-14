# Multiplayer voice — design record

Status: Implementing (WS2g of the live-multiplayer plan) · Date: 2026-10-13 · Owner: multiplayer

This records voice chat below the user interface: the codec the provider runs, and the session engine's path for voice
frames between peers. Microphone capture, resampling, voice activity detection, playback and the voice worker thread
come in a later slice, as does every in-game control. No player-facing behaviour changes until then.

WS2g lands in two pull requests, each with its own part of this record:

1. **The codec** (part 1): Opus through vendored Concentus, wrapped for the provider.
2. **The engine's voice path** (part 2): the codec seam, the frame format, the jitter buffer and the host's relay
   policy.

## Part 1: the codec

The code lives in `mods/TopiaForge.Multiplayer/Voice/`, inside the provider only, and every type is internal. The
engine never names a codec; the provider hands it one through the seam in part 2.

### 1.1 Settings

| Setting | Value |
| --- | --- |
| Format | 48 kHz mono, 16-bit samples |
| Frame | 20 ms, 960 samples, one Opus frame per packet |
| Application | VOIP, signal type voice |
| Bitrate | Unconstrained VBR, 24 kbit/s target by default, 20 to 24 kbit/s allowed |
| Audio band | Capped at wideband (8 kHz), so the encoder stays in SILK-only mode |
| Silence | Discontinuous transmission: TOC-only packets after a hangover |
| Loss | In-band FEC, sized by an expected-loss hint (10% by default, 0 turns it off) |
| Complexity | 10 by default, 5 to 10 allowed |
| Packet | At most 160 bytes |

`OpusVoiceCodec` holds the settings, validates packets, and creates one `OpusVoiceEncoder` per talker and one
`OpusVoiceDecoder` per speaker. Encoders and decoders belong to one thread each. Complexity, bitrate and the loss hint
can change live on an encoder; the voice service will feed the hint from the loss its transport measures.

### 1.2 Why wideband

At 20 to 24 kbit/s the encoder picks fullband hybrid mode (TOC configuration 15) unless capped. Hybrid splits the
bits between SILK and CELT, and SILK's share stays under the threshold at which it codes in-band FEC (LBRR) at all.
Measured on the synthetic speech the tests use, 24 kbit/s, complexity 10, on .NET 10:

| Mode | Loss hint | Frames FEC improved | FEC / PLC SNR on lost frames | Encode | Decode |
| --- | --- | --- | --- | --- | --- |
| Fullband hybrid | 10% or 20% | 0 of 20 | 1.9 / 1.9 dB | 0.76 ms, 134 KB | 0.16 ms, 26 KB |
| Fullband hybrid | 25% | 13 of 20 | 4.0 / 1.6 dB, and 6.4 dB round trip | 0.80 ms, 143 KB | 0.15 ms, 27 KB |
| Wideband SILK | 10% | 13 of 20 | 5.4 / 1.9 dB, and 9.0 dB round trip | 0.68 ms, 104 KB | 0.10 ms, 12 KB |

So below a 25% hint, fullband FEC is a no-op, and buying it at 25% costs the clean stream 3 dB. Wideband keeps FEC
working at the default hint, halves decode time and allocation, and covers the speech band (up to 8 kHz). The sizes
are allocation per frame inside Concentus.

### 1.3 Vendored Concentus

Concentus 2.2.2 is a C# port of libopus 1.1.2 (fixed point) and of the Speex resampler. Its NuGet package names
repository commit `6c2328dc19044601e33a9c11628b8d60e1f3011c`; the files were fetched from GitHub at that commit, each
checked against its git blob hash, and compiled into the provider as source.

- **Namespace and visibility.** `Concentus` became `TopiaForge.Multiplayer.ThirdParty.Concentus` and every top-level
  type is internal, so this copy cannot collide with another Concentus in the game; Mono has no binding redirects.
- **Subset.** 122 of 149 files. Left out: the native-library interop (`Native/`) and the two factories whose only
  addition is choosing a native libopus, the multistream codec and its layouts, the quality-comparison tool, and two
  unused definitions. The set is minimal: removing any remaining file breaks the build.
- **No SIMD.** netstandard2.1 includes `System.Numerics.Vector<T>`, which the game's Mono forwards to a
  `System.Numerics.Vectors` it does not ship (`MultiplayerMonoSpike.md`, section 5). The three `Vector<T>` paths are
  removed and the scalar code each duplicated remains, so there is nothing to fail at JIT time, and every platform
  computes the same bytes.
- **`PARITY`.** Upstream's project defines it for every build; it selects the bit-exact fixed-point helpers over
  floating-point approximations. The two files that test it define it themselves.
- **One upstream defect fixed.** libopus treats a payload of one byte or less as a lost frame by setting `data` to
  null. The port keeps a separate offset into the payload and did not reset it, so once a decoder had decoded audio,
  every discontinuous-transmission packet threw `ArgumentOutOfRangeException`. `opus_decode_frame` now resets the
  offset with the payload.

`mods/TopiaForge.Multiplayer/THIRD_PARTY_NOTICES.md`, which ships in the provider package, lists every change and
every file with its upstream blob hash, and quotes every copyright line the files carry with both licence texts:
the Opus BSD licence for the codec and the Speex BSD licence for the resampler.

### 1.4 Packets from the network

`OpusToc` checks the first byte of a packet (RFC 6716, section 3.1) before anything decodes it. A voice packet is 1 to
160 bytes, mono, holds one frame (code 0), and uses one of the nine configurations whose frames last 20 ms: SILK
narrow, medium and wide band, hybrid super-wide and full band, and CELT narrow, wide, super-wide and full band. A code-0
packet carries no length fields, so this check bounds the work: whatever follows the TOC byte, decoding produces exactly
one 20 ms frame. The host applies it to every frame it relays (part 2).

A decoder never throws on what the network sends. A packet that fails the check is concealed instead, and so is one
that Concentus rejects; the decoder then resets, because a throw can leave it half updated. Fuzzing with 20,000 random
payloads behind valid TOC bytes produced no rejection once the defect above was fixed.

### 1.5 Allocation and cost

The wrapper writes into the caller's buffers and allocates nothing: a test runs the same frames through the bare
Concentus objects and requires the same allocation, byte for byte, and the same output. Concentus itself allocates
scratch arrays for every frame, as upstream does: about 104 KB per encode and 12 KB per decode in wideband. A talker
and three listeners therefore make about 7 MB of short-lived garbage a second. The voice worker thread, which the next
slice adds, keeps that and the coding time off the game's main thread; removing it would mean rewriting upstream.

The Mono spike measured complexity 10 at about 0.43 ms per encode and 0.09 ms per decode. A test runs a four-player
session's codec work for one second of speech, four encodes and twelve decodes per frame, on one thread, and requires
it to finish within real time; it takes about 0.1 s on the development machine once compiled, 0.3 s cold.

### 1.6 Determinism

With analysis off and `PARITY` on, encoding and decoding are integer arithmetic throughout. A test freezes the SHA-256
of the packets for five seconds of synthetic speech at the default settings and at complexity 5, 20 kbit/s and a 20%
hint, and of the decoded audio. The test signal uses only IEEE basic arithmetic, never the runtime's sine, whose last
bit differs between C libraries. The hashes match on Windows and Linux under .NET 10; Mono was not run.

### 1.7 Tests

`OpusVoiceCodecTests` and `OpusVoiceCostTests`, in the multiplayer test project:

- round-trip SNR on synthetic voiced speech: at least 7 dB at complexity 10 and 5 (measured 9.0 and 9.1 dB), since a
  perceptual codec does not match the waveform closely, while a lost or misaligned signal scores near 0 dB;
- packet bounds at both bitrates and on full-scale noise, with every packet SILK-only wideband;
- loss: with a lost packet every 20 frames, FEC recovery beats concealment by at least 2 dB and on at least half the
  lost frames, and without a loss hint it does not;
- DTX: sustained silence becomes TOC-only packets that decode to near-silent comfort noise, and speech resumes at once;
- the frozen hashes;
- TOC validation over all 256 values and the length bounds;
- the four-player time budget, the allocation comparison, 4,000 hostile packets, setting bounds, resets, and the Speex
  resampler turning 44.1 and 16 kHz tones into 48 kHz at the same pitch and level.
