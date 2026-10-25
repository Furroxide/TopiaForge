import 'dart:convert';
import 'dart:typed_data';

import 'package:launcher_data/src/multiplayer/package_response.dart';
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

/// Replays `tests/fixtures/multiplayer/package-answer/case.json`, which the
/// game's host-package tests serve through `DescriptorEndpoint` and compare
/// byte for byte, so the C# writer and this reader stay pinned to each other.
void main() {
  final fixture = readMultiplayerFixture('package-answer/case.json');
  final answer = base64Decode(fixture['answerBase64']! as String);
  final archive = base64Decode(fixture['archiveBase64']! as String);

  test('the fixture describes its own archive', () {
    expect(archive, hasLength(fixture['length']));
    expect(answer, [
      ...latin1.encode('TFD1 200 ${archive.length}\n'),
      ...archive,
    ]);
  });

  test('the shared package answer reads whole and split at every byte', () {
    for (var cut = 0; cut <= answer.length; cut++) {
      final reader = Tfd1PackageReader();
      final body = <int>[
        ...reader.add(Uint8List.sublistView(answer, 0, cut)),
        ...reader.add(Uint8List.sublistView(answer, cut)),
      ];
      final result = reader.close();
      expect(
        result,
        isA<Tfd1PackageComplete>().having(
          (complete) => complete.length,
          'length',
          archive.length,
        ),
        reason: 'split at $cut',
      );
      expect(body, archive, reason: 'split at $cut');
    }
  });
}
