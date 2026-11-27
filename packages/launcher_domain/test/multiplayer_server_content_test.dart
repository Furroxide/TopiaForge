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
          entry('example.tools', version: '2.0.0'),
          entry('example.sandbox', hidden: true),
        ],
        addedRecords: ['Example.Tools'],
      );
      final bytes = record.encode();
      final read = ServerContentRecord.read(bytes);
      expect(read.status, ServerContentStatus.valid);
      expect(read.record!.encode(), bytes);
      expect(read.record!.addedRecords, {'example.tools'});
      final first = read.record!.entryFor('EXAMPLE.SANDBOX', '1.0.0')!;
      expect(first.hidden, isTrue);
      expect(first.sha256, testSha('example.sandbox'));
      expect(read.record!.entryFor('example.sandbox', '1.0.1'), isNull);
      final document = jsonDecode(utf8.decode(bytes)) as Map;
      expect(document['schemaVersion'], ServerContentRecord.schemaVersion);
      expect(
        (document['versions'] as List).map((item) => (item as Map)['id']),
        ['example.sandbox', 'example.tools'],
        reason: 'entries are written in the order the contract sorts them',
      );
    });

    test('withholds hidden and withheld versions by their receipts', () {
      final record = ServerContentRecord.empty()
          .withInstalled([
            entry('example.sandbox', hidden: true, receipt: 'a' * 64),
            entry('example.tools', receipt: 'b' * 64),
          ])
          .withWithheld([
            WithheldServerVersion(
              modId: 'example.maps',
              version: '1.0.0',
              receiptSha256: 'c' * 64,
            ),
          ]);
      expect(record.withholds('Example.Sandbox', '1.0.0', 'a' * 64), isTrue);
      expect(
        record.withholds('example.sandbox', '1.0.0', 'f' * 64),
        isFalse,
        reason: 'a version installed again since is the player\'s',
      );
      expect(record.withholds('example.sandbox', '1.0.1', 'a' * 64), isFalse);
      expect(
        record.withholds('example.tools', '1.0.0', 'b' * 64),
        isFalse,
        reason: 'a mod new to the player stays visible, its record disabled',
      );
      expect(record.withholds('example.maps', '1.0.0', 'c' * 64), isTrue);
      expect(record.withholds('example.maps', '1.0.0', 'd' * 64), isFalse);
      for (final (id, expected) in [
        ('example.sandbox', true),
        ('example.maps', true),
        ('example.tools', false),
        ('example.other', false),
      ]) {
        expect(
          record.withholds(id, '1.0.0', null),
          expected,
          reason: 'a receipt that cannot be read fails closed for $id',
        );
      }
    });

    test('a later install of a version replaces its entry', () {
      final record = ServerContentRecord.empty()
          .withInstalled([entry('example.sandbox', receipt: 'a' * 64)])
          .withInstalled([entry('Example.Sandbox', receipt: 'b' * 64)]);
      expect(record.entries, hasLength(1));
      expect(record.entries.single.receiptSha256, 'b' * 64);
    });

    test('an install replaces a version withheld after a repair', () {
      final record = ServerContentRecord.empty()
          .withWithheld([
            WithheldServerVersion(
              modId: 'example.sandbox',
              version: '1.0.0',
              receiptSha256: 'a' * 64,
            ),
          ])
          .withInstalled([entry('Example.Sandbox', hidden: true)]);
      expect(record.withheld, isEmpty);
      expect(record.entries, hasLength(1));
    });

    test('removing entries, withheld versions and added records', () {
      final maps = WithheldServerVersion(
        modId: 'example.maps',
        version: '1.0.0',
        receiptSha256: 'c' * 64,
      );
      final record = ServerContentRecord.empty()
          .withInstalled(
            [entry('example.sandbox'), entry('example.tools')],
            addedRecords: ['example.sandbox', 'example.tools'],
          )
          .withWithheld([maps]);
      final removed = record.without(
        [entry('EXAMPLE.SANDBOX')],
        addedRecords: ['Example.Sandbox'],
      );
      expect(removed.entries.map((item) => item.modId), ['example.tools']);
      expect(removed.addedRecords, {'example.tools'});
      expect(removed.withheld, [maps]);
      expect(removed.without(const [], unwithheld: [maps]).withheld, isEmpty);
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
      expect(
        () => ServerContentRecord.empty().withWithheld([
          for (
            var index = 0;
            index <= ServerContentRecord.maximumEntries;
            index++
          )
            WithheldServerVersion(
              modId: 'example.mod$index',
              version: '1.0.0',
              receiptSha256: 'a' * 64,
            ),
        ]),
        throwsA(isA<StateError>()),
      );
    });

    test('reads none of a damaged record, and salvage keeps what reads', () {
      final good = entry('example.sandbox', hidden: true).toJson();
      final document = {
        'schemaVersion': ServerContentRecord.schemaVersion,
        'versions': [
          good,
          {...good, 'version': 'not-a-version'},
          {...good, 'sha256': 'XYZ'},
          {...good, 'installedAt': 1.5},
          {...good, 'extra': true},
          good,
          entry('example.tools').toJson(),
        ],
        'withheld': [
          {'id': 'example.maps', 'version': '1.0.0', 'receipt': 'c' * 64},
          {'id': 'example.maps', 'version': '1.0', 'receipt': 'c' * 64},
        ],
        'addedRecords': ['example.sandbox', 7, '../escape', 'example.sandbox'],
        'unknown': 1,
      };
      final bytes = utf8.encode(jsonEncode(document));
      final read = ServerContentRecord.read(bytes);
      expect(read.status, ServerContentStatus.damaged);
      expect(read.record, isNull);
      final salvaged = ServerContentRecord.salvage(bytes);
      expect(salvaged.entries.map((item) => item.modId), [
        'example.sandbox',
        'example.tools',
      ]);
      expect(salvaged.withheld.map((item) => item.modId), ['example.maps']);
      expect(salvaged.addedRecords, {'example.sandbox'});
      for (final nothing in [
        utf8.encode('not json'),
        utf8.encode('[]'),
        List.filled(ServerContentRecord.maximumBytes + 1, 0x20),
      ]) {
        expect(ServerContentRecord.salvage(nothing).isEmpty, isTrue);
      }
    });

    test('reads a valid record written another way', () {
      final record = ServerContentRecord.empty().withInstalled([
        entry('example.sandbox'),
      ]);
      final pretty = const JsonEncoder.withIndent(
        '  ',
      ).convert(jsonDecode(utf8.decode(record.encode())));
      final read = ServerContentRecord.read(utf8.encode(pretty));
      expect(read.status, ServerContentStatus.valid);
      expect(read.record!.encode(), record.encode());
    });

    test('says why bytes are not a record', () {
      for (final (bytes, status) in [
        (utf8.encode('not json'), ServerContentStatus.damaged),
        (utf8.encode('[]'), ServerContentStatus.damaged),
        (
          utf8.encode(jsonEncode({'versions': []})),
          ServerContentStatus.damaged,
        ),
        (
          utf8.encode(jsonEncode({'schema': 'topiaforge.server-content/1'})),
          ServerContentStatus.damaged,
        ),
        (
          List.filled(ServerContentRecord.maximumBytes + 1, 0x20),
          ServerContentStatus.damaged,
        ),
        (
          utf8.encode(jsonEncode({'schemaVersion': 2, 'future': true})),
          ServerContentStatus.newer,
        ),
      ]) {
        final read = ServerContentRecord.read(bytes);
        expect(read.status, status);
        expect(read.record, isNull);
        expect(read.problem, isNotEmpty);
      }
      expect(ServerContentStatus.valid.isUsable, isTrue);
      expect(ServerContentStatus.missing.isUsable, isTrue);
      for (final status in [
        ServerContentStatus.newer,
        ServerContentStatus.damaged,
        ServerContentStatus.unreadable,
      ]) {
        expect(status.isUsable, isFalse);
      }
    });

    test('entries refuse what is not an id, version or digest', () {
      for (final make in <Object Function()>[
        () => entry('../escape'),
        () => entry('example.sandbox', version: '1'),
        () => entry('example.sandbox', receipt: 'A' * 64),
        () => entry('example.sandbox', installedAt: -1),
        () => WithheldServerVersion(
          modId: 'example sandbox',
          version: '1.0.0',
          receiptSha256: 'a' * 64,
        ),
        () => WithheldServerVersion(
          modId: 'example.sandbox',
          version: '1.0',
          receiptSha256: 'a' * 64,
        ),
        () => WithheldServerVersion(
          modId: 'example.sandbox',
          version: '1.0.0',
          receiptSha256: 'a' * 63,
        ),
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
