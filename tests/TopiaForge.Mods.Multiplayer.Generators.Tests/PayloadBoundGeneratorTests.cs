using System.Linq;

namespace TopiaForge.Mods.Multiplayer.Generators.Tests
{
    /// <summary>
    /// TFMP004 speaks only when a bound is missing. A <c>[NetworkBound]</c> the compiler cannot resolve, usually for want
    /// of <c>using TopiaForge.Mods;</c>, is the compiler's error to report, and a second error claiming no bound was
    /// written would send the author looking for the wrong fix.
    /// </summary>
    internal static partial class Program
    {
        private static void RunPayloadBoundGeneratorTests()
        {
            UnresolvedBoundReportsOnlyTheCompilersError();
            MissingBoundBesideAnotherUnresolvedAttributeStillReports();
        }

        private static void UnresolvedBoundReportsOnlyTheCompilersError()
        {
            var result = Generate("""
                [TopiaForge.Mods.MultiplayerContract(Id = "sample.unresolved-bound")]
                public partial class Contract
                {
                    [TopiaForge.Mods.ReplicatedState("state")]
                    private TopiaForge.Mods.ReplicatedState<State> state = new TopiaForge.Mods.ReplicatedState<State>(new State());
                }

                public sealed class State
                {
                    [NetworkBound(16)]
                    public string Name { get; set; } = string.Empty;
                }
                """);
            Assert(result.GeneratorDiagnostics.All(item => item.Id != "TFMP004"),
                "a [NetworkBound] the compiler cannot resolve is the compiler's error to report, not a missing bound: " +
                string.Join(", ", result.GeneratorDiagnostics.Select(item => item.Id + " " + item.GetMessage())));
            Assert(result.Compilation.GetDiagnostics().Any(item => item.Id == "CS0246"),
                "the compiler still says the attribute is not found");
        }

        private static void MissingBoundBesideAnotherUnresolvedAttributeStillReports()
        {
            var result = Generate("""
                [TopiaForge.Mods.MultiplayerContract(Id = "sample.missing-bound")]
                public partial class Contract
                {
                    [TopiaForge.Mods.ReplicatedState("state")]
                    private TopiaForge.Mods.ReplicatedState<State> state = new TopiaForge.Mods.ReplicatedState<State>(new State());
                }

                public sealed class State
                {
                    [Display("Name")]
                    public string Name { get; set; } = string.Empty;
                }
                """);
            Assert(result.GeneratorDiagnostics.Any(item => item.Id == "TFMP004"),
                "a member with no bound still reports TFMP004 when some other attribute fails to resolve: " +
                string.Join(", ", result.GeneratorDiagnostics.Select(item => item.Id + " " + item.GetMessage())));
        }
    }
}
