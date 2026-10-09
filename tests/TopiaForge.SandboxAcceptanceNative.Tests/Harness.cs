using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Minimal dependency-free harness shared by every offline contract check in this project.
internal static class Harness
{
    internal static readonly string Challenge = new string('a', 64);
    internal static int Checks { get; private set; }

    internal static void Check(bool condition, string label)
    {
        Checks++;
        if (!condition) throw new Exception("Check failed: " + label);
    }

    internal static void Reject(Action action, string label)
    {
        try { action(); }
        catch { Checks++; return; }
        throw new Exception("Not rejected: " + label);
    }

    internal static void Equal(string actual, string expected, string label)
    {
        Checks++;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new Exception("Check failed: " + label + "\n  expected: " + expected + "\n  actual:   " + actual);
    }

    internal static void Near(double actual, double expected, double tolerance, string label)
    {
        Checks++;
        if (Math.Abs(actual - expected) > tolerance) throw new Exception("Check failed: " + label + " (" + actual + " vs " + expected + ")");
    }

    /// <summary>Deep-copies the plain dictionary/array/primitive graph the observer emits, so a step mutates only its own snapshot.</summary>
    internal static Dictionary<string, object?> Clone(Dictionary<string, object?> value)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in value) copy[pair.Key] = CloneValue(pair.Value);
        return copy;
    }

    private static object? CloneValue(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string _: case bool _: case int _: case long _: case float _: case double _: return value;
            case Dictionary<string, object?> map: return Clone(map);
            case float[] floats: return (float[])floats.Clone();
            case int[] ints: return (int[])ints.Clone();
            case IEnumerable sequence: return sequence.Cast<object?>().Select(CloneValue).ToList();
            default: throw new InvalidDataException("Unsupported synthetic fact type: " + value.GetType().Name);
        }
    }

    /// <summary>Locates a repository file from the test binary directory; the manifest is the broker's reviewed input.</summary>
    internal static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Repository file not found: " + relative);
    }
}
