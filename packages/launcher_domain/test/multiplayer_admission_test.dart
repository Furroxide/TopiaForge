import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('admits mutually compatible required session mods', () {
    final report = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _session('example.gameplay', protocol: '1.2.0', hash: _hash('a')),
      ]),
      client: _profile('client', [
        _session('example.gameplay', protocol: '1.4.0', hash: _hash('a')),
      ]),
    );
    expect(report.isAdmitted, isTrue);
    expect(report.activeSessionMods, ['example.gameplay']);
  });

  test('required presence needs a session copy, not merely the same id', () {
    final clientMasquerade = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _session('example.gameplay', range: '1.0.0', hash: _hash('a')),
      ]),
      client: _profile('client', [
        _mode('example.gameplay', ModMultiplayerMode.clientLocal),
      ]),
    );
    final clientMissing = clientMasquerade.mismatches.singleWhere(
      (item) =>
          item.code == MultiplayerAdmissionMismatchCode.missingRequiredMod,
    );
    expect(clientMissing.serverValue, 'session/required');
    expect(clientMissing.clientValue, 'client-local');
    expect(clientMasquerade.activeSessionMods, isEmpty);

    final serverMasquerade = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _mode('example.gameplay', ModMultiplayerMode.serverOnly),
      ]),
      client: _profile('client', [
        _session('example.gameplay', range: '1.0.0', hash: _hash('a')),
      ]),
    );
    final serverMissing = serverMasquerade.mismatches.singleWhere(
      (item) =>
          item.code == MultiplayerAdmissionMismatchCode.missingRequiredMod,
    );
    expect(serverMissing.serverValue, 'server-only');
    expect(serverMissing.clientValue, 'session/required');
  });

  test('optional mods activate only after compatible negotiation', () {
    final optional = _session(
      'example.optional',
      range: '1.0.0',
      hash: _hash('a'),
      presence: ModMultiplayerPresence.optional,
    );
    final absent = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [optional]),
      client: _profile('client', []),
    );
    expect(absent.isAdmitted, isTrue);
    expect(absent.activeSessionMods, isEmpty);
    expect(
      absent.inactiveSessionReasons.map((item) => item.code),
      contains(MultiplayerAdmissionMismatchCode.optionalSessionModUnavailable),
    );

    final shared = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [optional]),
      client: _profile('client', [optional]),
    );
    expect(shared.isAdmitted, isTrue);
    expect(shared.activeSessionMods, ['example.optional']);

    final incompatible = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [optional]),
      client: _profile('client', [
        _session(
          'example.optional',
          protocol: '2.0.0',
          range: '2.0.0',
          hash: _hash('b'),
          presence: ModMultiplayerPresence.optional,
        ),
      ]),
    );
    expect(incompatible.isAdmitted, isTrue);
    expect(incompatible.activeSessionMods, isEmpty);
    expect(
      incompatible.inactiveSessionReasons.map((item) => item.code),
      contains(MultiplayerAdmissionMismatchCode.modProtocolMismatch),
    );

    final requiredByServer = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _session('example.optional', range: '1.0.0', hash: _hash('a')),
      ]),
      client: _profile('client', [
        _session(
          'example.optional',
          protocol: '2.0.0',
          range: '2.0.0',
          hash: _hash('b'),
          presence: ModMultiplayerPresence.optional,
        ),
      ]),
    );
    expect(
      requiredByServer.mismatches.map((item) => item.code),
      contains(MultiplayerAdmissionMismatchCode.modProtocolMismatch),
    );
    expect(requiredByServer.activeSessionMods, isEmpty);
  });

  test('generated contract lock hashes participate in admission', () {
    const lockPath = 'topiaforge.multiplayer.lock.json';
    final required = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _session('example.contract', hash: _hash('a'), path: lockPath),
      ]),
      client: _profile('client', [
        _session('example.contract', hash: _hash('b'), path: lockPath),
      ]),
    );
    expect(required.isAdmitted, isFalse);
    expect(
      required.mismatches.map((item) => item.code),
      contains(MultiplayerAdmissionMismatchCode.synchronizedContentMismatch),
    );
    expect(
      required.mismatches
          .singleWhere(
            (item) =>
                item.code ==
                MultiplayerAdmissionMismatchCode.synchronizedContentMismatch,
          )
          .message,
      contains(lockPath),
    );

    final optional = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _session(
          'example.contract',
          hash: _hash('a'),
          path: lockPath,
          presence: ModMultiplayerPresence.optional,
        ),
      ]),
      client: _profile('client', [
        _session(
          'example.contract',
          hash: _hash('b'),
          path: lockPath,
          presence: ModMultiplayerPresence.optional,
        ),
      ]),
    );
    expect(optional.isAdmitted, isTrue);
    expect(optional.activeSessionMods, isEmpty);
    expect(optional.mismatches, isEmpty);
    expect(
      optional.inactiveSessionReasons.map((item) => item.code),
      contains(MultiplayerAdmissionMismatchCode.synchronizedContentMismatch),
    );
  });

  test('enforces client-local and server-only logical sides', () {
    final allowed = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _mode('example.server', ModMultiplayerMode.serverOnly),
      ]),
      client: _profile('client', [
        _mode('example.client', ModMultiplayerMode.clientLocal),
      ]),
    );
    expect(allowed.isAdmitted, isTrue);

    final reversed = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _mode('example.client', ModMultiplayerMode.clientLocal),
      ]),
      client: _profile('client', [
        _mode('example.server', ModMultiplayerMode.serverOnly),
      ]),
    );
    expect(
      reversed.mismatches.map((item) => item.code),
      containsAll([
        MultiplayerAdmissionMismatchCode.clientLocalModOnServer,
        MultiplayerAdmissionMismatchCode.serverOnlyModOnClient,
      ]),
    );
  });

  test('requires mutual protocol compatibility and exact omitted ranges', () {
    final oneWay = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _session(
          'example.gameplay',
          protocol: '1.0.0',
          range: '>=1.0.0 <3.0.0',
          hash: _hash('a'),
        ),
      ]),
      client: _profile('client', [
        _session(
          'example.gameplay',
          protocol: '2.0.0',
          range: '>=2.0.0 <3.0.0',
          hash: _hash('a'),
        ),
      ]),
    );
    expect(
      oneWay.mismatches.map((item) => item.code),
      contains(MultiplayerAdmissionMismatchCode.modProtocolMismatch),
    );

    final omittedModRange = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _session(
          'example.gameplay',
          protocol: '1.0.0',
          range: null,
          hash: _hash('a'),
        ),
      ]),
      client: _profile('client', [
        _session(
          'example.gameplay',
          protocol: '1.1.0',
          range: '>=1.0.0 <2.0.0',
          hash: _hash('a'),
        ),
      ]),
    );
    expect(
      omittedModRange.mismatches.map((item) => item.code),
      contains(MultiplayerAdmissionMismatchCode.modProtocolMismatch),
    );

    final omittedTopiaForgeRange = const MultiplayerAdmissionPlanner().evaluate(
      server: _profileWithProtocol('server', '1.0.0'),
      client: _profileWithProtocol('client', '1.1.0', range: '>=1.0.0 <2.0.0'),
    );
    expect(
      omittedTopiaForgeRange.mismatches.map((item) => item.code),
      contains(MultiplayerAdmissionMismatchCode.topiaForgeProtocolMismatch),
    );
  });

  test('reports standalone, missing, protocol, and content failures', () {
    final standalone = _manifest('example.standalone');
    final report = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        MultiplayerAdmissionMod(manifest: standalone),
        _session('example.required', hash: _hash('a')),
        _session(
          'example.protocol',
          protocol: '1.0.0',
          range: '1.0.0',
          hash: _hash('a'),
        ),
      ]),
      client: _profile('client', [
        _session(
          'example.protocol',
          protocol: '2.0.0',
          range: '2.0.0',
          hash: _hash('b'),
        ),
      ]),
    );
    expect(
      report.mismatches.map((item) => item.code),
      containsAll([
        MultiplayerAdmissionMismatchCode.standaloneOnlyMod,
        MultiplayerAdmissionMismatchCode.missingRequiredMod,
        MultiplayerAdmissionMismatchCode.modProtocolMismatch,
        MultiplayerAdmissionMismatchCode.synchronizedContentMismatch,
      ]),
    );
  });

  test('invalid profiles fail structurally and deterministically', () {
    final invalidSession = ModManifest(
      schemaVersion: ModManifest.currentSchemaVersion,
      id: 'example.invalid',
      name: 'Invalid',
      version: '1.0.0',
      hashes: const {'Content/rules.json': 'not-a-sha'},
      multiplayer: const ModMultiplayerMetadata(
        modeName: 'session',
        presenceName: 'required',
        presenceIsPresent: true,
        synchronizedFiles: ['Content/rules.json'],
        synchronizedFilesIsPresent: true,
      ),
    );
    final report = const MultiplayerAdmissionPlanner().evaluate(
      server: MultiplayerAdmissionProfile(
        peerId: 'server',
        gameBuild: '0.0.2309',
        topiaForgeProtocolVersion: 'not-semver',
        topiaForgePeerVersionRange: 'bad range',
        mods: [
          _session('Example.Duplicate', hash: _hash('a')),
          _session('example.duplicate', hash: _hash('a')),
          MultiplayerAdmissionMod(manifest: invalidSession),
          MultiplayerAdmissionMod(
            manifest: _manifest('example.schema', schemaVersion: 4),
          ),
        ],
      ),
      client: _profile('client', []),
    );
    final invalid = report.mismatches
        .where(
          (item) =>
              item.code == MultiplayerAdmissionMismatchCode.invalidProfile,
        )
        .toList();
    expect(invalid.length, greaterThanOrEqualTo(4));
    expect(
      invalid,
      orderedEquals(
        [...invalid]..sort((left, right) {
          var comparison = left.modId.compareTo(right.modId);
          if (comparison != 0) return comparison;
          comparison = left.message.compareTo(right.message);
          return comparison != 0
              ? comparison
              : left.serverValue.compareTo(right.serverValue);
        }),
      ),
    );
    expect(
      report.mismatches.map((item) => item.code),
      isNot(
        contains(MultiplayerAdmissionMismatchCode.topiaForgeProtocolMismatch),
      ),
    );
  });

  test('exact profile is opt-in and curates the session inventory', () {
    final server = _profile('server', [
      _session(
        'example.gameplay',
        hash: _hash('a'),
        package: '1.0.0',
        archive: _hash('1'),
      ),
    ]);
    final client = _profile('client', [
      _session(
        'example.gameplay',
        hash: _hash('a'),
        package: '1.1.0',
        archive: _hash('2'),
      ),
    ]);
    expect(
      const MultiplayerAdmissionPlanner()
          .evaluate(server: server, client: client)
          .isAdmitted,
      isTrue,
    );
    final exact = const MultiplayerAdmissionPlanner().evaluate(
      server: server,
      client: client,
      policy: MultiplayerAdmissionPolicy.exactProfile,
    );
    expect(
      exact.mismatches.map((item) => item.code),
      contains(MultiplayerAdmissionMismatchCode.exactProfileMismatch),
    );
    expect(exact.activeSessionMods, isEmpty);

    final optionalInventory = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _session(
          'example.optional',
          hash: _hash('a'),
          presence: ModMultiplayerPresence.optional,
        ),
      ]),
      client: _profile('client', []),
      policy: MultiplayerAdmissionPolicy.exactProfile,
    );
    expect(
      optionalInventory.mismatches.map((item) => item.code),
      contains(MultiplayerAdmissionMismatchCode.exactProfileMismatch),
    );

    final missingArchive = const MultiplayerAdmissionPlanner().evaluate(
      server: _profile('server', [
        _session('example.hash', hash: _hash('a'), archive: ''),
      ]),
      client: _profile('client', [
        _session('example.hash', hash: _hash('a'), archive: ''),
      ]),
      policy: MultiplayerAdmissionPolicy.exactProfile,
    );
    expect(
      missingArchive.mismatches.map((item) => item.code),
      contains(MultiplayerAdmissionMismatchCode.exactProfileMismatch),
    );
  });
}

MultiplayerAdmissionProfile _profile(
  String id,
  List<MultiplayerAdmissionMod> mods,
) => MultiplayerAdmissionProfile(
  peerId: id,
  gameBuild: '0.0.2309',
  topiaForgeProtocolVersion: '1.0.0',
  topiaForgePeerVersionRange: '>=1.0.0 <2.0.0',
  mods: mods,
);

MultiplayerAdmissionProfile _profileWithProtocol(
  String id,
  String protocol, {
  String? range,
}) => MultiplayerAdmissionProfile(
  peerId: id,
  gameBuild: '0.0.2309',
  topiaForgeProtocolVersion: protocol,
  topiaForgePeerVersionRange: range,
  mods: const [],
);

MultiplayerAdmissionMod _session(
  String id, {
  String protocol = '1.0.0',
  String? range = '>=1.0.0 <2.0.0',
  required String hash,
  ModMultiplayerPresence presence = ModMultiplayerPresence.required,
  String package = '1.0.0',
  String? archive,
  String path = 'Content/rules.json',
}) {
  const lockPath = 'topiaforge.multiplayer.lock.json';
  final synchronizedFiles = <String>[lockPath, if (path != lockPath) path];
  return MultiplayerAdmissionMod(
    packageSha256: archive ?? _hash('f'),
    manifest: ModManifest(
      schemaVersion: ModManifest.currentSchemaVersion,
      id: id,
      name: id,
      version: package,
      hashes: {
        for (final synchronizedPath in synchronizedFiles)
          synchronizedPath: hash,
      },
      multiplayer: ModMultiplayerMetadata.session(
        presence: presence,
        protocolVersion: protocol,
        peerVersionRange: range,
        synchronizedFiles: synchronizedFiles,
      ),
    ),
  );
}

MultiplayerAdmissionMod _mode(String id, ModMultiplayerMode mode) =>
    MultiplayerAdmissionMod(
      packageSha256: _hash('f'),
      manifest: ModManifest(
        schemaVersion: ModManifest.currentSchemaVersion,
        id: id,
        name: id,
        version: '1.0.0',
        multiplayer: ModMultiplayerMetadata(modeName: mode.wireName),
      ),
    );

ModManifest _manifest(
  String id, {
  int schemaVersion = ModManifest.currentSchemaVersion,
  String version = '1.0.0',
}) => ModManifest(
  schemaVersion: schemaVersion,
  id: id,
  name: id,
  version: version,
);

String _hash(String value) => List.filled(64, value).join();
