import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_bloc.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_event.dart';

/// A snapshot that has not loaded leaves stand-ins in the state, and every
/// save of profiles, package sources or update settings replaces what is on
/// disk with what the state holds. So until a snapshot loads, none of those
/// saves is made, and the player's own files, which the repository read
/// before the snapshot failed, stay as they are.
void main() {
  const notLoaded =
      'The launcher state has not loaded, so nothing was changed. Refresh it, '
      'then try again.';

  Future<LauncherBloc> started(_Repository repository) async {
    final bloc = LauncherBloc(repository)..add(const LauncherStarted());
    await _until(() => repository.loads == 1 && !bloc.state.isBusy);
    return bloc;
  }

  test('a snapshot that failed to load is never saved over the player\'s '
      'profiles, sources or update settings', () async {
    final repository = _Repository()..loadFailure = const _Unreadable();
    final bloc = await started(repository);
    expect(bloc.state.snapshotLoaded, isFalse);
    expect(bloc.state.errorMessage, startsWith('Launcher settings'));

    final refusals = <LauncherEvent, String>{
      const ProfileCreated(): 'Profile could not be saved.',
      const PackageSourceAdded(name: 'Alex', url: 'https://example.test/a'):
          'Package sources could not be saved.',
      const PackageSourceEnabledChanged(_samSourceId, false):
          'Package sources could not be saved.',
      const PackageSourceRemoved(_samSourceId):
          'Package sources could not be saved.',
      const LauncherUpdateSettingsChanged(checkAutomatically: false):
          'Update settings could not be saved.',
    };
    var marker = 0;
    for (final MapEntry(key: event, value: status) in refusals.entries) {
      bloc.add(const ModSearchChanged('before'));
      bloc.add(event);
      // Events run one after another, so once this one has run, so has
      // [event].
      bloc.add(ModSearchChanged('after ${++marker}'));
      await _until(() => bloc.state.modSearch == 'after $marker');
      expect(repository.saves, isEmpty, reason: '$event');
      expect(bloc.state.statusMessage, status, reason: '$event');
      expect(bloc.state.errorMessage, notLoaded, reason: '$event');
    }

    // Once a snapshot loads, saves build on the player's own.
    repository.loadFailure = null;
    bloc.add(const LauncherRefreshRequested());
    await _until(() => repository.loads == 2 && !bloc.state.isBusy);
    expect(bloc.state.snapshotLoaded, isTrue);
    bloc.add(
      const PackageSourceAdded(name: 'Alex', url: 'https://example.test/a'),
    );
    await _until(() => repository.saves.isNotEmpty && !bloc.state.isBusy);
    expect(repository.saves.single, startsWith('sources '));
    expect(repository.savedSources.map((source) => source.id), [
      _samSourceId,
      startsWith('source-'),
    ]);
    bloc.add(const ProfileCreated());
    await _until(() => repository.saves.length == 2);
    expect(repository.savedProfiles.map((profile) => profile.id), [
      'sam-build',
      startsWith('profile-'),
    ]);
    await bloc.close();
  });

  test(
    'a setting that cannot be saved shows why, and changes nothing',
    () async {
      final repository = _Repository();
      final bloc = await started(repository);
      repository.saveFailure = const _Unreadable();

      bloc.add(const DeveloperModeToggled(true));
      bloc.add(const ModSearchChanged('toggled'));
      await _until(() => bloc.state.modSearch == 'toggled');
      expect(bloc.state.statusMessage, 'Action failed.');
      expect(
        bloc.state.errorMessage,
        startsWith('Launcher settings could not'),
      );
      expect(bloc.state.developerMode, isFalse);

      bloc.add(const LauncherRefreshRequested());
      await _until(() => repository.loads == 2 && !bloc.state.isBusy);
      expect(bloc.state.errorMessage, isNull);
      bloc.add(const LauncherUpdateSettingsChanged(checkAutomatically: false));
      bloc.add(const ModSearchChanged('changed'));
      await _until(() => bloc.state.modSearch == 'changed');
      expect(bloc.state.statusMessage, 'Action failed.');
      expect(
        bloc.state.errorMessage,
        startsWith('Launcher settings could not'),
      );
      expect(bloc.state.launcherUpdates.checkAutomatically, isTrue);
      expect(repository.saves, ['developer mode', 'update settings']);
      await bloc.close();
    },
  );
}

const _samSourceId = 'sam.packages';

/// What the launcher's repository throws while a file it saves cannot be
/// read just now: its message names no path.
final class _Unreadable implements Exception {
  const _Unreadable();

  @override
  String toString() =>
      'Launcher settings could not be read just now, so nothing was changed.';
}

Future<void> _until(bool Function() done) async {
  for (var attempt = 0; attempt < 500 && !done(); attempt++) {
    await Future<void>.delayed(const Duration(milliseconds: 2));
  }
  expect(done(), isTrue, reason: 'The bloc did not settle.');
}

/// Records each save it is asked to make. Its snapshot holds the player's
/// own profile and package source.
final class _Repository implements LauncherRepository {
  Object? loadFailure;
  Object? saveFailure;
  int loads = 0;
  final saves = <String>[];
  List<PackageSource> savedSources = const [];
  List<LauncherProfile> savedProfiles = const [];
  final _activities = StreamController<LaunchActivity>.broadcast();

  @override
  String get dataRoot => 'data';

  @override
  Stream<LaunchActivity> get launchActivities => _activities.stream;

  @override
  Future<void> dispose() => _activities.close();

  @override
  Future<LauncherSnapshot> loadSnapshot() async {
    loads++;
    final failure = loadFailure;
    if (failure != null) throw failure;
    return LauncherSnapshot(
      profiles: [
        LauncherProfile.defaultProfile().copyWith(
          id: 'sam-build',
          name: "Sam's build",
        ),
      ],
      selectedProfileId: 'sam-build',
      installedMods: const [],
      registryMods: const [],
      packageSources: const [
        PackageSource(
          id: _samSourceId,
          name: "Sam's packages",
          url: 'https://example.test/sam',
        ),
      ],
      recentLog: '',
    );
  }

  Future<void> _save(String what) async {
    saves.add(what);
    final failure = saveFailure;
    if (failure != null) throw failure;
  }

  @override
  Future<List<PackageSource>> savePackageSources(
    List<PackageSource> sources,
  ) async {
    await _save('sources ${sources.length}');
    return savedSources = sources;
  }

  @override
  Future<List<LauncherProfile>> saveProfiles(
    List<LauncherProfile> profiles,
    String selectedProfileId,
  ) async {
    await _save('profiles ${profiles.length}');
    return savedProfiles = profiles;
  }

  @override
  Future<void> setDeveloperMode(bool enabled) => _save('developer mode');

  @override
  Future<void> saveLauncherUpdateSettings(LauncherUpdateSettings settings) =>
      _save('update settings');

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
