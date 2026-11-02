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
        internal static int Measure(UiNode? node, string clockEpoch, int capacity)
        {
            var writer = new Counter(capacity);
            Write(writer, node, clockEpoch);
            return writer.Length;
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
