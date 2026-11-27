using System.Diagnostics;
using System.Text;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>What each fuzz loop ran and how long it took, reported once at the end of the harness.</summary>
internal static class FuzzLedger
{
    private static readonly List<(string Target, long Inputs, double Seconds)> Runs = new();

    /// <summary>Starts timing a fuzz target; dispose the result once its loop is done.</summary>
    internal static FuzzRun Start(string target) => new(target);

    internal static void Record(string target, long inputs, double seconds) => Runs.Add((target, inputs, seconds));

    /// <summary>Prints one line: each target's inputs and time, its runs added together where a seed is repeated.</summary>
    internal static void Print()
    {
        if (Runs.Count == 0) return;
        var targets = Runs.GroupBy(run => run.Target).Select(group => group.Key + " " + group.Sum(run => run.Inputs) + " in " +
                                                                      group.Sum(run => run.Seconds).ToString("0.00") + " s");
        Console.WriteLine("Fuzzing at " + HarnessOptions.FuzzScale + "x: " + Runs.Sum(run => run.Inputs) + " inputs in " +
                          Runs.Sum(run => run.Seconds).ToString("0.0") + " s (" + string.Join(", ", targets) + ").");
    }
}

/// <summary>One fuzz target's run: count its inputs, then dispose it to record them with the time taken.</summary>
internal sealed class FuzzRun : IDisposable
{
    private readonly string target;
    private readonly Stopwatch watch = Stopwatch.StartNew();

    internal FuzzRun(string target)
    {
        this.target = target;
    }

    internal long Inputs { get; set; }

    public void Dispose() => FuzzLedger.Record(target, Inputs, watch.Elapsed.TotalSeconds);
}

/// <summary>
/// Seeded mutations for the fuzzers. A seed makes the same inputs every run, so a failure repeats exactly, and the
/// iteration counts grow twentyfold under <c>--fuzz</c>.
/// </summary>
internal sealed class FuzzMutator
{
    private static readonly byte[] Interesting =
    {
        0x00, 0x01, 0x02, 0x05, 0x0A, 0x20, 0x22, 0x2C, 0x2D, 0x2F, 0x30, 0x3A, 0x3F, 0x40, 0x5C, 0x5D, 0x7B, 0x7D, 0x7F, 0x80,
        0x81, 0xBF, 0xC0, 0xE0, 0xE6, 0xE8, 0xED, 0xF0, 0xFE, 0xFF
    };

    internal FuzzMutator(int seed)
    {
        Random = new Random(seed);
    }

    internal Random Random { get; }

    /// <summary>Scales a loop's iteration count by the harness's fuzz factor.</summary>
    internal static int Iterations(int baseline) => baseline * HarnessOptions.FuzzScale;

    internal T Pick<T>(IReadOnlyList<T> items) => items[Random.Next(items.Count)];

    internal bool Chance(int percent) => Random.Next(100) < percent;

    internal byte[] Bytes(int count)
    {
        var bytes = new byte[count];
        Random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Applies one to three byte-level mutations, then trims to <paramref name="maximumLength"/>.</summary>
    internal byte[] Mutate(byte[] input, int maximumLength)
    {
        var bytes = new List<byte>(input);
        for (var round = 1 + Random.Next(3); round > 0; round--) MutateOnce(bytes);
        if (bytes.Count > maximumLength) bytes.RemoveRange(maximumLength, bytes.Count - maximumLength);
        return bytes.ToArray();
    }

    /// <summary>Joins the front of one input to the back of another, as a confused or hostile sender might.</summary>
    internal byte[] Splice(byte[] first, byte[] second, int maximumLength)
    {
        var cut = first.Length == 0 ? 0 : Random.Next(first.Length + 1);
        var from = second.Length == 0 ? 0 : Random.Next(second.Length + 1);
        var spliced = first.Take(cut).Concat(second.Skip(from)).ToArray();
        return spliced.Length > maximumLength ? spliced.Take(maximumLength).ToArray() : spliced;
    }

    /// <summary>Applies one to three character-level mutations drawn from <paramref name="alphabet"/>.</summary>
    internal string MutateText(string input, string alphabet, int maximumLength)
    {
        var text = new StringBuilder(input);
        for (var round = 1 + Random.Next(3); round > 0; round--)
        {
            var position = text.Length == 0 ? 0 : Random.Next(text.Length + 1);
            switch (Random.Next(6))
            {
                case 0 when text.Length > 0:
                    text.Remove(Math.Min(position, text.Length - 1), 1);
                    break;
                case 1 when text.Length > 0:
                    text[Math.Min(position, text.Length - 1)] = alphabet[Random.Next(alphabet.Length)];
                    break;
                case 2 when text.Length > 0:
                    var index = Math.Min(position, text.Length - 1);
                    text[index] = char.IsUpper(text[index]) ? char.ToLowerInvariant(text[index]) : char.ToUpperInvariant(text[index]);
                    break;
                case 3:
                    text.Length = Math.Min(text.Length, position);
                    break;
                case 4 when text.Length > 0:
                    var start = Random.Next(text.Length);
                    var length = Math.Min(text.Length - start, 1 + Random.Next(8));
                    text.Insert(position, text.ToString(start, length));
                    break;
                default:
                    text.Insert(position, alphabet[Random.Next(alphabet.Length)]);
                    break;
            }
        }

        return text.Length > maximumLength ? text.ToString(0, maximumLength) : text.ToString();
    }

    /// <summary>Writes <paramref name="value"/> as a varint, sometimes padded with needless continuation bytes.</summary>
    internal byte[] Varint(ulong value, bool minimal)
    {
        var bytes = new List<byte>();
        do
        {
            var next = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value != 0 ? (byte)(next | 0x80) : next);
        }
        while (value != 0);
        if (!minimal)
        {
            bytes[^1] |= 0x80;
            for (var extra = Random.Next(1, 4); extra > 1; extra--) bytes.Add(0x80);
            bytes.Add((byte)Random.Next(0, 128));
        }

        return bytes.ToArray();
    }

    private void MutateOnce(List<byte> bytes)
    {
        var position = bytes.Count == 0 ? 0 : Random.Next(bytes.Count);
        switch (Random.Next(11))
        {
            case 0 when bytes.Count > 0:
                for (var flips = 1 + Random.Next(4); flips > 0; flips--) bytes[Random.Next(bytes.Count)] ^= (byte)(1 << Random.Next(8));
                break;
            case 1 when bytes.Count > 0:
                bytes[position] = Interesting[Random.Next(Interesting.Length)];
                break;
            case 2 when bytes.Count > 0:
                bytes[position] = (byte)Random.Next(256);
                break;
            case 3:
                bytes.RemoveRange(position, bytes.Count - position);
                break;
            case 4 when bytes.Count > 0:
                bytes.RemoveRange(position, Math.Min(bytes.Count - position, 1 + Random.Next(16)));
                break;
            case 5:
                bytes.InsertRange(position, Bytes(1 + Random.Next(16)));
                break;
            case 6:
                bytes.Insert(position, Interesting[Random.Next(Interesting.Length)]);
                break;
            case 7 when bytes.Count > 0:
                var start = Random.Next(bytes.Count);
                var length = Math.Min(bytes.Count - start, 1 + Random.Next(32));
                bytes.InsertRange(position, bytes.GetRange(start, length));
                break;
            case 8:
                bytes.AddRange(Bytes(1 + Random.Next(64)));
                break;
            case 9 when bytes.Count > 1:
                var other = Random.Next(bytes.Count);
                (bytes[position], bytes[other]) = (bytes[other], bytes[position]);
                break;
            default:
                if (bytes.Count > 0) bytes[position] = (byte)(bytes[position] + (Random.Next(2) == 0 ? 1 : -1));
                break;
        }
    }
}
