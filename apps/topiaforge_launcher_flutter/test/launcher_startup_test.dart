import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge_launcher_flutter/src/launcher_instance.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_startup.dart';
import 'package:topiaforge_launcher_flutter/src/update_health_handshake.dart';

/// A scripted runner that records every call.
final class _Port implements LauncherInstancePort {
  _Port(this.answer);

  final LauncherInstanceClaim answer;
  final calls = <String>[];

  @override
  Future<LauncherInstanceClaim> claim(String scope) async {
    calls.add('claim');
    return answer;
  }

  @override
  Future<bool> handOff(String? link) async {
    calls.add('handOff');
    return true;
  }

  @override
  Future<void> release() async => calls.add('release');
}

void main() {
  const scope = '0123456789abcdef0123456789abcdef';
  const invite = 'topiaforge://join/ABCDE12345';
  final nonce = List.filled(64, 'b').join();
  late Directory root;
  late String marker;
  late List<int> exits;

  setUp(() async {
    root = await Directory.systemTemp.createTemp('topiaforge-startup-');
    final transaction = Directory(
      p.join(root.path, 'updates', 'transactions', scope),
    )..createSync(recursive: true);
    marker = p.join(transaction.path, 'health.json');
    exits = [];
  });

  tearDown(() => root.delete(recursive: true));

  List<String> healthCheck() => [
    updateHealthNonceOption,
    nonce,
    updateHealthFileOption,
    marker,
  ];

  Future<(LauncherStartOutcome, LauncherStart?)> run(
    _Port port,
    List<String> arguments, {
    Object? failure,
  }) async {
    LauncherStart? launched;
    final outcome =
        await LauncherStartup(
          instance: port,
          exit: (code) async => exits.add(code),
        ).run(
          arguments,
          dataRoot: root.path,
          scope: scope,
          launch: (start) async {
            launched = start;
            await Future<void>.value();
            final error = failure;
            if (error != null) throw error;
          },
        );
    return (outcome, launched);
  }

  test('a broken health check claims nothing and quits', () async {
    for (final arguments in [
      [updateHealthNonceOption, '', updateHealthFileOption, marker],
      [updateHealthNonceOption, nonce],
      [...healthCheck(), 'extra'],
      [updateHealthFileOption, marker, updateHealthNonceOption, nonce],
      [
        updateHealthNonceOption,
        nonce,
        updateHealthFileOption,
        p.join(root.path, 'outside.json'),
      ],
    ]) {
      final port = _Port(LauncherInstanceClaim.primary);
      exits.clear();

      final (outcome, launched) = await run(port, arguments);

      expect(outcome, LauncherStartOutcome.refused, reason: '$arguments');
      expect(port.calls, isEmpty, reason: '$arguments');
      expect(exits, [1]);
      expect(launched, isNull);
    }
  });

  test('a start that fails before its first frame releases its claim and '
      'quits', () async {
    final port = _Port(LauncherInstanceClaim.primary);

    final (outcome, _) = await run(port, [
      invite,
    ], failure: StateError('the data root cannot be read'));

    expect(outcome, LauncherStartOutcome.failed);
    expect(port.calls, ['claim', 'release']);
    expect(exits, [1]);
  });

  test(
    'a health check that fails beside a running launcher quits too',
    () async {
      final port = _Port(LauncherInstanceClaim.secondary);

      final (outcome, _) = await run(
        port,
        healthCheck(),
        failure: StateError('no frame'),
      );

      expect(outcome, LauncherStartOutcome.failed);
      expect(port.calls, ['claim', 'release']);
      expect(exits, [1]);
    },
  );

  test('a valid health check claims and launches with its request', () async {
    final port = _Port(LauncherInstanceClaim.primary);

    final (outcome, start) = await run(port, healthCheck());

    expect(outcome, LauncherStartOutcome.started);
    expect(port.calls, ['claim']);
    expect(exits, isEmpty);
    expect(start?.healthCheck?.nonce, nonce);
    expect(start?.healthCheck?.markerPath, p.normalize(marker));
    expect(start?.link, isNull);
  });

  test('a link start ignores everything after its link', () async {
    final port = _Port(LauncherInstanceClaim.primary);

    final (outcome, start) = await run(port, [
      invite,
      updateHealthNonceOption,
      '',
    ]);

    expect(outcome, LauncherStartOutcome.started);
    expect(start?.link, invite);
    expect(start?.healthCheck, isNull);
    expect(port.calls, ['claim']);
  });

  test('a later start hands off and quits without launching', () async {
    final port = _Port(LauncherInstanceClaim.secondary);

    final (outcome, launched) = await run(port, [invite]);

    expect(outcome, LauncherStartOutcome.handedOff);
    expect(port.calls, ['claim', 'handOff']);
    expect(exits, [0]);
    expect(launched, isNull);
  });
}
