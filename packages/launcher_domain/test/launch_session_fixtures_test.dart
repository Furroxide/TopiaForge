import 'dart:convert';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';

/// Replays tests/fixtures/multiplayer/{launch-v5,progress-v2,channels}, which
/// the game's C# tests replay too, so both runtimes read and write the same
/// session documents byte for byte.
void main() {
  group('launch request V5 fixtures', () {
    final fixture = readMultiplayerFixture('launch-v5/cases.json');
    _replay(
      asObjects(fixture['valid']),
      asObjects(fixture['invalid']),
      (text) =>
          LaunchTransport.writeProfileV5(LaunchTransport.readProfileV5(text)),
    );

    for (final item in asObjects(fixture['valid'])) {
      test('${item['id']} reads its fields', () {
        final request = LaunchTransport.readProfileV5(item['bytes']! as String);
        final expected = asObject(item['fields']);
        final session = request.session;
        expect({
          'command': request.command,
          'role': session.role.name,
          'executableCommand': request.executable.command,
          'intentId': session.intentId,
          'displayName': session.displayName,
          'developerMode': session.developerMode,
          'privacy': session.transport.privacy.wireName,
          'consented': session.transport.directConsent != null,
          'port': session.transport.port,
          'serviceOrigin': session.serviceOrigin,
          'targetKind': session.target?.kind,
          'hostName': session.host?.name,
          'packageCount': request.packages.length,
        }, expected);
        expect(request.executable.safeMode, isFalse);
        expect(request.executable.toJson()['schemaVersion'], 4);
      });

      test('${item['id']} is staged as V5 by the request reader', () {
        final staged = StagedLaunchRequest.decode(item['bytes']! as String);
        expect(staged.schemaVersion, 5);
        expect(staged.encode(), item['bytes']);
        expect(staged.session, isNotNull);
      });
    }
  });

  // The session fixtures pin the servers descriptor/ describes, so a change
  // to the descriptor fixtures must regenerate these too.
  test('every pinned server is one the descriptor fixtures describe', () {
    final descriptors = asObjects(
      readMultiplayerFixture('descriptor/cases.json')['valid'],
    );
    final pins = {
      for (final item in descriptors)
        '${item['serverId']} ${item['descriptorSha256']} '
            '${item['packageDigest']}',
    };
    final servers = {for (final item in descriptors) item['serverId']};
    final channels = readMultiplayerFixture('channels/cases.json');
    final targets = [
      for (final item in asObjects(
        readMultiplayerFixture('launch-v5/cases.json')['valid'],
      ))
        LaunchTransport.readProfileV5(item['bytes']! as String).session.target,
      for (final item in asObjects(asObject(channels['join'])['valid']))
        LaunchTransport.readSessionJoin(
          item['bytes']! as String,
        ).session.target,
    ].nonNulls.toList();
    expect(targets, isNotEmpty);
    for (final target in targets) {
      expect(
        pins,
        contains(
          '${target.serverId} ${target.descriptorSha256} '
          '${target.packageDigest}',
        ),
      );
    }
    final listings = [
      for (final item in asObjects(asObject(channels['handoff'])['valid']))
        if (LaunchTransport.readSessionHandoff(item['bytes']! as String).target
            case LaunchHandoffListing(:final serverId))
          serverId,
    ];
    expect(listings, isNotEmpty);
    expect(servers, containsAll(listings));
  });

  group('progress V2 fixtures', () {
    final fixture = readMultiplayerFixture('progress-v2/cases.json');
    final progress = asObject(fixture['progress']);
    _replay(
      asObjects(progress['valid']),
      asObjects(progress['invalid']),
      (text) =>
          LaunchTransport.writeProgress(LaunchTransport.readProgress(text)),
    );
    final outcome = asObject(fixture['outcome']);
    _replay(
      asObjects(outcome['valid']),
      asObjects(outcome['invalid']),
      (text) => LaunchTransport.writeOutcome(LaunchTransport.readOutcome(text)),
    );

    test('every version 2 record reports version 2', () {
      for (final item in asObjects(progress['valid'])) {
        final value = LaunchTransport.readProgress(item['bytes']! as String);
        expect(value.schemaVersion, 2);
        expect(value.session, isNotNull);
      }
      for (final item in asObjects(outcome['valid'])) {
        final value = LaunchTransport.readOutcome(item['bytes']! as String);
        expect(value.schemaVersion, 2);
        expect(value.multiplayer, isNotNull);
      }
    });

    test('the failure codes are the closed set the fixture lists', () {
      final codes = asObject(fixture['failureCodes']);
      String role(LaunchMultiplayerFailure failure) =>
          failure.allows(LaunchSessionRole.host) ? 'host' : 'join';
      expect({
        'join': [
          for (final item in LaunchMultiplayerFailure.values)
            if (role(item) == 'join') item.wireName,
        ],
        'host': [
          for (final item in LaunchMultiplayerFailure.values)
            if (role(item) == 'host') item.wireName,
        ],
      }, codes);
      expect([
        for (final item in LaunchSessionEndReason.values) item.wireName,
      ], fixture['endReasons']);
    });
  });

  group('session channel fixtures', () {
    final fixture = readMultiplayerFixture('channels/cases.json');
    for (final (name, roundTrip) in <(String, String Function(String))>[
      (
        'handoff',
        (text) => LaunchTransport.writeSessionHandoff(
          LaunchTransport.readSessionHandoff(text),
        ),
      ),
      (
        'join',
        (text) => LaunchTransport.writeSessionJoin(
          LaunchTransport.readSessionJoin(text),
        ),
      ),
      (
        'preferences',
        (text) => LaunchTransport.writePreferences(
          LaunchTransport.readPreferences(text),
        ),
      ),
      (
        'journal',
        (text) => LaunchTransport.writePreferencesJournal(
          LaunchTransport.readPreferencesJournal(text),
        ),
      ),
      (
        'quit',
        (text) => LaunchTransport.writeLauncherQuit(
          LaunchTransport.readLauncherQuit(text),
        ),
      ),
    ]) {
      group(name, () {
        final section = asObject(fixture[name]);
        _replay(
          asObjects(section['valid']),
          asObjects(section['invalid']),
          roundTrip,
        );
      });
    }

    for (final item in asObjects(fixture['journalApply'])) {
      test('journal ${item['id']} applies as the launcher must', () {
        final projection = LaunchTransport.readPreferences(
          item['projection']! as String,
        );
        final journal = LaunchTransport.readPreferencesJournal(
          item['journal']! as String,
        );
        expect(
          LaunchTransport.writePreferences(journal.applyTo(projection)),
          item['result'],
        );
      });
    }
  });
}

void _replay(
  List<Map<String, Object?>> valid,
  List<Map<String, Object?>> invalid,
  String Function(String text) roundTrip,
) {
  for (final item in valid) {
    test('${item['id']} writes back exactly', () {
      expect(roundTrip(item['bytes']! as String), item['bytes']);
    });
    test('${item['id']} reads the same in any member order', () {
      expect(roundTrip(item['reordered']! as String), item['bytes']);
    });
    test('${item['id']} matches its document', () {
      expect(jsonDecode(item['bytes']! as String), item['document']);
    });
  }
  for (final item in invalid) {
    test('${item['id']} is refused', () {
      expect(
        () => roundTrip(item['text']! as String),
        throwsFormatException,
        reason: item['about'] as String?,
      );
    });
  }
}
