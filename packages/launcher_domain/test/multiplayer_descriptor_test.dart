import 'dart:convert';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';

void main() {
  final fixture = readMultiplayerFixture('descriptor/cases.json');
  final keys = {
    for (final key in asObjects(fixture['keys'])) key['id']! as String: key,
  };
  final valid = asObjects(fixture['valid']);
  final invalid = asObjects(fixture['invalid']);

  test('the fixtures sign under the shared context label', () {
    expect(fixture['context'], 'TopiaForge server descriptor v1\n');
    expect(
      SignedServerDescriptorV1.signatureContext,
      textBytes(fixture['context']! as String),
    );
    expect(SignedServerDescriptorV1.signatureContext, hasLength(32));
  });

  group('valid descriptors', () {
    for (final item in valid) {
      final id = item['id']! as String;
      test('$id is written byte for byte', () {
        final key = keys[item['key']]!;
        final descriptor = descriptorFromFixture(asObject(item['descriptor']));
        expect(descriptor.serverId, key['serverId']);
        expect(descriptor.serverId, item['serverId']);
        expect(descriptor.packageDigest, item['packageDigest']);
        expect(
          descriptor.canonicalBytes,
          textBytes(item['canonicalDescriptor']! as String),
        );
        final signature = WireText.tryParseBase64Url(
          item['signature']! as String,
          SignedServerDescriptorV1.signatureBytes,
        )!;
        final envelope = SignedServerDescriptorV1.assemble(
          descriptor,
          signature,
        );
        expect(envelope.bytes, textBytes(item['signedBytes']! as String));
        expect(envelope.sha256, item['descriptorSha256']);
        expect(envelope.signedMessage, [
          ...SignedServerDescriptorV1.signatureContext,
          ...textBytes(item['canonicalDescriptor']! as String),
        ]);
      });

      test('$id reads back and verifies against its server id', () async {
        final bytes = textBytes(item['signedBytes']! as String);
        final signatures = KnownSignatures.fromFixture(fixture);
        final verifier = ServerDescriptorVerifier(signatures);
        final result = await verifier.verify(
          bytes,
          expectedServerId: item['serverId']! as String,
        );
        final verified = (result as ServerDescriptorAccepted).descriptor;
        final expected = descriptorFromFixture(asObject(item['descriptor']));
        expect(verified.serverId, item['serverId']);
        expect(verified.descriptorSha256, item['descriptorSha256']);
        expect(verified.packageDigest, item['packageDigest']);
        expect(verified.descriptor.name, expected.name);
        expect(verified.descriptor.packages, expected.packages);
        expect(verified.transportKey, expected.transportKey);
        expect(verified.envelope.bytes, bytes);
        expect(signatures.asked, hasLength(1));
        expect(
          await verifier.verify(bytes),
          isA<ServerDescriptorAccepted>(),
          reason: 'verifies without an expected server id',
        );
        final parsed = ServerDescriptorV1.fromJson(
          CanonicalJson.decode(
            textBytes(item['canonicalDescriptor']! as String),
            maximumBytes: SignedServerDescriptorV1.maximumBytes,
          ),
        );
        expect(parsed.canonicalBytes, expected.canonicalBytes);
      });
    }
  });

  group('tampered descriptors', () {
    const signatureCases = {
      'signature-flipped',
      'signature-without-context',
      'signed-by-other-key',
    };

    test('every case is refused, each for its own reason', () async {
      expect(invalid.length, greaterThanOrEqualTo(30));
      for (final item in invalid) {
        final id = item['id']! as String;
        final bytes = item.containsKey('bytesHex')
            ? hexBytes(item['bytesHex']! as String)
            : textBytes(item['bytes']! as String);
        final signatures = KnownSignatures.fromFixture(fixture);
        final result = await ServerDescriptorVerifier(
          signatures,
        ).verify(bytes, expectedServerId: item['expectedServerId'] as String?);
        expect(result, isA<ServerDescriptorRefused>(), reason: id);
        final refused = result as ServerDescriptorRefused;
        expect(refused.message, isNotEmpty, reason: id);
        final expected = signatureCases.contains(id)
            ? ServerDescriptorRefusal.badSignature
            : id == 'other-server-expected'
            ? ServerDescriptorRefusal.otherServer
            : ServerDescriptorRefusal.malformed;
        expect(refused.refusal, expected, reason: '$id: ${item['about']}');
        expect(
          refused.servedServerId,
          id == 'other-server-expected' ? keys['host-a']!['serverId'] : null,
          reason: '$id: only another server names the server it is',
        );
        expect(
          signatures.asked,
          hasLength(signatureCases.contains(id) ? 1 : 0),
          reason: '$id: only a well-formed descriptor reaches the signature',
        );
      }
    });

    test('a verifier that throws is a refusal, not an acceptance', () async {
      for (final thrown in <Object>[
        const FormatException('broken key'),
        StateError('broken key'),
        ArgumentError('broken key'),
      ]) {
        for (final synchronous in [false, true]) {
          final result = await ServerDescriptorVerifier(
            _ThrowingSignatures(thrown, synchronous: synchronous),
          ).verify(textBytes(valid.first['signedBytes']! as String));
          expect(
            (result as ServerDescriptorRefused).refusal,
            ServerDescriptorRefusal.badSignature,
            reason: '${thrown.runtimeType}, synchronous: $synchronous',
          );
        }
      }
    });

    test('refusals name the rule, never the host-chosen value', () async {
      const rules = {
        'bad-visibility': ('friends', 'Visibility is public, invite or lan.'),
        'bad-role': (
          '"required"',
          'A package role is session-required, session-optional or implicit.',
        ),
      };
      for (final item in invalid) {
        final rule = rules[item['id']];
        if (rule == null) continue;
        final refused =
            await const ServerDescriptorVerifier(
                  AcceptAllSignatures(),
                ).verify(textBytes(item['bytes']! as String))
                as ServerDescriptorRefused;
        expect(refused.message, rule.$2);
        expect(refused.message, isNot(contains(rule.$1)));
      }
    });

    test('input that is not bytes is refused, not thrown', () async {
      final result = await const ServerDescriptorVerifier(
        AcceptAllSignatures(),
      ).verify(const [0x7B, 0x17D, 0x7D]);
      expect(
        (result as ServerDescriptorRefused).refusal,
        ServerDescriptorRefusal.malformed,
      );
    });
  });

  group('descriptor rules', () {
    final baseline = descriptorFromFixture(asObject(valid.first['descriptor']));

    ServerDescriptorV1 rebuild({
      List<int>? signingKey,
      int? issuedAt,
      String? name,
      String? gameBuild,
      String? runtimeVersion,
      int? maxPlayers,
      String? gamemode,
      List<DescriptorPackage>? packages,
    }) => ServerDescriptorV1(
      signingKey: signingKey ?? baseline.signingKey,
      transportKey: baseline.transportKey,
      issuedAt: issuedAt ?? baseline.issuedAt,
      name: name ?? baseline.name,
      gameBuild: gameBuild ?? baseline.gameBuild,
      runtimeVersion: runtimeVersion ?? baseline.runtimeVersion,
      protocolVersion: baseline.protocolVersion,
      maxPlayers: maxPlayers ?? baseline.maxPlayers,
      passwordRequired: baseline.passwordRequired,
      voice: baseline.voice,
      visibility: baseline.visibility,
      hostServesPackages: baseline.hostServesPackages,
      gamemode: gamemode ?? baseline.gamemode,
      world: baseline.world,
      packages: packages ?? baseline.packages,
    );

    DescriptorPackage package(String id) => DescriptorPackage(
      id: id,
      version: '1.0.0',
      sha256: PureSha256.hex(utf8.encode(id)),
      role: DescriptorPackageRole.implicit,
    );

    test('refuses what it cannot represent', () {
      expect(
        () => rebuild(packages: [package('b.mod'), package('a.mod')]),
        throwsArgumentError,
      );
      expect(
        () => rebuild(packages: [package('a.mod'), package('a.mod')]),
        throwsArgumentError,
      );
      expect(() => rebuild(name: 'line\nbreak'), throwsArgumentError);
      expect(() => rebuild(name: '\ud800 lone'), throwsArgumentError);
      expect(() => rebuild(name: ' padded'), throwsArgumentError);
      expect(() => rebuild(gamemode: 'g' * 49), throwsArgumentError);
      expect(() => rebuild(maxPlayers: 1), throwsRangeError);
      expect(() => rebuild(maxPlayers: 17), throwsRangeError);
      expect(() => rebuild(issuedAt: -1), throwsRangeError);
      expect(() => rebuild(issuedAt: 9007199254740992), throwsRangeError);
      expect(() => rebuild(signingKey: Uint8List(31)), throwsArgumentError);
      expect(() => rebuild(gameBuild: 'x' * 17), throwsArgumentError);
      expect(() => rebuild(runtimeVersion: '0.2'), throwsArgumentError);
      expect(rebuild(maxPlayers: 2).maxPlayers, 2);
      expect(rebuild(maxPlayers: 16).maxPlayers, 16);
      expect(rebuild(name: 'n' * 48).name, hasLength(48));
    });

    test('package entries are mod ids, semantic versions and lowercase '
        'digests', () {
      DescriptorPackage make(String id, String version, String sha) =>
          DescriptorPackage(
            id: id,
            version: version,
            sha256: sha,
            role: DescriptorPackageRole.implicit,
          );
      final digest = '0' * 64;
      expect(() => make('a.mod', '1.0', digest), throwsArgumentError);
      expect(() => make('a', '1.0.0', digest), throwsArgumentError);
      expect(() => make('-a.mod', '1.0.0', digest), throwsArgumentError);
      expect(() => make('a.mod', '1.0.0', 'A' * 64), throwsArgumentError);
      expect(() => make('a.mod', '1.0.0', '0' * 63), throwsArgumentError);
      expect(
        make('A.Mod_2-x', '1.0.0-rc.1+build.7', digest).pin,
        'A.Mod_2-x@1.0.0-rc.1+build.7@$digest',
      );
      expect(DescriptorPackageRole.tryParse('required'), isNull);
    });

    test('versions and builds take no trailing line feed', () {
      // .NET's `$` would also match before a final line feed; these refuse it.
      expect(ServerDescriptorV1.isSemanticVersion('1.0.0'), isTrue);
      expect(ServerDescriptorV1.isSemanticVersion('1.0.0\n'), isFalse);
      expect(ServerDescriptorV1.isGameBuild('2545'), isTrue);
      expect(ServerDescriptorV1.isGameBuild('2545\n'), isFalse);
      expect(DescriptorPackage.isModId('a.mod\n'), isFalse);
    });

    test('semantic versions follow SemVer 2.0.0 within 64 characters', () {
      for (final version in [
        '0.0.0',
        '1.2.3-0a',
        '1.2.3-alpha.1',
        '1.2.3+build.01',
        '1.0.0-${'a' * 58}',
      ]) {
        expect(
          ServerDescriptorV1.isSemanticVersion(version),
          isTrue,
          reason: version,
        );
      }
      for (final version in [
        '01.0.0',
        '1.0',
        '1.0.0-01',
        '1.0.0-',
        '1.0.0+',
        'v1.0.0',
        '1.0.0-${'a' * 59}',
        '',
      ]) {
        expect(
          ServerDescriptorV1.isSemanticVersion(version),
          isFalse,
          reason: version,
        );
      }
    });

    test('pre-release identifiers split one way, so hostile versions are '
        'refused at once', () {
      for (final version in [
        '1.0.0-alpha',
        '1.0.0-0.3.7',
        '1.0.0-x.7.z.92',
        '1.0.0-x-y-z.--',
        '1.0.0--',
        '1.0.0-00a.1-',
        '1.0.0-alpha+001',
        '1.0.0+21AF26D3----117B344092BD',
      ]) {
        expect(
          ServerDescriptorV1.isSemanticVersion(version),
          isTrue,
          reason: version,
        );
      }
      for (final version in [
        '1.0.0-00',
        '1.0.0-a..b',
        '1.0.0-a.',
        '1.0.0-a_b',
      ]) {
        expect(
          ServerDescriptorV1.isSemanticVersion(version),
          isFalse,
          reason: version,
        );
      }
      // Were an identifier of hyphens splittable at each hyphen, a
      // backtracking matcher would try every split of every identifier
      // before refusing these: 0.6 seconds for the second one alone.
      final hostile = [
        '0.0.0-0.${'--.' * 18}-.',
        '0.0.0-0.${'----.' * 11}.',
        // Beyond 64 characters, refused before the pattern runs.
        '0.0.0-0.${'--.' * 19}',
        '0.0.0-0.${'--.' * 100000}',
      ];
      expect(hostile.take(2).map((version) => version.length), [64, 64]);
      final watch = Stopwatch()..start();
      for (final version in hostile) {
        expect(ServerDescriptorV1.isSemanticVersion(version), isFalse);
      }
      expect(watch.elapsedMilliseconds, lessThan(100));
    });

    test('512 packages fit within 256 KiB and 513 do not', () async {
      final most = [
        for (var index = 0; index < ServerDescriptorV1.maximumPackages; index++)
          package('mod.${index.toString().padLeft(4, '0')}'),
      ];
      final full = SignedServerDescriptorV1.assemble(
        rebuild(packages: most),
        Uint8List(64),
      );
      expect(
        full.bytes.length,
        lessThanOrEqualTo(SignedServerDescriptorV1.maximumBytes),
      );
      expect(
        await const ServerDescriptorVerifier(
          AcceptAllSignatures(),
        ).verify(full.bytes, expectedServerId: baseline.serverId),
        isA<ServerDescriptorAccepted>(),
      );
      expect(
        () => rebuild(packages: [...most, package('mod.9999')]),
        throwsArgumentError,
      );
      final oversized = Uint8List(SignedServerDescriptorV1.maximumBytes + 1)
        ..fillRange(0, SignedServerDescriptorV1.maximumBytes + 1, 0x20);
      final refused =
          await const ServerDescriptorVerifier(
                AcceptAllSignatures(),
              ).verify(oversized)
              as ServerDescriptorRefused;
      expect(refused.refusal, ServerDescriptorRefusal.malformed);
      expect(refused.message, contains('larger'));
    });

    test('members must agree with the key and the packages', () {
      final text = valid.first['canonicalDescriptor']! as String;
      final otherServer = keys['host-b']!['serverId']! as String;
      final cases = {
        text.replaceAll(valid.first['serverId']! as String, otherServer):
            'server id',
        text.replaceAll(valid.first['packageDigest']! as String, '0' * 64):
            'package digest',
      };
      cases.forEach((changed, expected) {
        final document = CanonicalJson.decode(
          textBytes(changed),
          maximumBytes: SignedServerDescriptorV1.maximumBytes,
        );
        expect(
          () => ServerDescriptorV1.fromJson(document),
          throwsA(
            isA<FormatException>().having(
              (error) => error.message,
              'message',
              contains(expected),
            ),
          ),
        );
      });
    });

    test('keeps its own copies of keys and packages', () {
      final key = Uint8List.fromList(baseline.signingKey);
      final packages = [package('a.mod')];
      final descriptor = rebuild(signingKey: key, packages: packages);
      key[0] ^= 0xFF;
      packages.add(package('b.mod'));
      expect(descriptor.signingKey, baseline.signingKey);
      expect(descriptor.packages, hasLength(1));
      expect(() => descriptor.signingKey[0] = 1, throwsUnsupportedError);
      expect(() => descriptor.packages.clear(), throwsUnsupportedError);
      expect(descriptor.toJson().keys.toSet(), {
        'schema',
        'serverId',
        'signingKey',
        'transportKey',
        'issuedAt',
        'name',
        'gameBuild',
        'runtimeVersion',
        'protocolVersion',
        'maxPlayers',
        'passwordRequired',
        'voice',
        'visibility',
        'hostServesPackages',
        'gamemode',
        'world',
        'packages',
        'packageDigest',
      });
    });

    test('an envelope needs a 64-byte signature', () {
      expect(
        () => SignedServerDescriptorV1.assemble(baseline, Uint8List(63)),
        throwsArgumentError,
      );
      expect(
        () => SignedServerDescriptorV1.parse(
          textBytes(
            (valid.first['signedBytes']! as String).replaceFirst(
              valid.first['signature']! as String,
              (valid.first['signature']! as String).substring(1),
            ),
          ),
        ),
        throwsFormatException,
      );
    });
  });
}

final class _ThrowingSignatures implements ServerDescriptorSignatureVerifier {
  const _ThrowingSignatures(this.thrown, {required this.synchronous});

  final Object thrown;
  final bool synchronous;

  @override
  Future<bool> verifySignature(SignedServerDescriptorV1 envelope) {
    if (synchronous) throw thrown;
    return Future.error(thrown);
  }
}
