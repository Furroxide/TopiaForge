using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The replication SPI's services: commands route to their own mod's handler, references resolve through their source,
    /// the microphone goes by priority, and what the SPI is given is bounded.
    /// </summary>
    internal static class RuntimeReplicationServiceTests
    {
        private static readonly ModNetProfile Process = new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process);

        internal static void Run()
        {
            CommandsRouteToTheirModsHandler();
            ReferencesResolveThroughTheirSource();
            TheMicrophoneGoesByPriority();
            DescriptorsCommandsAndReferencesAreBounded();
        }

        private static void CommandsRouteToTheirModsHandler()
        {
            var (services, control, runtime) = RuntimeReplicationTests.Services();
            var lifetime = new OwnerModLifetime();
            var blocks = services.Bind("io.example.blocks", Process, lifetime);
            var rival = services.Bind("io.example.rival", Process, new OwnerModLifetime());
            InternalCommand? seen = null;
            var answer = OperationResult<bool>.Success(true);
            var lease = blocks.RegisterCommand("blocks.place", command => { seen = command; return answer; });
            var payload = new byte[] { 1, 2, 3 };
            var result = blocks.SubmitCommand("blocks.place", payload);
            That(lease.Succeeded && ReferenceEquals(result, answer) && seen != null && seen.Kind == "blocks.place" && seen.IsLocal && seen.Sender == null &&
                 seen.Payload.ToArray().SequenceEqual(payload), "a mod's command reaches its own handler at once, as this process's own");
            payload[0] = 9;
            That(seen!.Payload.Span[0] == 1, "the handler's payload is a copy");
            That(rival.SubmitCommand("blocks.place", payload).ErrorCode == ModErrorCode.NotFound, "another mod cannot run it");
            var rivalRuns = 0;
            That(rival.RegisterCommand("blocks.place", _ => { rivalRuns++; return answer; }).Succeeded && rival.SubmitCommand("blocks.place", payload).Succeeded &&
                 rivalRuns == 1, "and has a kind of that name of its own");
            That(blocks.RegisterCommand("blocks.place", _ => answer).ErrorCode == ModErrorCode.Conflict, "a mod has one handler per kind");
            That(blocks.SubmitCommand("blocks.unknown", payload).ErrorCode == ModErrorCode.NotFound, "an unregistered kind runs nothing");
            That(blocks.SubmitCommand("Blocks.Place", payload).ErrorCode == ModErrorCode.InvalidArgument, "a kind is a lowercase token");
            var calls = 0;
            blocks.RegisterCommand("blocks.big", _ => { calls++; return answer; });
            That(blocks.SubmitCommand("blocks.big", new byte[InternalReplicationLimits.MaximumPayloadBytes]).Succeeded && calls == 1, "16 KiB is allowed");
            That(blocks.SubmitCommand("blocks.big", new byte[InternalReplicationLimits.MaximumPayloadBytes + 1]).ErrorCode == ModErrorCode.InvalidArgument && calls == 1,
                "a byte more is refused before the handler");
            control.Role = InternalSessionRole.Client;
            Frame(runtime);
            That(blocks.SubmitCommand("blocks.place", payload).Succeeded, "stub: a joined player's command still runs here until authority routing forwards it");

            lease.Value!.Dispose();
            That(blocks.SubmitCommand("blocks.place", payload).ErrorCode == ModErrorCode.NotFound, "the lease removes the handler");
            blocks.RegisterCommand("blocks.null", _ => null!);
            Throws<InvalidOperationException>(() => blocks.SubmitCommand("blocks.null", payload), "a handler that answers nothing is a fault");
            var failure = new InvalidOperationException("the handler's own failure");
            blocks.RegisterCommand("blocks.throw", _ => throw failure);
            try
            {
                blocks.SubmitCommand("blocks.throw", payload);
                That(false, "a handler that throws throws");
            }
            catch (InvalidOperationException thrown)
            {
                That(ReferenceEquals(thrown, failure), "the handler's own exception comes back unchanged");
            }

            lifetime.Dispose();
            That(!services.TryGetCommand("io.example.blocks", "blocks.big", out _) && services.TryGetCommand("io.example.rival", "blocks.place", out _),
                "a mod's handlers end with its context, and only its own");
            Throws<ArgumentNullException>(() => rival.SubmitCommand(null!, payload), "a kind is required");
            Throws<ArgumentNullException>(() => rival.SubmitCommand("blocks.place", null!), "and a payload");
            Throws<ArgumentNullException>(() => rival.RegisterCommand("blocks.place", null!), "and a handler");
        }

        private static void ReferencesResolveThroughTheirSource()
        {
            var (services, _, runtime) = RuntimeReplicationTests.Services();
            var seam = services.Bind("io.example.robotkit", Process, new OwnerModLifetime());
            var target = new SeamEntity();
            That(!seam.TryCreateRef(target, out var none) && !none.IsSpecified, "stub: with no source, no reference is made");
            That(!seam.TryResolveRef(new InternalNetRef(InternalNetRefKind.Entity, "17"), out var nothing) && nothing == null, "or resolved");

            var source = new ReferenceSource(target);
            services.References = source;
            That(seam.TryCreateRef(target, out var reference) && reference.Equals(new InternalNetRef(InternalNetRefKind.Entity, "17")),
                "the network world's source names the entity");
            That(seam.TryResolveRef(reference, out var resolved) && ReferenceEquals(resolved, target), "and finds it again");
            var asked = source.Resolutions;
            That(!seam.TryResolveRef(default, out _) && source.Resolutions == asked, "the default reference names nothing, and nobody is asked");
            source.ResolveToNothing = true;
            That(!seam.TryResolveRef(reference, out resolved) && resolved == null, "a source that answers yes with nothing is no");

            var log = (RecordingRuntimeLog)runtime.Log;
            source.Failure = new InvalidOperationException("the table at 203.0.113.4:27250 is gone");
            That(!seam.TryCreateRef(target, out reference) && !reference.IsSpecified && !seam.TryResolveRef(new InternalNetRef(InternalNetRefKind.Player, "p2"), out _),
                "a source that throws answers no");
            var errors = log.Lines.Where(line => line.Contains("reference source", StringComparison.Ordinal)).ToArray();
            That(errors.Length == 1 && !errors[0].Contains("203.0.113.4", StringComparison.Ordinal), "and is reported once, by type");
            Throws<ArgumentNullException>(() => seam.TryCreateRef(null!, out _), "a reference needs an entity");
        }

        private static void TheMicrophoneGoesByPriority()
        {
            var (services, _, _) = RuntimeReplicationTests.Services();
            var robotLifetime = new OwnerModLifetime();
            var robots = services.Bind("io.example.robotkit", Process, robotLifetime);
            var voice = services.Bind("io.example.voice", Process, new OwnerModLifetime());
            var arbiter = services.Microphone;

            var chat = voice.AcquireMicrophone(InternalMicrophoneUse.VoiceChat, "Push to talk").Value!;
            That(!chat.IsPreempted && arbiter.Holder == InternalMicrophoneUse.VoiceChat, "voice chat takes a free microphone");
            var dialogue = robots.AcquireMicrophone(InternalMicrophoneUse.RobotDialogue, "Talking to Sam").Value!;
            That(!dialogue.IsPreempted && chat.IsPreempted && arbiter.Holder == InternalMicrophoneUse.RobotDialogue, "robot dialogue comes first: voice chat yields");
            var refused = voice.AcquireMicrophone(InternalMicrophoneUse.VoiceChat, "Push to talk");
            That(refused.ErrorCode == ModErrorCode.Conflict && refused.ErrorMessage == "The microphone is in use for robot dialogue.",
                "and voice chat is refused while dialogue holds it (" + refused.ErrorMessage + ")");

            arbiter.NativeRecording = true;
            That(dialogue.IsPreempted && chat.IsPreempted && arbiter.Holder == InternalMicrophoneUse.NativeRecording, "the game's own recording comes before both");
            var blocked = robots.AcquireMicrophone(InternalMicrophoneUse.RobotDialogue, "Talking again");
            That(blocked.ErrorCode == ModErrorCode.Conflict && blocked.ErrorMessage == "The microphone is in use for the game's robot recording.", "dialogue waits for it");
            arbiter.NativeRecording = false;

            dialogue.Dispose();
            dialogue.Dispose();
            That(dialogue.IsReleased && !dialogue.IsPreempted && !chat.IsPreempted && arbiter.HoldsOf(InternalMicrophoneUse.RobotDialogue) == 0,
                "giving dialogue back, twice, releases it once and voice chat resumes");
            var first = robots.AcquireMicrophone(InternalMicrophoneUse.RobotDialogue, "One").Value!;
            var second = robots.AcquireMicrophone(InternalMicrophoneUse.RobotDialogue, "Two").Value!;
            That(arbiter.HoldsOf(InternalMicrophoneUse.RobotDialogue) == 2 && first.Use == InternalMicrophoneUse.RobotDialogue, "holds of one use coexist");
            robotLifetime.Dispose();
            That(first.IsReleased && second.IsReleased && arbiter.HoldsOf(InternalMicrophoneUse.RobotDialogue) == 0, "a context's holds end with its lifetime");
            chat.Dispose();
            That(arbiter.Holder == null, "and a free microphone has no holder");

            That(voice.AcquireMicrophone(InternalMicrophoneUse.NativeRecording, "Pretend").ErrorCode == ModErrorCode.InvalidArgument, "the game's recording is never taken");
            That(voice.AcquireMicrophone((InternalMicrophoneUse)7, "Odd").ErrorCode == ModErrorCode.InvalidArgument, "nor an undefined use");
            foreach (var reason in new[] { "", " padded", "line\nbreak", new string('r', 129) })
            {
                That(voice.AcquireMicrophone(InternalMicrophoneUse.VoiceChat, reason).ErrorCode == ModErrorCode.InvalidArgument, "'" + reason + "' is not a reason");
            }

            Throws<ArgumentNullException>(() => voice.AcquireMicrophone(InternalMicrophoneUse.VoiceChat, null!), "a reason is required");
            Throws<ArgumentOutOfRangeException>(() => arbiter.IsHeldAbove((InternalMicrophoneUse)7), "the arbiter knows only its uses");
        }

        private static void DescriptorsCommandsAndReferencesAreBounded()
        {
            var payload = new byte[] { 4, 5 };
            var descriptor = new InternalSpawnDescriptor("creator.content", TransformState.Identity, payload);
            payload[0] = 0;
            That(descriptor.Payload.Span[0] == 4 && descriptor.ContentKey == null && descriptor.Source == InternalSpawnSource.SdkSpawn, "a description copies its payload");
            That(new InternalSpawnDescriptor("creator.content", TransformState.Identity).Payload.Length == 0, "and has none by default");
            That(new InternalSpawnDescriptor("creator.content", TransformState.Identity, new byte[InternalReplicationLimits.MaximumPayloadBytes]).Payload.Length ==
                 InternalReplicationLimits.MaximumPayloadBytes, "16 KiB fits");
            foreach (var bad in new Action[]
                     {
                         () => _ = new InternalSpawnDescriptor("Creator", TransformState.Identity),
                         () => _ = new InternalSpawnDescriptor("creator.content", TransformState.Identity, new byte[InternalReplicationLimits.MaximumPayloadBytes + 1]),
                         () => _ = new InternalSpawnDescriptor("worlds.arena", TransformState.Identity, null, InternalSpawnSource.WorldContent),
                         () => _ = new InternalSpawnDescriptor("worlds.arena", TransformState.Identity, null, InternalSpawnSource.WorldContent, "key\twith tab"),
                         () => _ = new InternalSpawnDescriptor("worlds.arena", TransformState.Identity, null, InternalSpawnSource.WorldContent, new string('k', 257)),
                         () => _ = new InternalSpawnDescriptor("creator.content", TransformState.Identity, null, InternalSpawnSource.SdkSpawn, "key"),
                         () => _ = new InternalCommand("Blocks", Array.Empty<byte>(), null),
                         () => _ = new InternalCommand("blocks.place", new byte[InternalReplicationLimits.MaximumPayloadBytes + 1], null),
                         () => _ = new InternalCommand("blocks.place", Array.Empty<byte>(), " p1"),
                         () => _ = new InternalNetRef(InternalNetRefKind.Entity, ""),
                         () => _ = new InternalNetRef(InternalNetRefKind.Player, new string('p', 129))
                     })
            {
                Throws<ArgumentException>(bad, "out-of-bounds input is refused");
            }

            Throws<ArgumentOutOfRangeException>(() => _ = new InternalSpawnDescriptor("creator.content", TransformState.Identity, null, (InternalSpawnSource)4),
                "an undefined source is refused");
            Throws<ArgumentOutOfRangeException>(() => _ = new InternalNetRef(InternalNetRefKind.None, "1"), "a reference names something");
            Throws<ArgumentNullException>(() => _ = new InternalCommand("blocks.place", null!, null), "a command needs its payload");
            var player = new InternalNetRef(InternalNetRefKind.Player, "p1");
            That(player.Equals(new InternalNetRef(InternalNetRefKind.Player, "p1")) && !player.Equals(new InternalNetRef(InternalNetRefKind.Entity, "p1")) &&
                 player.GetHashCode() == new InternalNetRef(InternalNetRefKind.Player, "p1").GetHashCode() && player.ToString() == "Player:p1" &&
                 default(InternalNetRef).ToString() == "none", "references compare by what they name");
            That(new InternalCommand("blocks.place", Array.Empty<byte>(), "p2").Sender == "p2" && !new InternalCommand("blocks.place", Array.Empty<byte>(), "p2").IsLocal,
                "a forwarded command names its sender");
        }

        private sealed class ReferenceSource : IMultiplayerReferenceSource
        {
            private readonly IEntity entity;

            internal ReferenceSource(IEntity entity)
            {
                this.entity = entity;
            }

            internal Exception? Failure { get; set; }

            internal bool ResolveToNothing { get; set; }

            internal int Resolutions { get; private set; }

            public bool TryCreateRef(IEntity value, out InternalNetRef reference)
            {
                if (Failure != null) throw Failure;
                reference = new InternalNetRef(InternalNetRefKind.Entity, "17");
                return ReferenceEquals(value, entity);
            }

            public bool TryResolveRef(InternalNetRef reference, out IEntity? value)
            {
                Resolutions++;
                if (Failure != null) throw Failure;
                value = ResolveToNothing ? null : entity;
                return reference.Value == "17";
            }
        }
    }
}
