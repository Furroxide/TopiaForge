extern alias provider;

using System.Reflection;
using System.Reflection.Emit;
using provider::TopiaForge.Multiplayer.Voice;
using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The playback stream the audio thread reads: what the ring keeps and drops, why its reads cannot block or allocate,
/// and how the worker keeps it fed when the audio device's clock and the worker's drift apart.
/// </summary>
internal static class VoicePlaybackTests
{
    private const int Frame = 960;

    internal static void TheRingKeepsOrderAndCountsWhatItCannotPlay()
    {
        var stream = new VoicePlaybackStream(new ParticipantId("s"));
        var buffer = new float[Frame];
        stream.Read(buffer, 0, Frame);
        Assert(buffer.All(sample => sample == 0) && stream.Underruns == 0, "before anything is written, a read is silent and no underrun");

        // Frames go in and come out in order, across the ring's end many times.
        short next = 0;
        for (var round = 0; round < 40; round++)
        {
            var pcm = new short[Frame];
            for (var index = 0; index < Frame; index++) pcm[index] = next++;
            stream.Write(pcm);
            stream.Read(buffer, 0, Frame / 2);
            stream.Read(buffer, Frame / 2, Frame / 2);
            Assert(buffer.Select((sample, index) => (short)Math.Round(sample * 32_768) == pcm[index]).All(same => same), "frame " + round + " reads back intact");
        }

        Assert(stream.Underruns == 0 && stream.SamplesRead == 40 * Frame, "and nothing ran short");
        stream.Read(buffer, 0, 100);
        Assert(buffer.Take(100).All(sample => sample == 0) && stream.Underruns == 1 && stream.UnderrunSamples == 100,
            "a read of an empty ring is silence, counted as an underrun");

        // Too much waiting means the reader stalled: it skips to the newest 60 ms.
        for (var round = 0; round < 15; round++) stream.Write(Ramp(round));
        stream.Read(buffer, 0, Frame);
        Assert(stream.SkippedSamples == (15 * Frame) - VoicePlaybackStream.SkipToSamples, "a backlog over 200 ms is skipped down to 60 ms");
        Assert((short)Math.Round(buffer[0] * 32_768) == Ramp(12)[0], "and the read starts with the newest 60 ms");

        // A flush drops what waits at the next read; a full ring drops the newest; an ended stream counts nothing.
        stream.Write(Ramp(1));
        stream.Flush();
        var skipped = stream.SkippedSamples;
        stream.Read(buffer, 0, Frame);
        Assert(stream.SkippedSamples > skipped && buffer.All(sample => sample == 0), "a flushed frame never plays");
        var full = new VoicePlaybackStream(new ParticipantId("f"));
        for (var round = 0; round < 20; round++) full.Write(Ramp(round));
        Assert(full.Buffered == VoicePlaybackStream.Capacity && full.OverflowSamples == (20 * Frame) - VoicePlaybackStream.Capacity,
            "a full ring keeps what it has and drops the newest");
        var underruns = stream.Underruns;
        stream.End();
        stream.Read(buffer, 0, Frame);
        Assert(stream.IsEnded && stream.Underruns == underruns && buffer.All(sample => sample == 0), "a speaker who left plays silence, uncounted");
    }

    internal static void ReadsNeverAllocateOrWait()
    {
        // Writes and reads on one thread, through wrap-arounds, underruns and skips: not one byte is allocated.
        var stream = new VoicePlaybackStream(new ParticipantId("s"));
        var pcm = Ramp(3);
        var buffer = new float[2_048];
        for (var index = 0; index < 100; index++)
        {
            stream.Write(pcm);
            stream.Read(buffer, 0, 1_024);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            if (index % 3 != 0) stream.Write(pcm);
            if (index % 50 == 0) for (var burst = 0; burst < 12; burst++) stream.Write(pcm);
            stream.Read(buffer, index % 7, 1_024 + (index % 5));
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(allocated == 0, "ten thousand reads allocate nothing (" + allocated + " bytes)");
        Assert(stream.Underruns > 0 && stream.SkippedSamples > 0, "and the loop went through underruns and skips");

        // Read's own code calls nothing that blocks or locks, and allocates nothing but the exceptions for bad arguments.
        var (calls, allocations) = Inspect(typeof(VoicePlaybackStream).GetMethod("Read", BindingFlags.Public | BindingFlags.Instance)!);
        var blocking = calls.Where(IsBlocking).Select(call => call.DeclaringType!.Name + "." + call.Name).ToList();
        Assert(blocking.Count == 0, "Read calls nothing that can block (" + string.Join(", ", blocking) + ")");
        Assert(allocations.Count == 0, "and allocates nothing (" + string.Join(", ", allocations) + ")");

        // A reader on its own thread keeps going while another thread holds the stream's monitor and never lets go.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (stream)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.IsBackground = true;
        holder.Start();
        held.Wait();
        var reads = 0;
        var reader = new Thread(() =>
        {
            var local = new float[480];
            for (var index = 0; index < 1_000; index++, reads++) stream.Read(local, 0, local.Length);
        });
        reader.IsBackground = true;
        reader.Start();
        var finished = reader.Join(10_000);
        release.Set();
        holder.Join();
        Assert(finished && reads == 1_000, "a thousand reads finish while the stream's monitor is held elsewhere");
    }

    internal static void PlaybackFollowsTheAudioDevicesClock()
    {
        // The audio device reads 1,024 samples at a time on its own clock, 0.5% fast or slow against the worker's.
        foreach (var factor in new[] { 1.005, 0.995 })
        {
            var stream = new VoicePlaybackStream(new ParticipantId("s"));
            var channel = new VoiceSpeakerChannel(new OpusVoiceCodec(), stream);
            var buffer = new float[1_024];
            var readInterval = 1_024 * 1_000_000.0 / 48_000 / factor;
            var nextRead = 0.0;
            var highest = 0;
            for (long now = 0; now < 120_000_000; now += 1_000)
            {
                if (now >= nextRead)
                {
                    stream.Read(buffer, 0, buffer.Length);
                    nextRead += readInterval;
                }

                if (now % 20_000 != 0) continue;
                channel.Slot(now);
                highest = Math.Max(highest, stream.Buffered);
            }

            var label = factor > 1 ? "a fast device" : "a slow device";
            Assert(stream.Underruns == 0 && stream.SkippedSamples == 0, label + " never runs dry and never skips (" + stream.Underruns + ", " + stream.SkippedSamples + ")");
            Assert(highest <= 6_000, label + " never waits more than 125 ms of audio (" + highest + " samples)");
            Assert(factor > 1 ? channel.Extras >= 20 && channel.Skips == 0 : channel.Skips >= 20 && channel.Extras == 0,
                label + " is met with " + (factor > 1 ? "extra pulls" : "skipped pulls") + " (" + channel.Extras + " extra, " + channel.Skips + " skipped)");
        }
    }

    internal static void ReadsSurviveAWriterThatFlushesBetweenThem()
    {
        // Four streams, each with a reader on its own thread and a writer that writes and flushes as fast as it can, so
        // writes and flushes land between the reader's loads of the ring's positions. Every read completes, and every
        // sample it returns is one the writer wrote, or silence. A reader that loaded the end before the flush once saw a
        // flush past the end and failed on a negative length within a few milliseconds.
        const int Pairs = 4;
        const int Reads = 200_000;
        var failures = new Exception?[Pairs];
        var strays = new long[Pairs];
        var done = 0;
        var threads = new List<Thread>();
        for (var pair = 0; pair < Pairs; pair++)
        {
            var index = pair;
            var stream = new VoicePlaybackStream(new ParticipantId("s" + pair));
            var pcm = Ramp(0);
            var reader = new Thread(() =>
            {
                var buffer = new float[480];
                try
                {
                    for (var read = 0; read < Reads; read++)
                    {
                        stream.Read(buffer, 0, buffer.Length);
                        foreach (var sample in buffer)
                        {
                            if (sample < 0 || sample > Frame / 32_768f) strays[index]++;
                        }
                    }
                }
                catch (Exception exception)
                {
                    failures[index] = exception;
                }
                finally
                {
                    Interlocked.Increment(ref done);
                }
            });
            var writer = new Thread(() =>
            {
                while (Volatile.Read(ref done) < Pairs)
                {
                    stream.Write(pcm);
                    stream.Flush();
                }
            });
            reader.IsBackground = true;
            writer.IsBackground = true;
            threads.Add(reader);
            threads.Add(writer);
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
        var failure = failures.FirstOrDefault(each => each != null);
        Assert(failure == null, "800,000 reads beside a flushing writer never fail (" + failure?.GetType().Name + ": " + failure?.Message + ")");
        Assert(strays.Sum() == 0, "and return only what the writer wrote, or silence (" + strays.Sum() + " stray samples)");
    }

    private static short[] Ramp(int seed)
    {
        var pcm = new short[Frame];
        for (var index = 0; index < Frame; index++) pcm[index] = (short)((seed * 1_000) + index + 1);
        return pcm;
    }

    private static bool IsBlocking(MethodBase call)
    {
        var type = call.DeclaringType!;
        if (type == typeof(Monitor) || type == typeof(Thread) || type == typeof(SpinWait) || type == typeof(SpinLock)) return true;
        if (typeof(WaitHandle).IsAssignableFrom(type) || type == typeof(SemaphoreSlim) || type == typeof(ManualResetEventSlim)) return true;
        return type.Namespace == "System.Threading.Tasks" || type == typeof(ReaderWriterLockSlim) || type == typeof(Mutex);
    }

    /// <summary>Walks a method's IL: every method it calls, and every allocation other than an exception's.</summary>
    private static (List<MethodBase> Calls, List<string> Allocations) Inspect(MethodInfo method)
    {
        var single = new OpCode[0x100];
        var prefixed = new OpCode[0x100];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var code = (OpCode)field.GetValue(null)!;
            var value = (ushort)code.Value;
            if (value < 0x100) single[value] = code;
            else if ((value & 0xFF00) == 0xFE00) prefixed[value & 0xFF] = code;
        }

        var il = method.GetMethodBody()!.GetILAsByteArray()!;
        var calls = new List<MethodBase>();
        var allocations = new List<string>();
        for (var position = 0; position < il.Length;)
        {
            var code = il[position] == 0xFE ? prefixed[il[position + 1]] : single[il[position]];
            position += il[position] == 0xFE ? 2 : 1;
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
                _ => 4
            };
            if (code == OpCodes.Call || code == OpCodes.Callvirt) calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, position))!);
            if (code == OpCodes.Newobj)
            {
                var constructor = method.Module.ResolveMethod(BitConverter.ToInt32(il, position))!;
                if (!typeof(Exception).IsAssignableFrom(constructor.DeclaringType)) allocations.Add("new " + constructor.DeclaringType!.Name);
            }

            if (code == OpCodes.Newarr || code == OpCodes.Box) allocations.Add(code.Name!);
            position += size;
        }

        return (calls, allocations);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
