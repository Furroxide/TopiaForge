import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

final profile = LauncherProfile.defaultProfile();
final process = LaunchProcessIdentity(
  pid: 42,
  startTimeUtc: DateTime.utc(2026),
  executablePath: 'fixture/game.exe',
  nativeStartToken: 'start',
);

LaunchActivity activity({
  String request = 'request',
  String digest = 'digest',
  String install = 'install',
  String? profileId,
  int? revision,
  LaunchProcessIdentity? identity,
  String command = 'host-session',
}) => LaunchActivity(
  requestId: request,
  profileId: profileId ?? profile.id,
  profileRevision: revision ?? profile.revision,
  installIdentity: install,
  packageDigest: digest,
  command: command,
  process: identity ?? process,
);

LaunchActivity success({
  LaunchActivity? base,
  String intent = 'intent',
  String? invite = '0123456789',
  LaunchSessionRole role = LaunchSessionRole.host,
  int sequence = 2,
}) {
  final item = base ?? activity();
  return item
      .applyOutcome(
        LaunchOutcome(
          kind: 'launch',
          requestId: item.requestId,
          command: item.command,
          sequence: 1,
          status: 'succeeded',
          phase: 'idle',
          multiplayer: LaunchOutcomeMultiplayer(
            role: role,
            intentId: intent,
            serverId: 'a' * 64,
          ),
        ),
      )
      .applyProgress(
        LaunchProgress(
          requestId: item.requestId,
          sequence: sequence,
          phase: 'idle',
          session: LaunchProgressSession(
            role: role,
            intentId: intent,
            state: LaunchSessionState.hosting,
            serverId: 'a' * 64,
            players: 1,
            maxPlayers: 8,
            inviteCode: invite,
          ),
        ),
      );
}

void bind(HostingReceipt monitor, [LaunchActivity? receipt]) => monitor.bind(
  LaunchResult(
    started: true,
    message: '',
    requestId: 'request',
    latestActivity: receipt ?? activity(),
  ),
);

void main() {
  test(
    'buffers before spawn and waits for matching outcome, progress and invite',
    () {
      final monitor = HostingReceipt(profile, 'intent', needsInvite: true);
      monitor.accept(success(invite: null));
      expect(monitor.confirmed, isFalse);
      bind(monitor);
      expect(monitor.confirmed, isFalse);
      monitor.accept(success(sequence: 3));
      expect(monitor.confirmed, isTrue);
      expect(monitor.inviteCode, '0123456789');
    },
  );
  test('LAN can confirm without an Internet invite', () {
    final monitor = HostingReceipt(profile, 'intent', needsInvite: false);
    bind(monitor, success(invite: null));
    expect(monitor.confirmed, isTrue);
    expect(monitor.inviteCode, isNull);
  });
  final wrong = <String, LaunchActivity Function()>{
    'request': () => activity(request: 'old'),
    'digest': () => activity(digest: 'old'),
    'install': () => activity(install: 'old'),
    'profile': () => activity(profileId: 'old'),
    'revision': () => activity(revision: profile.revision + 1),
    'pid': () => activity(
      identity: LaunchProcessIdentity(
        pid: 43,
        startTimeUtc: process.startTimeUtc,
        executablePath: process.executablePath,
        nativeStartToken: process.nativeStartToken,
      ),
    ),
    'start time': () => activity(
      identity: LaunchProcessIdentity(
        pid: 42,
        startTimeUtc: DateTime.utc(2025),
        executablePath: process.executablePath,
        nativeStartToken: process.nativeStartToken,
      ),
    ),
    'executable': () => activity(
      identity: LaunchProcessIdentity(
        pid: 42,
        startTimeUtc: process.startTimeUtc,
        executablePath: 'fixture/other.exe',
        nativeStartToken: process.nativeStartToken,
      ),
    ),
    'native token': () => activity(
      identity: LaunchProcessIdentity(
        pid: 42,
        startTimeUtc: process.startTimeUtc,
        executablePath: process.executablePath,
        nativeStartToken: 'old',
      ),
    ),
  };
  for (final entry in wrong.entries) {
    test('rejects another ${entry.key}', () {
      final monitor = HostingReceipt(profile, 'intent', needsInvite: true);
      bind(monitor);
      monitor.accept(success(base: entry.value()));
      expect(monitor.confirmed, isFalse);
      expect(monitor.inviteCode, isNull);
      monitor.accept(entry.value().copyWith(processExited: true));
      expect(monitor.failed, isFalse);
    });
  }
  test('another command cannot end this host', () {
    final monitor = HostingReceipt(profile, 'intent', needsInvite: true);
    bind(monitor, success());
    monitor.accept(
      activity(command: 'join-session').copyWith(processExited: true),
    );
    expect(monitor.confirmed, isTrue);
  });
  test('embedded receipt data also needs the current intent', () {
    final monitor = HostingReceipt(profile, 'intent', needsInvite: true);
    bind(monitor, success(intent: 'old'));
    expect(monitor.confirmed, isFalse);
    monitor.accept(success());
    expect(monitor.confirmed, isTrue);
  });
  test('exit removes the invite and cannot revive with a late success', () {
    final monitor = HostingReceipt(profile, 'intent', needsInvite: true);
    bind(monitor, success());
    monitor.accept(activity().copyWith(processExited: true));
    expect(monitor.failed, isTrue);
    expect(monitor.inviteCode, isNull);
    monitor.accept(success());
    expect(monitor.confirmed, isFalse);
  });
  test('an unverified spawn cannot supply a trusted invite', () {
    final monitor = HostingReceipt(profile, 'intent', needsInvite: true);
    bind(
      monitor,
      LaunchActivity(
        requestId: 'request',
        profileId: profile.id,
        profileRevision: profile.revision,
        installIdentity: 'install',
        packageDigest: 'digest',
        command: 'host-session',
      ),
    );
    monitor.accept(success());
    expect(monitor.failed, isTrue);
    expect(monitor.inviteCode, isNull);
  });
}
