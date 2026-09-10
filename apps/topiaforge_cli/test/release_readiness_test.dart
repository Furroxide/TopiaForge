import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_metadata_readiness.dart';
import 'package:topiaforge/src/release_readiness.dart';

part 'release_readiness_git_test_part.dart';

/// Shared across both halves of this suite: the parts are one library, so the
/// fixtures `setUpAll` fills are top-level rather than locals of `main`.
const targetSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
late String repositoryRoot;
late List<int> readinessBytes;
late List<int> schemaBytes;

void main() {
  setUpAll(() {
    repositoryRoot = _repositoryRoot();
    readinessBytes = File(
      p.join(repositoryRoot, releaseReadinessPath),
    ).readAsBytesSync();
    schemaBytes = File(
      p.join(repositoryRoot, releaseReadinessSchemaPath),
    ).readAsBytesSync();
  });

  test('current decision is an honest blocked exact-gate summary', () {
    final decision = _parse(readinessBytes, schemaBytes, targetSha: targetSha);

    expect(decision.releaseVersion, '0.1.0-rc.1');
    expect(decision.status, 'blocked');
    expect(decision.isReady, isFalse);
    expect(decision.gates, hasLength(12));
    expect(decision.gates.map((gate) => gate.id), [
      'P0-IP-01',
      'P0-OSS-01',
      'P0-PRIV-01',
      'P0-TRUST-01',
      'P0-CRED-01',
      'P0-WIN-01',
      'P0-GAME-01',
      'P0-HOST-01',
      'P0-CAND-01',
      'P1-UX-01',
      'P1-E2E-01',
      'P1-SUPPORT-01',
    ]);
    // Two advisory gates carry a recorded owner approval (2026-08-28); the other
    // ten are unmet and carry no evidence. Naming the approved pair here means a
    // third approval appearing without a register entry fails this test.
    final approved = {
      for (final gate in decision.gates)
        if (gate.status == 'approved') gate.id: gate.evidenceIds,
    };
    expect(approved, {
      'P0-TRUST-01': ['EVID-P0-TRUST-01-0001'],
      'P1-SUPPORT-01': ['EVID-P1-SUPPORT-01-0001'],
    });
    expect(
      decision.gates
          .where((gate) => gate.status != 'approved')
          .every(
            (gate) => gate.status == 'blocked' && gate.evidenceIds.isEmpty,
          ),
      isTrue,
    );
    // Every blocking gate is unmet, so the computed status is decided entirely
    // by which of them the contract declares blocking. Pin that set: silently
    // downgrading one is exactly the change this file exists to catch.
    expect(
      decision.gates
          .where((gate) => gate.enforcement == 'blocking')
          .map((gate) => gate.id),
      ['P0-IP-01', 'P0-OSS-01', 'P0-PRIV-01', 'P0-CRED-01', 'P0-GAME-01'],
    );
    expect(decision.gates.where((gate) => gate.blocksRelease), hasLength(5));

    final publicSummary = decision.toPublicSummary();
    expect(publicSummary.keys, {
      'schema',
      'releaseVersion',
      'targetSha',
      'readinessBlobSha256',
      'status',
      'gates',
    });
    for (final gate in publicSummary['gates']! as List) {
      expect((gate as Map)['enforcement'], anyOf('advisory', 'blocking'));
    }

    final encoded = jsonEncode(publicSummary);
    for (final forbidden in const [
      'hostname',
      'username',
      'localPath',
      'timestamp',
      'credential',
      'rawLog',
    ]) {
      expect(encoded, isNot(contains('"$forbidden"')));
    }
  });

  test('readiness and BOM schemas define exactly the same gate contract', () {
    // The gate contract is duplicated: once in the readiness schema the
    // candidate is validated against, and once in the BOM schema the published
    // summary is validated against. A change applied to only one copy makes a
    // decision that validates at signing time fail at publication, so pin them
    // to each other here rather than relying on both being remembered.
    final readinessSchema = _jsonFile(
      p.join(repositoryRoot, releaseReadinessSchemaPath),
    );
    final bomSchema = _jsonFile(
      p.join(repositoryRoot, 'schemas', 'topiaforge.release-bom.schema.json'),
    );

    final readinessGate = _at(readinessSchema, ['definitions', 'gate']);
    final bomGate = _at(bomSchema, ['definitions', 'readinessGate']);

    for (final field in const [
      'id',
      'reasonCode',
      'status',
      'priority',
      'enforcement',
    ]) {
      expect(
        _at(bomGate, ['properties', field])['enum'],
        _at(readinessGate, ['properties', field])['enum'],
        reason:
            'Gate "$field" enum differs between the readiness and BOM '
            'schemas.',
      );
    }

    expect(
      bomGate['required'],
      readinessGate['required'],
      reason:
          'Gate required fields differ between the readiness and BOM schemas.',
    );

    expect(
      _at(bomGate, ['properties', 'reviewerRoles', 'items'])['enum'],
      _at(readinessSchema, ['definitions', 'reviewerRole'])['enum'],
      reason: 'Reviewer roles differ between the readiness and BOM schemas.',
    );

    // The accepted-risk block is duplicated too. If the allowed scopes drift, a
    // P1 disposition could validate at signing time and fail BOM validation at
    // publication, which is the same split this test exists to prevent.
    final readinessRisk = _at(readinessSchema, ['definitions', 'acceptedRisk']);
    final bomRisk = _at(bomGate, ['properties', 'acceptedRisk']);
    expect(
      _at(bomRisk, ['properties', 'scope'])['enum'],
      _at(readinessRisk, ['properties', 'scope'])['enum'],
      reason:
          'Accepted-risk scopes differ between the readiness and BOM '
          'schemas.',
    );
    expect(
      bomRisk['required'],
      readinessRisk['required'],
      reason:
          'Accepted-risk required fields differ between the readiness and '
          'BOM schemas.',
    );

    final readinessGates = _at(readinessSchema, ['properties', 'gates']);
    final bomGates = _at(bomSchema, [
      'definitions',
      'readinessSummary',
      'properties',
      'gates',
    ]);
    for (final bound in const ['minItems', 'maxItems']) {
      expect(
        bomGates[bound],
        readinessGates[bound],
        reason:
            'Gate count "$bound" differs between the readiness and BOM '
            'schemas.',
      );
    }
    expect(
      readinessGates['minItems'],
      readinessGates['maxItems'],
      reason: 'The readiness gate set must be an exact count.',
    );
  });

  test('P0 requires approval and P1 permits scoped accepted risk', () {
    final ready = _readinessJson(readinessBytes);
    for (final rawGate in ready['gates']! as List) {
      final gate = rawGate as Map;
      final id = gate['id']! as String;
      gate['status'] = 'approved';
      gate.remove('reasonCode');
      gate['evidenceIds'] = ['EVID-$id-0001'];
    }
    ready['status'] = 'ready';

    final p1 = (ready['gates']! as List).cast<Map>().singleWhere(
      (gate) => gate['id'] == 'P1-UX-01',
    );
    p1['status'] = 'accepted-risk';
    p1['acceptedRisk'] = {
      'scope': 'rc1-native-ux-accessibility',
      'decisionEvidenceId': 'EVID-P1-UX-01-0001',
    };
    expect(_parseJson(ready, schemaBytes).isReady, isTrue);

    p1['acceptedRisk'] = {
      'scope': 'rc1-support-incident-ownership',
      'decisionEvidenceId': 'EVID-P1-UX-01-0001',
    };
    expect(
      () => _parseJson(ready, schemaBytes),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('invalid accepted-risk scope'),
        ),
      ),
    );

    final p0 = (ready['gates']! as List).cast<Map>().singleWhere(
      (gate) => gate['id'] == 'P0-IP-01',
    );
    p0['status'] = 'accepted-risk';
    p0['acceptedRisk'] = {
      'scope': 'rc1-native-ux-accessibility',
      'decisionEvidenceId': 'EVID-P0-IP-01-0001',
    };
    expect(
      () => _parseJson(ready, schemaBytes),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('schema-invalid'),
        ),
      ),
    );
  });

  test('advisory gates are reported but do not hold the candidate', () {
    // The 0.x posture: approving only the five blocking gates reaches `ready`
    // while the advisory gates that remain unmet are still recorded as blocked.
    // Two of the seven advisory gates are already approved in the decision, so
    // five stay blocked.
    final ready = _readinessJson(readinessBytes);
    const blocking = {
      'P0-IP-01',
      'P0-OSS-01',
      'P0-PRIV-01',
      'P0-CRED-01',
      'P0-GAME-01',
    };
    for (final rawGate in ready['gates']! as List) {
      final gate = rawGate as Map;
      final id = gate['id']! as String;
      if (!blocking.contains(id)) continue;
      gate['status'] = 'approved';
      gate.remove('reasonCode');
      gate['evidenceIds'] = ['EVID-$id-0001'];
    }
    ready['status'] = 'ready';

    final decision = _parseJson(ready, schemaBytes);
    expect(decision.isReady, isTrue);
    expect(
      decision.gates.where((gate) => gate.status == 'blocked'),
      hasLength(5),
    );

    // One blocking gate left unapproved still stops the release.
    final held = _readinessJson(utf8.encode(jsonEncode(ready)))
      ..['status'] = 'blocked';
    final ip = (held['gates']! as List).cast<Map>().singleWhere(
      (gate) => gate['id'] == 'P0-IP-01',
    );
    ip['status'] = 'blocked';
    ip['reasonCode'] = 'approval-evidence-missing';
    ip['evidenceIds'] = <String>[];
    expect(_parseJson(held, schemaBytes).isReady, isFalse);
  });

  test('rejects a decision that relaxes its own enforcement', () {
    // The decision file is untrusted input. If it could declare a blocking gate
    // advisory, a candidate could unblock itself by editing one word.
    final relaxed = _readinessJson(readinessBytes);
    (relaxed['gates']! as List).cast<Map>().singleWhere(
      (gate) => gate['id'] == 'P0-IP-01',
    )['enforcement'] = 'advisory';
    expect(
      () => _parseJson(relaxed, schemaBytes),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('wrong identity'),
        ),
      ),
    );

    final unknownEnforcement = _readinessJson(readinessBytes);
    (unknownEnforcement['gates']! as List).cast<Map>().singleWhere(
      (gate) => gate['id'] == 'P0-WIN-01',
    )['enforcement'] = 'optional';
    expect(
      () => _parseJson(unknownEnforcement, schemaBytes),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('schema-invalid'),
        ),
      ),
    );
  });

  test('rejects aggregate, exact-set, role, and unknown-field drift', () {
    final aggregate = _readinessJson(readinessBytes)..['status'] = 'ready';
    expect(
      () => _parseJson(aggregate, schemaBytes),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('status does not match'),
        ),
      ),
    );

    final reordered = _readinessJson(readinessBytes);
    final reorderedGates = reordered['gates']! as List;
    final first = reorderedGates.removeAt(0);
    reorderedGates.insert(1, first);
    expect(
      () => _parseJson(reordered, schemaBytes),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('wrong identity'),
        ),
      ),
    );

    final wrongRole = _readinessJson(readinessBytes);
    ((wrongRole['gates']! as List).first as Map)['reviewerRoles'] = [
      'project-owner',
    ];
    expect(
      () => _parseJson(wrongRole, schemaBytes),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('reviewer-role contract'),
        ),
      ),
    );

    final unknownField = _readinessJson(readinessBytes)
      ..['reviewerName'] = 'must-not-be-public';
    expect(
      () => _parseJson(unknownField, schemaBytes),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('schema-invalid'),
        ),
      ),
    );
  });

  _exactTargetCommitTests();
}

ReleaseReadinessDecision _parse(
  List<int> readinessBytes,
  List<int> schemaBytes, {
  required String targetSha,
}) {
  return ReleaseReadinessDecision.fromCandidateBlobs(
    readinessBytes: readinessBytes,
    schemaBytes: schemaBytes,
    targetSha: targetSha,
    expectedReleaseVersion: '0.1.0-rc.1',
  );
}

ReleaseReadinessDecision _parseJson(
  Map<String, Object?> readiness,
  List<int> schemaBytes,
) {
  return _parse(
    utf8.encode(jsonEncode(readiness)),
    schemaBytes,
    targetSha: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
  );
}

Map<String, Object?> _readinessJson(List<int> bytes) {
  return (jsonDecode(utf8.decode(bytes)) as Map).cast<String, Object?>();
}

Map<String, Object?> _jsonFile(String path) =>
    (jsonDecode(File(path).readAsStringSync()) as Map).cast<String, Object?>();

Map<String, Object?> _at(Map<String, Object?> root, List<String> path) {
  var current = root;
  for (var depth = 0; depth < path.length; depth++) {
    final value = current[path[depth]];
    if (value is! Map) {
      // Name the exact path that broke. Without this the caller sees a bare
      // TypeError and has to rediscover which schema drifted.
      throw StateError(
        'Schema path "${path.take(depth + 1).join('.')}" is missing or is not '
        'an object; the readiness and BOM schemas have drifted apart.',
      );
    }
    current = value.cast<String, Object?>();
  }
  return current;
}

JsonSchema _readinessBomSchema(String repositoryRoot) {
  final complete =
      jsonDecode(
            File(
              p.join(
                repositoryRoot,
                'schemas',
                'topiaforge.release-bom.schema.json',
              ),
            ).readAsStringSync(),
          )
          as Map;
  return JsonSchema.create({
    r'$schema': 'http://json-schema.org/draft-07/schema#',
    r'$ref': '#/definitions/readiness',
    'definitions': complete['definitions'],
  });
}

String _git(String root, List<String> arguments) {
  final result = Process.runSync(
    'git',
    ['-C', root, ...arguments],
    stdoutEncoding: utf8,
    stderrEncoding: utf8,
  );
  if (result.exitCode != 0) {
    throw StateError('git ${arguments.first} failed in readiness test.');
  }
  return result.stdout as String;
}

String _repositoryRoot() {
  var directory = Directory.current.absolute;
  while (!File(p.join(directory.path, 'TopiaForge.slnx')).existsSync()) {
    if (directory.parent.path == directory.path) {
      throw StateError('Repository root not found.');
    }
    directory = directory.parent;
  }
  return directory.path;
}
