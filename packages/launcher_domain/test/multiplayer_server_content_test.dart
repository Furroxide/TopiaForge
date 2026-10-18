import 'dart:convert';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_support.dart';

/// Server profiles, the record of what joins installed, and cleanup plans.
void main() {
  ServerContentEntry entry(
    String id, {
    String version = '1.0.0',
    bool hidden = false,
    String receipt = '',
    int installedAt = 1791849600,
  }) => ServerContentEntry(
    modId: id,
    version: version,
    sha256: testSha(id, version),
    receiptSha256: receipt.isEmpty ? testSha('receipt:$id', version) : receipt,
    hidden: hidden,
    installedAt: installedAt,
  );

  group('server profiles', () {
    final derived = LauncherProfile(
      id: JoinPlanner.derivedProfileId(hostAServerId),
      name: "Server · Sam's Sandbox",
      enabledMods: const {'example.sandbox', 'example.tools'},
      selectedVersions: const {
        'example.sandbox': '1.2.0',
        'example.tools': '2.0.0',
      },
    );

    test('a first join saves the plan profile as it is', () {
      expect(ServerProfiles.merge(null, derived), same(derived));
    });

    test('a re-join replaces what the plan owns and keeps the rest', () {
      final existing = LauncherProfile(
        id: derived.id,
        name: 'Server · Old Name',
        enabledMods: const {'example.sandbox', 'example.retired'},
        selectedVersions: const {
          'example.sandbox': '1.0.0',
          'example.retired': '0.1.0',
        },
        configMetadata: const {'example.sandbox': 'tuned'},
        launchSettings: const LaunchSettings(
          safeMode: true,
          extraArguments: ['-windowed'],
          environment: {'TF_TRACE': '1'},
        ),
        launchSelection: LaunchSelection.unresolvedLegacy(const {
          'worldSelection': 'city',
        }),
        revision: 7,
        backupMetadata: const {'kept': true},
      );
      final merged = ServerProfiles.merge(existing, derived);
      expect(merged.id, derived.id);
      expect(merged.name, derived.name);
      expect(merged.inheritManagerModState, isFalse);
      expect(merged.enabledMods, derived.enabledMods);
      expect(merged.selectedVersions, derived.selectedVersions);
      expect(merged.launchSettings.safeMode, isFalse);
      expect(merged.launchSettings.extraArguments, ['-windowed']);
      expect(merged.launchSettings.environment, {'TF_TRACE': '1'});
      expect(
        merged.launchSelection.toJson(),
        existing.launchSelection.toJson(),
      );
      expect(merged.configMetadata, existing.configMetadata);
      expect(merged.backupMetadata, existing.backupMetadata);
      expect(merged.revision, 7);
      expect(ServerProfiles.isServerProfile(merged), isTrue);
    });

    test('a join never takes over a profile of the player own', () {
      final players = derived.copyWith(inheritManagerModState: true);
      expect(
        () => ServerProfiles.merge(players, derived),
        throwsA(isA<StateError>()),
      );
      expect(
        () => ServerProfiles.merge(null, players),
        throwsA(isA<ArgumentError>()),
      );
      expect(
        () => ServerProfiles.merge(
          derived.copyWith(id: 'mp-0000000000000000'),
          derived,
        ),
        throwsA(isA<ArgumentError>()),
      );
    });
  });

  group('the server content record', () {
    test('round trips in canonical form', () {
      final record = ServerContentRecord.empty().withInstalled(
        [
          entry('example.sandbox', hidden: true),
          entry('example.tools', version: '2.0.0'),
        ],
        addedRecords: ['Example.Tools'],
      );
      final bytes = record.encode();
      final read = ServerContentRecord.read(bytes)!;
      expect(read.damaged, isFalse);
      expect(read.record.encode(), bytes);
      expect(read.record.addedRecords, {'example.tools'});
      final first = read.record.entryFor('EXAMPLE.SANDBOX', '1.0.0')!;
      expect(first.hidden, isTrue);
      expect(first.sha256, testSha('example.sandbox'));
      expect(read.record.entryFor('example.sandbox', '1.0.1'), isNull);
      expect(
        jsonDecode(utf8.decode(bytes))['schema'],
        ServerContentRecord.schema,
      );
    });

    test('a later install of a version replaces its entry', () {
      final record = ServerContentRecord.empty()
          .withInstalled([entry('example.sandbox', receipt: 'a' * 64)])
          .withInstalled([entry('Example.Sandbox', receipt: 'b' * 64)]);
      expect(record.entries, hasLength(1));
      expect(record.entries.single.receiptSha256, 'b' * 64);
    });

    test('removing entries and added records', () {
      final record = ServerContentRecord.empty().withInstalled(
        [entry('example.sandbox'), entry('example.tools')],
        addedRecords: ['example.sandbox', 'example.tools'],
      );
      final removed = record.without(
        [entry('EXAMPLE.SANDBOX')],
        addedRecords: ['Example.Sandbox'],
      );
      expect(removed.entries.map((item) => item.modId), ['example.tools']);
      expect(removed.addedRecords, {'example.tools'});
      expect(removed.without([entry('example.tools')]).entries, isEmpty);
    });

    test('is bounded and refuses to pass its bound', () {
      final full = ServerContentRecord.empty().withInstalled([
        for (var index = 0; index < ServerContentRecord.maximumEntries; index++)
          entry('example.mod$index'),
      ]);
      expect(full.entries, hasLength(ServerContentRecord.maximumEntries));
      expect(full.encode().length, lessThan(ServerContentRecord.maximumBytes));
      expect(
        () => full.withInstalled([entry('example.onemore')]),
        throwsA(isA<StateError>()),
      );
      // Replacing an entry needs no room.
      expect(
        full.withInstalled([entry('example.mod3', receipt: 'c' * 64)]).entries,
        hasLength(ServerContentRecord.maximumEntries),
      );
    });

    test('salvages what is valid and says it was damaged', () {
      final good = entry('example.sandbox').toJson();
      final document = {
        'schema': ServerContentRecord.schema,
        'versions': [
          good,
          {...good, 'version': 'not-a-version'},
          {...good, 'sha256': 'XYZ'},
          {...good, 'installedAt': 1.5},
          {...good, 'extra': true},
          good,
          entry('example.tools').toJson(),
        ],
        'addedRecords': ['example.sandbox', 7, '../escape', 'example.sandbox'],
        'unknown': 1,
      };
      final read = ServerContentRecord.read(utf8.encode(jsonEncode(document)))!;
      expect(read.damaged, isTrue);
      expect(read.record.entries.map((item) => item.modId), [
        'example.sandbox',
        'example.tools',
      ]);
      expect(read.record.addedRecords, {'example.sandbox'});
    });

    test('reads a valid record written another way, as damaged', () {
      final record = ServerContentRecord.empty().withInstalled([
        entry('example.sandbox'),
      ]);
      final pretty = const JsonEncoder.withIndent(
        '  ',
      ).convert(jsonDecode(utf8.decode(record.encode())));
      final read = ServerContentRecord.read(utf8.encode(pretty))!;
      expect(read.damaged, isTrue);
      expect(read.record.encode(), record.encode());
    });

    test('nothing usable reads as null', () {
      for (final bytes in [
        utf8.encode('not json'),
        utf8.encode('[]'),
        utf8.encode(jsonEncode({'schema': 'topiaforge.server-content/2'})),
        utf8.encode(jsonEncode({'versions': []})),
        List.filled(ServerContentRecord.maximumBytes + 1, 0x20),
      ]) {
        expect(ServerContentRecord.read(bytes), isNull);
      }
    });

    test('entries refuse what is not an id, version or digest', () {
      for (final make in <ServerContentEntry Function()>[
        () => entry('../escape'),
        () => entry('example.sandbox', version: '1'),
        () => entry('example.sandbox', receipt: 'A' * 64),
        () => entry('example.sandbox', installedAt: -1),
      ]) {
        expect(make, throwsA(anyOf(isA<ArgumentError>(), isA<RangeError>())));
      }
    });
  });

  group('cleanup plans', () {
    ServerContentVersion version(
      String id, {
      int size = 100,
      List<String> servers = const ['mp-0000000000000001'],
      ServerContentKept? kept,
      String receipt = '',
    }) => ServerContentVersion(
      modId: id,
      version: '1.0.0',
      sha256: testSha(id),
      receiptSha256: receipt.isEmpty ? testSha('receipt:$id') : receipt,
      sizeBytes: size,
      serverProfileIds: servers,
      kept: kept,
    );

    const profile = ServerContentProfile(
      id: 'mp-0000000000000001',
      name: 'Server · One',
      revision: 2,
    );

    test('lists versions and profiles in order and sums what goes', () {
      final plan = ServerContentCleanupPlan(
        versions: [
          version('example.zeta', size: 5),
          version('example.alpha', size: 7),
          version('example.kept', kept: ServerContentKept.usedByProfile),
        ],
        profiles: [profile],
      );
      expect(plan.versions.map((item) => item.modId), [
        'example.alpha',
        'example.kept',
        'example.zeta',
      ]);
      expect(plan.removable.map((item) => item.modId), [
        'example.alpha',
        'example.zeta',
      ]);
      expect(plan.removableBytes, 12);
      expect(plan.isEmpty, isFalse);
      expect(
        ServerContentCleanupPlan(
          versions: const [],
          profiles: const [],
        ).isEmpty,
        isTrue,
      );
    });

    test('the fingerprint covers everything the plan acts on', () {
      ServerContentCleanupPlan make({
        int size = 100,
        String receipt = '',
        List<String> servers = const ['mp-0000000000000001'],
        ServerContentKept? kept,
        int revision = 2,
        Set<String>? scope,
        bool reversed = false,
      }) {
        final versions = [
          version('example.alpha'),
          version(
            'example.beta',
            size: size,
            receipt: receipt,
            servers: servers,
            kept: kept,
          ),
        ];
        return ServerContentCleanupPlan(
          versions: reversed ? versions.reversed : versions,
          profiles: [
            ServerContentProfile(
              id: profile.id,
              name: profile.name,
              revision: revision,
            ),
          ],
          scope: scope,
        );
      }

      final base = make().fingerprint;
      expect(make(reversed: true).fingerprint, base);
      for (final changed in [
        make(size: 101),
        make(receipt: 'd' * 64),
        make(servers: const ['mp-0000000000000002']),
        make(kept: ServerContentKept.pinnedByKeptServer),
        make(revision: 3),
        make(scope: const {'mp-0000000000000001'}),
      ]) {
        expect(changed.fingerprint, isNot(base));
      }
    });

    test('results say whether they were applied', () {
      const refused = ServerContentCleanupResult.refused(
        ServerContentCleanupRefusal.stale,
        'Plan again.',
      );
      expect(refused.applied, isFalse);
      expect(refused.freedBytes, 0);
      const applied = ServerContentCleanupResult.applied(
        removedVersions: ['example.alpha@1.0.0'],
        removedProfiles: ['mp-0000000000000001'],
        freedBytes: 7,
      );
      expect(applied.applied, isTrue);
      expect(applied.refusal, isNull);
    });
  });
}
