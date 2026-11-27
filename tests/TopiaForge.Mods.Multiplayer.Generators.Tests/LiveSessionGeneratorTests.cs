using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Generators.Tests
{
    /// <summary>Generator behavior added for live sessions: automatic binding, hooks, settings, and diagnostics.</summary>
    internal static partial class Program
    {
        private const string HookedContractSource = """
            #nullable enable
            using System.Collections.Generic;
            using TopiaForge.Mods;

            namespace Sample;

            [MultiplayerContract(Id = "sample.hooked")]
            public partial class HookedContract
            {
                public List<string> Started { get; } = new List<string>();
                public List<string> Ended { get; } = new List<string>();

                [ReplicatedState("score")]
                private ReplicatedState<ScoreState> score = new ReplicatedState<ScoreState>(new ScoreState());

                partial void OnMultiplayerSessionStarted(MultiplayerSessionSnapshot snapshot) =>
                    Started.Add(snapshot.Id.Value);

                private void OnMultiplayerSessionEnded(MultiplayerSessionSnapshot snapshot) =>
                    Ended.Add(snapshot.Id.Value + ":" + snapshot.State);
            }

            public sealed class ScoreState
            {
                public int Value { get; set; }
            }
            """;

        private static void RunLiveSessionGeneratorTests()
        {
            GeneratedBindingIsMarkedAndIdempotent();
            ContractRegistrationFailureAbortsBinding();
            SessionHooksFollowSessionsWhenImplemented();
            ContractsWithoutHooksGenerateNoTracker();
            ReportsServerOnlyHandlerPresentation();
            ReportsInvalidSettingsSeparatelyFromSignatures();
            OnlyNonDefaultDeliverySettingsChangeTheDigest();
            AllowsCommandRandomAndTargetsInPrediction();
            ReportsSharedRandomInPrediction();
            ReportsOneSideEffectPerExpression();
            DiagnosticsLinkToTheReference();
        }

        private static void GeneratedBindingIsMarkedAndIdempotent()
        {
            var contractType = LoadGenerated(ValidContractSource, "Sample.GameContract");
            var contract = Activator.CreateInstance(contractType)!;
            Assert(contract is IGeneratedMultiplayerBinding, "Generated contracts must carry the automatic-binding marker.");
            var binding = (IGeneratedMultiplayerBinding)contract;
            var session = new ThrowingBindingSession(throwOnPresentation: false);
            var first = binding.BindMultiplayer(session);
            Assert(first.Succeeded && session.ContractRegistrationCount == 1,
                "Binding registers the generated contract descriptor with the provider.");
            var registrations = session.CreatedRegistrationCount;

            var repeated = binding.BindMultiplayer(session);
            Assert(repeated.Succeeded && session.CreatedRegistrationCount == registrations &&
                   session.ContractRegistrationCount == 1,
                "A repeated bind for the same session succeeds without registering twice.");
            repeated.Value!.Dispose();
            Assert(!session.AllCreatedRegistrationsDisposed, "Disposing a repeated bind leaves the original binding live.");

            var other = binding.BindMultiplayer(new ThrowingBindingSession(throwOnPresentation: false));
            Assert(!other.Succeeded && other.ErrorCode == ModErrorCode.Conflict,
                "A different session cannot take over a live binding.");

            first.Value!.Dispose();
            Assert(session.AllCreatedRegistrationsDisposed, "Disposing the binding releases the contract registration too.");
            Assert(binding.BindMultiplayer(new ThrowingBindingSession(throwOnPresentation: false)).Succeeded,
                "After disposal another session can bind.");
        }

        private static void ContractRegistrationFailureAbortsBinding()
        {
            var contractType = LoadGenerated(ValidContractSource, "Sample.GameContract");
            var contract = (IGeneratedMultiplayerBinding)Activator.CreateInstance(contractType)!;
            var session = new ThrowingBindingSession(throwOnPresentation: false, failContractRegistration: true);
            var result = contract.BindMultiplayer(session);
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.Conflict && session.CreatedRegistrationCount == 0,
                "A rejected contract registration fails the bind before anything else registers.");
        }

        private static void SessionHooksFollowSessionsWhenImplemented()
        {
            var result = Generate(HookedContractSource);
            AssertNoErrors(result);
            var generated = SingleGeneratedSource(result).SourceText.ToString();
            Assert(generated.Contains("partial void OnMultiplayerSessionStarted(global::TopiaForge.Mods.MultiplayerSessionSnapshot snapshot);", StringComparison.Ordinal) &&
                   !generated.Contains("partial void OnMultiplayerSessionEnded", StringComparison.Ordinal),
                "Only a partial implementation needs a generated defining declaration.");

            var contractType = LoadGenerated(result, "Sample.HookedContract");
            var contract = Activator.CreateInstance(contractType)!;
            var started = (List<string>)contractType.GetProperty("Started")!.GetValue(contract)!;
            var ended = (List<string>)contractType.GetProperty("Ended")!.GetValue(contract)!;
            var session = new ThrowingBindingSession(throwOnPresentation: false);
            var binding = ((IGeneratedMultiplayerBinding)contract).BindMultiplayer(session);
            Assert(binding.Succeeded && started.SequenceEqual(new[] { "binding-test" }) && ended.Count == 0,
                "Binding to a Ready session starts it immediately.");

            session.ReplaceSession("second");
            Assert(ended.SequenceEqual(new[] { "binding-test:Ready" }) && started.SequenceEqual(new[] { "binding-test", "second" }),
                "Replacing the session ends the old one with its last snapshot, then starts the new one.");

            session.EndSession();
            Assert(ended.SequenceEqual(new[] { "binding-test:Ready", "second:Ended" }) && started.Count == 2,
                "An ended session reports its final snapshot once.");

            binding.Value!.Dispose();
            session.ReplaceSession("third");
            Assert(started.Count == 2 && ended.Count == 2, "Hooks stop with the binding.");
        }

        private static void ContractsWithoutHooksGenerateNoTracker()
        {
            var generated = SingleGeneratedSource(Generate(ValidContractSource)).SourceText.ToString();
            Assert(!generated.Contains("__TopiaForgeTrackSession", StringComparison.Ordinal) &&
                   !generated.Contains("SubscribeChanged", StringComparison.Ordinal),
                "Contracts without session hooks pay nothing for session tracking.");
        }

        private static void ReportsServerOnlyHandlerPresentation()
        {
            const string body = """
                Context.Ui.ShowToast("thanks");
                return OperationResult<Response>.Success(new Response());
                """;
            var serverOnly = Generate(ModCommand(body, "PredictionMode.None"));
            Assert(serverOnly.GeneratorDiagnostics.Count(item => item.Id == "TFMP015") == 1 &&
                   serverOnly.GeneratorDiagnostics.All(item => item.Severity.ToString() != "Error"),
                "A server-only handler that drives local UI gets one TFMP015 warning: " + Describe(serverOnly));
            var predicted = Generate(ModCommand(body, "PredictionMode.Owner"));
            Assert(predicted.GeneratorDiagnostics.Any(item => item.Id == "TFMP006") &&
                   predicted.GeneratorDiagnostics.All(item => item.Id != "TFMP015"),
                "Predicted handlers keep their stricter TFMP006 error instead: " + Describe(predicted));
        }

        private static void ReportsInvalidSettingsSeparatelyFromSignatures()
        {
            var rate = Generate(ValidContractSource.Replace(
                "MaximumPerSecond = 20, MaximumPayloadBytes = 512",
                "MaximumPerSecond = 0, MaximumPayloadBytes = 512",
                StringComparison.Ordinal));
            Assert(rate.GeneratorDiagnostics.Any(item => item.Id == "TFMP016" && item.GetMessage().Contains("MaximumPerSecond", StringComparison.Ordinal)) &&
                   rate.GeneratorDiagnostics.All(item => item.Id != "TFMP002"),
                "An out-of-range rate is a setting error, not a signature error: " + Describe(rate));
            var updates = Generate(ValidContractSource.Replace(
                "[ReplicatedState(\"score\")]",
                "[ReplicatedState(\"score\", MaximumUpdatesPerSecond = 61)]",
                StringComparison.Ordinal));
            Assert(updates.GeneratorDiagnostics.Any(item => item.Id == "TFMP016"),
                "State update rates are bounded: " + Describe(updates));
            var delivery = Generate(ValidContractSource.Replace(
                "[ReplicatedState(\"score\")]",
                "[ReplicatedState(\"score\", Delivery = (MultiplayerDelivery)5)]",
                StringComparison.Ordinal));
            Assert(delivery.GeneratorDiagnostics.Any(item => item.Id == "TFMP016"),
                "Undefined delivery modes are rejected: " + Describe(delivery));
        }

        private static void OnlyNonDefaultDeliverySettingsChangeTheDigest()
        {
            var baseline = DescriptorHash(SingleGeneratedSource(Generate(ValidContractSource)).SourceText.ToString());
            Assert(baseline == "0fd3f5bfa617d6ea847658d520fc76a75b5ff6ca727823d3bacdb9a7ffd35572",
                "The golden contract digest is unchanged by the new settings.");
            var explicitDefaults = Generate(ValidContractSource.Replace(
                "[ReplicatedState(\"score\")]",
                "[ReplicatedState(\"score\", Delivery = MultiplayerDelivery.Coalesced, MaximumUpdatesPerSecond = 0)]",
                StringComparison.Ordinal));
            Assert(DescriptorHash(SingleGeneratedSource(explicitDefaults).SourceText.ToString()) == baseline,
                "Spelling out the defaults keeps the digest byte-identical.");
            var immediate = Generate(ValidContractSource.Replace(
                "[ReplicatedState(\"score\")]",
                "[ReplicatedState(\"score\", Delivery = MultiplayerDelivery.Immediate, MaximumUpdatesPerSecond = 20)]",
                StringComparison.Ordinal));
            AssertNoErrors(immediate);
            var generated = SingleGeneratedSource(immediate).SourceText.ToString();
            Assert(DescriptorHash(generated) != baseline &&
                   generated.Contains(".Instance, (global::TopiaForge.Mods.MultiplayerDelivery)1, 20);", StringComparison.Ordinal),
                "Non-default settings change the digest and reach the state definition.");
        }

        private static void AllowsCommandRandomAndTargetsInPrediction()
        {
            var result = Generate(PredictedCommand("""
                var random = context.CreateRandom(3);
                var roll = random.Next(6) + context.CreateRandom().Next(2);
                var target = PresentationTarget.Only(context.SenderId);
                var everyone = PresentationTarget.Everyone;
                return OperationResult<Response>.Success(new Response { Value = roll + (target.Equals(everyone) ? 1 : 0) });
                """));
            Assert(result.GeneratorDiagnostics.All(item => item.Id != "TFMP005" && item.Id != "TFMP006"),
                "A command's own random stream and presentation targets are prediction-safe: " + Describe(result));
        }

        private static void ReportsSharedRandomInPrediction()
        {
            var shared = Generate("""
                using TopiaForge.Mods;
                [MultiplayerContract(Id = "sample.shared")]
                public partial class Contract
                {
                    private DeterministicRandom shared = new DeterministicRandom(1);

                    [MultiplayerCommand("command", Prediction = PredictionMode.Owner)]
                    private OperationResult<Response> Handle(MultiplayerCommandContext context, Request request) =>
                        OperationResult<Response>.Success(new Response { Value = shared.Next(6) });
                }
                public sealed class Request { public int Value { get; set; } }
                public sealed class Response { public int Value { get; set; } }
                """);
            Assert(shared.GeneratorDiagnostics.Any(item => item.Id == "TFMP006"),
                "A generator shared across commands diverges between prediction and replay: " + Describe(shared));
            var seeded = Generate(PredictedCommand("""
                return OperationResult<Response>.Success(new Response { Value = new DeterministicRandom(7).Next(2) });
                """));
            Assert(seeded.GeneratorDiagnostics.Any(item => item.Id == "TFMP006"),
                "Only the command's own stream is allowed; ad-hoc seeds are not: " + Describe(seeded));
        }

        private static void ReportsOneSideEffectPerExpression()
        {
            var result = Generate(PredictedCommand("""
                System.IO.File.WriteAllText(System.IO.Path.GetTempPath(), "x");
                return OperationResult<Response>.Success(new Response());
                """));
            Assert(result.GeneratorDiagnostics.Count(item => item.Id == "TFMP006") == 1,
                "Nested side effects in one expression report once: " + Describe(result));
        }

        private static void DiagnosticsLinkToTheReference()
        {
            var nonPartial = Generate("""
                using TopiaForge.Mods;
                [MultiplayerContract(Id = "sample.plain")]
                public class Contract { }
                """);
            var missingPartial = nonPartial.GeneratorDiagnostics.Single(item => item.Id == "TFMP001");
            Assert(missingPartial.Descriptor.HelpLinkUri == "https://docs.topiaforge.dev/diagnostics/TFMP001",
                "Diagnostics link to their reference entry.");
            var setting = Generate(ValidContractSource.Replace(
                "MaximumPerSecond = 20, MaximumPayloadBytes = 512",
                "MaximumPerSecond = 20, MaximumPayloadBytes = 1",
                StringComparison.Ordinal)).GeneratorDiagnostics.First(item => item.Id == "TFMP016");
            Assert(setting.Descriptor.HelpLinkUri == "https://docs.topiaforge.dev/diagnostics/TFMP016",
                "New diagnostics link to their reference entry too.");
        }

        private static string ModCommand(string body, string prediction) => $$"""
            using TopiaForge.Mods;
            [MultiplayerContract(Id = "sample.ui")]
            public partial class Contract : TopiaForgeMod
            {
                [MultiplayerCommand("command", Prediction = {{prediction}})]
                private OperationResult<Response> Handle(MultiplayerCommandContext command, Request request)
                {
                    {{body}}
                }
            }
            public sealed class Request { public int Value { get; set; } }
            public sealed class Response { public int Value { get; set; } }
            """;

        private static Type LoadGenerated(string source, string typeName)
        {
            var result = Generate(source);
            AssertNoErrors(result);
            return LoadGenerated(result, typeName);
        }

        private static Type LoadGenerated(GenerationResult result, string typeName)
        {
            using var assemblyStream = new MemoryStream();
            var emit = result.Compilation.Emit(assemblyStream);
            Assert(emit.Success, "Generated consumer failed to emit:\n" + JoinErrors(emit.Diagnostics));
            assemblyStream.Position = 0;
            return AssemblyLoadContext.Default.LoadFromStream(assemblyStream).GetType(typeName, true)!;
        }

        private static string Describe(GenerationResult result) =>
            string.Join(", ", result.GeneratorDiagnostics.Select(item => item.Id + ": " + item.GetMessage()));
    }
}
