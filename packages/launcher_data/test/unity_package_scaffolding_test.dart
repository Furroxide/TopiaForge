import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory scratch;
  late Directory repo;
  late LocalDeveloperRepository repository;

  setUp(() {
    scratch = Directory.systemTemp.createTempSync('unity-package-scaffolding-');
    repo = Directory(p.join(scratch.path, 'repo'))..createSync();
    _writeTemplate(repo);
    repository = LocalDeveloperRepository(
      dataRoot: p.join(scratch.path, 'data'),
      repositoryRoot: repo.path,
    );
  });

  tearDown(() {
    if (scratch.existsSync()) {
      scratch.deleteSync(recursive: true);
    }
  });

  test(
    'two package ids produce distinct valid asmdefs and namespaces',
    () async {
      final output = Directory(p.join(scratch.path, 'output'))..createSync();
      final hyphenPath = await repository.createUnityPackage(
        parentDirectory: output.path,
        id: 'com.audit.foo-bar',
        name: 'Hyphen Package',
      );
      final underscorePath = await repository.createUnityPackage(
        parentDirectory: output.path,
        id: 'com.audit.foo_bar',
        name: 'Underscore Package',
      );

      final hyphen = _inspectPackage(Directory(hyphenPath));
      final underscore = _inspectPackage(Directory(underscorePath));
      expect(hyphen.packageId, 'com.audit.foo-bar');
      expect(hyphen.displayName, 'Hyphen Package');
      expect(underscore.packageId, 'com.audit.foo_bar');
      expect(underscore.displayName, 'Underscore Package');
      expect(hyphen.runtimeAssembly, 'P_com.P_audit.P_foo_H_bar.Runtime');
      expect(underscore.runtimeAssembly, 'P_com.P_audit.P_foo_U_bar.Runtime');
      expect(hyphen.runtimeAssembly, isNot(underscore.runtimeAssembly));
      expect(hyphen.editorAssembly, isNot(underscore.editorAssembly));
      expect(hyphen.editorReference, hyphen.runtimeAssembly);
      expect(underscore.editorReference, underscore.runtimeAssembly);
      expect(hyphen.runtimeNamespace, 'P_com.P_audit.P_foo_H_bar');
      expect(underscore.runtimeNamespace, 'P_com.P_audit.P_foo_U_bar');
      expect(hyphen.sourceNamespace, hyphen.runtimeNamespace);
      expect(underscore.sourceNamespace, underscore.runtimeNamespace);
      expect(
        hyphen.menuPath,
        'TopiaForge/Packages/com.audit.foo-bar/Say Hello',
      );
      expect(
        underscore.menuPath,
        'TopiaForge/Packages/com.audit.foo_bar/Say Hello',
      );
      expect(hyphen.menuPath, isNot(underscore.menuPath));
      expect(hyphen.metaGuids, isNot(equals(underscore.metaGuids)));
      expect(
        hyphen.metaGuids.toSet().intersection(underscore.metaGuids.toSet()),
        isEmpty,
      );
      expect([
        ...hyphen.metaGuids,
        ...underscore.metaGuids,
      ], everyElement(matches(RegExp(r'^[0-9a-f]{32}$'))));
      expect(hyphen.hasTemplateIdentity, isFalse);
      expect(underscore.hasTemplateIdentity, isFalse);
    },
  );

  test('community scaffold packs a deterministic integrity listing', () async {
    final output = Directory(p.join(scratch.path, 'community'))..createSync();
    final packagePath = await repository.createUnityPackage(
      parentDirectory: output.path,
      id: 'com.community.widget',
      name: 'Community Widget',
    );
    final first = p.join(packagePath, 'dist', 'vpm-first');
    final second = p.join(packagePath, 'dist', 'vpm-second');
    for (final destination in [first, second]) {
      final summary = await repository.packUnityPackages(
        outputDir: destination,
        packageDirectories: [packagePath],
        repositoryId: 'com.community.repo',
        repositoryName: 'Community Repository',
        repositoryAuthor: 'Community Author',
      );
      expect(summary, hasLength(2));
    }

    const archiveName = 'com.community.widget-0.1.0.zip';
    final firstBytes = File(p.join(first, archiveName)).readAsBytesSync();
    final secondBytes = File(p.join(second, archiveName)).readAsBytesSync();
    expect(firstBytes, secondBytes);
    expect(
      File(p.join(first, 'index.json')).readAsBytesSync(),
      File(p.join(second, 'index.json')).readAsBytesSync(),
    );
    final index =
        jsonDecode(File(p.join(first, 'index.json')).readAsStringSync())
            as Map<String, Object?>;
    expect(index['id'], 'com.community.repo');
    expect(index['name'], 'Community Repository');
    expect(index['author'], 'Community Author');
    final versions =
        ((((index['packages'] as Map)['com.community.widget']
                as Map)['versions'])
            as Map);
    final entry = versions['0.1.0'] as Map;
    expect(entry['url'], archiveName);
    expect(entry['zipSHA256'], sha256.convert(firstBytes).toString());

    final archive = ZipDecoder().decodeBytes(firstBytes);
    final names = archive.files.map((file) => file.name).toList();
    expect(names, orderedEquals([...names]..sort()));
    expect(names, contains('package.json'));
    expect(
      names,
      contains('Runtime/P_com.P_community.P_widget.Runtime.asmdef'),
    );
    expect(names.where((name) => name.startsWith('dist/')), isEmpty);
    final manifestFile = archive.files.singleWhere(
      (file) => file.name == 'package.json',
    );
    final manifest =
        jsonDecode(utf8.decode(manifestFile.content as List<int>)) as Map;
    expect(manifest['name'], 'com.community.widget');
  });

  test('community packaging rejects duplicate package ids', () async {
    final firstParent = Directory(p.join(scratch.path, 'first'))..createSync();
    final secondParent = Directory(p.join(scratch.path, 'second'))
      ..createSync();
    final first = await repository.createUnityPackage(
      parentDirectory: firstParent.path,
      id: 'com.community.duplicate',
      name: 'First',
    );
    final second = await repository.createUnityPackage(
      parentDirectory: secondParent.path,
      id: 'com.community.duplicate',
      name: 'Second',
    );

    await expectLater(
      repository.packUnityPackages(
        outputDir: p.join(scratch.path, 'vpm'),
        packageDirectories: [first, second],
        repositoryId: 'com.community.repo',
        repositoryName: 'Community Repository',
        repositoryAuthor: 'Community Author',
      ),
      throwsA(
        predicate(
          (error) => error.toString().contains('Duplicate VPM package id'),
        ),
      ),
    );
  });

  test('VPM packaging rejects linked source content', () async {
    if (Platform.isWindows) return;
    final output = Directory(p.join(scratch.path, 'linked'))..createSync();
    final packagePath = await repository.createUnityPackage(
      parentDirectory: output.path,
      id: 'com.community.linked',
      name: 'Linked Package',
    );
    final outside = File(p.join(scratch.path, 'outside.txt'))
      ..writeAsStringSync('secret');
    Link(p.join(packagePath, 'Runtime', 'linked.txt')).createSync(outside.path);

    await expectLater(
      repository.packUnityPackages(
        outputDir: p.join(scratch.path, 'vpm-linked'),
        packageDirectories: [packagePath],
        repositoryId: 'com.community.repo',
        repositoryName: 'Community Repository',
        repositoryAuthor: 'Community Author',
      ),
      throwsA(predicate((error) => error.toString().contains('symlink'))),
    );
  });

  test('VPM packaging rejects malformed and retired dependency ids', () async {
    final packageRoot = _writePackagingFixture(
      scratch,
      folder: 'invalid-dependency',
      id: 'com.community.invalid-dependency',
    );
    final manifestFile = File(p.join(packageRoot.path, 'package.json'));
    final retired = String.fromCharCodes(const [
      114,
      111,
      98,
      111,
      116,
      111,
      112,
      105,
      97,
      46,
      100,
      101,
      112,
    ]);
    for (final dependency in ['bad dependency', retired]) {
      final manifest =
          jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>;
      manifest['vpmDependencies'] = {dependency: '*'};
      manifestFile.writeAsStringSync(jsonEncode(manifest));

      await expectLater(
        repository.packUnityPackages(
          outputDir: p.join(scratch.path, 'vpm-invalid'),
          packageDirectories: [packageRoot.path],
          repositoryId: 'com.community.repo',
          repositoryName: 'Community Repository',
          repositoryAuthor: 'Community Author',
        ),
        throwsA(
          predicate(
            (error) => error.toString().contains('invalid VPM dependency'),
          ),
        ),
      );
    }
  });

  test('VPM packaging accepts 214-char ids and rejects 215', () async {
    final prefix = 'com.example.';
    final validId = prefix + List.filled(214 - prefix.length, 'a').join();
    final valid = _writePackagingFixture(
      scratch,
      folder: 'boundary-valid',
      id: validId,
    );

    await repository.packUnityPackages(
      outputDir: p.join(scratch.path, 'vpm-boundary'),
      packageDirectories: [valid.path],
      repositoryId: 'com.community.repo',
      repositoryName: 'Community Repository',
      repositoryAuthor: 'Community Author',
    );
    final index =
        jsonDecode(
              File(
                p.join(scratch.path, 'vpm-boundary', 'index.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect((index['packages'] as Map).keys, contains(validId));

    final invalid = _writePackagingFixture(
      scratch,
      folder: 'boundary-invalid',
      id: '${validId}a',
    );
    await expectLater(
      repository.packUnityPackages(
        outputDir: p.join(scratch.path, 'vpm-boundary-invalid'),
        packageDirectories: [invalid.path],
        repositoryId: 'com.community.repo',
        repositoryName: 'Community Repository',
        repositoryAuthor: 'Community Author',
      ),
      throwsStateError,
    );
  });
}

Directory _writePackagingFixture(
  Directory scratch, {
  required String folder,
  required String id,
}) {
  final root = Directory(p.join(scratch.path, folder))..createSync();
  File(p.join(root.path, 'package.json')).writeAsStringSync(
    jsonEncode({'name': id, 'version': '0.1.0', 'displayName': 'Fixture'}),
  );
  File(p.join(root.path, 'README.md')).writeAsStringSync('fixture');
  return root;
}

void _writeTemplate(Directory repo) {
  final root = Directory(
    p.join(repo.path, 'templates', 'TopiaForge.UnityPackageTemplate'),
  )..createSync(recursive: true);
  File(p.join(root.path, 'package.json')).writeAsStringSync(
    jsonEncode({
      'name': 'io.github.furroxide.topiaforge.example',
      'version': '0.1.0',
      'displayName': 'Example',
      'author': {'name': 'Example Author'},
    }),
  );
  for (final area in const ['Runtime', 'Editor']) {
    Directory(p.join(root.path, area)).createSync();
  }
  File(
    p.join(root.path, 'Runtime', 'TopiaForge.Example.Runtime.asmdef'),
  ).writeAsStringSync(
    jsonEncode({
      'name': 'TopiaForge.Example.Runtime',
      'rootNamespace': 'TopiaForge.Example',
      'references': <String>[],
    }),
  );
  File(
    p.join(root.path, 'Runtime', 'TopiaForge.Example.Runtime.asmdef.meta'),
  ).writeAsStringSync('guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
  File(
    p.join(root.path, 'Editor', 'TopiaForge.Example.Editor.asmdef'),
  ).writeAsStringSync(
    jsonEncode({
      'name': 'TopiaForge.Example.Editor',
      'rootNamespace': 'TopiaForge.Example.Editor',
      'references': ['TopiaForge.Example.Runtime'],
    }),
  );
  File(
    p.join(root.path, 'Editor', 'TopiaForge.Example.Editor.asmdef.meta'),
  ).writeAsStringSync('guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
  File(
    p.join(root.path, 'Runtime', 'ExampleBehaviour.cs'),
  ).writeAsStringSync('namespace TopiaForge.Example { public class Demo {} }');
  File(p.join(root.path, 'Editor', 'ExampleEditor.cs')).writeAsStringSync(
    'namespace TopiaForge.Example.Editor { '
    '[MenuItem("TopiaForge/Example/Say Hello")] public class DemoEditor {} }',
  );
}

_PackageInspection _inspectPackage(Directory root) {
  final manifest =
      jsonDecode(File(p.join(root.path, 'package.json')).readAsStringSync())
          as Map<String, Object?>;
  final asmdefs = root
      .listSync(recursive: true)
      .whereType<File>()
      .where((file) => p.extension(file.path) == '.asmdef')
      .toList();
  final decoded = <Map<String, Object?>>[
    for (final file in asmdefs)
      jsonDecode(file.readAsStringSync()) as Map<String, Object?>,
  ];
  final runtime = decoded.singleWhere(
    (item) => (item['name'] as String).endsWith('.Runtime'),
  );
  final editor = decoded.singleWhere(
    (item) => (item['name'] as String).endsWith('.Editor'),
  );
  final sources = root
      .listSync(recursive: true)
      .whereType<File>()
      .where(
        (file) => const {'.asmdef', '.cs'}.contains(p.extension(file.path)),
      )
      .map((file) => file.readAsStringSync())
      .join('\n');
  final metaGuids = root
      .listSync(recursive: true)
      .whereType<File>()
      .where((file) => file.path.endsWith('.meta'))
      .map(
        (file) => RegExp(
          r'^guid:\s*([0-9a-f]{32})$',
          multiLine: true,
        ).firstMatch(file.readAsStringSync())!.group(1)!,
      )
      .toList();
  final namespaceMatch = RegExp(r'namespace\s+([^\s{]+)').firstMatch(
    File(
      p.join(root.path, 'Runtime', 'ExampleBehaviour.cs'),
    ).readAsStringSync(),
  );
  final menuMatch = RegExp(r'MenuItem\("([^"]+)"\)').firstMatch(
    File(p.join(root.path, 'Editor', 'ExampleEditor.cs')).readAsStringSync(),
  );
  return _PackageInspection(
    packageId: manifest['name'] as String,
    displayName: manifest['displayName'] as String,
    runtimeAssembly: runtime['name'] as String,
    editorAssembly: editor['name'] as String,
    editorReference: (editor['references'] as List).single as String,
    runtimeNamespace: runtime['rootNamespace'] as String,
    sourceNamespace: namespaceMatch!.group(1)!,
    menuPath: menuMatch!.group(1)!,
    metaGuids: metaGuids,
    hasTemplateIdentity: sources.contains('TopiaForge.Example'),
  );
}

class _PackageInspection {
  const _PackageInspection({
    required this.packageId,
    required this.displayName,
    required this.runtimeAssembly,
    required this.editorAssembly,
    required this.editorReference,
    required this.runtimeNamespace,
    required this.sourceNamespace,
    required this.menuPath,
    required this.metaGuids,
    required this.hasTemplateIdentity,
  });

  final String packageId;
  final String displayName;
  final String runtimeAssembly;
  final String editorAssembly;
  final String editorReference;
  final String runtimeNamespace;
  final String sourceNamespace;
  final String menuPath;
  final List<String> metaGuids;
  final bool hasTemplateIdentity;
}
