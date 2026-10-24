using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.RobotKit;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class VoiceChatTests
    {
        /// <summary>
        /// The game's own recording reaches the arbiter before any mod can claim or use the microphone: the arbiter reads it
        /// from the game whenever a use is decided, so a robot conversation sees the game as it is at the moment it asks, in
        /// the frame the game starts or stops recording, before voice chat's frame runs, and with no voice chat at all.
        /// </summary>
        private static void TheGamesRecordingReachesEveryClaimAtOnce()
        {
            // No voice chat, as before it starts or when it could not: the arbiter alone follows the game.
            var game = new FakeNativeRecording();
            var arbiter = new MultiplayerMicrophoneArbiter();
            var claims = new RobotDialogueMicrophone((use, reason) => Lease(arbiter, use, reason));
            That(claims.TryClaim().TryGetValue(out var unwatched) && !arbiter.NativeRecording, "until the plugin points it at the game, the game never records");
            unwatched!.Release();
            arbiter.ObserveNativeRecording(game.Read);
            game.Recording = true;
            var refused = claims.TryClaim();
            That(!refused.Succeeded && refused.ErrorCode == ModErrorCode.Conflict && arbiter.HoldsOf(InternalMicrophoneUse.RobotDialogue) == 0,
                "the game starts recording, and a conversation that asks at that moment is refused, with no voice chat frame between");
            game.Recording = false;
            That(claims.TryClaim().TryGetValue(out var claim), "the moment the game is done, a conversation may start");
            game.Recording = true;
            That(claim!.IsPreempted, "the game records again: the conversation's next check sees it at once");
            claim.Abandon();
            That(!claim.MayEndAbandoned, "and it may not end the device while the game records");
            game.Recording = false;
            That(claim.MayEndAbandoned, "the moment the game is done, the conversation may end what it left on the device");
            claim.Release();

            // A state that cannot be read counts as recording, for a conversation as for voice chat, until it reads again.
            game.Unreadable = true;
            var unreadable = claims.TryClaim();
            That(!unreadable.Succeeded && unreadable.ErrorCode == ModErrorCode.Conflict && arbiter.NativeUnreadable && arbiter.NativeRecording,
                "a game whose recording state cannot be read keeps a conversation off the microphone");
            game.Unreadable = false;
            That(claims.TryClaim().TryGetValue(out var readable) && !arbiter.NativeUnreadable, "readable again, a conversation may start");
            readable!.Release();

            // With voice chat: the mods' update runs before voice chat's tick in the plugin's frame, and its claim sees the game.
            var rig = VoiceRig.On();
            var robot = new RobotDialogueMicrophone((use, reason) => Lease(rig.Arbiter, use, reason));
            rig.Input.Hold(CapsLock);
            rig.Frame(3);
            That(rig.Microphone.IsOpen && rig.Voice.Transmitting, "Lee talks");
            rig.Native.Recording = true;
            var early = robot.TryClaim();
            That(!early.Succeeded && early.ErrorCode == ModErrorCode.Conflict && rig.Arbiter.HoldsOf(InternalMicrophoneUse.RobotDialogue) == 0,
                "in the frame the game starts recording, a conversation that asks before voice chat ticks is refused");
            rig.Frame();
            That(!rig.Microphone.IsOpen && rig.Microphone.Closes == 0 && rig.Runtime.Status == VoiceTalkStatus.MicBusy,
                "and voice chat lets go in that frame without ending the game's device");
            rig.Native.Recording = false;
            That(robot.TryClaim().TryGetValue(out var after), "in the frame the game is done, a conversation that asks before voice chat ticks may start");
            rig.Frame();
            That(!rig.Microphone.IsOpen && rig.Runtime.Status == VoiceTalkStatus.MicBusy, "and voice chat waits for the conversation");
            after!.Release();
            rig.Frame();
            That(rig.Microphone.IsOpen && rig.Voice.Transmitting, "then talks again");

            // Voice chat gone while the game records, as when the game closes: nothing it saw is left behind in the arbiter.
            rig.Native.Recording = true;
            rig.Frame();
            rig.Runtime.Dispose();
            rig.Native.Recording = false;
            That(robot.TryClaim().TryGetValue(out var late), "with voice chat gone, a conversation still follows the game, which is done");
            late!.Release();
        }

        /// <summary>
        /// Where the arbiter's reading of the game comes from, which no harness can run: the plugin points it at the game's
        /// state right after it creates the runtime, before any mod loads and whether voice chat starts or not, and the
        /// reading counts the game's decoding of what it heard as well as its recording.
        /// </summary>
        private static void NativeRecordingIsWatchedBeforeAnyModLoads(string root)
        {
            var plugin = PluginSourceSyntax.Read();
            var startup = plugin.Statements("Awake").OfType<TryStatementSyntax>().Single(attempt => attempt.Block.Statements.Any(CreatesRuntime)).Block.Statements;
            var created = IndexOf(startup, CreatesRuntime);
            var watched = IndexOf(startup, statement => PluginSourceSyntax.Calls(statement, "ObserveNativeRecording"));
            var loaded = IndexOf(startup, statement => PluginSourceSyntax.CallsOn(statement, "runtime", "Load"));
            That(created >= 0 && watched > created && loaded > watched,
                "the plugin points the arbiter at the game after creating the runtime and before mods load (" + created + ", " + watched + ", " + loaded + ")");
            var observes = plugin.Statements("ObserveNativeRecording").SelectMany(statement => statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                .Where(call => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "ObserveNativeRecording").ToArray();
            That(observes.Length == 1 && observes[0].ArgumentList.Arguments.Single().Expression.ToString() == "UnityVoiceNativeRecording.Read",
                "and gives it the game's own state to read");

            var path = Path.Combine(root, "src", "TopiaForge.ModManager", "Multiplayer", "Voice", "Unity", "UnityVoiceGame.cs");
            var reader = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(type => type.Identifier.ValueText == "UnityVoiceNativeRecording");
            var reads = reader.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(either => either.IsKind(SyntaxKind.LogicalOrExpression)).ToArray();
            That(reads.Length == 1 && ReadsState(reads[0], "Recording") && ReadsState(reads[0], "IsDecodingVoice"),
                "the game records from the push-to-record key until it has decoded what it heard: either state holds the microphone");
        }

        private static bool CreatesRuntime(StatementSyntax statement) =>
            statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: IdentifierNameSyntax target, Right: ObjectCreationExpressionSyntax made } } &&
            target.Identifier.ValueText == "runtime" && made.Type.ToString() == "ModRuntime";

        private static int IndexOf(SyntaxList<StatementSyntax> statements, Func<StatementSyntax, bool> match)
        {
            for (var index = 0; index < statements.Count; index++)
            {
                if (match(statements[index])) return index;
            }

            return -1;
        }

        private static bool ReadsState(SyntaxNode code, string state) =>
            code.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(access =>
                access.Name.Identifier.ValueText == state && access.Expression is IdentifierNameSyntax owner && owner.Identifier.ValueText == "UIState");
    }
}
