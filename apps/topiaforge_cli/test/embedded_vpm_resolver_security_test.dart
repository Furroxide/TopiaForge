import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory repositoryRoot;
  late Directory scratch;

  setUpAll(() {
    repositoryRoot = _findRepositoryRoot();
  });

  setUp(() {
    scratch = Directory.systemTemp.createTempSync(
      'topiaforge-vpm-bridge-test-',
    );
  });

  tearDown(() {
    if (scratch.existsSync()) {
      scratch.deleteSync(recursive: true);
    }
  });

  test('embedded VPM package remains a bounded read-only detector', () {
    final packageRoot = Directory(
      p.join(
        repositoryRoot.path,
        'templates',
        'TopiaForge.UnityWorldTemplate',
        'Packages',
        'io.github.furroxide.topiaforge.vpm-resolver',
      ),
    );
    final resolver = File(
      p.join(packageRoot.path, 'Editor', 'VpmResolver.cs'),
    ).readAsStringSync();
    final parser = File(
      p.join(packageRoot.path, 'Editor', 'MiniJson.cs'),
    ).readAsStringSync();
    final safeReader = File(
      p.join(packageRoot.path, 'Editor', 'VpmSafeFileReader.cs'),
    ).readAsStringSync();
    final allCSharp = packageRoot
        .listSync(recursive: true)
        .whereType<File>()
        .where((file) => p.extension(file.path) == '.cs')
        .map((file) => file.readAsStringSync())
        .join('\n');

    expect(resolver, contains('[InitializeOnLoad]'));
    expect(resolver, contains('MaxManifestBytes'));
    expect(resolver, contains('VpmSafeFileReader.IsValidPackageId'));
    expect(safeReader, contains('id.Length > 214'));
    expect(safeReader, contains('previousWasSeparator'));
    expect(safeReader, contains('RetiredPackageIdPrefixes'));
    expect(resolver, contains('SemanticVersionPattern'));
    expect(resolver, contains('value.Length <= 128'));
    expect(resolver, contains('StringComparer.OrdinalIgnoreCase'));
    expect(resolver, contains('topiaforge unity resolve'));
    expect(resolver, contains('EditorGUIUtility.systemCopyBuffer'));
    expect(
      resolver,
      contains(
        'All locked packages are present at their exact locked versions',
      ),
    );
    expect(parser, contains('private const int MaxDepth = 64'));
    expect(parser, contains('duplicate object property'));
    expect(parser, contains('unexpected trailing content'));
    expect(parser, contains('guarantees progress on malformed input'));

    const forbiddenApis = <String>[
      'using System.Net',
      'using System.IO.Compression',
      'System.Diagnostics',
      'WebClient',
      'HttpClient',
      'WebRequest',
      'DownloadData',
      'DownloadString',
      'ZipArchive',
      'ExtractToDirectory',
      'Directory.Delete(',
      'Directory.Move(',
      'Directory.CreateDirectory(',
      'File.Delete(',
      'File.Move(',
      'File.Create(',
      'File.Write',
      'File.ReadAll',
      'ReadToEnd(',
      '.CopyTo(',
      'Process.Start(',
    ];
    for (final api in forbiddenApis) {
      expect(
        allCSharp,
        isNot(contains(api)),
        reason: 'embedded project-open code must not use $api',
      );
    }

    expect(allCSharp, isNot(contains('vpm-resolver-repos.json')));
    expect(allCSharp, isNot(contains('zipSHA256')));
  });

  test('scaffolded Unity worlds retain the safe recovery bridge', () async {
    final output = Directory(p.join(scratch.path, 'output'))..createSync();
    final dataRoot = Directory(p.join(scratch.path, 'data'))..createSync();
    final repository = LocalDeveloperRepository(
      dataRoot: dataRoot.path,
      repositoryRoot: repositoryRoot.path,
    );

    final projects = await repository.createUnityProject(
      parentDirectory: output.path,
      name: 'Safe World',
    );
    final project = projects.single;
    final generatedResolver = File(
      p.join(
        project.path,
        'Packages',
        'io.github.furroxide.topiaforge.vpm-resolver',
        'Editor',
        'VpmResolver.cs',
      ),
    );
    final sourceResolver = File(
      p.join(
        repositoryRoot.path,
        'templates',
        'TopiaForge.UnityWorldTemplate',
        'Packages',
        'io.github.furroxide.topiaforge.vpm-resolver',
        'Editor',
        'VpmResolver.cs',
      ),
    );

    expect(
      generatedResolver.readAsStringSync(),
      sourceResolver.readAsStringSync(),
    );
    expect(
      File(
        p.join(project.path, 'Packages', 'vpm-resolver-repos.json'),
      ).existsSync(),
      isFalse,
      reason: 'a scaffold must not capture a machine-local repository path',
    );
  });

  test('template metadata and author guidance describe explicit recovery', () {
    final templateRoot = p.join(
      repositoryRoot.path,
      'templates',
      'TopiaForge.UnityWorldTemplate',
    );
    final packageJson =
        jsonDecode(
              File(
                p.join(
                  templateRoot,
                  'Packages',
                  'io.github.furroxide.topiaforge.vpm-resolver',
                  'package.json',
                ),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    final description = packageJson['description'] as String;
    final templateReadme = File(
      p.join(templateRoot, 'README.md'),
    ).readAsStringSync();
    final vpmGuide = File(
      p.join(repositoryRoot.path, 'docs', 'UnityVpm.md'),
    ).readAsStringSync();
    final packagesIgnore = File(
      p.join(templateRoot, 'Packages', '.gitignore'),
    ).readAsStringSync();

    expect(description, contains('read-only recovery bridge'));
    expect(description, contains('never downloads'));
    expect(templateReadme, contains('topiaforge unity resolve .'));
    expect(templateReadme, isNot(contains('self-restores on open')));
    expect(vpmGuide, contains('never reads package listings'));
    expect(vpmGuide, contains('or changes `Packages/`'));
    expect(packagesIgnore, contains('/vpm-resolver-repos.json'));
  });

  test('world template ships a valid sample prefab contract', () {
    final worldRoot = p.join(
      repositoryRoot.path,
      'templates',
      'TopiaForge.UnityWorldTemplate',
      'Assets',
      'World',
    );
    final prefab = File(p.join(worldRoot, 'World.prefab'));
    final metadata = File(p.join(worldRoot, 'World.prefab.meta'));

    expect(prefab.existsSync(), isTrue);
    expect(metadata.existsSync(), isTrue);
    final source = prefab.readAsStringSync();
    expect(source, contains('m_Name: World'));
    expect(source, contains('m_Name: SpawnPoint'));
    expect(source, contains('BoxCollider:'));
    expect(source, contains('MeshRenderer:'));
  });
}

Directory _findRepositoryRoot() {
  var candidate = Directory.current.absolute;
  while (candidate.parent.path != candidate.path) {
    final template = Directory(
      p.join(candidate.path, 'templates', 'TopiaForge.UnityWorldTemplate'),
    );
    if (File(p.join(candidate.path, 'AGENTS.md')).existsSync() &&
        template.existsSync()) {
      return candidate;
    }
    candidate = candidate.parent;
  }
  throw StateError('Could not locate the TopiaForge repository root.');
}
