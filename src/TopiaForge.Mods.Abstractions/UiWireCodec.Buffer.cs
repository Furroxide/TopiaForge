using System;
using System.Text;

namespace TopiaForge.Mods
{
    internal interface IUiWireWriter
    {
        void WriteByte(byte value);
        void WriteSingle(float value);
        void WriteUInt64(ulong value);
        void WriteVarUInt(ulong value);
        void WriteString(string value);
    }

    internal interface IUiWireReader
    {
        bool AtEnd { get; }
        bool TryReadByte(out byte value);
        bool TryReadSingle(out float value);
        bool TryReadUInt64(out ulong value);
        bool TryReadVarUInt(ulong maximum, out ulong value);
        bool TryReadInt32(int maximum, out int value);
        bool TryReadString(int maximumBytes, out string value);
    }

    internal static partial class UiWireCodec
    {
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>Runs the real encoder without retaining bytes, preserving its shape and capacity failures.</summary>
        internal static int Measure(UiNode? node, string clockEpoch, int capacity, bool retained = false, UiWireEnvelope? envelope = null)
        {
            var writer = new Counter(capacity);
            WriteComposition(writer, node, clockEpoch, retained, envelope);
            return writer.Length;
        }

        internal static int MeasureTargetedSurface(
            string ownerPackageId, string playerId, ulong token, ulong revision,
            UiSurfaceRequest request, string body, bool visible, UiNode? content, string clockEpoch,
            bool retained = false, UiWireEnvelope? envelope = null)
        {
            var writer = new Counter(32 * 1024);
            writer.WriteByte(1);
            writer.WriteByte(10);
            writer.WriteString(ownerPackageId);
            writer.WriteString(playerId);
            writer.WriteVarUInt(token);
            writer.WriteVarUInt(revision);
            WriteTargetedSurfaceBody(writer, request, body, visible, content, clockEpoch, retained, envelope);
            return writer.Length;
        }

        // The channel writes its own message type, mod id, player id, surface token and revision first.
        internal static void WriteTargetedSurfaceBody(
            IUiWireWriter writer, UiSurfaceRequest request, string body, bool visible, UiNode? content, string clockEpoch,
            bool retained, UiWireEnvelope? envelope = null)
        {
            Text(writer, request.Id);
            Text(writer, request.Title);
            Text(writer, body);
            writer.WriteByte((byte)request.Kind);
            writer.WriteSingle(request.Width);
            writer.WriteSingle(request.Height);
            writer.WriteByte((byte)request.HudAnchor);
            writer.WriteByte((byte)request.HudVisibility);
            Flag(writer, visible);
            WriteComposition(writer, content, clockEpoch, retained, envelope);
        }

        private sealed class Counter : IUiWireWriter
        {
            private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
            private readonly int capacity;
            internal int Length { get; private set; }
            internal Counter(int capacity) { this.capacity = capacity; }
            private void Add(int count)
            {
                if (count > capacity - Length) throw new InvalidOperationException("The remote UI message is full.");
                Length += count;
            }
            public void WriteByte(byte value) => Add(1);
            public void WriteSingle(float value) => Add(4);
            public void WriteUInt64(ulong value) => Add(8);
            public void WriteVarUInt(ulong value)
            {
                do { Add(1); value >>= 7; } while (value != 0);
            }
            public void WriteString(string value)
            {
                var bytes = Utf8.GetByteCount(value);
                WriteVarUInt((ulong)bytes); Add(bytes);
            }
        }
    }
}
