using System.Buffers.Binary;

namespace TopiaForge.Acceptance.Windows;

internal static class LoopbackCaptureTests
{
    internal static void Run()
    {
        var pcm = Format(false);
        var integer = LoopbackFormat.Parse(pcm);
        var waveform = new LoopbackWaveform(integer, 1);
        var data = new byte[16]; // Eight mono samples at 8kHz = one millisecond.
        BinaryPrimitives.WriteInt16LittleEndian(data, short.MinValue);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), 16384);
        waveform.Append(data, 8, false);
        Check(waveform.Frames == 8 && waveform.Peak == 1 && Math.Abs(waveform.Rms - Math.Sqrt(1.25 / 8)) < 1e-10,
            "PCM peak/RMS derive from all actual samples");
        waveform.Append(new byte[16], 8, false);
        Check(waveform.Frames == 8, "duration cap truncates later packets");
        using var wav = new MemoryStream();
        waveform.Write(wav);
        var bytes = wav.ToArray();
        Check(System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) == bytes.Length - 8,
            "WAV RIFF length is exact");
        Check(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(46)) == 8 && System.Text.Encoding.ASCII.GetString(bytes, 50, 4) == "data",
            "WAV fact chunk records actual frame count");
        var floating = LoopbackFormat.Parse(Format(true));
        var floats = new LoopbackWaveform(floating, 1);
        var floatData = new byte[32];
        BinaryPrimitives.WriteSingleLittleEndian(floatData, -0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(floatData.AsSpan(4), 0.75f);
        floats.Append(floatData, 8, false);
        Check(floats.Peak == 0.75 && Math.Abs(floats.Rms - Math.Sqrt(0.625 / 8)) < 1e-10, "float metrics preserve signed amplitudes");
        var silent = new LoopbackWaveform(floating, 1);
        silent.Append(floatData, 8, true);
        Check(silent.SilentFrames == 8 && silent.Peak == 0 && silent.Rms == 0, "explicit native silence flag clears packet bytes");
        var badFloat = new byte[32]; BinaryPrimitives.WriteSingleLittleEndian(badFloat, float.NaN);
        Reject(() => new LoopbackWaveform(floating, 1).Append(badFloat, 8, false), "nonfinite audio rejected");
        Reject(() => new LoopbackWaveform(floating, 1).Append(new byte[31], 8, false), "partial packet rejected");
        var bad = pcm.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(12), 1);
        Reject(() => LoopbackFormat.Parse(bad), "inconsistent block alignment rejected");
        bad = pcm.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(14), 24);
        Reject(() => LoopbackFormat.Parse(bad), "unsupported PCM24 rejected");
        bad = pcm.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(4), 999999);
        Reject(() => LoopbackFormat.Parse(bad), "unbounded sample rate rejected");
        Reject(() => LoopbackFormat.Parse(new byte[4096]), "unbounded format rejected");
        var extensible = new byte[40]; pcm.CopyTo(extensible, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(extensible, ushort.MaxValue - 1);
        BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(16), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(extensible.AsSpan(18), 16);
        new Guid("00000001-0000-0010-8000-00aa00389b71").TryWriteBytes(extensible.AsSpan(24));
        Check(LoopbackFormat.Parse(extensible).Bits == 16, "bounded extensible PCM admitted");
        extensible[24] = 9; Reject(() => LoopbackFormat.Parse(extensible), "unknown extensible subtype rejected");
        var profile = new DeviceProfile(1920,1080,96,"display","render-id",false,Array.Empty<string>());
        Reject(() => LoopbackCapture.ValidateRequest(profile,1000), "disabled loopback refused before device activation");
        Reject(() => LoopbackCapture.ValidateRequest(profile with { LoopbackEnabled = true },3001), "capture duration cannot exceed three seconds");
        Reject(() => LoopbackCapture.ValidateRequest(profile with { LoopbackEnabled = true, AudioEndpointId = "" },1000), "missing pinned endpoint refused");
        Console.WriteLine("Loopback waveform/request contracts passed (17 checks; no device opened).");
    }
    private static byte[] Format(bool floating)
    {
        var bytes = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, floating ? (ushort)3 : (ushort)1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 8000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), floating ? 32000 : 16000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), floating ? (ushort)4 : (ushort)2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), floating ? (ushort)32 : (ushort)16);
        return bytes;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Loopback contract: " + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Loopback contract did not refuse: " + message);
    }
}
