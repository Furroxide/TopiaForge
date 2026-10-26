import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';

final class LocalCacheFixture {
  LocalCacheFixture._(this.root, this.game, this.data);

  final Directory root;
  final Directory game;
  final Directory data;
  late LocalLauncherRepository repository;
  late GameInstall install;

  static Future<LocalCacheFixture> create() async {
    final root = Directory.systemTemp.createTempSync('tf-local-cache-');
    addTearDown(() => root.deleteSync(recursive: true));
    final fixture = LocalCacheFixture._(
      root,
      createTestGame(root),
      Directory(p.join(root.path, 'data')),
    );
    fixture.repository = fixture.open();
    fixture.install = await fixture.repository.selectGameDirectory(
      fixture.game.path,
    );
    await fixture.sources();
    return fixture;
  }

  LocalLauncherRepository open({
    PackageInstallCommitHook? hook,
    PackageMetadataValidator? validator,
  }) {
    final repository = LocalLauncherRepository(
      dataRoot: data.path,
      repositoryRoot: root.path,
      packageMetadataValidator: validator ?? (_) async => const [],
      packageInstallCommitHook: hook,
      gameRunningProbe: (_) async => false,
    );
    addTearDown(repository.dispose);
    return repository;
  }

  Directory get cache => Directory(p.join(data.path, 'package-cache'));
  File cached(TestArchive archive) =>
      File(p.join(cache.path, '${archive.sha256Hex}.topiaforgemod'));
  File receipt(TestArchive archive) => File(
    p.join(
      game.path,
      'BepInEx',
      'TopiaForge',
      'packages',
      archive.id,
      archive.version,
      'topiaforge.install.json',
    ),
  );
  List<File> get archives => cache.existsSync()
      ? cache
            .listSync()
            .whereType<File>()
            .where((file) => p.extension(file.path) == '.topiaforgemod')
            .toList()
      : [];

  Future<void> put(
    TestArchive archive, {
    LocalLauncherRepository? using,
  }) async {
    await (using ?? repository).installPackage(
      archive.writeTo(root).path,
      install,
    );
  }

  Future<void> sources([List<TestArchive> packages = const []]) async {
    final source = File(p.join(root.path, 'source.json'))
      ..writeAsStringSync(
        jsonEncode({
          'packages': {
            for (final archive in packages)
              archive.id: {
                'versions': {
                  archive.version: {
                    'manifest': archive.manifestJson,
                    'url': archive.writeTo(root).uri.toString(),
                    'zipSHA256': archive.sha256Hex,
                  },
                },
              },
          },
        }),
      );
    await repository.savePackageSources([
      const PackageSource(
        id: 'io.github.furroxide.topiaforge.local',
        name: 'Bundled Local Packages',
        url: '.',
        enabled: false,
        builtIn: true,
      ),
      const PackageSource(
        id: ModRegistryFormat.officialSourceId,
        name: ModRegistryFormat.officialSourceName,
        url: ModRegistryFormat.officialRegistryUrl,
        enabled: false,
        builtIn: true,
      ),
      if (packages.isNotEmpty)
        PackageSource(
          id: 'test.local',
          name: 'Local',
          url: source.uri.toString(),
        ),
    ]);
  }

  String get log =>
      File(p.join(data.path, 'logs', 'launcher.log')).readAsStringSync();

  /// Starts another process that holds the package cache lease, shared or
  /// exclusive, the way a second TopiaForge process holds it.
  Future<LeaseHolder> holdLease({bool shared = false}) async {
    final script = File(p.join(root.path, 'hold_lease.dart'))
      ..writeAsStringSync(_holdLeaseScript);
    data.createSync(recursive: true);
    final process = await Process.start(Platform.resolvedExecutable, [
      script.path,
      p.join(data.path, 'package-cache.lock'),
      shared ? 'shared' : 'exclusive',
    ]);
    final holder = LeaseHolder._(process);
    addTearDown(holder.dispose);
    try {
      final said = await process.stdout
          .transform(utf8.decoder)
          .transform(const LineSplitter())
          .first
          .timeout(const Duration(seconds: 30));
      expect(said, 'held');
      return holder;
    } on Object {
      await holder.dispose();
      rethrow;
    }
  }
}

/// Another process holding the package cache lease.
final class LeaseHolder {
  LeaseHolder._(this._process) {
    _exitCode = _process.exitCode.then((code) {
      _exited = true;
      return code;
    });
    _errors = _process.stderr.drain<void>();
  }

  final Process _process;
  late final Future<int> _exitCode;
  late final Future<void> _errors;
  Future<void>? _disposal;
  bool _exited = false;

  Future<int> get exitCode => _exitCode;

  /// Reaps the child before fixture teardown can delete its locked files.
  /// Also safe after [end], or when startup or an expectation failed.
  Future<void> dispose() => _disposal ??= _dispose();

  Future<void> _dispose() async {
    if (!_exited) _process.kill(ProcessSignal.sigkill);
    await _exitCode.timeout(const Duration(seconds: 10));
    await _errors.timeout(const Duration(seconds: 10));
  }

  /// Ends the holder without letting go of the lease, as a crash does, and
  /// returns its exit code.
  Future<int> end() async {
    try {
      await _process.stdin.close().timeout(const Duration(seconds: 10));
      return await _exitCode.timeout(const Duration(seconds: 10));
    } finally {
      await dispose();
    }
  }
}

/// Holds the package cache lease at the path and in the mode it is given
/// until its input closes, then ends without releasing it.
const _holdLeaseScript = '''
import 'dart:io';

Future<void> main(List<String> args) async {
  final lease = await File(args[0]).open(mode: FileMode.append);
  await lease.lock(args[1] == 'shared' ? FileLock.shared : FileLock.exclusive);
  stdout.writeln('held');
  await stdout.flush();
  await stdin.drain<void>();
  exit(3);
}
''';
