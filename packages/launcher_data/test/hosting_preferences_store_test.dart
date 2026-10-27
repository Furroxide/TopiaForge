import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test(
    'first port edit preserves the unread native name, capacity, visibility and voice',
    () async {
      final root = Directory.systemTemp.createTempSync('hosting-preferences-');
      addTearDown(() => root.deleteSync(recursive: true));
      final install = GameInstall(
        path: p.join(root.path, 'game'),
        executablePath: p.join(root.path, 'game', 'Robotopia.exe'),
        bepInExStatus: ComponentState.ready,
        loaderStatus: ComponentState.ready,
      );
      final native = MultiplayerHostingDefaults(
        name: 'Native evening',
        maxPlayers: 16,
        visibility: ServerVisibility.invite,
        port: 12340,
        voice: true,
      );
      final journal = File(
        p.join(
          install.path,
          'BepInEx',
          'TopiaForge',
          'staging',
          'multiplayer-preferences-journal-v1.json',
        ),
      );
      journal.parent.createSync(recursive: true);
      journal.writeAsStringSync(
        LaunchTransport.writePreferencesJournal(
          MultiplayerPreferencesJournal('5f0c2a9e8d7b6a5f4e3d2c1b0a998877', [
            HostingChange(sequence: 1, hosting: native),
          ]),
        ),
      );
      final repo = LocalMultiplayerRepository(
        dataRoot: p.join(root.path, 'data'),
        service: MultiplayerServiceConfiguration.none,
        gameProbe: (_) async => false,
      );
      addTearDown(repo.dispose);
      final before = journal.readAsStringSync();
      await expectLater(
        repo.changePreferences(
          const MultiplayerPreferencesChange(hostingPort: 12345),
        ),
        throwsA(isA<MultiplayerPreferencesException>()),
      );
      expect(journal.readAsStringSync(), before);
      final round = await repo.syncWithGame(install);
      expect(round, isA<MultiplayerPreferencesSynced>());
      expect(round.preferences.hosting.toJson(), native.toJson());
      final changed = await repo.changePreferences(
        const MultiplayerPreferencesChange(hostingPort: 12345),
      );
      expect(changed.hosting.toJson(), {...native.toJson(), 'port': 12345});
      expect(
        (await repo.loadPreferences()).hosting.toJson(),
        changed.hosting.toJson(),
      );
      final next = MultiplayerHostingDefaults(
        name: 'Reviewed',
        maxPlayers: 10,
        port: 0,
      );
      final saved = await repo.changePreferences(
        MultiplayerPreferencesChange(hostingDefaults: next, expected: changed),
      );
      expect((await repo.loadPreferences()).hosting.toJson(), next.toJson());
      await expectLater(
        repo.changePreferences(
          MultiplayerPreferencesChange(
            hostingDefaults: native,
            expected: changed,
          ),
        ),
        throwsA(isA<MultiplayerPreferencesException>()),
      );
      expect(
        (await repo.loadPreferences()).hosting.toJson(),
        saved.hosting.toJson(),
      );
    },
  );
}
