using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Generators.Tests
{
    /// <summary>
    /// The generated <c>Submit{Handler}</c> method: a command whose answer reaches a callback, never a task to poll or
    /// wait on, and never once the binding is released or the caller has withdrawn. These run against a session that is
    /// not TopiaForge's engine; the engine's own delivery is covered with the multiplayer suites.
    /// </summary>
    internal static partial class Program
    {
        private const string AnsweredContractSource = """
            #nullable enable
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using TopiaForge.Mods;

            namespace Sample;

            [MultiplayerContract(Id = "sample.answered")]
            public partial class AnsweredContract
            {
                public List<string> Answers { get; } = new List<string>();

                [MultiplayerCommand("roll", MaximumPerSecond = 4, MaximumPayloadBytes = 64)]
                private OperationResult<RollResult> Roll(MultiplayerCommandContext command, RollRequest request) =>
                    OperationResult<RollResult>.Success(new RollResult { Value = 4 });

                public void Press(CancellationToken cancellationToken) =>
                    SubmitRoll(new RollRequest(), answer => Answers.Add(answer.Result.Succeeded ? "rolled" : answer.Result.ErrorCode.ToString()), cancellationToken);

                public void PressWithoutAnswer() => SubmitRoll(new RollRequest());
            }

            public sealed class RollRequest
            {
            }

            public sealed class RollResult
            {
                public int Value { get; set; }
            }
            """;

        private static void RunAnsweredSubmitGeneratorTests()
        {
            AnsweredSubmitAnswersAtOnceWhenTheSessionDoes();
            AnsweredSubmitWaitsForAnAnswerStillOnTheWay();
            AnsweredSubmitNeverAnswersAReleasedBinding();
            AnsweredSubmitNeverAnswersAWithdrawnSubmission();
            AnsweredSubmitWithoutAnAnswerStillSubmits();
            AnsweredSubmitNeedsABinding();
            AnsweredSubmitIsDiscoverableAndDocumented();
            ReportsAnsweredSubmitNameCollision();
        }

        private static void AnsweredSubmitAnswersAtOnceWhenTheSessionDoes()
        {
            var contract = BoundAnsweredContract(out var session, out _);
            Press(contract, CancellationToken.None);
            Assert(Answers(contract).SequenceEqual(new[] { "rolled" }) && session.SubmissionCount == 1,
                "a session that answers at once, as a host answers its own player, answers before the submit returns");
        }

        private static void AnsweredSubmitWaitsForAnAnswerStillOnTheWay()
        {
            var contract = BoundAnsweredContract(out var session, out _);
            session.HoldSubmissions = true;
            Press(contract, CancellationToken.None);
            Assert(Answers(contract).Count == 0, "nothing answers before the host does");
            session.ReleaseHeldSubmissions();
            Assert(Answers(contract).SequenceEqual(new[] { "rolled" }), "the answer runs once it arrives");
        }

        private static void AnsweredSubmitNeverAnswersAReleasedBinding()
        {
            var contract = BoundAnsweredContract(out var session, out var binding);
            session.HoldSubmissions = true;
            Press(contract, CancellationToken.None);
            binding.Dispose();
            session.ReleaseHeldSubmissions();
            Assert(Answers(contract).Count == 0, "an answer arriving after the binding was released never runs");

            var rebound = ((IGeneratedMultiplayerBinding)contract).BindMultiplayer(session);
            Assert(rebound.Succeeded, "the contract binds again");
            Press(contract, CancellationToken.None);
            session.ReleaseHeldSubmissions();
            Assert(Answers(contract).SequenceEqual(new[] { "rolled" }), "a new binding answers its own submissions");
            rebound.Value!.Dispose();
        }

        private static void AnsweredSubmitNeverAnswersAWithdrawnSubmission()
        {
            var contract = BoundAnsweredContract(out var session, out _);
            session.HoldSubmissions = true;
            using var scope = new CancellationTokenSource();
            Press(contract, scope.Token);
            scope.Cancel();
            session.ReleaseHeldSubmissions();
            Assert(Answers(contract).Count == 0, "a submission withdrawn by its caller's token is never answered");

            var before = session.SubmissionCount;
            Press(contract, scope.Token);
            Assert(session.SubmissionCount == before, "a token already cancelled, such as a stopped scope's, submits nothing");
        }

        private static void AnsweredSubmitWithoutAnAnswerStillSubmits()
        {
            var contract = BoundAnsweredContract(out var session, out _);
            contract.GetType().GetMethod("PressWithoutAnswer")!.Invoke(contract, null);
            Assert(session.SubmissionCount == 1, "a submit without an answer callback still submits");
        }

        private static void AnsweredSubmitNeedsABinding()
        {
            var contract = Activator.CreateInstance(LoadGenerated(AnsweredContractSource, "Sample.AnsweredContract"))!;
            try
            {
                Press(contract, CancellationToken.None);
                throw new InvalidOperationException("An unbound contract must not submit.");
            }
            catch (TargetInvocationException exception) when (
                exception.InnerException is InvalidOperationException inner &&
                inner.Message.Contains("BindMultiplayer must succeed", StringComparison.Ordinal))
            {
            }
        }

        private static void AnsweredSubmitIsDiscoverableAndDocumented()
        {
            var contractType = LoadGenerated(AnsweredContractSource, "Sample.AnsweredContract");
            var answered = contractType.GetMethod("SubmitRoll")!;
            Assert(answered.ReturnType == typeof(void) &&
                   answered.GetParameters().Select(parameter => parameter.Name).SequenceEqual(new[] { "request", "answered", "cancellationToken" }) &&
                   answered.GetCustomAttribute<EditorBrowsableAttribute>() == null,
                "the answered submit is the visible author proxy: request, answer callback, then token");

            var generated = SingleGeneratedSource(Generate(AnsweredContractSource)).SourceText.ToString()
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert(generated.Contains("/// Submits one <c>roll</c> command, and hands the host's answer to <paramref name=\"answered\"/>.", StringComparison.Ordinal),
                "IntelliSense says what the answered submit does");
            Assert(generated.Contains("It never runs once the mod stops", StringComparison.Ordinal),
                "IntelliSense states the answer's lifetime rule");
            Assert(generated.Contains("call\n    /// <see cref=\"SubmitRoll\"/> instead", StringComparison.Ordinal),
                "the task-returning proxy points at the answered one");
        }

        private static void ReportsAnsweredSubmitNameCollision() => ExpectDiagnostic("TFMP012", """
            using TopiaForge.Mods;
            [MultiplayerContract(Id = "test.answered-collision")]
            public partial class Contract
            {
                [MultiplayerCommand("roll", MaximumPerSecond = 4, MaximumPayloadBytes = 64)]
                private OperationResult<RollResult> Roll(MultiplayerCommandContext command, RollRequest request) =>
                    OperationResult<RollResult>.Success(new RollResult());

                public void SubmitRoll() { }
            }

            public sealed class RollRequest { }

            public sealed class RollResult { }
            """);

        private static object BoundAnsweredContract(out ThrowingBindingSession session, out IDisposable binding)
        {
            var contract = Activator.CreateInstance(LoadGenerated(AnsweredContractSource, "Sample.AnsweredContract"))!;
            session = new ThrowingBindingSession(throwOnPresentation: false);
            var bound = ((IGeneratedMultiplayerBinding)contract).BindMultiplayer(session);
            Assert(bound.Succeeded, "the answered contract binds: " + bound.ErrorMessage);
            binding = bound.Value!;
            return contract;
        }

        private static void Press(object contract, CancellationToken cancellationToken) =>
            contract.GetType().GetMethod("Press")!.Invoke(contract, new object[] { cancellationToken });

        private static List<string> Answers(object contract) =>
            (List<string>)contract.GetType().GetProperty("Answers")!.GetValue(contract)!;
    }
}
