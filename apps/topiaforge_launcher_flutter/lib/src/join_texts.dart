import 'package:launcher_domain/launcher_domain.dart';

import 'join_address_redaction.dart';

export 'join_address_redaction.dart';

/// What a failure card offers beside Retry and Copy details.
enum JoinFailureAction {
  none,

  /// "Allow a direct connection…": asks the approved prompt, then joins
  /// again with a direct connection for this session.
  allowDirect,

  /// Trust the server a typed address now answers as, and join it.
  trust,

  /// Repair the TopiaForge runtime, then join again.
  repair,
}

/// A failure card's words: a title, what happened, what to do next, and
/// whether trying again can help.
final class JoinFailureText {
  const JoinFailureText({
    required this.title,
    required this.sentence,
    this.nextStep,
    this.retry = false,
    this.action = JoinFailureAction.none,
  });

  final String title;

  /// What happened. The failure's own detail stands in its place when it has
  /// one: the data layer and the service word theirs for people.
  final String sentence;

  /// What to do next, when the failure's detail does not already say it.
  final String? nextStep;

  /// Whether Retry is offered: only where trying again can help.
  final bool retry;
  final JoinFailureAction action;

  /// The words for [failure]. [startedGame] says whether the join had
  /// already handed itself to the game, which then goes on by itself.
  static JoinFailureText of(JoinFailure failure, {bool startedGame = false}) {
    final text = _texts[failure.code]!;
    if (failure.code == JoinFailureCode.installFailed &&
        failure.rollbackComplete == false) {
      return const JoinFailureText(
        title: 'The install did not finish',
        sentence:
            'The install stopped part way, and not everything it changed '
            'could be put back yet.',
        nextStep:
            'Restart the launcher before you play: it puts back what '
            'the install left. Then join again.',
      );
    }
    if (failure.code == JoinFailureCode.cancelled && startedGame) {
      return const JoinFailureText(
        title: 'Join cancelled',
        sentence:
            'You stopped following the join here, but Robotopia had '
            'already started it and may still enter the server.',
        nextStep:
            'If it does and you want to leave, leave the session in '
            'Robotopia.',
        retry: true,
      );
    }
    return text;
  }

  /// The card's body for [failure]: its own detail, without addresses, or
  /// this text's sentence when it has none.
  String bodyFor(JoinFailure failure) {
    final detail = withoutAddresses(failure.detail.trim());
    return detail.isEmpty ? sentence : detail;
  }
}

/// The steps the rail shows, in order, with their names.
const joinSteps = <(JoinStage, String)>[
  (JoinStage.resolving, 'Finding the server'),
  (JoinStage.fetchingDescriptor, 'Checking the server'),
  (JoinStage.checking, 'Planning the mods'),
  (JoinStage.awaitingConsent, 'Your go-ahead'),
  (JoinStage.downloading, 'Downloading mods'),
  (JoinStage.installing, 'Installing mods'),
  (JoinStage.preparingProfile, "Saving the server's profile"),
  (JoinStage.launching, 'Starting Robotopia'),
  (JoinStage.connecting, 'Connecting'),
  (JoinStage.synchronizing, 'Receiving the world'),
  (JoinStage.inSession, 'In the session'),
];

/// What the status bar says while [state] runs; [server] is the server's
/// name without addresses, or null before it is known.
String joinStatusOf(JoinSessionState state, String? server) {
  final named = server ?? 'the server';
  return switch (state.stage) {
    JoinStage.resolving => 'Finding the server…',
    JoinStage.fetchingDescriptor => 'Checking the server…',
    JoinStage.checking => "Planning $named's mods…",
    JoinStage.awaitingConsent => 'Waiting for your go-ahead to join $named.',
    JoinStage.downloading => "Downloading $named's mods…",
    JoinStage.installing => "Installing $named's mods…",
    JoinStage.preparingProfile => "Saving $named's profile…",
    JoinStage.launching => 'Starting Robotopia to join $named…',
    JoinStage.connecting => 'Connecting to $named…',
    JoinStage.synchronizing => "Receiving $named's world…",
    JoinStage.inSession => 'Joined $named.',
    JoinStage.ended => _endedStatus(state.outcome!),
  };
}

String _endedStatus(JoinOutcome outcome) {
  final end = outcome.sessionEnd;
  if (end != null) return sessionEndMessage(end);
  final failure = outcome.failure!;
  if (failure.code == JoinFailureCode.cancelled) {
    return 'The join was cancelled.';
  }
  return 'The join stopped: ${JoinFailureText.of(failure).title.toLowerCase()}.';
}

/// Why a session that began has ended, for the player.
String sessionEndMessage(JoinSessionEndReason reason) => switch (reason) {
  JoinSessionEndReason.left => 'You left the session.',
  JoinSessionEndReason.hostEnded => 'The host ended the session.',
  JoinSessionEndReason.kicked => 'The host removed you from the session.',
  JoinSessionEndReason.banned => 'The host banned you from the session.',
  JoinSessionEndReason.timedOut => 'The connection to the session timed out.',
  JoinSessionEndReason.replaced => 'Another session replaced this one.',
  JoinSessionEndReason.admissionRejected =>
    'The server stopped admitting this install, so the session ended.',
  JoinSessionEndReason.protocolMismatch =>
    "The server's multiplayer version changed, so the session ended.",
  JoinSessionEndReason.transportFailed =>
    'The connection to the session failed.',
  JoinSessionEndReason.gameExited => 'Robotopia closed, so the session ended.',
};

const _texts = <JoinFailureCode, JoinFailureText>{
  JoinFailureCode.invalidLink: JoinFailureText(
    title: 'This link cannot be joined',
    sentence: 'The link does not lead to a server this launcher can join.',
    nextStep: 'Ask the host for a new link.',
  ),
  JoinFailureCode.inviteExpired: JoinFailureText(
    title: 'The invite has expired',
    sentence:
        'No server answers to this invite any more: it was mistyped, it '
        'expired, or its session ended.',
    nextStep: 'Ask the host for a new invite.',
  ),
  JoinFailureCode.serverNotListed: JoinFailureText(
    title: 'The server is no longer listed',
    sentence:
        'The TopiaForge service does not list this server any more: its '
        'session ended, or it is no longer public.',
    nextStep: 'Ask the host whether the server still runs, or for an invite.',
  ),
  JoinFailureCode.serviceUnavailable: JoinFailureText(
    title: 'The TopiaForge service is unavailable',
    sentence: 'The multiplayer service could not answer just now.',
    nextStep:
        'Try again in a few minutes. Joining on your local network or '
        'by address still works.',
    retry: true,
  ),
  JoinFailureCode.rateLimited: JoinFailureText(
    title: 'Too many requests just now',
    sentence: 'The TopiaForge service is turning requests away for a moment.',
    nextStep: 'Wait a moment, then try again.',
    retry: true,
  ),
  JoinFailureCode.offline: JoinFailureText(
    title: 'No network connection',
    sentence: 'This computer is not connected to a network.',
    nextStep: 'Check your connection, then try again.',
    retry: true,
  ),
  JoinFailureCode.unreachable: JoinFailureText(
    title: 'The server could not be reached',
    sentence: 'The server did not answer.',
    nextStep:
        'Check that the server still runs and that you are on its '
        'network, then try again.',
    retry: true,
  ),
  JoinFailureCode.descriptorInvalid: JoinFailureText(
    title: "The server's description could not be verified",
    sentence:
        'The server sent a description this launcher could not verify, '
        'so nothing was joined.',
    nextStep:
        'Ask the host to restart the server. Join only servers you '
        'trust.',
  ),
  JoinFailureCode.directNotAllowed: JoinFailureText(
    title: 'This server needs a direct connection',
    sentence:
        'Reaching this server would show it your IP address, and Hide my '
        'IP keeps it hidden.',
    nextStep:
        'Allow a direct connection for this session if you trust the '
        'host, or join through an invite.',
    action: JoinFailureAction.allowDirect,
  ),
  JoinFailureCode.identityChanged: JoinFailureText(
    title: 'This address now answers as another server',
    sentence:
        'The address you typed now answers as a different server than '
        'before. The host may have set the server up again, or something else '
        'may be answering.',
    nextStep: 'Trust it only if the host told you the server changed.',
    action: JoinFailureAction.trust,
  ),
  JoinFailureCode.gameBuildMismatch: JoinFailureText(
    title: 'Another Robotopia build',
    sentence: 'The server runs a different Robotopia build than this install.',
    nextStep: 'Update Robotopia, or ask the host which build to use.',
  ),
  JoinFailureCode.runtimeOutdated: JoinFailureText(
    title: 'TopiaForge needs an update',
    sentence: 'The server needs a newer TopiaForge than this launcher has.',
    nextStep: 'Update TopiaForge, then join again.',
  ),
  JoinFailureCode.contentUnavailable: JoinFailureText(
    title: "Some of the server's mods are not available",
    sentence:
        'This launcher cannot get every mod the server runs at its exact '
        'version.',
    nextStep: 'Fix what each mod below says, then try again.',
    retry: true,
  ),
  JoinFailureCode.admissionBlocked: JoinFailureText(
    title: 'The server would not admit this install',
    sentence: "The server's rules would refuse this install as it is.",
    nextStep: 'Read the reasons below, or ask the host.',
  ),
  JoinFailureCode.standaloneOnServer: JoinFailureText(
    title: 'The server runs a single-player mod',
    sentence:
        'One of the server\'s mods refuses multiplayer sessions, so '
        'nobody can join this server.',
    nextStep: 'Ask the host to remove it.',
  ),
  JoinFailureCode.cancelled: JoinFailureText(
    title: 'Join cancelled',
    sentence:
        'You stopped the join. Nothing more was downloaded, installed '
        'or started.',
    retry: true,
  ),
  JoinFailureCode.downloadFailed: JoinFailureText(
    title: 'A download failed',
    sentence:
        'A mod could not be downloaded. Try again; if it keeps failing, '
        'its source may be down.',
    retry: true,
  ),
  JoinFailureCode.hostDownloadUnsupported: JoinFailureText(
    title: 'A mod is only on the host',
    sentence:
        'This launcher cannot download mods from a host yet. Ask the '
        'host to publish it to a mod source you use.',
  ),
  JoinFailureCode.integrityFailed: JoinFailureText(
    title: 'A download did not match',
    sentence:
        'A downloaded mod is not the exact file the server runs, so '
        'nothing was installed.',
    retry: true,
  ),
  JoinFailureCode.manifestMismatch: JoinFailureText(
    title: 'A download did not match its listing',
    sentence:
        'A downloaded mod does not match how its source lists it, so '
        'nothing was installed.',
    retry: true,
  ),
  JoinFailureCode.installFailed: JoinFailureText(
    title: 'The install did not finish',
    sentence:
        "The server's mods could not be installed. Your mods are as "
        'they were.',
    retry: true,
  ),
  JoinFailureCode.profileSaveFailed: JoinFailureText(
    title: "The server's profile could not be saved",
    sentence:
        "The launcher could not save the server's profile. Your "
        'profiles are as they were.',
    retry: true,
  ),
  JoinFailureCode.repairNeeded: JoinFailureText(
    title: 'The runtime needs a repair',
    sentence:
        'The TopiaForge runtime in this install is missing or out of '
        'date. Repair it, then try again.',
    retry: true,
    action: JoinFailureAction.repair,
  ),
  JoinFailureCode.gameRunning: JoinFailureText(
    title: 'Robotopia is running',
    sentence:
        'Robotopia is running, so the join could not go on. Close the '
        'game, then try again.',
    retry: true,
  ),
  JoinFailureCode.launchBlocked: JoinFailureText(
    title: 'Robotopia could not start the join',
    sentence:
        'Something stopped Robotopia from starting for this server. '
        'Try again.',
    retry: true,
  ),
  JoinFailureCode.startFailed: JoinFailureText(
    title: 'Robotopia did not start the join',
    sentence:
        'Robotopia could not start the join. Try again; if it keeps '
        'failing, open Diagnostics.',
    retry: true,
  ),
  JoinFailureCode.gameExited: JoinFailureText(
    title: 'Robotopia closed',
    sentence: 'Robotopia closed before the join finished.',
    nextStep: 'Try again. If the game keeps closing, open Diagnostics.',
    retry: true,
  ),
  JoinFailureCode.timeout: JoinFailureText(
    title: 'The connection timed out',
    sentence: 'The server did not answer in time.',
    nextStep: 'Try again in a moment.',
    retry: true,
  ),
  JoinFailureCode.natTraversalFailed: JoinFailureText(
    title: 'A connection could not be set up',
    sentence: "Your network and the host's could not reach each other.",
    nextStep:
        'Try again. If it keeps failing, join through an invite, which '
        'can use a relay.',
    retry: true,
  ),
  JoinFailureCode.relayUnavailable: JoinFailureText(
    title: 'The relay is unavailable',
    sentence:
        'The relay that keeps your address hidden could not carry this '
        'session.',
    nextStep: 'Try again later.',
    retry: true,
  ),
  JoinFailureCode.serverFull: JoinFailureText(
    title: 'The server is full',
    sentence: 'Every place on the server is taken.',
    nextStep: 'Try again when someone leaves.',
    retry: true,
  ),
  JoinFailureCode.passwordRejected: JoinFailureText(
    title: 'The password was not accepted',
    sentence:
        'The server needs a password, and the one typed in Robotopia was '
        'missing or wrong.',
    nextStep: 'Try again, and type the password when Robotopia asks for it.',
    retry: true,
  ),
  JoinFailureCode.kicked: JoinFailureText(
    title: 'The host removed you',
    sentence: 'The host removed you from the session.',
    nextStep: 'Ask the host before you join again.',
  ),
  JoinFailureCode.protocolMismatch: JoinFailureText(
    title: 'Another multiplayer version',
    sentence: "The server speaks another version of TopiaForge's multiplayer.",
    nextStep: 'Update TopiaForge, or ask the host which version to use.',
  ),
  JoinFailureCode.admissionChanged: JoinFailureText(
    title: 'The server changed',
    sentence:
        'The server changed its mods again after the launcher planned '
        'the join.',
    nextStep: 'Try again to plan with its new mods.',
    retry: true,
  ),
  JoinFailureCode.snapshotFailed: JoinFailureText(
    title: 'The world could not be loaded',
    sentence: "Robotopia could not take in the server's world.",
    nextStep: 'Try again.',
    retry: true,
  ),
  JoinFailureCode.disconnected: JoinFailureText(
    title: 'The connection was lost',
    sentence: 'The connection to the server was lost.',
    nextStep: 'Try again.',
    retry: true,
  ),
};
