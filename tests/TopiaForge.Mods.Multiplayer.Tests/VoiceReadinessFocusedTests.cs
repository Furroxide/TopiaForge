extern alias provider;

using provider::TopiaForge.Multiplayer.Engine;
using TopiaForge.Mods;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Readiness-sensitive voice fixtures, raw protocol security, mutes and session isolation.</summary>
internal static class VoiceReadinessFocusedTests
{
    internal static void Run()
    {
        RateLimitedJoinKeepsItsFiveStepBudget();
        AManualJoinStillHonorsReadinessHolds();
        VoiceSessionTests.SpeakersAreStampedByTheHost();
        VoiceSessionTests.AClientCannotSpeakAsSomeoneElse();
        VoiceSessionTests.AClientRefusesFramesItsRosterDoesNotExplain();
        VoiceSessionTests.FloodingMutesThenRemovesTheSpeaker();
        VoiceSessionTests.MutesStopTheRelay();
        VoiceSessionTests.AnEngineWithoutVoiceSendsNothingForIt();
        VoiceSessionTests.ARestartedClientIsHeardAtOnce();
        VoiceSessionTests.VoiceStartsAfreshAcrossRoleChanges();
        VoiceModerationTests.HostStallsStrikeNobody();
        VoiceModerationTests.ModerationOutlivesTheGracePeriod();
        VoiceModerationTests.TheModerationTableIsBounded();
        VoiceModerationTests.ListenerMutesStayWithTheirSession();
        VoiceModerationTests.AShortStreamRestartsCleanly();
        VoiceBudgetTests.FourSpeakersFitTheirBudget();
        VoiceBudgetTests.VoiceIsNeverHeldBehindTheSessionStream();
        VoiceBudgetTests.FramesShareDatagramsInPairs();
        VoiceBudgetTests.AStalledTalkerIsPacedNotStruck();
        VoiceBudgetTests.TheReceiveQueueIsBounded();
        VoiceBudgetTests.VoiceKeepsItsPaceOnARateLimitedLink();
        VoiceModerationTests.ABitrateFloodIsStruckAndMuted();
        VoiceModerationTests.ListenerMutesStayBehindWhenTheLinkMovesToAnotherSession();
        VoiceModerationTests.AFullTableForgetsWhoeverWasSeenLongestAgo();
        VoiceRestartTests.ARestartIsHeardFromTheDatagramBesideItsAcknowledgement();
        VoiceRestartTests.ARestartIsNoticedWhenItsFirstDatagramIsLost();
        VoiceMuteTests.JoinAcceptedSaysWhetherTheHostKeptTheRecord();
        VoiceMuteTests.MutesSurviveAnInPlaceReplacementHoweverTheClientResynchronizes();
        VoiceMuteTests.MutesStayBehindWhenTheRecordExpired();
        VoiceMuteTests.MutesMadeWithOneHostNeverReachAnother();
        VoiceMuteTests.AMuteSetMadeWhileJoiningAppliesToTheSessionTheJoinFinds();
        VoiceBudgetTests.AVoiceOverloadIsShedAndTheSessionLaneKeepsItsPace();
        VoiceBudgetTests.VoiceWithinTheRateIsNeverShed();
        VoiceServiceTests.SpeechReachesEveryListenerWithBoundedDelay();
        VoiceServiceTests.VoiceFitsTheTransportShares();
        VoiceServiceTests.ALateListenerHearsTheSpeakerAsTheyAreNow();
        VoiceServiceTests.NobodyToHearMeansNothingIsCoded();
        VoiceServiceTests.AReconnectOrRestartStartsTheSpeakerAfresh();
        VoiceServiceTests.MutesStopASpeakerAtOnce();
        VoiceProviderTests.TheLiveSwitchTurnsVoiceOn();
        VoiceProviderTests.TheWorkerCodesOnItsOwnThread();
        VoiceProviderTests.TheLossHintFollowsTheTransport();
        VoiceServiceTests.AReturningSpeakerIsHeardBeforeTheSessionSaysSo();
        VoiceSpeakerBoundTests.AHostileRosterCannotGrowTheStreams();
        VoiceSpeakerBoundTests.VoiceBeforeItsSpeakerIsListedIsKeptForAGrace();
        VoiceStallTests.StallsLeaveNoLastingDelay();
        VoiceStallTests.APausedReaderHearsNothingStaleWhenItResumes();
        VoiceServiceTests.NothingLeavesUntilVoiceIsTurnedOn();
        VoiceServiceTests.AHostCodesOnlyForReadyListeners();
        VoiceMuteTests.AWishToHearNobodyMadeWithNoLinkHoldsWhereverTheJoinGoes();
        VoiceMuteTests.NamesGivenBeforeTheNewHostListsItsPlayersNameNobodyThere();
        VoiceMuteTests.NamesFromARestartedHostsOldListNameNobody();
        VoiceServiceTests.AWorkerThatGivesUpLeavesNothingQueued();
        VoiceServiceTests.TheIndicatorAndStatisticsBelongToTheMainThread();
        VoiceProviderTests.EveryEngineDiagnosticHasACodeOfItsOwn();
        VoiceMuteTests.MutesFromBeforeARestartStayBehindWhenItsFirstAnswerIsLost();
        VoiceMuteTests.AJoinReplacedBeforeItsRequestLeftKeepsTheMutes();
        VoiceMuteTests.NamesGivenBeforeTheSameHostListsAgainStay();
        VoiceOfferTests.ASessionWithoutVoiceCarriesNone();
        VoiceOfferTests.ATalkerPastItsOwnGateIsDroppedOnArrivalWithoutAStrike();
        VoiceOfferTests.AHostileHostIsNeverPlayed();
        VoiceOfferTests.AJoinShownWithoutVoiceStaysWithoutIt();
        VoiceOfferTests.EachHostingRunAppliesItsOwnSetting();
        VoiceOfferTests.JoinAcceptedSaysWhenTheSessionOffersNoVoice();
        Console.WriteLine("Adjacent voice readiness: 64 tests passed.");
    }

    private static void RateLimitedJoinKeepsItsFiveStepBudget()
    {
        using var rig = new VoiceServiceRig(new VirtualLinkConditions
        {
            LatencyTicks = 1,
            TicksPerSecond = 100,
            BandwidthBytesPerSecond = 64_000
        });
        var host = rig.AddHost();
        var client = rig.AddClient("a");
        Assert(rig.Tick == 5 && rig.NowMicros == 1_050_000 && rig.Network.Now == 5,
            "a rate-limited join uses exactly the original five transport and audio steps");
        Assert(client.Engine.Snapshot.State == MultiplayerSessionState.Ready && host.Engine.ConnectionsAwaitingReady == 1,
            "the client completes its local turn while its real acknowledgement still travels to the host");
    }

    private static void AManualJoinStillHonorsReadinessHolds()
    {
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        var client = rig.AddClient("held", untilReady: false);
        Assert(rig.Tick == 0 && client.Engine.Snapshot.State == MultiplayerSessionState.Connecting,
            "a manual join returns before any fixture pump");
        using var hold = client.Engine.HoldReadiness();
        rig.Step(5);
        client.Engine.Pump(rig.NowMicros - VoiceServiceRig.StepMicros);
        Assert(client.Engine.Snapshot.State == MultiplayerSessionState.Synchronizing && client.Engine.IsAwaitingLocalReadiness &&
               host.Engine.ConnectionsAwaitingReady == 1,
            "a same-timestamp pump cannot cross a local readiness hold or acknowledge it to the host");
        hold.Dispose();
        Assert(client.Engine.Snapshot.State == MultiplayerSessionState.Ready,
            "only releasing the hold permits local readiness");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
