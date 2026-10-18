using System;
using System.Linq;

namespace TopiaForge.Mods.Multiplayer.Generators.Tests
{
    /// <summary>
    /// What a predicted handler may read and call. It works from its request, its state and its own data, and it may
    /// call this mod's own methods that keep to the same rules; only what the generator cannot prove stays inside the
    /// handler is refused.
    /// </summary>
    internal static partial class Program
    {
        private const string PerPlayerStateSource = """
            using TopiaForge.Mods;

            [MultiplayerContract(Id = "sample.per-player")]
            public partial class Contract
            {
                internal const int MaximumPlayers = 16;

                [ReplicatedState("counts")]
                private ReplicatedState<CountsState> counts = new ReplicatedState<CountsState>(new CountsState());

                [MultiplayerCommand("increment", Prediction = PredictionMode.Owner, MaximumPerSecond = 10, MaximumPayloadBytes = 64)]
                private OperationResult<CountResult> Increment(MultiplayerCommandContext command, CountRequest request)
                {
                    var player = command.SenderId.Value;
                    var updated = counts.Update(current => WithPress(current, player));
                    if (!updated.TryGetValue(out var state))
                    {
                        return OperationResult<CountResult>.Failure(updated.ErrorCode, updated.ErrorMessage);
                    }

                    return OperationResult<CountResult>.Success(new CountResult { Count = CountOf(state, player) });
                }

                private static OperationResult<CountsState> WithPress(CountsState current, string player)
                {
                    var players = current.Players;
                    for (var index = 0; index < players.Length; index++)
                    {
                        if (players[index].Player != player)
                        {
                            continue;
                        }

                        var copy = new PlayerCount[players.Length];
                        for (var other = 0; other < players.Length; other++)
                        {
                            copy[other] = players[other];
                        }

                        copy[index] = new PlayerCount { Player = player, Count = players[index].Count + 1 };
                        return OperationResult<CountsState>.Success(new CountsState { Players = copy });
                    }

                    if (players.Length >= MaximumPlayers)
                    {
                        return OperationResult<CountsState>.Failure(ModErrorCode.Conflict, "The list is full.");
                    }

                    var grown = new PlayerCount[players.Length + 1];
                    for (var other = 0; other < players.Length; other++)
                    {
                        grown[other] = players[other];
                    }

                    grown[players.Length] = new PlayerCount { Player = player, Count = 1 };
                    return OperationResult<CountsState>.Success(new CountsState { Players = grown });
                }

                private static int CountOf(CountsState state, string player)
                {
                    foreach (var entry in state.Players)
                    {
                        if (entry.Player == player)
                        {
                            return entry.Count;
                        }
                    }

                    return 0;
                }
            }

            public sealed class CountsState
            {
                [NetworkBound(16)]
                public PlayerCount[] Players { get; set; } = new PlayerCount[0];
            }

            public sealed class PlayerCount
            {
                [NetworkBound(128)]
                public string Player { get; set; } = string.Empty;

                public int Count { get; set; }
            }

            public sealed class CountRequest
            {
            }

            public sealed class CountResult
            {
                public int Count { get; set; }
            }
            """;

        private static void RunPredictionPrecisionGeneratorTests()
        {
            AllowsPredictedReadsOfPayloadArrays();
            AllowsPredictedPureHelpers();
            AllowsPredictedLocalFunctions();
            AllowsPredictedRecursiveHelpers();
            AllowsPredictedArrayCopies();
            ReportsPredictedHelperFieldMutation();
            ReportsPredictedHelperFieldRead();
            ReportsPredictedHelperContextAccess();
            ReportsPredictedVirtualHelper();
            ReportsPredictedArrayCopyIntoAField();
            ReportsPredictedExternalArrayMember();
            PredictedSideEffectNamesTheWayOut();
        }

        private static void AllowsPredictedReadsOfPayloadArrays()
        {
            var result = Generate(PredictedCommandOver("""
                var players = request.Players;
                var total = 0;
                for (var index = 0; index < players.Length; index++)
                {
                    total += players[index].Count;
                }

                return OperationResult<Response>.Success(new Response { Value = total + players.Length });
                """));
            AssertNoPredictionDiagnostics(result, "reading a payload array, its length and its elements' members");
        }

        private static void AllowsPredictedPureHelpers()
        {
            var result = Generate(PerPlayerStateSource);
            AssertNoPredictionDiagnostics(result, "a per-player count kept by pure helpers of the mod's own");
        }

        private static void AllowsPredictedLocalFunctions()
        {
            var result = Generate(PredictedCommandOver("""
                return OperationResult<Response>.Success(new Response { Value = Twice(request.Players.Length) });

                static int Twice(int value) => value * 2;
                """));
            AssertNoPredictionDiagnostics(result, "a local function that keeps to the rules");
        }

        private static void AllowsPredictedRecursiveHelpers()
        {
            var result = Generate("""
                using TopiaForge.Mods;
                [MultiplayerContract(Id = "sample.recursive")]
                public partial class Contract
                {
                    [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                    private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request) =>
                        OperationResult<Response>.Success(new Response { Value = Sum(request.Value) + Even(request.Value) });

                    private static int Sum(int value) => value <= 0 ? 0 : value + Sum(value - 1);

                    private static int Even(int value) => value == 0 ? 1 : Odd(value - 1);

                    private static int Odd(int value) => value == 0 ? 0 : Even(value - 1);
                }
                public sealed class Request { public int Value { get; set; } }
                public sealed class Response { public int Value { get; set; } }
                """);
            AssertNoPredictionDiagnostics(result, "helpers that call themselves and each other, each checked once");
        }

        private static void AllowsPredictedArrayCopies()
        {
            var result = Generate(PredictedCommandOver("""
                var players = request.Players;
                var copy = (PlayerCount[])players.Clone();
                var again = new PlayerCount[copy.Length];
                copy.CopyTo(again, 0);
                System.Array.Copy(again, copy, again.Length);
                System.Array.Resize(ref copy, copy.Length + 1);
                copy[copy.Length - 1] = new PlayerCount { Player = "new", Count = 1 };
                var none = System.Array.Empty<PlayerCount>();
                return OperationResult<Response>.Success(new Response { Value = copy.Length + none.Length });
                """));
            AssertNoPredictionDiagnostics(result, "cloning, copying, resizing and emptying arrays the handler holds");
        }

        private static void ReportsPredictedHelperFieldMutation() => ExpectPredictionDiagnostic("""
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                private int executions;

                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    Count();
                    return OperationResult<Response>.Success(new Response());
                }

                private void Count() => executions++;
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """, "Contract.executions", "a helper's field write is reported inside the helper");

        private static void ReportsPredictedHelperFieldRead() => ExpectPredictionDiagnostic("""
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                private int limit = 3;

                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request) =>
                    OperationResult<Response>.Success(new Response { Value = Limit() });

                private int Limit() => limit;
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """, "Contract.limit", "a field the peers may hold differently is refused inside a helper too");

        private static void ReportsPredictedHelperContextAccess() => ExpectPredictionDiagnostic("""
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                public IModContext Context { get; set; } = null!;

                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    Toast();
                    return OperationResult<Response>.Success(new Response());
                }

                private void Toast() => Context.Ui.ShowToast("pressed");
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """, "ShowToast", "a helper's process-local service call is reported inside the helper");

        private static void ReportsPredictedVirtualHelper() => ExpectPredictionDiagnostic("""
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request) =>
                    OperationResult<Response>.Success(new Response { Value = Score(request.Value) });

                protected virtual int Score(int value) => value;
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """, "Contract.Score(int)", "a virtual helper is refused, since an override could run instead");

        private static void ReportsPredictedArrayCopyIntoAField() => ExpectPredictionDiagnostic("""
            using TopiaForge.Mods;
            [MultiplayerContract]
            public partial class Contract
            {
                private int[] cache = new int[4];

                [MultiplayerCommand("command", Prediction = PredictionMode.Owner, MaximumPayloadBytes = 256)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    System.Array.Copy(request.Values, cache, request.Values.Length);
                    return OperationResult<Response>.Success(new Response());
                }
            }
            public sealed class Request { [NetworkBound(4)] public int[] Values { get; set; } = new int[0]; }
            public sealed class Response { public int Value { get; set; } }
            """, "Contract.cache", "an array copy may not write into the mod's own field");

        private static void ReportsPredictedExternalArrayMember() => ExpectPredictionDiagnostic(PredictedCommandOver("""
            var copy = (PlayerCount[])request.Players.Clone();
            System.Array.Sort(copy);
            return OperationResult<Response>.Success(new Response { Value = copy.Length });
            """), "System.Array.Sort", "sorting may run the elements' own comparison, which the generator cannot read");

        private static void PredictedSideEffectNamesTheWayOut()
        {
            var result = Generate(PredictedCommandOver("""
                var copy = (PlayerCount[])request.Players.Clone();
                System.Array.Sort(copy);
                return OperationResult<Response>.Success(new Response { Value = copy.Length });
                """));
            var message = result.GeneratorDiagnostics.First(item => item.Id == "TFMP006").GetMessage();
            Assert(message.Contains("runs on the sender and again on the host", StringComparison.Ordinal) &&
                   message.Contains("[PresentationEvent]", StringComparison.Ordinal) &&
                   message.Contains("PredictionMode.None", StringComparison.Ordinal),
                "TFMP006 says why the work is refused and how to move it out of the prediction: " + message);
        }

        /// <summary>A predicted command whose request carries a bounded array of nested payloads.</summary>
        private static string PredictedCommandOver(string body) => $$"""
            using TopiaForge.Mods;
            [MultiplayerContract(Id = "sample.predicted-over")]
            public partial class Contract
            {
                [MultiplayerCommand("command", Prediction = PredictionMode.Owner, MaximumPayloadBytes = 4096)]
                private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request)
                {
                    {{body}}
                }
            }

            public sealed class Request
            {
                [NetworkBound(8)]
                public PlayerCount[] Players { get; set; } = new PlayerCount[0];
            }

            public sealed class PlayerCount
            {
                [NetworkBound(32)]
                public string Player { get; set; } = string.Empty;

                public int Count { get; set; }
            }

            public sealed class Response { public int Value { get; set; } }
            """;

        private static void AssertNoPredictionDiagnostics(GenerationResult result, string what)
        {
            var found = result.GeneratorDiagnostics.Where(item => item.Id == "TFMP005" || item.Id == "TFMP006").ToArray();
            Assert(found.Length == 0, "a predicted handler may do this (" + what + "): " +
                string.Join("; ", found.Select(item => item.Id + " " + item.GetMessage())));
            AssertNoErrors(result);
        }

        /// <summary>Expects TFMP006 naming <paramref name="symbol"/>, where the refused work is, not merely the call to it.</summary>
        private static void ExpectPredictionDiagnostic(string source, string symbol, string what)
        {
            var found = Generate(source).GeneratorDiagnostics.Where(item => item.Id == "TFMP006").ToArray();
            Assert(found.Any(item => item.GetMessage().Contains(symbol, StringComparison.Ordinal)),
                what + ": expected TFMP006 naming '" + symbol + "', received " +
                string.Join("; ", found.Select(item => item.GetMessage())));
        }
    }
}
