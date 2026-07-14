part of 'launcher_domain_test.dart';

void _developerModelTests() {
  group('UgcLiveSyncSettings', () {
    test(
      'toRuntimeConfig emits exactly the keys the C# UgcLiveSyncConfig expects',
      () {
        final config = const UgcLiveSyncSettings(
          transport: 'automerge',
          watchFolder: r'C:\watch',
          editorUrl: 'https://h/?project=automerge:doc&scene=main',
          documentUrl: 'automerge:doc',
          sceneId: 'main',
          autoConnectOnStart: true,
        ).toRuntimeConfig();

        // The mod (mods/TopiaForge.UgcLiveSync/UgcLiveSyncConfig.cs) deserializes these exact DataMember names; this
        // pins the cross-language contract so a renamed/dropped key can never silently ship.
        expect(
          config.keys.toSet(),
          equals({
            'transport',
            'watchFolder',
            'editorUrl',
            'documentUrl',
            'syncServerUrl',
            'sceneId',
            'autoConnectOnStart',
            'maxSnapshotBytes',
            'debounceMilliseconds',
          }),
        );
        expect(config['transport'], 'automerge');
        expect(config['autoConnectOnStart'], isTrue);
        expect(
          config['maxSnapshotBytes'],
          UgcLiveSyncSettings.defaultMaxSnapshotBytes,
        );
      },
    );

    test('normalizes an unknown transport to localFolder', () {
      expect(UgcLiveSyncSettings.normalizeTransport('bogus'), 'localFolder');
      expect(UgcLiveSyncSettings.normalizeTransport('Automerge'), 'automerge');
      expect(
        const UgcLiveSyncSettings(
          transport: 'weird',
        ).toRuntimeConfig()['transport'],
        'localFolder',
      );
    });

    test('round-trips through toJson and back', () {
      const settings = UgcLiveSyncSettings(
        transport: 'automerge',
        watchFolder: 'watch',
        editorUrl: 'https://h/?project=automerge:doc',
        sceneId: 'main',
        autoConnectOnStart: true,
        debounceMilliseconds: 350,
      );

      final restored = UgcLiveSyncSettings.fromJson(settings.toJson());

      expect(restored.transport, 'automerge');
      expect(restored.watchFolder, 'watch');
      expect(restored.editorUrl, settings.editorUrl);
      expect(restored.sceneId, 'main');
      expect(restored.autoConnectOnStart, isTrue);
      expect(restored.debounceMilliseconds, 350);
    });

    test('UnityCompanionSettings persists nested liveSync', () {
      const companion = UnityCompanionSettings(
        enabled: true,
        liveSync: UgcLiveSyncSettings(transport: 'automerge'),
      );

      final restored = UnityCompanionSettings.fromJson(companion.toJson());

      expect(restored.liveSync.transport, 'automerge');
    });

    test('UgcLiveSyncStatusSnapshot parses the C# status handshake', () {
      // Keys mirror the C# UgcLiveSyncStatusFile [DataMember] names (cross-language contract).
      final snapshot = UgcLiveSyncStatusSnapshot.fromJson(const {
        'schemaVersion': 2,
        'status': 'Connected',
        'transport': 'automerge',
        'defaultWatchFolder': r'C:\game\ugc',
        'connectedDocumentUrl': 'automerge:abc123',
        'sceneId': 'main',
        'availableScenes': ['main', 'lobby'],
        'lastAppliedUtc': '2026-06-30T12:00:00Z',
      });

      expect(snapshot.status, 'Connected');
      expect(snapshot.isLive, isTrue);
      expect(snapshot.transport, 'automerge');
      expect(snapshot.defaultWatchFolder, r'C:\game\ugc');
      expect(snapshot.connectedDocumentUrl, 'automerge:abc123');
      expect(snapshot.availableScenes, ['main', 'lobby']);

      // A bare/empty status is non-live and has safe defaults.
      final empty = UgcLiveSyncStatusSnapshot.fromJson(const {});
      expect(empty.schemaVersion, 0);
      expect(empty.isLive, isFalse);
      expect(empty.status, 'Idle');
      expect(empty.availableScenes, isEmpty);
    });

    test('RegisteredProject + UnityEditor round-trip and kind parsing', () {
      const project = RegisteredProject(
        path: r'C:\proj\my-world',
        name: 'My World',
        kind: ProjectKind.unityWorld,
        unityVersion: '6000.0.23f1',
        lastOpenedUtc: '2026-06-30T12:00:00Z',
      );
      final restored = RegisteredProject.fromJson(project.toJson());
      expect(restored.path, project.path);
      expect(restored.kind, ProjectKind.unityWorld);
      expect(restored.isUnity, isTrue);
      expect(restored.unityVersion, '6000.0.23f1');
      expect(restored.lastOpenedUtc, '2026-06-30T12:00:00Z');

      expect(projectKindFromString('modCSharp'), ProjectKind.modCSharp);
      expect(projectKindFromString('unityPackage'), ProjectKind.unityPackage);
      expect(projectKindFromString('bogus'), ProjectKind.unknown);
      expect(const RegisteredProject(path: 'p', name: 'n').isUnity, isFalse);

      const editor = UnityEditor(version: '6000.0.23f1', path: r'C:\unity.exe');
      final editorBack = UnityEditor.fromJson(editor.toJson());
      expect(editorBack.version, '6000.0.23f1');
      expect(editorBack.path, r'C:\unity.exe');
    });
  });
}
