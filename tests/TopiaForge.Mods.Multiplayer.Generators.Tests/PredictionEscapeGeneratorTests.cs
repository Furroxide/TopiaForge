namespace TopiaForge.Mods.Multiplayer.Generators.Tests
{
    /// <summary>
    /// Ways a predicted handler could reach work the generator never reads. Helpers are checked by the same rules, and a
    /// helper's parameters count as predicted data, so nothing that is not predicted data may be handed to one.
    /// </summary>
    internal static partial class Program
    {
        private static void RunPredictionEscapeGeneratorTests()
        {
            AllowsPredictedHelpersReachedThroughTheModInstance();
            AllowsPredictedStaticHelpersOfInitializedTypes();
            ReportsPredictedModInstanceHandedToAHelper();
            ReportsPredictedModInstanceKeptInALocal();
            ReportsPredictedExtensionOfTheModInstance();
            ReportsPredictedArrayCopyIntoAFieldThroughAHelper();
            ReportsPredictedTypeArgumentConstruction();
            ReportsPredictedHelperStaticConstructor();
            ReportsPredictedAsyncHelper();
            ReportsPredictedInterfacePropertyGetter();
            ReportsPredictedEventSubscription();
            ReportsPredictedStaticReplicatedState();
        }

        private static void AllowsPredictedHelpersReachedThroughTheModInstance()
        {
            var result = Generate(PredictedCommandWith("""
                [ReplicatedState("state")]
                private ReplicatedState<Response> state = new ReplicatedState<Response>(new Response());

                private int Twice(int value) => value * 2;

                private OperationResult<Response> Next(Response current) =>
                    OperationResult<Response>.Success(new Response { Value = Twice(current.Value) });
                """, """
                var twice = Twice(request.Value) + this.Twice(request.Value);
                var first = state.Update(this.Next);
                var second = this.state.Update(current => Next(current));
                """));
            AssertNoPredictionDiagnostics(result, "instance helpers, state and method groups reached through the mod");
        }

        private static void AllowsPredictedStaticHelpersOfInitializedTypes()
        {
            var result = Generate(PredictedCommandWith("""
                static Contract() { System.Console.WriteLine("loaded"); }

                private static int Twice(int value) => value * 2;
                """, """
                var total = Twice(request.Value) + Helpers.Sum(request.Value, 1);
                """) + """
                public static class Helpers { public static int Sum(int left, int right) => left + right; }
                """);
            AssertNoPredictionDiagnostics(
                result,
                "the contract's own static helpers, whose type its instance already initialised, and a type without a static constructor");
        }

        private static void ReportsPredictedModInstanceHandedToAHelper() => ExpectPredictionDiagnostic(PredictedCommandWith("""
            private static void Count(Contract mod) => mod.presses++;
            """, """
            Count(this);
            """), "uses 'this'", "the mod handed to a helper's parameter would open its fields to the helper");

        private static void ReportsPredictedModInstanceKeptInALocal() => ExpectPredictionDiagnostic(PredictedCommandWith("", """
            var self = this;
            self.presses++;
            """), "uses 'this'", "the mod kept in a local would pass as predicted data");

        private static void ReportsPredictedExtensionOfTheModInstance() => ExpectPredictionDiagnostic(PredictedCommandWith("", """
            this.Count();
            """) + """
            public static class ContractExtensions { public static void Count(this Contract mod) => mod.presses++; }
            """, "uses 'this'", "an extension method of the mod receives the mod as a parameter");

        private static void ReportsPredictedArrayCopyIntoAFieldThroughAHelper() => ExpectPredictionDiagnostic(PredictedCommandWith("""
            private static void Keep(Contract mod, int[] values) => System.Array.Copy(values, mod.cache, values.Length);
            """, """
            Keep(this, request.Values);
            """), "uses 'this'", "an array copy may not write into the mod's own field through a helper");

        private static void ReportsPredictedTypeArgumentConstruction() => ExpectPredictionDiagnostic(PredictedCommandWith("""
            private static T Make<T>() where T : new() => new T();
            """, """
            var made = Make<Recorder>();
            """) + """
            public sealed class Recorder { public Recorder() { System.IO.File.WriteAllText("recorded", "unsafe"); } }
            """, "uses 'new T()'", "a type argument's constructor is not the generator's to read");

        private static void ReportsPredictedHelperStaticConstructor()
        {
            ExpectPredictionDiagnostic(PredictedCommandWith("", """
                var sum = Audit.Sum(request.Value, 1);
                """) + """
                public static class Audit
                {
                    static Audit() { System.IO.File.WriteAllText("audit", "unsafe"); }

                    public static int Sum(int left, int right) => left + right;
                }
                """, "uses 'Audit.Audit()'", "a static helper's first call runs its type's static constructor");
            ExpectPredictionDiagnostic(PredictedCommandWith("", """
                var cells = new Cell[1];
                var one = cells[0].One();
                """) + """
                public struct Cell
                {
                    static Cell() { System.IO.File.WriteAllText("cell", "unsafe"); }

                    public int One() => 1;
                }
                """, "uses 'Cell.Cell()'", "a struct's first method call runs its static constructor");
        }

        private static void ReportsPredictedAsyncHelper() => ExpectPredictionDiagnostic(PredictedCommandWith("""
            private static async void Later() => await new Delay();
            """, """
            Later();
            """) + """
            public sealed class Delay { public DelayAwaiter GetAwaiter() => new DelayAwaiter(); }
            public sealed class DelayAwaiter : System.Runtime.CompilerServices.INotifyCompletion
            {
                public bool IsCompleted => false;
                public void OnCompleted(System.Action continuation) => System.IO.File.WriteAllText("later", "unsafe");
                public void GetResult() { }
            }
            """, "uses 'Contract.Later()'", "an await calls its awaiter's members, which the generator does not read");

        private static void ReportsPredictedInterfacePropertyGetter() => ExpectPredictionDiagnostic(PredictedCommandWith("", """
            IReading reading = new Reading();
            var value = reading.Value;
            """) + """
            public interface IReading { int Value { get; } }
            public sealed class Reading : IReading
            {
                public int Value { get { System.IO.File.WriteAllText("reading", "unsafe"); return 1; } }
            }
            """, "uses 'IReading.Value'", "an interface's property runs the implementation's getter, not an auto-property");

        private static void ReportsPredictedEventSubscription() => ExpectPredictionDiagnostic(PredictedCommandWith("", """
            Signals.Changed += () => { };
            """) + """
            public static class Signals { public static event System.Action? Changed; }
            """, "uses 'Signals.Changed'", "subscribing to an event changes state outside the prediction");

        private static void ReportsPredictedStaticReplicatedState() => ExpectPredictionDiagnostic(PredictedCommandWith("", """
            var updated = Holder.Shared.Update(current => OperationResult<Response>.Success(current));
            """) + """
            public static class Holder
            {
                public static readonly ReplicatedState<Response> Shared = new ReplicatedState<Response>(Start());

                private static Response Start()
                {
                    System.IO.File.WriteAllText("holder", "unsafe");
                    return new Response();
                }
            }
            """, "uses 'Holder.Shared'", "a static replicated state is never generated state, and reading it runs its type's initializer");

        /// <summary>A predicted command on a contract with a plain field, an array field and members of its own.</summary>
        private static string PredictedCommandWith(string members, string body) => $$"""
            using TopiaForge.Mods;
            [MultiplayerContract(Id = "sample.predicted-with")]
            public partial class Contract
            {
                internal int presses;
                internal int[] cache = new int[4];

                {{members}}

                [MultiplayerCommand("command", Prediction = PredictionMode.Owner, MaximumPayloadBytes = 256)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    {{body}}
                    return OperationResult<Response>.Success(new Response());
                }
            }

            public sealed class Request
            {
                public int Value { get; set; }

                [NetworkBound(4)]
                public int[] Values { get; set; } = new int[0];
            }

            public sealed class Response { public int Value { get; set; } }
            """;
    }
}
