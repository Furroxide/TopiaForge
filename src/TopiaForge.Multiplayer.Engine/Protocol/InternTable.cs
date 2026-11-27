using System;
using System.Collections.Generic;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// Sender half of a per-connection string intern table for owner and wire ids. A reference is a varint; zero
    /// introduces a new string that takes the next index. Once the table is full, new strings travel inline after
    /// <see cref="InlineReference"/> and take no index, so a long-lived connection never runs out of names. The table is
    /// valid only on an ordered channel, where every definition reaches the reader before its first use.
    /// </summary>
    internal sealed class InternWriter
    {
        /// <summary>Gets the reference that marks a string sent inline, without an index.</summary>
        internal const ulong InlineReference = ProtocolLimits.MaximumInternedStrings + 1;

        private readonly Dictionary<string, uint> ids = new Dictionary<string, uint>(StringComparer.Ordinal);

        internal void Write(ProtocolWriter writer, string value)
        {
            if (ids.TryGetValue(value, out var id))
            {
                writer.WriteVarUInt(id);
                return;
            }

            if (ids.Count >= ProtocolLimits.MaximumInternedStrings)
            {
                writer.WriteVarUInt(InlineReference);
                writer.WriteString(value);
                return;
            }

            writer.WriteVarUInt(0);
            writer.WriteString(value);
            ids.Add(value, (uint)(ids.Count + 1));
        }
    }

    /// <summary>Receiver half of a per-connection intern table. It rejects references it has not been taught.</summary>
    internal sealed class InternReader
    {
        private readonly List<string> values = new List<string>();

        internal string Read(ProtocolReader reader)
        {
            var id = reader.ReadVarUInt(InternWriter.InlineReference);
            if (id == InternWriter.InlineReference) return reader.ReadIdentity();
            if (id != 0)
            {
                if ((int)id > values.Count) throw new ProtocolException("An interned id was used before it was defined.");
                return values[(int)id - 1];
            }

            if (values.Count >= ProtocolLimits.MaximumInternedStrings)
            {
                throw new ProtocolException("The peer defined more interned strings than the table holds.");
            }

            var value = reader.ReadIdentity();
            values.Add(value);
            return value;
        }
    }
}
