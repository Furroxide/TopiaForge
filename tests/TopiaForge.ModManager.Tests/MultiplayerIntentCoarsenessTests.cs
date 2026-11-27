using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.MultiplayerIntentTests;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The input-active frame is intent tracking's coarse source (WS3.4): its whole Update and LateUpdate carry the player's
    /// intent, scripted work in them included. Authority routing (WS3.7) weighs it apart from the player's direct actions,
    /// so the seam says which one a call carries, and what an input frame starts stays coarse: a command it runs, and a
    /// control's event its code fires, whichever mod owns the control. A click, the player's own command, an interaction, a
    /// motion lease and a participant's forwarded click are direct.
    /// </summary>
    internal static class MultiplayerIntentCoarsenessTests
    {
        private const string Builder = "io.example.builder";
        private const string Neighbour = "io.example.neighbour";
        private static readonly ContextCallback Callback = state => ((Action)state!)();

        internal static void Run()
        {
            WhatAnInputFrameStartsStaysCoarse();
            DirectActionsStayDirect();
            TheSeamsRecordSaysWhichItIs();
            Console.WriteLine("MultiplayerIntentCoarsenessTests passed.");
        }

        private static void WhatAnInputFrameStartsStaysCoarse()
        {
            var (runtime, _) = Online(InternalSessionRole.Client);
            var intents = runtime.Intents;
            var (builder, neighbour) = Seams(runtime);
            var registry = new ModServiceRegistry();
            var commands = new OwnerCommandService(Builder, new OwnerModLifetime(), new OwnerFacadeStoppingTests.Logger(), registry, intents);
            var neighbourCommands = new OwnerCommandService(Neighbour, new OwnerModLifetime(), new OwnerFacadeStoppingTests.Logger(), registry, intents);
            var seen = new List<string>();
            neighbourCommands.Register(new CommandDefinition("ping", "Ping"), _ =>
            {
                seen.Add("command:" + Weigh(neighbour.CurrentIntent));
                return OperationResult<string>.Success("pong");
            });
            var hook = new MultiplayerIntentKitHook(intents);
            IntentRecord? frame = null;
            IntentRecord? first = null;
            IntentRecord? second = null;
            Action update = () =>
            {
                seen.Add("frame:" + Weigh(builder.CurrentIntent));
                commands.TryExecute(Neighbour + ":ping", Array.Empty<string>(), out _);
                var own = hook.BeginPlayerAction(Builder);
                seen.Add("own-control:" + Weigh(builder.CurrentIntent));
                first = intents.VisibleTo(Builder);
                hook.End(own);
                var theirs = hook.BeginPlayerAction(Neighbour);
                seen.Add("their-control:" + Weigh(neighbour.CurrentIntent));
                hook.End(theirs);
                var again = hook.BeginPlayerAction(Builder);
                second = intents.VisibleTo(Builder);
                hook.End(again);
                seen.Add("after:" + Weigh(builder.CurrentIntent));
            };
            intents.RunInputFrame(ref frame, Builder, late: false, Callback, update);
            Sequence(new[]
            {
                "frame:InputActiveFrame/coarse", "command:OwnerCommand/coarse", "own-control:UiCallback/coarse",
                "their-control:UiCallback/coarse", "after:InputActiveFrame/coarse"
            }, seen, "a command an input frame runs, and a control's event its code fires, whichever mod owns it, stay coarse");
            True(first != null && ReferenceEquals(first, second), "the frame's control events reuse one record, so they allocate nothing again");
            True(intents.Scope.Flowing == null, "and nothing is left flowing after the frame");

            // The player's own click on the same control afterwards is a click again, not the record the frame's code used.
            var click = hook.BeginPlayerAction(Builder);
            True(Weigh(builder.CurrentIntent) == "UiCallback/direct", "the player's click after the frame is direct (" + Weigh(builder.CurrentIntent) + ")");
            hook.End(click);
        }

        private static void DirectActionsStayDirect()
        {
            var (runtime, _) = Online(InternalSessionRole.ListenHost);
            var intents = runtime.Intents;
            var (builder, neighbour) = Seams(runtime);
            var registry = new ModServiceRegistry();
            var commands = new OwnerCommandService(Builder, new OwnerModLifetime(), new OwnerFacadeStoppingTests.Logger(), registry, intents);
            var neighbourCommands = new OwnerCommandService(Neighbour, new OwnerModLifetime(), new OwnerFacadeStoppingTests.Logger(), registry, intents);
            var seen = new List<string>();
            neighbourCommands.Register(new CommandDefinition("ping", "Ping"), _ =>
            {
                seen.Add("command:" + Weigh(neighbour.CurrentIntent));
                return OperationResult<string>.Success("pong");
            });
            var hook = new MultiplayerIntentKitHook(intents);

            var click = hook.BeginPlayerAction(Builder);
            seen.Add("click:" + Weigh(builder.CurrentIntent));
            commands.TryExecute(Neighbour + ":ping", Array.Empty<string>(), out _);
            hook.End(click);
            registry.TryExecutePlayerCommand(Neighbour + ":ping", Array.Empty<string>(), intents, out _);
            IntentRecord? slot = null;
            Action interaction = () => seen.Add("interaction:" + Weigh(builder.CurrentIntent));
            intents.RunInteraction(ref slot, Builder, Callback, interaction);
            using (intents.EnterMotionLease(Builder)) seen.Add("lease:" + Weigh(builder.CurrentIntent));
            using (intents.EnterForwarded(Builder, IntentSource.UiCallback, "participant-5")) seen.Add("forwarded-click:" + Weigh(builder.CurrentIntent));
            using (intents.EnterForwarded(Builder, IntentSource.InputActiveFrame, "participant-5"))
            {
                seen.Add("forwarded-frame:" + Weigh(builder.CurrentIntent));
                commands.TryExecute(Neighbour + ":ping", Array.Empty<string>(), out _);
            }

            using (intents.EnterForwarded(Builder, IntentSource.OwnerCommand, "participant-5", fromInputFrame: true))
            {
                seen.Add("forwarded-frame-command:" + Weigh(builder.CurrentIntent));
            }

            Sequence(new[]
            {
                "click:UiCallback/direct", "command:OwnerCommand/direct", "command:OwnerCommand/direct", "interaction:InteractionHandler/direct",
                "lease:MotionLease/direct", "forwarded-click:UiCallback/direct@participant-5", "forwarded-frame:InputActiveFrame/coarse@participant-5",
                "command:OwnerCommand/coarse@participant-5", "forwarded-frame-command:OwnerCommand/coarse@participant-5"
            }, seen, "a click, the commands it and the player run, an interaction, a lease and a forwarded click are direct; a forwarded frame stays coarse");
            True(intents.Scope.Flowing == null, "and nothing is left flowing");
        }

        private static void TheSeamsRecordSaysWhichItIs()
        {
            var frame = new InternalIntentRecord(InternalIntent.InputActiveFrame, null);
            var click = new InternalIntentRecord(InternalIntent.UiCallback, null);
            var fired = new InternalIntentRecord(InternalIntent.UiCallback, null, fromInputFrame: true);
            True(frame.IsInputFrame && !frame.IsDirectAction, "an input frame is coarse");
            True(!click.IsInputFrame && click.IsDirectAction, "a click is direct");
            True(fired.IsInputFrame && !fired.IsDirectAction && fired.Source == InternalIntent.UiCallback, "a control's event an input frame fired is coarse");
            var scripted = default(InternalIntentRecord);
            True(!scripted.IsInputFrame && !scripted.IsDirectAction && !scripted.IsPlayerIntent, "scripted code carries neither");
            True(!fired.Equals(click) && fired.Equals(new InternalIntentRecord(InternalIntent.UiCallback, null, fromInputFrame: true)),
                "records that differ only in being coarse are different answers");
            True(fired.GetHashCode() != click.GetHashCode(), "and hash apart");
            True(fired.ToString() == "UiCallback from an input frame of the local player" &&
                 new InternalIntentRecord(InternalIntent.OwnerCommand, "participant-2", fromInputFrame: true).ToString() == "OwnerCommand from an input frame of a participant",
                "its text says so, and never names the participant (" + fired + ")");
            Throws<ArgumentException>(() => new InternalIntentRecord(InternalIntent.None, null, fromInputFrame: true), "scripted code has no input frame");
        }

        private static (RuntimeReplication Builder, RuntimeReplication Neighbour) Seams(MultiplayerRuntime runtime)
        {
            var services = new MultiplayerReplication(runtime);
            var profile = new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process);
            return (services.Bind(Builder, profile, new OwnerModLifetime()), services.Bind(Neighbour, profile, new OwnerModLifetime()));
        }

        /// <summary>Whose intent a call carries, and whether it is a direct action or an input frame's: what WS3.7 asks.</summary>
        private static string Weigh(InternalIntentRecord intent) =>
            !intent.IsPlayerIntent
                ? "none"
                : intent.Source + (intent.IsDirectAction ? "/direct" : "/coarse") + (intent.IsLocalPlayer ? string.Empty : "@" + intent.ParticipantId);
    }
}
