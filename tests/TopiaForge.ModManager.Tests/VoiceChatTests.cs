using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.RobotKit;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Voice chat in the game without Unity: the microphone's gate and its consent, the arbitration with the game's own
    /// recording and robot conversations, the transmit state, the push-to-talk binding and its census, the preferences,
    /// the mutes, playback, the frames' allocations and the audio thread's read. Over fakes and the runtime's real
    /// microphone arbiter; the provider tests over the virtual network are the multiplayer harness's.
    /// </summary>
    internal static partial class VoiceChatTests
    {
        private static readonly InputBinding CapsLock = VoicePushToTalk.DefaultKey;
        private static readonly InputBinding Forward = VoicePushToTalk.DefaultMouseButton;

        internal static void Run(string root)
        {
            TheMicrophoneOpensOnlyAfterConsentWhileOnAndTalking();
            TheTestOpensTheMicrophoneAndSendsNothing();
            VoiceYieldsToTheGamesRecording();
            VoiceYieldsToARobotConversationThatYieldsToTheGame();
            AStateThatCannotBeReadKeepsTheMicrophoneClosed();
            TheGamesRecordingReachesEveryClaimAtOnce();
            NativeRecordingIsWatchedBeforeAnyModLoads(Program.FindRepoRoot());
            ConsentIsAskedInItsApprovedWording();
            WithdrawingConsentTurnsVoiceOffAndAsksAgain();
            OnlyConsentToTheCurrentWordingCounts();
            JournalWritersTakeTurns(root);
            PreferencesThatCannotBeKeptSaySo();
            TheTransmitStateReadsAsTextAndGlyph();
            PushToTalkNeverTakesAReservedControl();
            TheCensusReadsTheGamesBindings();
            BindingsFallBackWhenTheGameUsesThem();
            CaptureRefusesReservedAndTakenControls();
            EscapeCancelsTheCaptureAlone(Program.FindRepoRoot());
            TheGamesBindingsMoveTheDefaults();
            TheMicrophonesToChooseFrom();
            MutesFollowThePlayerKeyAcrossAJoin();
            PlayersAreMutedOnlyWhileTheSessionListsThem();
            SpeakersPlayUntilTheirStreamEnds();
            SettledFramesAllocateNothing();
            TheAudioThreadsReadNeverWaits();
            VoiceChatStaysWithinItsBounds(Program.FindRepoRoot());
            Console.WriteLine("VoiceChatTests passed.");
        }

        private static void TheMicrophoneOpensOnlyAfterConsentWhileOnAndTalking()
        {
            // Never agreed: a stored switch that is on without consent reads as off, however long push-to-talk is held.
            var never = new VoiceRig();
            never.Channel.Stored = new VoiceSwitch(0, true);
            never.Snapshot = VoiceRig.Session("s1", VoiceRig.Player("p2", "Sam", "key-sam"));
            never.Start();
            never.Input.Hold(CapsLock);
            never.Presenter.ToggleTest();
            never.Frame(30);
            That(!never.Presenter.VoiceOn && never.Microphone.Opens == 0 && !never.Voice.Enabled && !never.Presenter.Testing &&
                 never.Runtime.Status == VoiceTalkStatus.ConsentNeeded, "without consent nothing opens the microphone, the test included");

            // Agreed, but switched off.
            var off = new VoiceRig();
            off.Channel.Stored = VoiceSwitch.Agreed().WithEnabled(false);
            off.Snapshot = never.Snapshot;
            off.Start();
            off.Input.Hold(CapsLock);
            off.Frame(30);
            That(off.Microphone.Opens == 0 && !off.Voice.Enabled && off.Runtime.Status == VoiceTalkStatus.Off, "switched off, nothing opens either");

            // On: closed until push-to-talk is held, open while it is, closed at the release.
            var rig = VoiceRig.On();
            rig.Frame(10);
            That(rig.Voice.Enabled && rig.Microphone.Opens == 0 && rig.Runtime.Status == VoiceTalkStatus.Ready &&
                 rig.Presenter.StatusText == "Hold Caps Lock or Mouse Forward to talk.", "on, the microphone stays closed until the player talks");
            rig.Input.Hold(Forward);
            rig.Frame();
            That(rig.Microphone.IsOpen && rig.Voice.Transmitting, "holding Mouse Forward opens it and transmits");
            rig.Frame(5);
            var sent = rig.Voice.SubmittedWhileTransmitting;
            That(sent > 0 && rig.Voice.LastRate == 48_000, "what it hears goes to the voice service at its own rate");
            rig.Input.Release(Forward);
            rig.Frame();
            That(!rig.Microphone.IsOpen && !rig.Voice.Transmitting && rig.Microphone.Closes == 1 && rig.Voice.SubmittedWhileTransmitting > sent,
                "the release sends what was heard up to it, then closes the microphone");

            // Typing keys type: push-to-talk is ignored while a text field has the keyboard.
            rig.Input.IsTyping = true;
            rig.Input.Hold(CapsLock);
            rig.Frame(5);
            That(!rig.Microphone.IsOpen && !rig.Voice.Transmitting, "push-to-talk is ignored while the player types");
            rig.Input.IsTyping = false;
            rig.Input.Release(CapsLock);

            // Playing on one's own, nobody hears: nothing opens.
            rig.Control.Role = InternalSessionRole.Solo;
            rig.Input.Hold(CapsLock);
            rig.Frame(5);
            That(!rig.Microphone.IsOpen && rig.Runtime.Status == VoiceTalkStatus.NotInSession, "in solo play push-to-talk opens nothing");
            rig.Control.Role = InternalSessionRole.Client;
            rig.Input.Release(CapsLock);

            // Voice activation listens while the session can hear the player, and closes while nobody can.
            rig.Presenter.SetMode(1);
            rig.Frame(2);
            That(rig.Microphone.IsOpen && rig.Voice.Mode == InternalVoiceTransmitMode.VoiceActivation && rig.Voice.Transmitting &&
                 rig.Presenter.StatusText == VoiceText.Listening, "voice activation keeps the microphone open in a session, and listens");
            rig.Voice.Speech = true;
            rig.Frame();
            That(rig.Presenter.StatusText == VoiceText.Talking && rig.Presenter.Indicator == VoiceIndicator.Talking, "speech above the level reads as talking");
            rig.Voice.Speech = false;
            rig.Voice.State = InternalVoiceTransmitState.NoListeners;
            rig.Frame(2);
            That(!rig.Microphone.IsOpen && rig.Runtime.Status == VoiceTalkStatus.NoListeners, "and closes it while nobody else can hear");
            rig.Voice.State = InternalVoiceTransmitState.Idle;
            rig.Frame(2);
            That(rig.Microphone.IsOpen, "opening again once someone can");

            // Off again: the microphone closes in the same frame, and the switch goes to the launcher.
            rig.Presenter.SetVoiceOn(false);
            rig.Frame();
            That(!rig.Microphone.IsOpen && !rig.Voice.Enabled && rig.Channel.Records.Last().Equals(new VoiceSwitch(MultiplayerPrivacyText.VoiceConsentVersion, false)),
                "switched off, it closes at once, and the launcher keeps the consent with the switch off");

            // Back on: the frame voice turns on, the service's state is a pump old, so nothing opens until the next.
            rig.Presenter.SetVoiceOn(true);
            rig.Presenter.Tick(VoiceRig.FrameSeconds);
            rig.Runtime.Tick(VoiceRig.FrameSeconds);
            That(!rig.Microphone.IsOpen && rig.Voice.Enabled, "the frame voice turns on, the microphone stays closed");
            rig.Frame();
            That(rig.Microphone.IsOpen, "and opens at the next");
        }

        private static void TheTestOpensTheMicrophoneAndSendsNothing()
        {
            var rig = VoiceRig.On(InternalSessionRole.Solo);
            rig.Presenter.ToggleTest();
            rig.Frame(3);
            That(rig.Presenter.Testing && rig.Microphone.IsOpen && !rig.Voice.Transmitting && rig.Voice.Submitted > 0 &&
                 rig.Voice.SubmittedWhileTransmitting == 0 && rig.Runtime.Status == VoiceTalkStatus.Testing && rig.Presenter.TestNote == VoiceText.TestRunning,
                "the test opens the microphone, even in solo play, and only meters what it hears");
            rig.Voice.InputLevelDb = -20f;
            That(rig.Presenter.LevelDb == -20f, "the meter shows the service's level while the microphone is read");
            rig.Presenter.ToggleTest();
            rig.Frame();
            That(!rig.Presenter.Testing && !rig.Microphone.IsOpen && rig.Presenter.LevelDb == VoiceChatRuntime.SilenceDb, "stopping it closes the microphone and the meter falls silent");
            rig.Presenter.ToggleTest();
            rig.Frame();
            rig.Presenter.OnHidden();
            rig.Frame();
            That(!rig.Presenter.Testing && !rig.Microphone.IsOpen, "leaving VOICE stops the test");
            rig.Presenter.ToggleTest();
            rig.Presenter.SetVoiceOn(false);
            rig.Frame();
            That(!rig.Presenter.Testing && !rig.Microphone.IsOpen && !rig.Presenter.CanTest && rig.Presenter.TestNote == VoiceText.TestOff, "and so does turning voice chat off");
        }

        private static void VoiceYieldsToTheGamesRecording()
        {
            var rig = VoiceRig.On();
            rig.Input.Hold(CapsLock);
            rig.Frame(3);
            That(rig.Microphone.IsOpen && rig.Voice.Transmitting && rig.Arbiter.HoldsOf(InternalMicrophoneUse.VoiceChat) == 1, "Lee talks, holding the microphone for voice chat");

            rig.Native.Recording = true;
            var read = rig.Microphone.SamplesRead;
            rig.Frame();
            That(rig.Arbiter.NativeRecording && !rig.Microphone.IsOpen && rig.Microphone.Abandons == 1 && rig.Microphone.Closes == 0 && rig.Microphone.Recording,
                "the game starts recording: voice chat lets go at once, and never ends the device the game records on");
            That(!rig.Voice.Transmitting && rig.Arbiter.HoldsOf(InternalMicrophoneUse.VoiceChat) == 0 && rig.Runtime.Status == VoiceTalkStatus.MicBusy &&
                 rig.Presenter.Indicator == VoiceIndicator.MicBusy && rig.Presenter.StatusText == VoiceText.MicBusy, "it stops transmitting and says why");
            rig.Frame(30);
            That(rig.Microphone.SamplesRead == read && rig.Microphone.Opens == 1 && rig.Microphone.EndedAbandoned == 0,
                "nothing is read, reopened or ended while the game records, though push-to-talk is still held");

            rig.Native.Recording = false;
            rig.Frame();
            That(rig.Microphone.EndedAbandoned == 1 && rig.Microphone.IsOpen && rig.Microphone.Opens == 2 && rig.Voice.Transmitting,
                "once the game is done, voice chat ends what it left behind and opens again");
        }

        private static void VoiceYieldsToARobotConversationThatYieldsToTheGame()
        {
            var rig = VoiceRig.On();
            var claims = new RobotDialogueMicrophone((use, reason) => Lease(rig.Arbiter, use, reason));
            rig.Input.Hold(CapsLock);
            rig.Frame(3);
            That(claims.TryClaim().TryGetValue(out var claim), "a robot conversation claims the microphone while voice chat has it");
            rig.Frame();
            That(!rig.Microphone.IsOpen && rig.Microphone.Abandons == 1 && rig.Microphone.Closes == 0 && rig.Runtime.Status == VoiceTalkStatus.MicBusy &&
                 !rig.Voice.Transmitting, "voice chat yields to it at once, without ending the device");
            claim!.Release();
            rig.Frame();
            That(rig.Microphone.IsOpen && rig.Microphone.EndedAbandoned == 1 && rig.Voice.Transmitting, "and takes the microphone back when the conversation is done");

            // While the game records, a conversation cannot start.
            rig.Native.Recording = true;
            rig.Frame();
            var refused = claims.TryClaim();
            That(!refused.Succeeded && refused.ErrorCode == ModErrorCode.Conflict && rig.Arbiter.HoldsOf(InternalMicrophoneUse.RobotDialogue) == 0,
                "a conversation is refused while the game records (" + refused.ErrorCode + ")");
            rig.Native.Recording = false;
            rig.Frame();

            // The game overtakes a conversation: the conversation must neither read nor end the device until the game is done.
            That(claims.TryClaim().TryGetValue(out var overtaken), "a second conversation claims the microphone");
            rig.Native.Recording = true;
            rig.Frame();
            That(overtaken!.IsPreempted && !overtaken.MayEndAbandoned, "the game's recording overtakes it");
            overtaken.Abandon();
            That(!overtaken.MayEndAbandoned, "an abandoned conversation may not end the device while the game records");
            rig.Native.Recording = false;
            rig.Frame();
            That(overtaken.MayEndAbandoned && !rig.Microphone.IsOpen && rig.Runtime.Status == VoiceTalkStatus.MicBusy,
                "once the game is done it may end its own recording, and voice chat still waits for it");
            overtaken.Release();
            overtaken.Release();
            rig.Frame();
            That(rig.Microphone.IsOpen && rig.Arbiter.HoldsOf(InternalMicrophoneUse.RobotDialogue) == 0, "released once, however often asked, voice chat goes on");

            // A context without the runtime's seam claims nothing and refuses nothing.
            That(new RobotDialogueMicrophone(null).TryClaim().TryGetValue(out var free) && !free!.IsPreempted, "without the runtime's seam a claim always succeeds");
        }

        private static void AStateThatCannotBeReadKeepsTheMicrophoneClosed()
        {
            var rig = VoiceRig.On();
            rig.Native.Unreadable = true;
            rig.Input.Hold(CapsLock);
            rig.Frame(5);
            That(rig.Microphone.Opens == 0 && rig.Arbiter.NativeRecording && rig.Runtime.Status == VoiceTalkStatus.CannotCheckGame &&
                 rig.Presenter.Indicator == VoiceIndicator.MicBusy && rig.Presenter.StatusTone == ScreenTone.Danger,
                "a game whose recording state cannot be read counts as recording: the microphone stays closed and the page says why");
            rig.Native.Unreadable = false;
            rig.Frame();
            That(rig.Microphone.IsOpen && !rig.Arbiter.NativeRecording, "readable again, push-to-talk works");
        }

        private static void ConsentIsAskedInItsApprovedWording()
        {
            var rig = new VoiceRig();
            rig.Snapshot = VoiceRig.Session("s1", VoiceRig.Player("p2", "Sam", "key-sam"));
            rig.Start();
            That(!rig.Presenter.VoiceOn && rig.Presenter.ConsentQuestion == null && rig.Presenter.Indicator == VoiceIndicator.Muted &&
                 rig.Presenter.StatusText == VoiceText.ConsentNeeded && rig.Presenter.ConsentText == VoiceText.ConsentAsked && !rig.Presenter.CanWithdrawConsent,
                "voice chat starts off, needing consent");
            rig.Presenter.SetVoiceOn(true);
            var question = rig.Presenter.ConsentQuestion;
            That(question != null && question.Title == "Voice chat" && question.Body == MultiplayerPrivacyText.VoiceBody &&
                 question.AcceptLabel == "Turn on voice chat" && question.DeclineLabel == "Not now" && !rig.Presenter.VoiceOn,
                "turning it on asks first, in the approved wording, word for word");
            rig.Presenter.SetVoiceOn(true);
            That(ReferenceEquals(rig.Presenter.ConsentQuestion, question), "asked again while it waits, it is the same question");
            var dialog = new RecordingDialog();
            question!.Shown(dialog);
            rig.Input.Hold(CapsLock);
            rig.Frame(10);
            That(rig.Microphone.Opens == 0 && !rig.Voice.Enabled, "nothing opens while the question waits");

            question.Answer(false);
            question.Answer(true);
            rig.Frame();
            That(dialog.Closes == 1 && !rig.Presenter.VoiceOn && rig.Channel.Records.Count == 0 && rig.Presenter.ConsentQuestion == null &&
                 LastNotice(rig) == VoiceText.StaysOff && rig.Microphone.Opens == 0, "Not now leaves voice off, records nothing, and only the first answer counts");

            rig.Presenter.SetVoiceOn(true);
            var again = rig.Presenter.ConsentQuestion;
            That(again != null && !ReferenceEquals(again, question), "turning it on again asks again");
            again!.Answer(true);
            rig.Frame();
            That(rig.Presenter.VoiceOn && rig.Channel.Records.Last().Equals(VoiceSwitch.Agreed()) && rig.Voice.Enabled &&
                 rig.Channel.Records.Last().Consent == MultiplayerPrivacyText.VoiceConsentVersion, "Turn on voice chat records consent to this wording, and voice goes on");
            rig.Frame();
            That(rig.Microphone.IsOpen, "with push-to-talk held, the microphone opens now");

            // A question nobody answers is withdrawn as the game closes, answering no and closing its dialog.
            var closing = new VoiceRig();
            closing.Start();
            closing.Presenter.SetVoiceOn(true);
            var waiting = closing.Presenter.ConsentQuestion!;
            var shown = new RecordingDialog();
            waiting.Shown(shown);
            closing.Presenter.Stop();
            That(waiting.IsAnswered && shown.Closes == 1 && !closing.Presenter.VoiceOn && closing.Channel.Records.Count == 0, "a question withdrawn answers no");
            var late = new RecordingDialog();
            waiting.Shown(late);
            That(late.Closes == 1, "and a dialog opened for it afterwards closes at once");
        }

        private static void WithdrawingConsentTurnsVoiceOffAndAsksAgain()
        {
            var rig = VoiceRig.On();
            That(rig.Presenter.CanWithdrawConsent && rig.Presenter.ConsentText == VoiceText.ConsentGiven, "consent given, it can be withdrawn");
            rig.Input.Hold(CapsLock);
            rig.Frame(3);
            rig.Presenter.WithdrawConsent();
            rig.Frame();
            That(!rig.Microphone.IsOpen && !rig.Voice.Enabled && rig.Channel.Records.Last().Equals(VoiceSwitch.Off) && !rig.Presenter.VoiceOn &&
                 rig.Presenter.ConsentText == VoiceText.ConsentAsked && !rig.Presenter.CanWithdrawConsent && LastNotice(rig) == VoiceText.Withdrawn,
                "withdrawing closes the microphone in that frame, and the launcher is told no consent");
            rig.Presenter.SetVoiceOn(true);
            That(rig.Presenter.ConsentQuestion != null && !rig.Presenter.VoiceOn, "turning it on asks again");
        }

        private static void OnlyConsentToTheCurrentWordingCounts()
        {
            var version = MultiplayerPrivacyText.VoiceConsentVersion;
            That(!new VoiceSwitch(version - 1, true).IsOn && new VoiceSwitch(version, true).IsOn && new VoiceSwitch(version + 1, true).IsOn,
                "consent to an older wording counts for nothing; to this one, or a newer, it counts");
            That(!new VoiceSwitch(version - 1, false).WithEnabled(true).IsOn && !VoiceSwitch.Off.WithEnabled(true).Enabled,
                "the switch cannot go on without consent");
            That(VoiceSwitch.Withdrawn().Equals(VoiceSwitch.Off) && VoiceSwitch.Agreed().Consent == version && new VoiceSwitch(-3, false).Consent == 0,
                "withdrawn is no consent, and agreeing agrees to this wording");

            var epoch = new string('a', 32);
            var hosting = new MultiplayerHostingDefaults("Lee's server", 8, "lan", 27250, false);
            var projection = new MultiplayerPreferencesProjection(3, 1, "Lee", 1, version, true, true, false, hosting, null, epoch);
            That(VoiceSwitch.From(projection, null).IsOn && !VoiceSwitch.From(null, null).IsOn, "the launcher's projection is read as it is; with none, voice is off");
            var withdrawn = new MultiplayerPreferencesJournal(epoch, new[]
            {
                MultiplayerPreferenceChange.Voice(1, version, true),
                MultiplayerPreferenceChange.DisplayName(2, "Robin"),
                MultiplayerPreferenceChange.Voice(3, 0, false)
            });
            That(!VoiceSwitch.From(projection, withdrawn).HasConsent, "a withdrawal the launcher has not applied yet counts at once");
            var applied = new MultiplayerPreferencesProjection(3, 3, "Lee", 1, version, true, true, false, hosting, null, epoch);
            That(VoiceSwitch.From(applied, withdrawn).IsOn, "changes the projection already counts are not applied twice");
            var other = new MultiplayerPreferencesJournal(new string('b', 32), new[] { MultiplayerPreferenceChange.Voice(1, 0, false) });
            That(!VoiceSwitch.From(applied, other).IsOn, "a journal under another epoch applies whole");
        }

        private static void PreferencesThatCannotBeKeptSaySo()
        {
            // Settings that cannot be read last for this session only, and are never written over.
            var unreadable = new VoiceRig();
            unreadable.Store.FailLoad = true;
            unreadable.Start();
            That(unreadable.Presenter.IsLoaded && LastNotice(unreadable) == VoicePreferences.SettingsReadFailed, "settings that cannot be read say so");
            unreadable.Presenter.SetMode(1);
            unreadable.Frame();
            That(unreadable.Store.Saves == 0 && unreadable.Presenter.Mode == 1, "a change applies for this session and writes nothing over the file");

            // A launcher channel that cannot be read leaves voice off: the safe direction.
            var noChannel = new VoiceRig();
            noChannel.Channel.Stored = VoiceSwitch.Agreed();
            noChannel.Channel.FailRead = true;
            noChannel.Start();
            That(noChannel.Presenter.IsLoaded && !noChannel.Presenter.VoiceOn, "a switch that cannot be read is off");

            // A switch that cannot be recorded is tried again every five seconds until it is.
            var flaky = VoiceRig.On();
            flaky.Channel.FailRecord = true;
            flaky.Presenter.WithdrawConsent();
            flaky.Frame();
            That(flaky.Preferences.SwitchUnsaved && !flaky.Presenter.VoiceOn && !flaky.Voice.Enabled && LastNotice(flaky) == VoicePreferences.SwitchRecordFailed &&
                 flaky.Presenter.SettingsNote == VoiceText.SwitchUnsaved, "the switch goes off at once, and the page says it is not saved yet");
            flaky.Channel.FailRecord = false;
            flaky.Frame((int)(4f / VoiceRig.FrameSeconds));
            That(flaky.Channel.Records.Count == 0 && flaky.Preferences.SwitchUnsaved, "not before five seconds");
            flaky.Frame((int)(1.5f / VoiceRig.FrameSeconds));
            That(flaky.Channel.Records.Count == 1 && flaky.Channel.Records[0].Equals(VoiceSwitch.Off) && !flaky.Preferences.SwitchUnsaved &&
                 flaky.Presenter.SettingsNote.Length == 0, "then the withdrawal reaches the launcher");

            // Settings are written after each change, the newest once the one in flight finishes.
            var saving = VoiceRig.On();
            var saves = saving.Store.Saves;
            saving.Presenter.SetThresholdDb(-30f);
            saving.Presenter.SetMicVolumePercent(150f);
            saving.Frame();
            var stored = saving.Store.Stored!;
            That(saving.Store.Saves > saves && stored.ThresholdDb == -30f && stored.MicVolume == 1.5f, "settings are written as they change");
            var document = VoiceSettings.FromDocument(stored.ToDocument());
            That(document.ThresholdDb == -30f && document.MicVolume == 1.5f && document.Mode == stored.Mode &&
                 VoicePushToTalk.Same(document.PushToTalk[0]!.Value, CapsLock), "and read back as written");
            Throws<System.Runtime.Serialization.SerializationException>(
                () => VoiceSettings.FromDocument(new VoiceSettingsDocument { SchemaVersionValue = 2 }), "a newer game's settings are refused, never read wrong");
            That(VoiceSettings.FromDocument(new VoiceSettingsDocument { PushToTalk = new List<string> { "key:V", "mouse:Nope" }, ThresholdDb = float.NaN }).ThresholdDb ==
                 VoiceSettings.DefaultThresholdDb, "a value that is no number takes its default");
        }

        private static void TheTransmitStateReadsAsTextAndGlyph()
        {
            var rig = VoiceRig.On();
            Expect(rig, VoiceTalkStatus.Ready, VoiceIndicator.Idle, ScreenTone.Success, "Hold Caps Lock or Mouse Forward to talk.");
            rig.Input.Hold(CapsLock);
            rig.Frame(2);
            Expect(rig, VoiceTalkStatus.Talking, VoiceIndicator.Talking, ScreenTone.Success, VoiceText.Talking);
            rig.Voice.State = InternalVoiceTransmitState.MutedByHost;
            rig.Frame();
            Expect(rig, VoiceTalkStatus.MutedByHost, VoiceIndicator.MutedByHost, ScreenTone.Warning, VoiceText.MutedByHost);
            That(!rig.Microphone.IsOpen, "muted by the host, the microphone closes");
            rig.Voice.State = InternalVoiceTransmitState.Idle;
            rig.Native.Recording = true;
            rig.Frame();
            Expect(rig, VoiceTalkStatus.MicBusy, VoiceIndicator.MicBusy, ScreenTone.Warning, VoiceText.MicBusy);
            rig.Native.Recording = false;
            rig.Microphone.Devices.Clear();
            rig.Frame(2);
            Expect(rig, VoiceTalkStatus.NoMicrophone, VoiceIndicator.NoMic, ScreenTone.Warning, VoiceText.NoMicrophone);
            rig.Microphone.Devices.Add("USB Microphone");
            rig.Microphone.Answer = VoiceMicrophoneOpen.Failed;
            rig.Frame((int)(2.5f / VoiceRig.FrameSeconds));
            Expect(rig, VoiceTalkStatus.MicrophoneFailed, VoiceIndicator.NoMic, ScreenTone.Danger, VoiceText.MicrophoneFailed);
            rig.Microphone.Answer = VoiceMicrophoneOpen.Opened;
            rig.Frame((int)(2.5f / VoiceRig.FrameSeconds));
            Expect(rig, VoiceTalkStatus.Talking, VoiceIndicator.Talking, ScreenTone.Success, VoiceText.Talking);
            rig.Input.Release(CapsLock);
            rig.Voice.State = InternalVoiceTransmitState.NotConnected;
            rig.Frame();
            Expect(rig, VoiceTalkStatus.Connecting, VoiceIndicator.Idle, ScreenTone.Muted, VoiceText.Connecting);
            rig.Voice.State = InternalVoiceTransmitState.NotOffered;
            rig.Frame();
            Expect(rig, VoiceTalkStatus.NotOffered, VoiceIndicator.Muted, ScreenTone.Muted, VoiceText.NotOffered);
            rig.Voice.State = InternalVoiceTransmitState.Failed;
            rig.Frame();
            Expect(rig, VoiceTalkStatus.Failed, VoiceIndicator.Muted, ScreenTone.Danger, VoiceText.Failed);
            rig.Voice.State = InternalVoiceTransmitState.Idle;
            rig.Presenter.SetVoiceOn(false);
            rig.Frame();
            Expect(rig, VoiceTalkStatus.Off, VoiceIndicator.Muted, ScreenTone.Muted, VoiceText.Off);
            rig.Control.Voice = null;
            rig.Frame();
            Expect(rig, VoiceTalkStatus.Unavailable, VoiceIndicator.Muted, ScreenTone.Muted, VoiceText.Unavailable);
            That(!rig.Presenter.CanSwitch && !rig.Presenter.CanTest, "without a voice service nothing can be switched or tested");
        }

        private static void Expect(VoiceRig rig, VoiceTalkStatus status, VoiceIndicator indicator, ScreenTone tone, string text)
        {
            That(rig.Runtime.Status == status && rig.Presenter.Indicator == indicator && rig.Presenter.StatusTone == tone && rig.Presenter.StatusText == text,
                status + " reads as " + indicator + " and \"" + text + "\" (" + rig.Runtime.Status + ", " + rig.Presenter.Indicator + ", " +
                rig.Presenter.StatusTone + ", \"" + rig.Presenter.StatusText + "\")");
        }

        /// <summary>Takes the presenter's notices and gives the last, or empty when it had none.</summary>
        private static string LastNotice(VoiceRig rig)
        {
            var last = string.Empty;
            while (rig.Presenter.TryTakeNotice(out var notice)) last = notice.Text;
            return last;
        }

        private static OperationResult<IInternalMicrophoneLease> Lease(MultiplayerMicrophoneArbiter arbiter, InternalMicrophoneUse use, string reason)
        {
            var taken = arbiter.TryAcquire(use, reason);
            return taken.TryGetValue(out var hold)
                ? OperationResult<IInternalMicrophoneLease>.Success(new HoldLease(hold))
                : OperationResult<IInternalMicrophoneLease>.Failure(taken.ErrorCode, taken.ErrorMessage);
        }

        /// <summary>The lease the runtime's replication seam hands out, over one hold of the arbiter.</summary>
        private sealed class HoldLease : IInternalMicrophoneLease
        {
            private readonly MultiplayerMicrophoneArbiter.Hold hold;

            internal HoldLease(MultiplayerMicrophoneArbiter.Hold hold)
            {
                this.hold = hold;
            }

            public InternalMicrophoneUse Use => hold.Use;

            public bool IsPreempted => hold.IsPreempted;

            public bool IsReleased => hold.IsReleased;

            public void Dispose() => hold.Release();
        }

        private sealed class RecordingDialog : IPromptDialog
        {
            internal int Closes { get; private set; }

            public void Close() => Closes++;
        }
    }
}
