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
            UnresolvedBoundMasksNothingOnItsNeighbours();
            ABoundOfAnotherTypeIsNoBound();
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

        private static void UnresolvedBoundMasksNothingOnItsNeighbours()
        {
            var result = Generate("""
                [TopiaForge.Mods.MultiplayerContract(Id = "sample.neighbour-bound")]
                public partial class Contract
                {
                    [TopiaForge.Mods.ReplicatedState("state")]
                    private TopiaForge.Mods.ReplicatedState<State> state = new TopiaForge.Mods.ReplicatedState<State>(new State());
                }

                public sealed class State
                {
                    [NetworkBound(16)]
                    public string Name { get; set; } = string.Empty;

                    public string Title { get; set; } = string.Empty;
                }
                """);
            var missing = result.GeneratorDiagnostics.Where(item => item.Id == "TFMP004").ToArray();
            Assert(missing.Length == 1 && missing[0].GetMessage().Contains("'Title'", System.StringComparison.Ordinal),
                "an unresolved bound is the compiler's to report for its own member only; a neighbour with no bound still " +
                "reports TFMP004: " + string.Join(", ", result.GeneratorDiagnostics.Select(item => item.Id + " " + item.GetMessage())));
        }

        private static void ABoundOfAnotherTypeIsNoBound()
        {
            var result = Generate("""
                using Other;

                namespace Other
                {
                    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Field)]
                    public sealed class NetworkBoundAttribute : System.Attribute
                    {
                        public NetworkBoundAttribute(int maximum)
                        {
                        }
                    }
                }

                [TopiaForge.Mods.MultiplayerContract(Id = "sample.other-bound")]
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
            Assert(result.GeneratorDiagnostics.Any(item => item.Id == "TFMP004"),
                "a [NetworkBound] that resolves to another type is no bound, and the compiler sees nothing wrong, so " +
                "TFMP004 must say so: " +
                string.Join(", ", result.GeneratorDiagnostics.Select(item => item.Id + " " + item.GetMessage())));
            Assert(!result.Compilation.GetDiagnostics().Any(item =>
                    item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error &&
                    item.Id.StartsWith("CS", System.StringComparison.Ordinal)),
                "the compiler accepts the other type's attribute");
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
