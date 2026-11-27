import 'dart:convert';

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
  LaunchActivity unknown({
    bool terminal = false,
    String intent = 'intent-one',
    String request = 'request-one',
  }) => rig.base().applyOutcome(
    LaunchTransport.readOutcome(
      jsonEncode({
        'schemaVersion': 2,
        'requestId': request,
        'sequence': 3,
        'kind': terminal ? 'session' : 'launch',
        if (!terminal) 'command': 'host-session',
        'phase': 'idle',
        'status': terminal ? 'succeeded' : 'failed',
        'blocks': [],
        'multiplayer': {
          'role': 'host',
          'intentId': intent,
          terminal ? 'endReason' : 'failure': 'private-203.0.113.77:47819',
          'detail': 'secret provider detail; token=fixture-secret',
        },
      }),
    ),
  );
  for (final terminal in [false, true]) {
    test('unknown host result finishes safely (terminal: $terminal)', () async {
      bind();
      monitor.accept(unknown(terminal: terminal));
      expect(await monitor.completed.future, 1);
      expect(
        rig.messages.single,
        terminal
            ? 'TFHOST140: Hosting ended before confirmation. Start a new hosted session.'
            : 'TFHOST140: The game refused hosting. Check the in-game status and repair the selected profile before retrying.',
      );
    });
    test(
      'unknown host result cannot overtake cancellation (terminal: $terminal)',
      () async {
        bind();
        monitor.cancel();
        monitor.accept(unknown(terminal: terminal));
        expect(await monitor.completed.future, 130);
        expect(rig.messages.length, 1);
      },
    );
    test('unknown foreign result is ignored (terminal: $terminal)', () {
      bind();
      monitor.accept(unknown(terminal: terminal, intent: 'foreign'));
      monitor.accept(unknown(terminal: terminal, request: 'foreign'));
      expect(monitor.completed.isCompleted, isFalse);
      expect(rig.messages, isEmpty);
    });
  }
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
