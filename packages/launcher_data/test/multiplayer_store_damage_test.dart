import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

/// A damaged file stays where it is until what replaces it is written, so a
/// write that fails or stops part way never leaves the defaults, or an empty
/// record, in place of what was salvaged; and of the damaged copies beside
/// it, only the ones the store made are ever dropped.
void main() {
  final now = DateTime.utc(2026, 10, 15, 12);
  late Directory root;
  late String folder;

  setUp(() {
    root = Directory.systemTemp.createTempSync('tf-mp-damage-');
    folder = p.join(root.resolveSymbolicLinksSync(), 'multiplayer');
    Directory(folder).createSync(recursive: true);
  });
  tearDown(() => root.deleteSync(recursive: true));

  MultiplayerStore store([DateTime? at]) =>
      MultiplayerStore(folder, clock: () => at ?? now);
  File inFolder(String name) => File(p.join(folder, name));
  List<String> names() => Directory(
    folder,
  ).listSync().map((entity) => p.basename(entity.path)).toList()..sort();

  group('a damaged file whose replacement cannot be written', () {
    test('stays in place, so the settings salvaged from it last', () async {
      final chosen = MultiplayerSettings(
        displayName: 'Sam',
        lanDiscoveryEnabled: false,
      );
      // One member is damaged; the others are what the player chose.
      final damaged = utf8.encode(
        jsonEncode(chosen.toJson()..['upnpEnabled'] = 'yes'),
      );
      final file = inFolder(MultiplayerStore.settingsFileName)
        ..writeAsBytesSync(damaged);
      final shared = store();
      for (var attempt = 0; attempt < 3; attempt++) {
        final load = await _withFullDisk(shared.loadSettings);
        expect(load.recovery, MultiplayerStoreRecovery.salvaged);
        expect(load.settings.toJson(), chosen.toJson());
        expect(file.readAsBytesSync(), damaged);
      }

      // With room again, what was salvaged takes its place. Each read kept a
      // copy, all at one time, and only the newest few stay.
      final salvaged = await shared.loadSettings();
      expect(salvaged.recovery, MultiplayerStoreRecovery.salvaged);
      final load = await shared.loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.none);
      expect(load.settings.toJson(), chosen.toJson());
      expect(
        names().where((name) => name.startsWith('settings.damaged-')),
        hasLength(MultiplayerStore.keptDamagedCopies),
      );
    });

    test('stays in place, so the known hosts salvaged from it last', () async {
      final typed = JoinAddressTarget(host: '127.0.0.1', port: 27250);
      final valid = KnownHosts.empty().remember(typed, hostAServerId, now);
      final document = jsonDecode(utf8.decode(valid.encode())) as Map;
      final damaged = CanonicalJson.encode({
        'hosts': [
          ...(document['hosts'] as List),
          {'target': 'nope', 'serverId': hostBServerId, 'seenAt': 1},
        ],
        'schema': document['schema'],
      });
      final file = inFolder(MultiplayerStore.knownHostsFileName)
        ..writeAsBytesSync(damaged);
      final shared = store();
      for (var attempt = 0; attempt < 2; attempt++) {
        final hosts = await _withFullDisk(shared.loadKnownHosts);
        // Another server at the remembered address is still a change.
        expect(hosts.check(typed, hostBServerId), KnownHostCheck.mismatch);
        expect(file.readAsBytesSync(), damaged);
      }

      final hosts = await shared.loadKnownHosts();
      expect(hosts.check(typed, hostBServerId), KnownHostCheck.mismatch);
      expect(file.readAsBytesSync(), valid.encode());
    });
  });

  test('only the damaged copies the store made are dropped', () async {
    final theirs = [
      // As Windows names a copy made beside one of the store's own.
      'settings.damaged-20261001T120000000Z - Copy.json',
      'settings.damaged-.json',
      'settings.damaged-mine.json',
    ];
    for (final name in theirs) {
      inFolder(name).writeAsStringSync('{}');
    }
    for (var minute = 0; minute < 4; minute++) {
      inFolder(MultiplayerStore.settingsFileName).writeAsStringSync('{');
      final load = await store(
        now.add(Duration(minutes: minute)),
      ).loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.reset);
    }
    expect(
      names(),
      [
        ...theirs,
        'settings.damaged-20261015T120100000Z.json',
        'settings.damaged-20261015T120200000Z.json',
        'settings.damaged-20261015T120300000Z.json',
        MultiplayerStore.settingsFileName,
      ]..sort(),
    );
  });
}

/// Runs [body] where every temporary file fails to write, as on a full disk,
/// so nothing the store writes reaches its place.
Future<T> _withFullDisk<T>(Future<T> Function() body) => IOOverrides.runZoned(
  body,
  createFile: (path) =>
      path.endsWith('.tmp') ? _FullDiskFile(path) : _realFile(path),
  // Inside an override zone the SDK's own asynchronous type lookup reads
  // every path as missing; its synchronous one does not.
  fseGetType: (path, followLinks) async =>
      FileSystemEntity.typeSync(path, followLinks: followLinks),
);

/// A temporary file that cannot be written, as on a full disk.
final class _FullDiskFile implements File {
  _FullDiskFile(this.path);

  @override
  final String path;

  @override
  Future<File> writeAsBytes(
    List<int> bytes, {
    FileMode mode = FileMode.write,
    bool flush = false,
  }) async => throw FileSystemException(
    'No space left on device',
    path,
    const OSError('No space left on device', 28),
  );

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

final class _Plain extends IOOverrides {}

final _plain = _Plain();

/// A file as `dart:io` makes one, whatever overrides are in force.
File _realFile(String path) =>
    IOOverrides.runWithIOOverrides(() => File(path), _plain);
