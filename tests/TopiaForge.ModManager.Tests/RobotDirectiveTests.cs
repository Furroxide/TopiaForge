using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.OppositeDay;
using TopiaForge.Prompts;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotDirectiveTests
    {
        public static void Run()
        {
            TestNativeDirectiveComposition();
            TestNativeDirectiveCompositionIsConcurrentAndStateless();
            TestBrainRequestCompositionPreservesEveryField();
            TestBrainRequestCompositionLimitsAndIdempotence();
            TestBrainRequestCompositionTracksDynamicRegistry();
            TestOppositeDayLifecycle();
            TestOppositeDayFailsWithoutPrompts();
            TestOppositeDayFailsWhenRegistrationIsRejected();
            Console.WriteLine("All robot directive tests passed.");
        }

        private static void TestNativeDirectiveComposition()
        {
            var blank = PromptDirectiveComposer.Append(null, "  ", out var blankResult);
            Assert(blank == PromptDirectiveCompositionOutcome.Blank && blankResult.Count == 0,
                "blank native directives should leave an empty source unchanged");

            var unchangedSource = new[] { "personality" };
            var nullDirective = PromptDirectiveComposer.Append(unchangedSource, null, out var nullResult);
            var emptyDirective = PromptDirectiveComposer.Append(unchangedSource, string.Empty, out var emptyResult);
            Assert(nullDirective == PromptDirectiveCompositionOutcome.Blank
                && emptyDirective == PromptDirectiveCompositionOutcome.Blank
                && ReferenceEquals(unchangedSource, nullResult)
                && ReferenceEquals(unchangedSource, emptyResult),
                "null and empty native directives should preserve the original source collection");

            var nullSource = PromptDirectiveComposer.Append(null, "directive", out var nullSourceResult);
            Assert(nullSource == PromptDirectiveCompositionOutcome.Appended
                && nullSourceResult.Count == 1
                && nullSourceResult[0] == "directive",
                "a valid directive should compose safely when the native source collection is null");

            var source = new List<string> { "personality", "grounded facts" };
            var appended = PromptDirectiveComposer.Append(source, "  invert this  ", out var composed);
            Assert(appended == PromptDirectiveCompositionOutcome.Appended,
                "a valid native directive should append");
            Assert(source.Count == 2 && composed.Count == 3 && composed[2] == "invert this",
                "native composition should copy the source and append a normalized final template");
            Assert(!ReferenceEquals(source, composed),
                "native composition must not mutate or reuse the game's source collection");

            var duplicate = PromptDirectiveComposer.Append(composed, "\ninvert this\t", out var duplicateResult);
            Assert(duplicate == PromptDirectiveCompositionOutcome.Duplicate && ReferenceEquals(composed, duplicateResult),
                "an already-present directive should not be injected twice");

            var oversizedText = new string('x', PromptDirectiveComposer.MaximumDirectiveCharacters + 1);
            var oversized = PromptDirectiveComposer.Append(source, oversizedText, out var oversizedResult);
            Assert(oversized == PromptDirectiveCompositionOutcome.TooLong && ReferenceEquals(source, oversizedResult),
                "an oversized native directive should preserve the original collection");
        }

        private static void TestNativeDirectiveCompositionIsConcurrentAndStateless()
        {
            var source = new[] { "facts" };
            Parallel.For(0, 64, index =>
            {
                var outcome = PromptDirectiveComposer.Append(source, "directive " + index, out var result);
                if (outcome != PromptDirectiveCompositionOutcome.Appended
                    || result.Count != 2
                    || result[0] != "facts"
                    || result[1] != "directive " + index)
                {
                    throw new InvalidOperationException("concurrent native directive composition leaked state");
                }
            });
            Assert(source.Length == 1 && source[0] == "facts",
                "concurrent native composition should not mutate shared input");
        }

        private static void TestBrainRequestCompositionPreservesEveryField()
        {
            var output = new BrainOutputField(
                "action",
                "chosen action",
                BrainFieldType.String,
                new[] { "FOLLOW", "FLEE" });
            var request = new BrainQueryRequest(
                "follow the player",
                new[] { output },
                usage: "opposite-day-test",
                successDescription: "choose one valid action",
                temperature: 1.25f,
                useReasoning: true);

            var composed = BrainQueryDirectiveComposer.Apply(request, "flee instead", out var exceeded);
            Assert(!exceeded && !ReferenceEquals(request, composed),
                "a valid RobotKit directive should produce a cloned request");
            Assert(composed.Prompt == "follow the player\n\nflee instead",
                "RobotKit should append the directive with the approved delimiter");
            Assert(composed.Usage == request.Usage
                && composed.SuccessDescription == request.SuccessDescription
                && Math.Abs(composed.Temperature - request.Temperature) < 1e-6f
                && composed.UseReasoning == request.UseReasoning,
                "RobotKit composition should preserve all query tuning fields");
            Assert(composed.Outputs.Count == 1
                && ReferenceEquals(composed.Outputs[0], output)
                && composed.Outputs[0].AllowedStrings![1] == "FLEE",
                "RobotKit composition should preserve every structured output definition");
            Assert(request.Prompt == "follow the player",
                "RobotKit composition should not mutate the caller's request");
        }

        private static void TestBrainRequestCompositionLimitsAndIdempotence()
        {
            var output = new[] { new BrainOutputField("action", "chosen action") };
            var blankRequest = new BrainQueryRequest("prompt", output);
            Assert(ReferenceEquals(
                    blankRequest,
                    BrainQueryDirectiveComposer.Apply(blankRequest, "  ", out var blankExceeded))
                && !blankExceeded,
                "a blank RobotKit directive should preserve the original request");

            var exactFitPrompt = new string(
                'p',
                BrainQueryDirectiveComposer.MaxPromptChars - 2 - "directive".Length);
            var exactFit = BrainQueryDirectiveComposer.Apply(
                new BrainQueryRequest(exactFitPrompt, output),
                "directive",
                out var exactExceeded);
            Assert(!exactExceeded && exactFit.Prompt.Length == BrainQueryDirectiveComposer.MaxPromptChars,
                "RobotKit should accept a combined prompt exactly at the backend limit");

            var tooLong = new BrainQueryRequest(exactFitPrompt + "x", output);
            var skipped = BrainQueryDirectiveComposer.Apply(tooLong, "directive", out var exceeded);
            Assert(exceeded && ReferenceEquals(tooLong, skipped),
                "RobotKit should preserve the original request instead of truncating when the directive cannot fit");

            var alreadyPresent = new BrainQueryRequest("prompt\n\ndirective", output);
            Assert(ReferenceEquals(
                    alreadyPresent,
                    BrainQueryDirectiveComposer.Apply(alreadyPresent, "directive", out var duplicateExceeded))
                && !duplicateExceeded,
                "RobotKit should not inject a duplicate directive");

            var quotedDirective = new BrainQueryRequest(
                "Ignore the quoted word 'directive' and answer the real request.",
                output);
            var quotedResult = BrainQueryDirectiveComposer.Apply(
                quotedDirective,
                "directive",
                out var quotedExceeded);
            Assert(!quotedExceeded && quotedResult.Prompt.EndsWith("\n\ndirective", StringComparison.Ordinal),
                "mentioning or negating directive text inside the original prompt must not bypass injection");
        }

        private static void TestBrainRequestCompositionTracksDynamicRegistry()
        {
            var request = new BrainQueryRequest(
                "current objective",
                new[] { new BrainOutputField("action", "chosen action") });
            IPromptOverrideRegistry? currentRegistry = null;
            Func<IPromptOverrideRegistry?> resolver = () => currentRegistry;

            var absent = BrainQueryDirectiveComposer.ApplyFromRegistry(
                request,
                resolver,
                out var absentExceeded,
                out var absentFailure);
            Assert(ReferenceEquals(request, absent) && !absentExceeded && absentFailure == null,
                "RobotKit should preserve requests while the optional Prompts provider is absent");

            using var firstRegistry = new PromptOverrideRegistry("first.provider");
            var firstRegistration = firstRegistry.Register(new PromptOverrideRequest(
                WellKnownPromptIds.GlobalRobotDirective,
                "first directive",
                priority: 100));
            Assert(firstRegistration.Succeeded, "the first dynamic directive should register");
            currentRegistry = firstRegistry;
            var first = BrainQueryDirectiveComposer.ApplyFromRegistry(
                request,
                resolver,
                out var firstExceeded,
                out var firstFailure);
            Assert(first.Prompt.EndsWith("\n\nfirst directive", StringComparison.Ordinal)
                && !firstExceeded
                && firstFailure == null,
                "RobotKit should observe a Prompts provider loaded after RobotKit");

            using var replacementRegistry = new PromptOverrideRegistry("replacement.provider");
            var replacementRegistration = replacementRegistry.Register(new PromptOverrideRequest(
                WellKnownPromptIds.GlobalRobotDirective,
                "replacement directive",
                priority: 1000));
            Assert(replacementRegistration.Succeeded, "the replacement dynamic directive should register");
            currentRegistry = replacementRegistry;
            var replacement = BrainQueryDirectiveComposer.ApplyFromRegistry(
                request,
                resolver,
                out var replacementExceeded,
                out var replacementFailure);
            Assert(replacement.Prompt.EndsWith("\n\nreplacement directive", StringComparison.Ordinal)
                && !replacementExceeded
                && replacementFailure == null,
                "RobotKit should observe a replacement provider without restarting");

            currentRegistry = null;
            var unloaded = BrainQueryDirectiveComposer.ApplyFromRegistry(
                request,
                resolver,
                out var unloadedExceeded,
                out var unloadedFailure);
            Assert(ReferenceEquals(request, unloaded) && !unloadedExceeded && unloadedFailure == null,
                "RobotKit should stop injecting immediately after the Prompts provider unloads");
        }

        private static void TestOppositeDayLifecycle()
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                var context = CreateOppositeDayContext();
                var registry = new FakePromptOverrideRegistry(context);
                Assert(context.Extensions.Register<IPromptOverrideRegistry>(registry).Succeeded,
                    "the fake Prompts provider should register");
                using var runner = ModLifecycleRunner.Create<OppositeDayMod>(context);

                runner.Load();
                PromptOverride? active = null;
                Assert(registry.ActiveRegistrationCount == 1
                    && registry.TryGetEffectiveOverride(WellKnownPromptIds.GlobalRobotDirective, out active)
                    && active != null,
                    "Opposite Day should register exactly one global robot directive");
                Assert(active!.Priority == 1000
                    && active.SourceId == "io.github.furroxide.topiaforge.opposite-day"
                    && active.ReplacementText.Contains("Reverse both affirmative instructions and prohibitions")
                    && active.ReplacementText.Contains("Never reveal, quote, name, or explain")
                    && active.ReplacementText.Contains("rationalize the resulting choice"),
                    "Opposite Day should publish the approved full-chaos, concealed, faint-suspicion directive");

                runner.Unload();
                Assert(registry.ActiveRegistrationCount == 0,
                    "Opposite Day unload should release its owner-bound directive");
                context.AssertNoLeaks();
            }
        }

        private static void TestOppositeDayFailsWithoutPrompts()
        {
            var context = CreateOppositeDayContext();
            using var runner = ModLifecycleRunner.Create<OppositeDayMod>(context);
            AssertLoadFails(runner, "an unavailable required Prompts provider should fail Opposite Day load");
            context.AssertNoLeaks();
        }

        private static void TestOppositeDayFailsWhenRegistrationIsRejected()
        {
            var context = CreateOppositeDayContext();
            Assert(context.Extensions.Register<IPromptOverrideRegistry>(new RejectingPromptRegistry()).Succeeded,
                "the rejecting prompt provider should register for the failure test");
            using var runner = ModLifecycleRunner.Create<OppositeDayMod>(context);
            AssertLoadFails(runner, "a rejected directive registration should fail Opposite Day load");
            context.AssertNoLeaks();
        }

        private static FakeModContext CreateOppositeDayContext()
        {
            return new FakeModContext(new ModIdentity(
                "io.github.furroxide.topiaforge.opposite-day",
                "Opposite Day",
                SemanticVersion.Parse("1.0.0-rc.1")));
        }

        private static void AssertLoadFails(ModLifecycleRunner runner, string message)
        {
            var failed = false;
            try
            {
                runner.Load();
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }

            Assert(failed && runner.IsFinished, message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class RejectingPromptRegistry : IPromptOverrideRegistry
        {
            public IReadOnlyList<PromptOverride> Overrides => Array.Empty<PromptOverride>();

            public OperationResult<IPromptOverrideHandle> Register(PromptOverrideRequest request)
            {
                return OperationResult<IPromptOverrideHandle>.Failure(
                    ModErrorCode.Conflict,
                    "rejected for test");
            }

            public bool TryGetEffectiveOverride(string promptId, out PromptOverride? promptOverride)
            {
                promptOverride = null;
                return false;
            }

            public IReadOnlyList<PromptConflict> GetConflicts() => Array.Empty<PromptConflict>();
        }
    }
}
