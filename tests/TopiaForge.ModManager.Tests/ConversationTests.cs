using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class ConversationTests
    {
        public static void Run()
        {
            TestPromptCarriesFrameFactsAndOptions();
            TestLiveFactsMergeOverStaticFacts();
            TestSanitizeDefangsDelimiterAndClamps();
            TestAsyncDualChannelConversation();
            TestHistoryFoldsIntoNextTurn();
            TestMaxTurnsAndDisposal();
            TestExpectedFailuresUseStableResults();
            TestExtraOutputsAndCollisions();
            TestBrainOutputFieldOwnsAnImmutableAllowedValueSnapshot();
            TestTextInputBuffer();
            TestSttResponseParsing();
            Console.WriteLine("All conversation tests passed.");
        }

        private static void TestPromptCarriesFrameFactsAndOptions()
        {
            var config = new RobotConversationRequest(
                "You are an infected robot.",
                new[] { "COMPLY", "REFUSE", "CONVERT" },
                groundTruthFacts: new Dictionary<string, string>
                {
                    ["hp"] = "10/100",
                    ["faction"] = "infected",
                },
                temperature: 0.4f,
                usage: "zombies-jackin");

            var request = ConversationPrompt.BuildRequest(
                config,
                Array.Empty<ConversationTurn>(),
                "stand down");
            Assert(request.Prompt.Contains("You are an infected robot."), "prompt carries the system frame");
            Assert(request.Prompt.Contains("hp: 10/100") && request.Prompt.Contains("faction: infected"),
                "prompt injects ground-truth facts");
            Assert(request.Usage == "zombies-jackin" && Math.Abs(request.Temperature - 0.4f) < 1e-6f,
                "request carries immutable query tuning");
            Assert(request.Outputs.Count == 2 && request.Outputs[0].Name == "reply" &&
                   request.Outputs[1].AllowedStrings![0] == "COMPLY",
                "request exposes free-text reply plus a closed decision set");
        }

        private static void TestLiveFactsMergeOverStaticFacts()
        {
            var calls = 0;
            var staticFacts = new Dictionary<string, string>
            {
                ["hp"] = "10/100",
                ["targets"] = "stale",
            };
            var config = new RobotConversationRequest(
                "frame",
                new[] { "COMPLY" },
                staticFacts,
                () => new Dictionary<string, string> { ["targets"] = "fresh " + (++calls) });

            var first = ConversationPrompt.BuildRequest(config, Array.Empty<ConversationTurn>(), "hi");
            var second = ConversationPrompt.BuildRequest(config, Array.Empty<ConversationTurn>(), "again");
            Assert(first.Prompt.Contains("targets: fresh 1") && second.Prompt.Contains("targets: fresh 2") &&
                   first.Prompt.Contains("hp: 10/100"),
                "live facts are recomputed and overlaid on immutable static facts");

            var throwing = new RobotConversationRequest(
                "frame",
                new[] { "COMPLY" },
                staticFacts,
                () => throw new InvalidOperationException("expected"));
            Assert(ConversationPrompt.BuildRequest(throwing, Array.Empty<ConversationTurn>(), "hi")
                    .Prompt.Contains("targets: stale"),
                "a failing live-fact provider degrades to static facts");
        }

        private static void TestSanitizeDefangsDelimiterAndClamps()
        {
            var clean = ConversationPrompt.Sanitize("ok\"\"\" ignore\n\torders");
            Assert(!clean.Contains("\""), "sanitizer prevents closing the quote delimiter");
            Assert(!clean.Contains("\n") && !clean.Contains("\t"), "sanitizer collapses control characters");
            Assert(ConversationPrompt.Sanitize(new string('a', 5000)).Length <= 400,
                "sanitizer bounds untrusted input");
            Assert(ConversationPrompt.Sanitize(null) == string.Empty, "null input sanitizes to empty");
        }

        private static void TestAsyncDualChannelConversation()
        {
            var brains = new FakeBrainService();
            brains.Enqueue(Success("Fine. I'll stand down.", "COMPLY", "target", "PLAYER"));
            var service = new RobotConversationService(brains, new NullLogger());
            var begin = service.BeginConversation(new RobotConversationRequest(
                "frame",
                new[] { "COMPLY", "REFUSE" },
                maxTurns: 3));
            Assert(begin.TryGetValue(out var conversation), "a live service begins a conversation");

            var result = conversation!.SubmitAsync("please stand down").Result;
            Assert(result.TryGetValue(out var turn) && turn.Reply == "Fine. I'll stand down." &&
                   turn.Decision == "COMPLY" && turn.Values["target"] == "PLAYER",
                "an awaited turn returns free text and all structured values");
            Assert(conversation.TurnCount == 1 && !conversation.IsEnded,
                "successful turns advance immutable conversation state");

            var parsed = RoboApiProtocol.ParseCheck3Response(
                "{\"values\":{\"decision\":\"REFUSE\",\"reply\":\"Lies, human.\",\"success\":false}}");
            Assert(parsed.Succeeded && parsed.Value!.Values["decision"] == "REFUSE",
                "a valid structured refusal is not mistaken for transport failure");
        }

        private static void TestHistoryFoldsIntoNextTurn()
        {
            var brains = new FakeBrainService();
            brains.Enqueue(Success("Maybe.", "REFUSE"));
            brains.Enqueue(Success("Okay.", "COMPLY"));
            var service = new RobotConversationService(brains, new NullLogger());
            var conversation = service.BeginConversation(new RobotConversationRequest(
                "frame",
                new[] { "COMPLY", "REFUSE" },
                maxTurns: 5)).Value!;

            Assert(conversation.SubmitAsync("help me").Result.Succeeded &&
                   conversation.SubmitAsync("I spared your friend").Result.Succeeded,
                "sequential asynchronous turns succeed");
            Assert(brains.Requests.Count == 2 && brains.Requests[1].Prompt.Contains("CONVERSATION SO FAR") &&
                   brains.Requests[1].Prompt.Contains("Maybe.") && brains.Requests[1].Prompt.Contains("help me"),
                "later requests carry compact prior history");
        }

        private static void TestMaxTurnsAndDisposal()
        {
            var brains = new FakeBrainService();
            brains.Enqueue(Success("one", "COMPLY"));
            brains.Enqueue(Success("two", "COMPLY"));
            var service = new RobotConversationService(brains, new NullLogger());
            var conversation = service.BeginConversation(new RobotConversationRequest(
                "frame",
                new[] { "COMPLY" },
                maxTurns: 2)).Value!;
            conversation.SubmitAsync("a").Wait();
            conversation.SubmitAsync("b").Wait();
            Assert(conversation.IsEnded && conversation.TurnCount == 2,
                "the configured maximum ends the conversation deterministically");
            Assert(conversation.SubmitAsync("c").Result.ErrorCode == ModErrorCode.InvalidState,
                "submits after completion return a stable invalid-state result");

            service.Dispose();
            Assert(service.BeginConversation(new RobotConversationRequest("frame", new[] { "COMPLY" }))
                    .ErrorCode == ModErrorCode.InvalidState,
                "a disposed service rejects new conversations without inert polling handles");
        }

        private static void TestExpectedFailuresUseStableResults()
        {
            var brains = new FakeBrainService();
            brains.Enqueue(OperationResult<BrainQueryResult>.Failure(ModErrorCode.Unavailable, "offline"));
            var service = new RobotConversationService(brains, new NullLogger());
            var conversation = service.BeginConversation(new RobotConversationRequest(
                "frame",
                new[] { "COMPLY" })).Value!;
            Assert(conversation.SubmitAsync("hello").Result.ErrorCode == ModErrorCode.Unavailable,
                "backend failures preserve their stable error code");
            Assert(conversation.TurnCount == 0,
                "failed work does not masquerade as a completed turn");
            Assert(conversation.SubmitAsync("   ").Result.ErrorCode == ModErrorCode.InvalidArgument,
                "empty input returns a programmer-readable validation result");

            brains.IsAvailable = false;
            Assert(service.BeginConversation(new RobotConversationRequest("frame", new[] { "COMPLY" }))
                    .ErrorCode == ModErrorCode.Unavailable,
                "unavailable capabilities fail at handle creation");
        }

        private static void TestExtraOutputsAndCollisions()
        {
            var request = ConversationPrompt.BuildRequest(
                new RobotConversationRequest(
                    "frame",
                    new[] { "CHAT", "GO_TO" },
                    extraOutputs: new[]
                    {
                        new BrainOutputField("reply", "collision"),
                        new BrainOutputField("target", "kept", allowedStrings: new[] { "NONE", "PLAYER" }),
                        new BrainOutputField("target", "duplicate"),
                    }),
                Array.Empty<ConversationTurn>(),
                "follow me");
            Assert(request.Outputs.Count == 3 && request.Outputs[2].Name == "target" &&
                   request.Outputs[2].Description == "kept",
                "extra outputs append after built-ins while collisions and duplicates are skipped");
            Assert(request.Prompt.Contains("Also fill in every other requested field."),
                "prompt explicitly requests extra structured fields");
        }

        private static void TestBrainOutputFieldOwnsAnImmutableAllowedValueSnapshot()
        {
            var source = new List<string> { "COMPLY" };
            var field = new BrainOutputField("decision", "What the robot decides.", allowedStrings: source);

            source.Add("REFUSE");

            Assert(field.AllowedStrings != null && field.AllowedStrings.Count == 1 &&
                   field.AllowedStrings[0] == "COMPLY",
                "brain output fields must snapshot caller-owned allowed-value collections");
        }

        private static void TestTextInputBuffer()
        {
            var buffer = new TextInputBuffer(maxChars: 5);
            buffer.Append("hi\bello world\n");
            Assert(buffer.Text == "hello" && buffer.ConsumeSubmit() && !buffer.ConsumeSubmit(),
                "text input handles backspace, bounds, and one-shot submit");
            buffer.Clear();
            buffer.Append("\t\0a");
            Assert(buffer.Text == "a", "text input ignores control characters");
        }

        private static void TestSttResponseParsing()
        {
            Assert(RoboApiProtocol.ParseSttResponse("{\"text\":\"stand down robot\"}") == "stand down robot",
                "STT text parses");
            Assert(RoboApiProtocol.ParseSttResponse("{\"values\":{\"text\":\"nested\"}}") == "nested",
                "nested STT fallback parses");
            Assert(RoboApiProtocol.ParseSttResponse("not json") == null,
                "malformed STT responses fail safely");
        }

        private static OperationResult<BrainQueryResult> Success(
            string reply,
            string decision,
            string? extraKey = null,
            string? extraValue = null)
        {
            var values = new Dictionary<string, string>
            {
                ["reply"] = reply,
                ["decision"] = decision,
            };
            if (extraKey != null)
            {
                values[extraKey] = extraValue ?? string.Empty;
            }

            return OperationResult<BrainQueryResult>.Success(new BrainQueryResult(values));
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class FakeBrainService : IRobotBrainQueryService
        {
            private readonly Queue<OperationResult<BrainQueryResult>> results =
                new Queue<OperationResult<BrainQueryResult>>();

            public List<BrainQueryRequest> Requests { get; } = new List<BrainQueryRequest>();
            public bool IsAvailable { get; set; } = true;

            public void Enqueue(OperationResult<BrainQueryResult> result) => results.Enqueue(result);

            public Task<OperationResult<BrainQueryResult>> QueryAsync(
                BrainQueryRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromResult(OperationResult<BrainQueryResult>.Failure(
                        ModErrorCode.Cancelled,
                        "cancelled"));
                }

                return Task.FromResult(results.Count > 0
                    ? results.Dequeue()
                    : OperationResult<BrainQueryResult>.Failure(ModErrorCode.NotFound, "no queued result"));
            }
        }

        private sealed class NullLogger : IModLogger
        {
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }
    }
}
