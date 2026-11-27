using System.Text.Json;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.CounterSample;
using TopiaForge.Multiplayer.DroneSample;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Golden check that generator changes leave existing contracts byte-identical: each sample's generated descriptor
/// must still match the contract lock checked in beside it.
/// </summary>
internal static class GeneratedLockStabilityTests
{
    internal static void SampleLocksMatchGeneratedDescriptors()
    {
        var samples = Path.Combine(FindRepositoryRoot(), "samples", "multiplayer");
        AssertMatchesLock(
            new CounterSampleMod(),
            Path.Combine(samples, "TopiaForge.Multiplayer.CounterSample", "topiaforge.multiplayer.lock.json"));
        AssertMatchesLock(
            new DroneSampleMod(),
            Path.Combine(samples, "TopiaForge.Multiplayer.DroneSample", "topiaforge.multiplayer.lock.json"));
    }

    private static void AssertMatchesLock(IGeneratedMultiplayerContract contract, string lockPath)
    {
        var descriptor = contract.MultiplayerContractDescriptor;
        using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
        var entry = document.RootElement.GetProperty("contracts").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == descriptor.Id);
        Assert(entry.GetProperty("schemaSha256").GetString() == descriptor.SchemaSha256 &&
               entry.GetProperty("wireFormatRevision").GetInt32() == descriptor.WireFormatRevision,
            descriptor.Id + " must keep the schema digest recorded in its checked-in lock");
        Assert(Ids(entry, "stateIds").SequenceEqual(descriptor.StateIds) &&
               Ids(entry, "commandIds").SequenceEqual(descriptor.CommandIds) &&
               Ids(entry, "objectTypeIds").SequenceEqual(descriptor.ObjectTypeIds) &&
               Ids(entry, "eventIds").SequenceEqual(descriptor.EventIds),
            descriptor.Id + " must keep the wire ids recorded in its checked-in lock");
    }

    private static IEnumerable<string> Ids(JsonElement entry, string name) =>
        entry.GetProperty(name).EnumerateArray().Select(item => item.GetString()!);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TopiaForge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate TopiaForge.slnx.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Generated lock stability failed: " + message);
    }
}
