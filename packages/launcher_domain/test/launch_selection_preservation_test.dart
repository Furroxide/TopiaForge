import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

Map<String, Object?> legacyRoundTrip(Map<String, Object?> source) {
  final saved = LauncherProfile.fromJson(source).toJson();
  final selection = saved['launchSelection'];
  if (selection is Map && selection['kind'] == 'unresolved-legacy') {
    return Map<String, Object?>.from(selection['legacy'] as Map);
  }
  return {
    if (saved.containsKey('worldSelection'))
      'worldSelection': saved['worldSelection'],
  };
}

void main() {
  test('durable selection version requires an integer JSON token', () {
    expect(
      () =>
          LaunchSelection.fromJson({'schemaVersion': 1.0, 'kind': 'main-menu'}),
      throwsFormatException,
    );
  });
  const identity = {'id': 'profile', 'name': 'Profile'};
  test('durable revision cannot exceed the shared wire integer', () {
    expect(
      () => LauncherProfile.fromJson({...identity, 'revision': 2147483648}),
      throwsFormatException,
    );
  });
  test('explicit null revision cannot become revision zero', () {
    expect(
      () => LauncherProfile.fromJson({...identity, 'revision': null}),
      throwsFormatException,
    );
  });
  test('unknown legacy transition survives profile read and write', () {
    final legacy = {
      'worldId': 'example.world',
      'gamemodeId': 'example.mode',
      'loadMode': 'future-transition',
      'launchIntoGamemode': true,
    };
    expect(legacyRoundTrip({...identity, 'worldSelection': legacy}), {
      'worldSelection': legacy,
    });
  });
  test('absent legacy selection is not populated with retired defaults', () {
    expect(legacyRoundTrip(identity), isEmpty);
  });
  test('explicit null legacy selection remains explicit null', () {
    expect(legacyRoundTrip({...identity, 'worldSelection': null}), {
      'worldSelection': null,
    });
  });
  test('partial legacy fields keep property presence and extra values', () {
    final legacy = {
      'gamemodeId': 'example.mode',
      'loadMode': null,
      'launchIntoGamemode': false,
      'extension': {
        'value': [1, null, false],
      },
    };
    expect(legacyRoundTrip({...identity, 'worldSelection': legacy}), {
      'worldSelection': legacy,
    });
  });
  test('malformed legacy values remain available for explicit repair', () {
    const legacy = {'worldId': 42, 'gamemodeId': null};
    expect(legacyRoundTrip({...identity, 'worldSelection': legacy}), {
      'worldSelection': legacy,
    });
  });
}
