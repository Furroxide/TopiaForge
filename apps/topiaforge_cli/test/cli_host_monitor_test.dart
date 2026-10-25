import 'package:test/test.dart';
import 'package:topiaforge/src/cli_host_monitor.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'cli_host_fixture.dart';

void main() {
  late HostFixture rig;
  late CliHostMonitor monitor;
  setUp(() {
    rig = HostFixture();
    monitor = CliHostMonitor(rig.profile, 'intent-one', true, rig.messages.add);
  });
  tearDown(() {
    monitor.close();
    return rig.dispose();
  });
  void bind() => monitor.bind(
    LaunchResult(
      started: true,
      message: 'started',
      requestId: 'request-one',
      latestActivity: rig.base(),
    ),
  );
  test('fast invite before receipt is printed exactly once', () async {
    monitor.accept(rig.success());
    monitor.accept(rig.success(sequence: 3));
    bind();
    monitor.accept(rig.success(sequence: 4));
    expect(await monitor.completed.future, 0);
    expect(rig.messages.where((m) => m.startsWith('Invite code:')).length, 1);
    expect(rig.messages.last, 'Invite link: topiaforge://join/0123456789');
  });
  test('successful host waits for invite arriving in later progress', () async {
    bind();
    monitor.accept(rig.success(invite: null));
    expect(monitor.completed.isCompleted, isFalse);
    expect(rig.messages, isEmpty);
    monitor.accept(rig.success(sequence: 3));
    expect(await monitor.completed.future, 0);
  });
  test('foreign request, intent and digest cannot publish an invite', () async {
    bind();
    monitor.accept(rig.success(requestId: 'foreign'));
    monitor.accept(rig.success(intent: 'foreign'));
    monitor.accept(rig.success(digest: 'foreign'));
    expect(rig.messages, isEmpty);
    monitor.unavailable();
    expect(await monitor.completed.future, 3);
  });
  test('old progress cannot replace newer progress with an invite', () async {
    bind();
    monitor.accept(rig.success(invite: null, sequence: 8));
    monitor.accept(rig.success(sequence: 7));
    expect(monitor.completed.isCompleted, isFalse);
    monitor.unavailable();
    expect(await monitor.completed.future, 3);
  });
  test('stream ending before receipt is not success', () async {
    monitor.unavailable();
    bind();
    expect(await monitor.completed.future, 3);
  });
  test('cancellation stops wait without reporting an invite', () async {
    monitor.cancel();
    bind();
    monitor.accept(rig.success());
    expect(await monitor.completed.future, 130);
    expect(rig.messages.join(), isNot(contains('Invite code:')));
  });
  test('process exit before invitation fails', () async {
    bind();
    monitor.accept(rig.success().copyWith(processExited: true));
    expect(await monitor.completed.future, 1);
    expect(rig.messages.join(), isNot(contains('Invite code:')));
  });
  test('no receipt cannot confirm hosting', () async {
    monitor.bind(const LaunchResult(started: true, message: 'started'));
    expect(await monitor.completed.future, 3);
  });
  test('another process cannot confirm hosting', () async {
    final own = LaunchProcessIdentity(
      pid: 10,
      startTimeUtc: DateTime.utc(2026),
      executablePath: 'fixture/game.exe',
    );
    final other = LaunchProcessIdentity(
      pid: 11,
      startTimeUtc: DateTime.utc(2026),
      executablePath: 'fixture/game.exe',
    );
    monitor.bind(
      LaunchResult(
        started: true,
        message: 'started',
        requestId: 'request-one',
        latestActivity: rig.base().copyWith(process: own),
      ),
    );
    monitor.accept(rig.success().copyWith(process: other));
    monitor.accept(rig.success());
    expect(rig.messages, isEmpty);
    monitor.accept(rig.success().copyWith(process: own));
    expect(await monitor.completed.future, 0);
  });
}
