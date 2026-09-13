using System;
using System.Threading.Tasks;

// Offline contract checks for the native observer: wire vocabulary, bounded operation gating, per-cycle
// recipes, fact shapes and step postconditions. Synthetic facts are not native game evidence.
internal static class Program
{
    public static async Task<int> Main()
    {
        await WireContractTests.Run();
        ScenarioStepTests.Run();
        OperationGateTests.Run();
        FactShapeTests.Run();
        PostconditionTests.Run();
        Console.WriteLine("Sandbox native protocol/progress contract checks: " + Harness.Checks + " passed. Synthetic facts are not native game evidence.");
        return 0;
    }
}
