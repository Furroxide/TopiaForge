using System.Buffers.Binary;

namespace TopiaForge.Acceptance.Windows;

internal sealed record LoopbackFormat(byte[] Bytes, int SampleRate, int Channels, int Bits, int BlockAlign, bool FloatingPoint)
{
    internal static LoopbackFormat Parse(byte[] bytes)
    {
        if (bytes.Length is not (18 or 40)) throw new InvalidDataException("Unbounded or unsupported WAVEFORMATEX size.");
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
        var rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        var average = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var align = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
        var extra = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(16));
        var floating = tag == 3;
        if (tag == 65534)
        {
            if (bytes.Length != 40 || extra != 22 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18)) != bits)
                throw new InvalidDataException("Unsupported extensible audio precision.");
            var subtype = new Guid(bytes.AsSpan(24,16));
            floating = subtype == new Guid("00000003-0000-0010-8000-00aa00389b71");
            if (!floating && subtype != new Guid("00000001-0000-0010-8000-00aa00389b71")) throw new InvalidDataException("Unsupported audio subtype.");
        }
        else if (bytes.Length != 18 || extra != 0 || tag is not (1 or 3)) throw new InvalidDataException("Unsupported audio encoding.");
        if (channels is < 1 or > 8 || rate is < 8000 or > 192000 || (floating ? bits != 32 : bits != 16)
            || align != channels * bits / 8 || average != checked(rate * align)) throw new InvalidDataException("Audio format is outside admitted capture bounds.");
        return new(bytes.ToArray(), rate, channels, bits, align, floating);
    }
}

internal sealed class LoopbackWaveform(LoopbackFormat format, int milliseconds)
{
    private readonly MemoryStream samples = new();
    private readonly int maximumFrames = checked(format.SampleRate * milliseconds / 1000);
    private double squares;
    internal double Peak { get; private set; }
    internal long Frames { get; private set; }
    internal long SilentFrames { get; private set; }
    internal double Rms => Frames == 0 ? 0 : Math.Sqrt(squares / (Frames * format.Channels));
    internal bool Complete => Frames >= maximumFrames;
    internal void Append(byte[] data, int frames, bool silent)
    {
        if (milliseconds is < 1 or > 3000 || frames < 0 || frames > format.SampleRate || data.Length != checked(frames * format.BlockAlign)
            || data.Length > 8 * 192000 * 4) throw new InvalidDataException("Audio packet is outside capture bounds.");
        var admitted = (int)Math.Min(frames, maximumFrames - Frames);
        var length = checked(admitted * format.BlockAlign);
        if (silent) { Array.Clear(data, 0, length); SilentFrames += admitted; }
        for (var offset = 0; offset < length; offset += format.Bits / 8)
        {
            var value = format.FloatingPoint ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset)))
                : BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset)) / 32768d;
            if (!double.IsFinite(value) || Math.Abs(value) > 8) throw new InvalidDataException("Audio packet contains invalid sample values.");
            squares += value * value; Peak = Math.Max(Peak, Math.Abs(value));
        }
        samples.Write(data, 0, length); Frames += admitted;
        if (samples.Length > 20 * 1024 * 1024) throw new InvalidDataException("Audio memory bound exceeded.");
    }
    internal void Write(Stream destination)
    {
        using var writer = new BinaryWriter(destination, System.Text.Encoding.ASCII, true);
        var data = samples.ToArray();
        // fmt contains the exact admitted mix format; fact records actual captured frames.
        writer.Write("RIFF"u8); writer.Write(checked(4 + 8 + format.Bytes.Length + 12 + 8 + data.Length)); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(format.Bytes.Length); writer.Write(format.Bytes);
        writer.Write("fact"u8); writer.Write(4); writer.Write(checked((uint)Frames));
        writer.Write("data"u8); writer.Write(data.Length); writer.Write(data); writer.Flush();
    }
}
