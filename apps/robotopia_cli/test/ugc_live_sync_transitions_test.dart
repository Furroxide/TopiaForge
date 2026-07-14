import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:robotopia/src/ugc_live_sync_transitions.dart';
import 'package:robotopia/src/ugc_publisher_liveness.dart';
import 'package:test/test.dart';

void main() {
  const automerge = UgcLiveSyncSettings(
    transport: 'automerge',
    watchFolder: '/workspace/watch',
    editorUrl: 'https://editor.example.test',
    syncServerUrl: 'https://configured-sync.example.test',
    sceneId: 'main',
    autoConnectOnStart: true,
    maxSnapshotBytes: 123456,
    debounceMilliseconds: 375,
  );

  test(
    'publisher failure explicitly falls back to the local-folder channel',
    () {
      final fallback = UgcLiveSyncTransitions.localFallback(automerge);

      expect(fallback.transport, 'localFolder');
      expect(fallback.watchFolder, automerge.watchFolder);
      expect(fallback.sceneId, automerge.sceneId);
      expect(fallback.autoConnectOnStart, true);
      expect(fallback.documentUrl, isEmpty);
      expect(fallback.editorUrl, isEmpty);
      expect(fallback.maxSnapshotBytes, 123456);
      expect(fallback.debounceMilliseconds, 375);
    },
  );

  test('go-live requires an owned publisher lease, pid, and document', () {
    expect(
      UgcLiveSyncTransitions.connectPublisherSession(automerge, const {
        'documentUrl': 'automerge:abc',
      }),
      isNull,
    );

    final connected =
        UgcLiveSyncTransitions.connectPublisherSession(automerge, {
          'documentUrl': 'automerge:abc',
          'syncUrl': 'wss://live-sync.example.test',
          'sceneId': 'session-scene',
          'publisherLeaseToken': 'lease-token',
          'publisherPid': pid,
        });
    expect(connected, isNotNull);
    expect(connected!.transport, 'automerge');
    expect(connected.documentUrl, 'automerge:abc');
    expect(connected.syncServerUrl, 'wss://live-sync.example.test');
    expect(connected.sceneId, 'main');
    expect(connected.autoConnectOnStart, true);
  });

  test('publisher liveness accepts this process and rejects a dead pid', () {
    expect(UgcPublisherLiveness.isAlive(pid), isTrue);
    expect(UgcPublisherLiveness.isAlive(2147483647), isFalse);
  });
}
