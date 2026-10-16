import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_metadata_readiness.dart';

import 'release_candidate_fixture.dart';

/// Publication metadata for a candidate whose live game acceptance was not
/// run. The BOM carries the qualified summary exactly, so it must show
/// P0-GAME-01 blocked and bind the not-run record rather than claim a run.
void main() {
  late CandidateFixture fixture;
  late JsonSchema bomReadiness;
  setUpAll(() async {
    fixture = await CandidateFixture.create(liveGameAcceptanceRun: false);
    final bom =
        jsonDecode(
              File(
                p.join(
                  Directory.current.parent.parent.path,
                  'schemas',
                  'topiaforge.release-bom.schema.json',
                ),
              ).readAsStringSync(),
            )
            as Map;
    bomReadiness = JsonSchema.create({
      r'$schema': 'http://json-schema.org/draft-07/schema#',
      r'$ref': '#/definitions/readiness',
      'definitions': bom['definitions'],
    });
  });
  tearDownAll(() => fixture.dispose());

  Future<ReleaseMetadataReadiness> load({bool allowUnresolved = false}) =>
      ReleaseMetadataReadiness.load(
        repositoryRoot: fixture.root.path,
        version: CandidateFixture.version,
        targetSha: fixture.sha,
        assetsDirectory: fixture.assets.path,
        allowUnresolved: allowUnresolved,
      );

  test(
    'publication metadata binds the not-run qualification exactly',
    () async {
      final qualified = await fixture.qualify();
      final readiness = await load();
      expect(readiness.status, 'ready');
      expect(readiness.blockingReasons, isEmpty);
      expect(readiness.toBomJson(), qualified.toBomJson());
      final summary = readiness.summary!;
      expect(
        summary['acceptanceSha256'],
        fileDigest(fixture.asset('release-candidate-acceptance-v1.json')),
      );
      expect(fixture.acceptance['result'], 'not-run');
    },
  );

  test('the BOM schema accepts the summary with GAME still blocked', () async {
    final bom = (await load()).toBomJson();
    final result = bomReadiness.validate(bom);
    expect(result.isValid, isTrue, reason: result.errors.join('\n'));
    final gates = ((bom['summary']! as Map)['gates']! as List).cast<Map>();
    final game = gates.singleWhere((gate) => gate['id'] == 'P0-GAME-01');
    expect(game['status'], 'blocked');
    expect(game['enforcement'], 'advisory');
    expect(game['reasonCode'], 'acceptance-evidence-missing');
    expect(game['evidenceIds'], isEmpty);
    expect(
      gates
          .where((gate) => gate['status'] == 'accepted-risk')
          .map((gate) => gate['id']),
      ['P1-UX-01', 'P1-E2E-01'],
    );
  });

  test('a changed not-run record cannot reach publication metadata', () async {
    final record = fixture.asset('release-candidate-acceptance-v1.json');
    final original = record.readAsBytesSync();
    addTearDown(() => record.writeAsBytesSync(original));
    record.writeAsStringSync('\n', mode: FileMode.append);
    await expectLater(load(), throwsStateError);
    final unresolved = await load(allowUnresolved: true);
    expect(unresolved.status, 'unavailable');
    expect(unresolved.summary, isNull);
    expect(bomReadiness.validate(unresolved.toBomJson()).isValid, isTrue);
  });
}
