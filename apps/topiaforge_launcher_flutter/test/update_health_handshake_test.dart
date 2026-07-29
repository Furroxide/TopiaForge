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
}
