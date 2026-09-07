import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

ProfileLaunchConfigurationV4 command({
  String profileId = 'profile',
  bool safe = false,
  bool inherit = false,
  List<String> enabled = const [],
  Map<String, String> versions = const {},
  List<PackageIdentity> packages = const [],
}) => ProfileLaunchConfigurationV4(
  profileId: profileId,
  profileRevision: 0,
  requestId: 'request',
  command: 'main-menu',
  safeMode: safe,
  inheritManagerModState: inherit,
  enabledMods: enabled,
  selectedVersions: versions,
  packages: packages,
);

void main() {
  test('V4 copies exact package and version selections', () {
    final enabled = ['alpha.mod'];
    final versions = {'alpha.mod': '1.0.0'};
    final packages = [PackageIdentity(id: 'alpha.mod', version: '1.0.0')];
    final configuration = command(
      enabled: enabled,
      versions: versions,
      packages: packages,
    );
    enabled.clear();
    versions.clear();
    packages.clear();
    expect(configuration.enabledMods, ['alpha.mod']);
    expect(configuration.selectedVersions, {'alpha.mod': '1.0.0'});
    expect(configuration.packages.single.id, 'alpha.mod');
    expect(configuration.toJson()['schemaVersion'], 4);
    expect(() => configuration.enabledMods.clear(), throwsUnsupportedError);
    expect(
      () => configuration.selectedVersions.clear(),
      throwsUnsupportedError,
    );
  });
  test('explicit main menu is present even without a gameplay plan', () {
    final wire = command().toJson();
    expect(wire['command'], 'main-menu');
    expect(wire['requestId'], 'request');
    expect(wire, isNot(contains('plan')));
    expect(wire, isNot(contains('worldLaunch')));
  });
  test('exact empty profile remains distinct from inheritance', () {
    const exact = LauncherProfile(id: 'empty', name: 'Empty');
    expect(
      LauncherProfile.fromJson(exact.toJson()).inheritManagerModState,
      isFalse,
    );
    expect(
      LauncherProfile.fromJson({
        'id': 'missing',
        'name': 'Missing',
      }).inheritManagerModState,
      isFalse,
    );
    expect(LauncherProfile.defaultProfile().inheritManagerModState, isTrue);
    expect(command().inheritManagerModState, isFalse);
    expect(command(inherit: true).inheritManagerModState, isTrue);
  });
  test(
    'safe mode has no effective packages or process environment in wire',
    () {
      final configuration = command(safe: true);
      expect(configuration.safeMode, isTrue);
      expect(configuration.packages, isEmpty);
      expect(configuration.toJson(), isNot(contains('environment')));
      expect(
        () => command(
          safe: true,
          packages: [PackageIdentity(id: 'alpha.mod', version: '1.0.0')],
        ),
        throwsFormatException,
      );
    },
  );
  test('unsafe identities and malformed versions are rejected', () {
    expect(() => command(profileId: 'unsafe\nprofile'), throwsFormatException);
    expect(() => command(enabled: ['../escape']), throwsFormatException);
    expect(
      () => command(versions: {'alpha.mod': '1.0'}),
      throwsFormatException,
    );
    expect(
      () => command(enabled: ['alpha.mod', 'ALPHA.MOD']),
      throwsFormatException,
    );
    expect(
      () => command(versions: {'alpha.mod': '1.0.0', 'ALPHA.MOD': '1.0.0'}),
      throwsFormatException,
    );
  });
  test(
    'malformed or retired declaration IDs cannot enter target wire plans',
    () {
      for (final id in [
        '../escape',
        'robo'
            'topia.world.retired',
      ]) {
        expect(() => LaunchRequest(targetId: id), throwsFormatException);
        expect(
          () => LaunchRequest(targetId: 'valid.target', worldOverride: id),
          throwsFormatException,
        );
      }
    },
  );
}
