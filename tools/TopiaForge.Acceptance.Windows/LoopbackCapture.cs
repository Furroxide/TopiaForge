using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TopiaForge.Acceptance.Windows;

internal sealed record AudioFact(string Path, string Sha256, long Length, string EndpointId, int SampleRate,
    int Channels, int BitsPerSample, string Encoding, long Frames, int RequestedMilliseconds,
    double CapturedMilliseconds, double Rms, double Peak, int Discontinuities, int TimestampErrors,
    long SilentFrames, string StartedUtc, string CompletedUtc, bool InitialDiscontinuity);

// WASAPI shared render-endpoint loopback: never requests a capture/microphone endpoint.
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/capturing-a-stream
internal static class LoopbackCapture
{
    internal static Task<AudioFact> Capture(OwnedGameProbe game, DeviceProfile profile, string root,
        string relative, int milliseconds, CancellationToken cancellationToken)
    {
        ValidateRequest(profile, milliseconds);
        var path = BoundedJson.Child(root, relative);
        if (!relative.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Loopback output must be a WAV artifact.");
        if (File.Exists(path) || Directory.Exists(path)) throw new InvalidDataException("Loopback output already exists.");
        return Task.Run(() => CaptureOnWorker(game, profile, path, relative, milliseconds, cancellationToken), cancellationToken);
    }
    internal static void ValidateRequest(DeviceProfile profile, int milliseconds)
    {
        if (!profile.LoopbackEnabled) throw new InvalidOperationException("Render loopback is not enabled by the admitted device profile.");
        if (milliseconds is < 1 or > 3000) throw new ArgumentOutOfRangeException(nameof(milliseconds), "Capture must be between 1 and 3000 milliseconds.");
        if (string.IsNullOrWhiteSpace(profile.AudioEndpointId) || profile.AudioEndpointId.Length > 1024 || profile.AudioEndpointId.Any(char.IsControl))
            throw new InvalidDataException("A bounded exact render-endpoint id is required.");
    }
    internal static string DescribeDefaultEndpoint() => Task.Run(() =>
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI loopback requires Windows.");
        Wasapi.Check(Wasapi.CoInitializeEx(IntPtr.Zero, 0));
        Wasapi.IMMDeviceEnumerator? enumerator = null;
        try { enumerator = CreateEnumerator(); return ReadDefaultRenderId(enumerator); }
        finally { Release(enumerator); Wasapi.CoUninitialize(); }
    }).GetAwaiter().GetResult();

    private static AudioFact CaptureOnWorker(OwnedGameProbe game, DeviceProfile profile, string path, string relative,
        int milliseconds, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI loopback requires Windows.");
        cancellationToken.ThrowIfCancellationRequested();
        Wasapi.Check(Wasapi.CoInitializeEx(IntPtr.Zero, 0));
        Wasapi.IMMDeviceEnumerator? enumerator = null;
        Wasapi.IMMDevice? device = null;
        Wasapi.IAudioClient? audio = null;
        Wasapi.IAudioCaptureClient? capture = null;
        var formatPointer = IntPtr.Zero;
        var started = false;
        try
        {
            enumerator = CreateEnumerator();
            RequireEnvironment(game, profile, enumerator);
            Wasapi.Check(enumerator.GetDefaultAudioEndpoint(0, 0, out device)); // eRender, eConsole
            RequireRender(device);
            if (!string.Equals(ReadEndpointId(device), profile.AudioEndpointId, StringComparison.Ordinal))
                throw new InvalidOperationException("The opened render endpoint changed during admission.");
            var audioId = Wasapi.AudioClientInterface;
            Wasapi.Check(device.Activate(ref audioId, 23, IntPtr.Zero, out var audioObject));
            audio = (Wasapi.IAudioClient)audioObject;
            Wasapi.Check(audio.GetMixFormat(out formatPointer));
            if (formatPointer == IntPtr.Zero) throw new InvalidDataException("The render endpoint returned no mix format.");
            var extra = unchecked((ushort)Marshal.ReadInt16(formatPointer, 16));
            if (extra is not (0 or 22)) throw new InvalidDataException("The render endpoint format exceeds its bound.");
            var formatBytes = new byte[18 + extra];
            Marshal.Copy(formatPointer, formatBytes, 0, formatBytes.Length);
            var format = LoopbackFormat.Parse(formatBytes);
            var session = Guid.Empty;
            Wasapi.Check(audio.Initialize(0, 0x00020000, 1000000, 0, formatPointer, ref session)); // shared, LOOPBACK, 100ms
            Wasapi.Check(audio.GetBufferSize(out var bufferFrames));
            if (bufferFrames == 0 || bufferFrames > format.SampleRate) throw new InvalidDataException("The endpoint buffer exceeds one second.");
            var captureId = Wasapi.CaptureClientInterface;
            Wasapi.Check(audio.GetService(ref captureId, out var captureObject));
            capture = (Wasapi.IAudioCaptureClient)captureObject;
            var waveform = new LoopbackWaveform(format, milliseconds);
            var discontinuities = 0; var timestampErrors = 0; var seenPacket = false; var initialDiscontinuity = false;
            var startedUtc = DateTime.UtcNow.ToString("o");
            RequireEnvironment(game, profile, enumerator);
            Wasapi.Check(audio.Start()); started = true;
            var watch = Stopwatch.StartNew();
            var nextEnvironmentCheck = 0L;
            while (watch.ElapsedMilliseconds < milliseconds && !waveform.Complete)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (watch.ElapsedMilliseconds >= nextEnvironmentCheck)
                {
                    RequireEnvironment(game, profile, enumerator);
                    nextEnvironmentCheck = watch.ElapsedMilliseconds + 50;
                }
                Wasapi.Check(capture.GetNextPacketSize(out var next));
                var packets = 0;
                while (next != 0 && !waveform.Complete)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++packets > 512 || next > bufferFrames || watch.ElapsedMilliseconds > milliseconds + 100)
                        throw new InvalidDataException("Loopback packet drain exceeded its bound.");
                    Wasapi.Check(capture.GetBuffer(out var pointer, out var frames, out var flags, out _, out _));
                    try
                    {
                        if (frames == 0 || frames > bufferFrames || (flags & ~7u) != 0) throw new InvalidDataException("Invalid loopback packet.");
                        var bytes = new byte[checked((int)frames * format.BlockAlign)];
                        var silent = (flags & 2) != 0;
                        if (!silent)
                        {
                            if (pointer == IntPtr.Zero) throw new InvalidDataException("Loopback returned a null non-silent packet.");
                            Marshal.Copy(pointer, bytes, 0, bytes.Length);
                        }
                        if (!seenPacket) initialDiscontinuity = (flags & 1) != 0;
                        seenPacket = true;
                        if ((flags & 1) != 0) discontinuities++;
                        if ((flags & 4) != 0) timestampErrors++;
                        waveform.Append(bytes, checked((int)frames), silent);
                    }
                    finally { Wasapi.Check(capture.ReleaseBuffer(frames)); }
                    Wasapi.Check(capture.GetNextPacketSize(out next));
                }
                if (cancellationToken.WaitHandle.WaitOne(5)) cancellationToken.ThrowIfCancellationRequested();
            }
            Wasapi.Check(audio.Stop()); started = false;
            cancellationToken.ThrowIfCancellationRequested();
            RequireEnvironment(game, profile, enumerator);
            if (waveform.Frames == 0) throw new InvalidOperationException("No render-loopback frames were observed; silence is not fabricated.");
            BoundedJson.PhysicalPath(path);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { waveform.Write(stream); stream.Flush(true); }
            return new(relative, BoundedJson.Hash(path), new FileInfo(path).Length, profile.AudioEndpointId,
                format.SampleRate, format.Channels, format.Bits, format.FloatingPoint ? "ieee-float" : "pcm",
                waveform.Frames, milliseconds, waveform.Frames * 1000d / format.SampleRate, waveform.Rms, waveform.Peak,
                discontinuities, timestampErrors, waveform.SilentFrames, startedUtc, DateTime.UtcNow.ToString("o"), initialDiscontinuity);
        }
        finally
        {
            try { if (started && audio != null) audio.Stop(); }
            finally
            {
                Release(capture); Release(audio); Release(device); Release(enumerator);
                if (formatPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPointer);
                Wasapi.CoUninitialize();
            }
        }
    }
    private static Wasapi.IMMDeviceEnumerator CreateEnumerator()
    {
        var clsid = Wasapi.EnumeratorClass; var iid = Wasapi.EnumeratorInterface;
        Wasapi.Check(Wasapi.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var enumerator));
        return enumerator;
    }
    private static void RequireEnvironment(OwnedGameProbe game, DeviceProfile profile, Wasapi.IMMDeviceEnumerator enumerator)
    {
        game.RequireForeground(profile.Width, profile.Height, profile.Dpi);
        ScreenCapture.RequireMonitor(game.Window, profile.DisplayName);
        if (!string.Equals(ReadDefaultRenderId(enumerator), profile.AudioEndpointId, StringComparison.Ordinal))
            throw new InvalidOperationException("The default render endpoint differs from the admitted device profile.");
    }
    private static string ReadDefaultRenderId(Wasapi.IMMDeviceEnumerator enumerator)
    {
        Wasapi.IMMDevice? endpoint = null;
        try
        {
            Wasapi.Check(enumerator.GetDefaultAudioEndpoint(0, 0, out endpoint));
            RequireRender(endpoint);
            return ReadEndpointId(endpoint);
        }
        finally { Release(endpoint); }
    }
    private static string ReadEndpointId(Wasapi.IMMDevice endpoint)
    {
        var id = IntPtr.Zero;
        try
        {
            Wasapi.Check(endpoint.GetId(out id));
            if (id == IntPtr.Zero) throw new InvalidDataException("The render endpoint has no identifier.");
            // Endpoint ids are OS-owned, but bounded before creating a managed string.
            var length = 0;
            while (length <= 1024 && Marshal.ReadInt16(id, length * 2) != 0) length++;
            if (length == 0 || length > 1024) throw new InvalidDataException("The render endpoint id exceeds its bound.");
            return Marshal.PtrToStringUni(id, length)!;
        }
        finally { if (id != IntPtr.Zero) Marshal.FreeCoTaskMem(id); }
    }
    private static void RequireRender(Wasapi.IMMDevice endpoint)
    {
        Wasapi.Check(endpoint.GetState(out var state));
        Wasapi.Check(((Wasapi.IMMEndpoint)endpoint).GetDataFlow(out var flow));
        if (state != 1 || flow != 0) throw new InvalidOperationException("Only an active render endpoint is admitted.");
    }
    private static void Release(object? value)
    {
        if (OperatingSystem.IsWindows() && value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
