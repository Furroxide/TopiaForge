import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  final malformed = <String, Map<String, Object?>>{
    'null enabled collection': {'enabledMods': null},
    'scalar enabled collection': {'enabledMods': 'example.mod'},
    'null enabled entry': {
      'enabledMods': ['example.mod', null],
    },
    'numeric enabled entry': {
      'enabledMods': [42],
    },
    'empty enabled entry': {
      'enabledMods': [''],
    },
    'duplicate enabled entries': {
      'enabledMods': ['example.mod', 'example.mod'],
    },
    'case folded duplicate enabled entries': {
      'enabledMods': ['Example.Mod', 'example.mod'],
    },
    'null pin collection': {'selectedVersions': null},
    'array pin collection': {'selectedVersions': []},
    'numeric pin value': {
      'selectedVersions': {'example.mod': 1},
    },
    'null pin value': {
      'selectedVersions': {'example.mod': null},
    },
    'case folded duplicate pins': {
      'selectedVersions': {'Example.Mod': '1.0.0', 'example.mod': '1.0.0'},
    },
    'coerced inherit flag': {'inheritManagerModState': 'true'},
    'null inherit flag': {'inheritManagerModState': null},
    'null launch settings': {'launchSettings': null},
    'scalar launch settings': {'launchSettings': 42},
    'coerced arguments': {
      'launchSettings': {
        'extraArguments': [42],
      },
    },
    'omitted null arguments': {
      'launchSettings': {
        'extraArguments': [null],
      },
    },
    'null argument collection': {
      'launchSettings': {'extraArguments': null},
    },
    'coerced environment': {
      'launchSettings': {
        'environment': {'EXAMPLE': false},
      },
    },
    'null environment collection': {
      'launchSettings': {'environment': null},
    },
  };
  for (final entry in malformed.entries) {
    test('raw profile refuses ${entry.key} without changing selections', () {
      expect(
        () => LauncherProfile.fromJson({
          'id': 'profile',
          'name': 'Profile',
          ...entry.value,
        }),
        throwsFormatException,
      );
    });
  }
  test('valid empty argument and explicit false keep their meaning', () {
    final profile = LauncherProfile.fromJson({
      'id': 'profile',
      'name': 'Profile',
      'inheritManagerModState': false,
      'launchSettings': {
        'safeMode': false,
        'extraArguments': ['', '--flag'],
        'environment': {'EXAMPLE': ''},
      },
    });
    expect(profile.launchSettings.extraArguments, ['', '--flag']);
    expect(profile.launchSettings.environment, {'EXAMPLE': ''});
    expect(profile.inheritManagerModState, isFalse);
  });
}
