import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_metadata.dart';
import 'package:topiaforge/src/release_metadata_source.dart';
import 'package:topiaforge/src/release_policy.dart';

void main() {
  for (final unexpected in <String?>[null, 'asset', 'metadata']) {
    test(
      unexpected == null
          ? 'nested caller resolves relative assets and metadata from its CWD'
          : 'nested caller refuses same-name $unexpected under candidate root',
      () async {
        final fixture = _SourceFixture.create();
        addTearDown(() => fixture.root.deleteSync(recursive: true));
        final caller = Directory(p.join(fixture.root.path, 'apps/worker'))
          ..createSync(recursive: true);
        final artifact = TopiaForgeReleaseCatalog.load(
          fixture.root.path,
        ).release('0.1.0-rc.1').artifacts.first;
        final assets = p.join('src', 'assets');
        final metadata = p.join('src', 'metadata');
        void write(String base, String relative, String name) =>
            File(p.join(base, relative, name))
              ..createSync(recursive: true)
              ..writeAsStringSync('Synthetic output, not candidate evidence.');
        write(caller.path, assets, artifact);
        write(caller.path, metadata, 'release-bom.json');
        if (unexpected != null) {
          write(
            fixture.root.path,
            unexpected == 'asset' ? assets : metadata,
            unexpected == 'asset' ? artifact : 'release-bom.json',
          );
        }
        final result = await Process.run(Platform.resolvedExecutable, [
          p.absolute('test/fixtures/release_metadata_source_probe.dart'),
          fixture.root.path,
          fixture.sha,
          assets,
          metadata,
        ], workingDirectory: caller.path);
        if (unexpected == null) {
          expect(result.exitCode, 0, reason: '${result.stderr}');
          expect(result.stdout, contains('source accepted'));
        } else {
          expect(result.exitCode, isNot(0));
          expect(result.stderr, contains('untracked source inputs'));
        }
      },
    );
  }
  test(
    'source guard permits separate hosted downloads and metadata outputs',
    () async {
      final fixture = _SourceFixture.create();
      addTearDown(() => fixture.root.deleteSync(recursive: true));
      File(p.join(fixture.root.path, 'staged-assets/payload.zip'))
        ..createSync(recursive: true)
        ..writeAsStringSync('synthetic download');
      File(p.join(fixture.root.path, 'hosted-verification/windows.json'))
        ..createSync(recursive: true)
        ..writeAsStringSync('synthetic verification');
      await fixture.verify();
    },
  );
  test(
    'source guard refuses untracked source before metadata writes',
    () async {
      final fixture = _SourceFixture.create();
      addTearDown(() => fixture.root.deleteSync(recursive: true));
      File(p.join(fixture.root.path, 'src/unreviewed.dart'))
        ..createSync(recursive: true)
        ..writeAsStringSync('unreviewed source');
      await expectLater(
        fixture.verify(),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            contains('untracked source'),
          ),
        ),
      );
    },
  );
  test('ignored extra legal input cannot enter publication metadata', () async {
    final fixture = _SourceFixture.create();
    addTearDown(() => fixture.root.deleteSync(recursive: true));
    File(
      p.join(fixture.root.path, 'third_party/BepInEx/LICENSES/ignored.txt'),
    ).writeAsStringSync('unreviewed legal text');
    await expectLater(
      fixture.verify(),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('not tracked'),
        ),
      ),
    );
  });

  for (final flag in ['--assume-unchanged', '--skip-worktree']) {
    test('source guard refuses consumed input hidden by $flag', () async {
      final fixture = _SourceFixture.create();
      addTearDown(() => fixture.root.deleteSync(recursive: true));
      fixture.git(['update-index', flag, '--', 'THIRD_PARTY_NOTICES.md']);
      final before = fixture.git(['ls-files', '-v', 'THIRD_PARTY_NOTICES.md']);
      File(
        p.join(fixture.root.path, 'THIRD_PARTY_NOTICES.md'),
      ).writeAsStringSync('unreviewed legal input');
      await expectLater(fixture.verify(), throwsStateError);
      expect(fixture.git(['ls-files', '-v', 'THIRD_PARTY_NOTICES.md']), before);
    });
  }
  test(
    'source guard ignores replacement refs for exact target identity',
    () async {
      final fixture = _SourceFixture.create();
      addTearDown(() => fixture.root.deleteSync(recursive: true));
      File(
        p.join(fixture.root.path, 'THIRD_PARTY_NOTICES.md'),
      ).writeAsStringSync('replacement legal input');
      fixture.git(['add', '--', 'THIRD_PARTY_NOTICES.md']);
      fixture.git([
        '-c',
        'commit.gpgsign=false',
        'commit',
        '--quiet',
        '-m',
        'test: replacement source',
      ]);
      fixture.git([
        'replace',
        fixture.sha,
        fixture.git(['rev-parse', 'HEAD']),
      ]);
      await expectLater(fixture.verify(), throwsStateError);
    },
  );

  for (final staged in [false, true]) {
    test(
      'publication rejects ${staged ? 'staged' : 'unstaged'} source drift before output',
      () async {
        final root = Directory.systemTemp.createTempSync('metadata-source-');
        addTearDown(() => root.deleteSync(recursive: true));
        final file = File(p.join(root.path, 'source.txt'))
          ..writeAsStringSync('frozen');
        String git(List<String> args) {
          final result = Process.runSync(
            'git',
            args,
            workingDirectory: root.path,
          );
          expect(result.exitCode, 0, reason: '${result.stderr}');
          return '${result.stdout}'.trim();
        }

        git(['init', '--quiet']);
        git(['config', 'user.name', 'Metadata Source Test']);
        git(['config', 'user.email', 'metadata@example.invalid']);
        git(['add', '--', 'source.txt']);
        git([
          '-c',
          'commit.gpgsign=false',
          'commit',
          '--quiet',
          '-m',
          'test: freeze source',
        ]);
        final sha = git(['rev-parse', 'HEAD']);
        file.writeAsStringSync('changed after acceptance');
        if (staged) git(['add', '--', 'source.txt']);
        final assets = Directory(p.join(root.path, 'assets'))..createSync();
        final output = Directory(p.join(root.path, 'metadata'));
        await expectLater(
          const TopiaForgeReleaseMetadataBuilder().build(
            repositoryRoot: root.path,
            version: '0.1.0-rc.1',
            targetSha: sha,
            assetsDirectory: assets.path,
            outputDirectory: output.path,
          ),
          throwsA(
            isA<StateError>().having(
              (error) => error.message,
              'message',
              contains('source differs from the exact target SHA'),
            ),
          ),
        );
        expect(output.existsSync(), isFalse);
        expect(assets.listSync(), isEmpty);
      },
    );
  }
}

final class _SourceFixture {
  _SourceFixture(this.root, this.sha);
  final Directory root;
  final String sha;
  Future<void> verify() => verifyMetadataPublicationSource(
    root.path,
    sha,
    '0.1.0-rc.1',
    p.join(root.path, 'release-assets'),
    p.join(root.path, 'release-assets'),
    allowUnresolved: false,
  );

  String git(List<String> args) {
    final result = Process.runSync('git', args, workingDirectory: root.path);
    if (result.exitCode != 0) throw StateError('${result.stderr}');
    return '${result.stdout}'.trim();
  }

  static _SourceFixture create() {
    final repository = Directory.current.parent.parent.path;
    final root = Directory.systemTemp.createTempSync('metadata-inputs-');
    void write(String name, String content) => File(p.join(root.path, name))
      ..createSync(recursive: true)
      ..writeAsStringSync(content);
    for (final name in [
      'release/release-policy.json',
      'release/catalog.json',
    ]) {
      write(name, File(p.join(repository, name)).readAsStringSync());
    }
    final policy = TopiaForgeReleasePolicy.load(root.path);
    final release = TopiaForgeReleaseCatalog.load(
      root.path,
    ).release('0.1.0-rc.1');
    for (final name in {
      'schemas/topiaforge.release-bom.schema.json',
      'schemas/topiaforge.release-spdx.schema.json',
      policy.gameBuildMetadataFile,
      policy.bepInExProvenanceFile,
      release.notesFile,
      'src/TopiaForge.Mods.UnityUi/Assets/topiaforge-ui.manifest.json',
      'src/TopiaForge.Mods.UnityUi/Assets/topiaforge-ui.bundle',
      'THIRD_PARTY_NOTICES.md',
      'DCO',
      'mods/LICENSE',
      'packages/launcher_ui/LICENSE',
      'packages/launcher_ui/fonts/Audiowide-OFL.txt',
      'packages/launcher_ui/fonts/Quicksand-OFL.txt',
      'tools/unity-ui-bundle/Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt',
      'templates/TopiaForge.UnityWorldTemplate/Packages/'
          'io.github.furroxide.topiaforge.vpm-resolver/LICENSE.md',
      'templates/TopiaForge.UnityWorldTemplate/Packages/'
          'io.github.furroxide.topiaforge.world-companion/LICENSE.md',
      if (policy.licenseFile != null) policy.licenseFile!,
      'third_party/BepInEx/LICENSES/tracked.txt',
    }) {
      write(name, 'Synthetic source identity fixture.');
    }
    write('.gitignore', 'third_party/BepInEx/LICENSES/ignored.txt\n');
    String git(List<String> args) {
      final result = Process.runSync('git', args, workingDirectory: root.path);
      if (result.exitCode != 0) throw StateError('${result.stderr}');
      return '${result.stdout}'.trim();
    }

    git(['init', '--quiet']);
    git(['config', 'core.autocrlf', 'false']);
    git(['config', 'user.name', 'Metadata Inputs Test']);
    git(['config', 'user.email', 'metadata@example.invalid']);
    git(['add', '--', '.']);
    git([
      '-c',
      'commit.gpgsign=false',
      'commit',
      '--quiet',
      '-m',
      'test: freeze synthetic inputs',
    ]);
    return _SourceFixture(root, git(['rev-parse', 'HEAD']));
  }
}
