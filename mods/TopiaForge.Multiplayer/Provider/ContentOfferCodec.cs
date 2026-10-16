using System;
using System.Collections.Generic;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// The content a joining player presents for admission, inside its join request. Version 1 is binary, written with the
    /// engine's protocol primitives:
    /// <code>
    /// [version 1][game build][protocol version][peer range][mod count]
    /// per mod: [id][version][sha256: 32 bytes][mode]
    ///   session mods only: [presence][protocol version][peer range present][peer range]?[file count]([path][sha256: 32 bytes])*
    /// </code>
    /// Mods are sorted by id, each once, so one offer has one spelling; client-local mods are never presented. Reading is
    /// strict and bounded: anything else is refused, and the host answers with a protocol refusal.
    /// </summary>
    internal static class ContentOfferCodec
    {
        private const byte Version = 1;
        private const int DigestBytes = 32;

        /// <summary>The longest game build a descriptor allows.</summary>
        private const int MaximumGameBuildLength = 16;

        /// <summary>Encodes <paramref name="offer"/>; null when it would not fit the join request's content field.</summary>
        internal static byte[]? TryEncode(InternalContentOffer offer)
        {
            if (offer == null) throw new ArgumentNullException(nameof(offer));
            var writer = new ProtocolWriter(256 + (offer.Mods.Count * 96));
            writer.WriteByte(Version);
            writer.WriteString(offer.GameBuild);
            writer.WriteString(offer.ProtocolVersion);
            writer.WriteString(offer.PeerVersionRange);
            writer.WriteVarUInt((ulong)offer.Mods.Count);
            foreach (var mod in offer.Mods)
            {
                writer.WriteString(mod.Id);
                writer.WriteString(mod.Version);
                WriteDigest(writer, mod.PackageSha256);
                writer.WriteByte((byte)mod.Mode);
                if (mod.Mode != InternalContentMode.Session) continue;
                writer.WriteByte((byte)mod.Presence);
                writer.WriteString(mod.ProtocolVersion!);
                writer.WriteOptionalString(mod.PeerVersionRange);
                writer.WriteVarUInt((ulong)mod.SynchronizedFiles.Count);
                foreach (var file in mod.SynchronizedFiles)
                {
                    writer.WriteString(file.Path);
                    WriteDigest(writer, file.Sha256);
                }
            }

            return writer.Length > JoinContent.MaximumContentBytes ? null : writer.ToArray();
        }

        /// <summary>Reads an offer; false for anything that is not exactly a valid version 1 offer.</summary>
        internal static bool TryDecode(byte[] bytes, out InternalContentOffer? offer)
        {
            offer = null;
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length == 0 || bytes.Length > JoinContent.MaximumContentBytes) return false;
            try
            {
                var reader = new ProtocolReader(bytes);
                if (reader.ReadByte() != Version) return false;
                var gameBuild = reader.ReadString(MaximumGameBuildLength);
                var protocolVersion = reader.ReadString(InternalContentLimits.MaximumVersionLength);
                var peerRange = reader.ReadString(InternalContentLimits.MaximumRangeLength);
                var count = (int)reader.ReadVarUInt(InternalContentLimits.MaximumPackages);
                var mods = new List<InternalContentDescriptor>(count);
                string? previous = null;
                for (var index = 0; index < count; index++)
                {
                    var mod = ReadMod(reader);
                    if (mod.Mode == InternalContentMode.ClientLocal) return false;
                    if (previous != null && string.CompareOrdinal(previous, mod.Id) >= 0) return false;
                    previous = mod.Id;
                    mods.Add(mod);
                }

                if (!reader.AtEnd || !ServerDescriptor.IsGameBuild(gameBuild)) return false;
                offer = new InternalContentOffer(gameBuild, protocolVersion, peerRange, mods.AsReadOnly());
                return true;
            }
            catch (ProtocolException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                // A descriptor's own rules refused a field: the offer is malformed.
                return false;
            }
        }

        private static InternalContentDescriptor ReadMod(ProtocolReader reader)
        {
            var id = reader.ReadString(InternalContentLimits.MaximumIdLength);
            var version = reader.ReadString(InternalContentLimits.MaximumVersionLength);
            var sha256 = ReadDigest(reader);
            var mode = reader.ReadByte();
            if (!Enum.IsDefined(typeof(InternalContentMode), (int)mode)) throw new ProtocolException("A content mode is unknown.");
            if ((InternalContentMode)mode != InternalContentMode.Session)
            {
                return new InternalContentDescriptor(id, version, sha256, (InternalContentMode)mode);
            }

            var presence = reader.ReadByte();
            if (presence != (byte)InternalContentPresence.Required && presence != (byte)InternalContentPresence.Optional)
            {
                throw new ProtocolException("A session mod's presence is unknown.");
            }

            var protocol = reader.ReadString(InternalContentLimits.MaximumVersionLength);
            var range = reader.ReadOptionalString(InternalContentLimits.MaximumRangeLength);
            var fileCount = (int)reader.ReadVarUInt(InternalContentLimits.MaximumSynchronizedFiles);
            var files = new InternalSynchronizedFile[fileCount];
            for (var index = 0; index < fileCount; index++)
            {
                files[index] = new InternalSynchronizedFile(reader.ReadString(InternalContentLimits.MaximumPathLength), ReadDigest(reader));
            }

            return new InternalContentDescriptor(id, version, sha256, InternalContentMode.Session, (InternalContentPresence)presence, protocol, range, files);
        }

        private static void WriteDigest(ProtocolWriter writer, string hex)
        {
            for (var index = 0; index < DigestBytes; index++)
            {
                writer.WriteByte((byte)((Nibble(hex[index * 2]) << 4) | Nibble(hex[(index * 2) + 1])));
            }
        }

        private static string ReadDigest(ProtocolReader reader)
        {
            var digest = new byte[DigestBytes];
            for (var index = 0; index < DigestBytes; index++) digest[index] = reader.ReadByte();
            return WireText.ToHex(digest);
        }

        private static int Nibble(char character) => character <= '9' ? character - '0' : character - 'a' + 10;
    }
}
