import 'dart:convert';
import 'dart:io';

import 'package:flutter/scheduler.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge_launcher_flutter/src/update_health_handshake.dart';

void main() {
  test('writes a bounded health marker after the scheduled frame', () async {
    final root = await Directory.systemTemp.createTemp('topiaforge-health-');
    addTearDown(() => root.delete(recursive: true));
    const transactionId = '0123456789abcdef0123456789abcdef';
    final transaction = Directory(
      p.join(root.path, 'updates', 'transactions', transactionId),
    )..createSync(recursive: true);
    final marker = File(p.join(transaction.path, 'health.json'));
    final nonce = List.filled(64, 'a').join();
    FrameCallback? frameCallback;

    scheduleUpdateHealthHandshake(
      [
        '--topiaforge-update-health-nonce',
        nonce,
        '--topiaforge-update-health-file',
        marker.path,
      ],
      dataRoot: root.path,
      frameScheduler: (callback) {
        frameCallback = callback;
      },
    );
    expect(marker.existsSync(), isFalse);
    expect(frameCallback, isNotNull);
    frameCallback!(Duration.zero);

    final decoded = jsonDecode(marker.readAsStringSync()) as Map;
    expect(decoded['formatVersion'], 1);
    expect(decoded['nonce'], nonce);
    expect(decoded['healthy'], isTrue);
    expect(decoded['processId'], pid);
  });

  test('rejects health markers outside a real transaction directory', () async {
    final root = await Directory.systemTemp.createTemp(
      'topiaforge-health-unsafe-',
    );
    addTearDown(() => root.delete(recursive: true));
    final nonce = List.filled(64, 'b').join();

    expect(
      () => scheduleUpdateHealthHandshake([
        '--topiaforge-update-health-nonce',
        nonce,
        '--topiaforge-update-health-file',
        p.join(root.path, 'outside.json'),
      ], dataRoot: root.path),
      throwsStateError,
    );
  });

  test(
    'reads only the helper\'s exact command line as a health check',
    () async {
      final root = await Directory.systemTemp.createTemp(
        'topiaforge-health-shape-',
      );
      addTearDown(() => root.delete(recursive: true));
      const transactionId = '0123456789abcdef0123456789abcdef';
      Directory(
        p.join(root.path, 'updates', 'transactions', transactionId),
      ).createSync(recursive: true);
      final marker = p.join(
        root.path,
        'updates',
        'transactions',
        transactionId,
        'health.json',
      );
      final nonce = List.filled(64, 'c').join();
      final exact = [
        updateHealthNonceOption,
        nonce,
        updateHealthFileOption,
        marker,
      ];

      expect(readUpdateHealthRequest(const [], dataRoot: root.path), isNull);
      expect(
        readUpdateHealthRequest(['--verbose'], dataRoot: root.path),
        isNull,
      );
      expect(readUpdateHealthRequest(exact, dataRoot: root.path)?.nonce, nonce);
      for (final arguments in [
        [...exact, 'topiaforge://join/ABCDE12345'],
        ['topiaforge://join/ABCDE12345', ...exact],
        [exact[0], exact[1]],
        [exact[2], exact[3]],
        [exact[2], exact[3], exact[0], exact[1]],
        [exact[0], '', exact[2], exact[3]],
        [exact[0], nonce.toUpperCase(), exact[2], exact[3]],
        [exact[0], nonce, exact[2], exact[3], exact[2], exact[3]],
      ]) {
        expect(
          () => readUpdateHealthRequest(arguments, dataRoot: root.path),
          throwsStateError,
          reason: '$arguments',
        );
      }
    },
  );
}
