using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using System.Threading;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class VoiceChatTests
    {
        private const int MeasuredFrames = 2_000;

        private static void SettledFramesAllocateNothing()
        {
            var rig = VoiceRig.On();
            rig.Presenter.OnShown();
            rig.Frame(5);
            var idle = Seam.Allocated(() => rig.Frame(), MeasuredFrames);
            That(idle == 0, "an idle frame, voice on and VOICE showing, allocates nothing (" + idle + " bytes over " + MeasuredFrames + " frames)");

            rig.Input.Hold(CapsLock);
            rig.Voice.Talks(new ParticipantId("p2"));
            rig.Frame(5);
            var buffer = rig.Runtime.Capture.Buffer;
            var submitted = rig.Voice.SubmittedWhileTransmitting;
            That(rig.Microphone.IsOpen && rig.Voice.Transmitting && rig.Emitters.Playing == 1, "Lee talks while Sam's voice plays");
            var talking = Seam.Allocated(() => rig.Frame(), MeasuredFrames);
            That(talking == 0 && ReferenceEquals(buffer, rig.Runtime.Capture.Buffer) && rig.Voice.SubmittedWhileTransmitting > submitted,
                "a talking frame, the microphone read and handed over and a voice playing, allocates nothing (" + talking + " bytes)");

            var screen = (IVoiceScreen)rig.Presenter;
            var reading = Seam.Allocated(() => ReadScreen(screen), MeasuredFrames);
            That(reading == 0, "VOICE's getters, read every frame by its view, allocate nothing (" + reading + " bytes)");
        }

        /// <summary>Reads what the VOICE view binds each frame.</summary>
        private static int ReadScreen(IVoiceScreen screen)
        {
            var total = screen.StatusText.Length + (int)screen.StatusTone + (int)screen.Indicator + screen.SettingsNote.Length + screen.ConsentText.Length;
            total += screen.BindingText(0).Length + screen.BindingText(1).Length + screen.BindingNote.Length + screen.DeviceNote.Length + screen.TestNote.Length;
            total += screen.Mode + screen.Device + screen.DeviceOptions.Count + screen.CapturingSlot + (screen.VoiceOn ? 1 : 0) + (screen.CanTest ? 1 : 0);
            total += (int)screen.LevelDb + (int)screen.ThresholdDb + (int)screen.MicVolumePercent + (int)screen.VoiceVolumePercent;
            var players = screen.Players;
            for (var index = 0; index < players.Count; index++) total += (screen.IsMuted(players[index].Key) ? 1 : 0) + (screen.IsSpeaking(players[index].Key) ? 1 : 0);
            return total;
        }

        private static void TheAudioThreadsReadNeverWaits()
        {
            var reader = new VoiceEmitterReader();
            var data = new float[960];
            data[3] = 1f;
            reader.Read(data);
            That(data.All(sample => sample == 0f), "with no stream attached the audio thread reads silence");
            var stream = new FakePlaybackStream(new ParticipantId("p2"));
            reader.Attach(stream);
            reader.Read(data);
            That(data.All(sample => sample == 0.25f) && stream.SamplesRead == data.Length, "with one, it reads the stream for the whole buffer");
            var allocated = Seam.Allocated(() => reader.Read(data), 10_000);
            That(allocated == 0, "a read allocates nothing (" + allocated + " bytes)");

            // Its code takes no lock and waits for nothing: a volatile read, a clear, and the stream's own wait-free read.
            var read = typeof(VoiceEmitterReader).GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var scan = Scan(read);
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "Volatile.Read", "Array.Clear", "IVoicePlaybackStream.Read" };
            That(scan.Calls.Count >= 3 && scan.Calls.All(allowed.Contains) && scan.Allocations == 0,
                "the audio thread's read calls only " + string.Join(", ", allowed) + " and allocates nothing (" + string.Join(", ", scan.Calls) + ")");
            var attach = Scan(typeof(VoiceEmitterReader).GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)!);
            That(attach.Calls.SequenceEqual(new[] { "Volatile.Write" }) && attach.Allocations == 0, "handing over a stream is one volatile write");
        }

        /// <summary>Two writers of the launcher's preference journal in this game, as the display name's and voice chat's, take turns.</summary>
        private static void JournalWritersTakeTurns(string root)
        {
            var paths = new ManagerPaths(Path.Combine(root, "voice-journal", "BepInEx"));
            paths.EnsureCreated();
            using var committing = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holding = new LaunchStagingStore(paths, () =>
            {
                committing.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            });
            var plain = new LaunchStagingStore(paths);
            Exception? failure = null;
            var voice = new Thread(() =>
            {
                try
                {
                    holding.RecordPreferenceChange(sequence => MultiplayerPreferenceChange.Voice(sequence, 1, true));
                }
                catch (Exception error)
                {
                    failure = error;
                }
            });
            using var named = new ManualResetEventSlim();
            var name = new Thread(() =>
            {
                try
                {
                    plain.RecordPreferenceChange(sequence => MultiplayerPreferenceChange.DisplayName(sequence, "Robin"));
                }
                catch (Exception error)
                {
                    failure = error;
                }

                named.Set();
            });
            voice.Start();
            That(committing.Wait(TimeSpan.FromSeconds(10)), "voice chat's change reaches its commit");
            name.Start();
            That(!named.Wait(TimeSpan.FromMilliseconds(300)), "the name's change waits while voice chat's is being written");
            release.Set();
            That(voice.Join(TimeSpan.FromSeconds(10)) && name.Join(TimeSpan.FromSeconds(10)) && failure == null, "both finish (" + failure?.Message + ")");
            var journal = plain.ReadPreferencesJournal();
            That(journal != null && journal.Changes.Select(change => change.Kind).SequenceEqual(new[] { "voice", "displayName" }) &&
                 journal.Changes.Select(change => change.Sequence).SequenceEqual(new[] { 1, 2 }), "and the journal keeps both, in turn");
        }

        /// <summary>
        /// Voice chat's own code: Unity-free outside its Unity folder, no log anywhere, every file within the 400-line cap, and
        /// no member, parameter or type shaped like an address or carrying a network type, so nothing about a peer's network
        /// can pass through it to the page or a log.
        /// </summary>
        private static void VoiceChatStaysWithinItsBounds(string root)
        {
            var voice = Path.Combine(root, "src", "TopiaForge.ModManager", "Multiplayer", "Voice");
            var unityFree = Directory.EnumerateFiles(voice, "*.cs", SearchOption.TopDirectoryOnly).ToArray();
            var all = Directory.EnumerateFiles(voice, "*.cs", SearchOption.AllDirectories).ToArray();
            That(unityFree.Length >= 15 && all.Length > unityFree.Length, "the scan covers voice chat (" + unityFree.Length + " Unity-free files of " + all.Length + ")");
            var unity = new Regex(@"using\s+UnityEngine|\bUnityEngine\.", RegexOptions.CultureInvariant);
            var logging = new Regex(@"\b(?:Debug\.Log\w*|Console\.Write\w*|TopiaForgeLog\.\w+|[Ll]ogger\.\w+|ReportDiagnostic|LogInfo|LogWarn|LogError)\b", RegexOptions.CultureInvariant);
            foreach (var file in unityFree) That(!unity.IsMatch(File.ReadAllText(file)), Path.GetFileName(file) + " stays free of Unity");
            foreach (var file in all.Concat(Directory.EnumerateFiles(Path.Combine(root, "src", "TopiaForge.ModManager", "Overlay", "Multiplayer"), "MultiplayerVoice*.cs")))
            {
                var source = File.ReadAllText(file);
                var match = logging.Match(source);
                That(!match.Success, Path.GetFileName(file) + " writes no log (" + match.Value + ")");
                var lines = File.ReadLines(file).Count();
                That(lines <= MultiplayerUiArchitectureTests.UiFileCap, Path.GetFileName(file) + " stays within the 400-line cap (" + lines + " lines)");
            }

            var types = typeof(VoiceChatRuntime).Assembly.GetTypes().Where(type => type.Namespace == "TopiaForge.ModManager.Voice").ToArray();
            That(types.Length >= 20 && types.Contains(typeof(VoicePresenter)) && types.Contains(typeof(VoiceMuteBook)), "the scan covers voice chat's types (" + types.Length + ")");
            var words = new[] { "address", "endpoint", "hostname", "socket" };
            var offenders = new List<string>();
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var type in types)
            {
                foreach (var member in type.GetMembers(All))
                {
                    if (words.Any(word => member.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)) offenders.Add(type.Name + "." + member.Name);
                    var carried = member is FieldInfo field ? field.FieldType : member is PropertyInfo property ? property.PropertyType : member is MethodInfo method ? method.ReturnType : null;
                    if (carried != null && IsNetworkType(carried)) offenders.Add(type.Name + "." + member.Name + " (network type)");
                    if (!(member is MethodBase body)) continue;
                    foreach (var parameter in body.GetParameters())
                    {
                        if (IsNetworkType(parameter.ParameterType) || words.Any(word => (parameter.Name ?? string.Empty).IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            offenders.Add(type.Name + "." + member.Name + "(" + parameter.Name + ")");
                        }
                    }
                }
            }

            That(offenders.Count == 0, "nothing in voice chat is shaped like an address or carries a network type (" + string.Join(", ", offenders) + ")");
        }

        private static bool IsNetworkType(Type type)
        {
            var element = type.IsArray || type.IsByRef ? type.GetElementType()! : type;
            return element == typeof(IPAddress) || typeof(EndPoint).IsAssignableFrom(element) || element.Namespace == "System.Net.Sockets" || element == typeof(Socket);
        }

        /// <summary>The calls <paramref name="method"/>'s code makes, as Type.Method, and how many objects or arrays it creates or boxes.</summary>
        private static (List<string> Calls, int Allocations) Scan(MethodInfo method)
        {
            var il = method.GetMethodBody()!.GetILAsByteArray()!;
            var calls = new List<string>();
            var allocations = 0;
            var position = 0;
            while (position < il.Length)
            {
                int value = il[position++];
                if (value == 0xFE) value = 0xFE00 | il[position++];
                var code = OpCodesByValue[(short)value];
                var operand = position;
                position += OperandSize(code, il, position);
                if (code == OpCodes.Newobj || code == OpCodes.Newarr || code == OpCodes.Box) allocations++;
                if (code.OperandType != OperandType.InlineMethod) continue;
                var called = method.Module.ResolveMethod(BitConverter.ToInt32(il, operand))!;
                calls.Add(called.DeclaringType!.Name + "." + called.Name);
            }

            return (calls, allocations);
        }

        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetValue(null)).OfType<OpCode>().ToDictionary(code => code.Value);

        private static int OperandSize(OpCode code, byte[] il, int position)
        {
            switch (code.OperandType)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                case OperandType.InlineSwitch:
                    return 4 + (4 * BitConverter.ToInt32(il, position));
                default:
                    return 4;
            }
        }
    }
}
