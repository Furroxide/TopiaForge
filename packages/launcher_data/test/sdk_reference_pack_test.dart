import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory temp;

  setUp(() {
    temp = Directory.systemTemp.createTempSync('topiaforge-sdk-pack-');
  });

  tearDown(() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
  });

  File fixtureFile(String relative, String contents) {
    final file = File(p.join(temp.path, relative));
    file.parent.createSync(recursive: true);
    file.writeAsStringSync(contents);
    return file;
  }

  test('writes and verifies a compile-only reference pack', () {
    final assembly = fixtureFile(
      'input/TopiaForge.Mods.Abstractions.dll',
      'dll',
    );
    final docs = fixtureFile(
      'input/TopiaForge.Mods.Abstractions.xml',
      '<doc />',
    );
    final analyzer = fixtureFile(
      'input/TopiaForge.Mods.Analyzers.dll',
      'analyzer',
    );

    final pack = const SdkReferencePackWriter().write(
      destination: Directory(p.join(temp.path, 'sdk', '1.0.0')),
      sdkVersion: '1.0.0',
      dotnetSdkVersion: '10.0.301',
      toolVersion: '1.0.0',
      references: {'TopiaForge.Mods.Abstractions': assembly},
      documentation: {'TopiaForge.Mods.Abstractions': docs},
      analyzers: {'TopiaForge.Mods.Analyzers': analyzer},
    );

    expect(pack.version, '1.0.0');
    expect(pack.gameVersion, '0.0.2309');
    expect(pack.toolVersion, '1.0.0');
    expect(pack.packages, hasLength(2));
    expect(pack.feed.existsSync(), isTrue);
    final contract = pack.packages.singleWhere(
      (package) => package.id == 'TopiaForge.Mods.Abstractions',
    );
    final contractArchive = ZipDecoder().decodeBytes(
      File(p.join(pack.root.path, contract.path)).readAsBytesSync(),
      verify: true,
    );
    final names = contractArchive.files.map((file) => file.name).toSet();
    expect(
      names,
      containsAll({
        'TopiaForge.Mods.Abstractions.nuspec',
        'ref/netstandard2.1/TopiaForge.Mods.Abstractions.dll',
        'ref/netstandard2.1/TopiaForge.Mods.Abstractions.xml',
        'build/TopiaForge.Mods.Abstractions.props',
        'build/TopiaForge.Mods.Abstractions.targets',
        'buildTransitive/TopiaForge.Mods.Abstractions.props',
        'buildTransitive/TopiaForge.Mods.Abstractions.targets',
      }),
    );
    expect(names.where((name) => name.startsWith('lib/')), isEmpty);
    final analyzerPackage = pack.packages.singleWhere(
      (package) => package.kind == 'analyzer',
    );
    final analyzerArchive = ZipDecoder().decodeBytes(
      File(p.join(pack.root.path, analyzerPackage.path)).readAsBytesSync(),
    );
    expect(
      analyzerArchive.files.map((file) => file.name),
      contains('analyzers/dotnet/cs/TopiaForge.Mods.Analyzers.dll'),
    );
    expect(
      SdkReferencePack.load(pack.root).manifestSha256,
      pack.manifestSha256,
    );
    final repeated = const SdkReferencePackWriter().write(
      destination: Directory(p.join(temp.path, 'sdk-copy', '1.0.0')),
      sdkVersion: '1.0.0',
      dotnetSdkVersion: '10.0.301',
      toolVersion: '1.0.0',
      references: {'TopiaForge.Mods.Abstractions': assembly},
      documentation: {'TopiaForge.Mods.Abstractions': docs},
      analyzers: {'TopiaForge.Mods.Analyzers': analyzer},
    );
    expect(repeated.manifestSha256, pack.manifestSha256);
    expect(
      repeated.packages.map((package) => package.sha256),
      pack.packages.map((package) => package.sha256),
    );
  });

  test('rejects a tampered cached assembly', () {
    final assembly = fixtureFile(
      'input/TopiaForge.Mods.Abstractions.dll',
      'dll',
    );
    final pack = const SdkReferencePackWriter().write(
      destination: Directory(p.join(temp.path, 'sdk', '1.0.0')),
      sdkVersion: '1.0.0',
      dotnetSdkVersion: '10.0.301',
      references: {'TopiaForge.Mods.Abstractions': assembly},
      documentation: {
        'TopiaForge.Mods.Abstractions': fixtureFile(
          'input/TopiaForge.Mods.Abstractions.xml',
          '<doc />',
        ),
      },
    );
    File(
      p.join(pack.root.path, pack.packages.single.path),
    ).writeAsStringSync('changed');

    expect(() => SdkReferencePack.load(pack.root), throwsStateError);
  });

  test(
    'testing package carries runtime helpers while contracts remain ref-only',
    () {
      final abstractions = fixtureFile(
        'input/TopiaForge.Mods.Abstractions.dll',
        'abs-ref',
      );
      final abstractionsDocs = fixtureFile(
        'input/TopiaForge.Mods.Abstractions.xml',
        '<doc />',
      );
      final testing = fixtureFile(
        'input/TopiaForge.Mods.Testing.dll',
        'test-ref',
      );
      final testingRuntime = fixtureFile(
        'input/runtime/TopiaForge.Mods.Testing.dll',
        'test-runtime',
      );
      final testingDocs = fixtureFile(
        'input/TopiaForge.Mods.Testing.xml',
        '<doc />',
      );
      final pack = const SdkReferencePackWriter().write(
        destination: Directory(p.join(temp.path, 'sdk', '1.0.0')),
        sdkVersion: '1.0.0',
        dotnetSdkVersion: '10.0.301',
        references: {
          'TopiaForge.Mods.Abstractions': abstractions,
          'TopiaForge.Mods.Testing': testing,
        },
        documentation: {
          'TopiaForge.Mods.Abstractions': abstractionsDocs,
          'TopiaForge.Mods.Testing': testingDocs,
        },
        runtimeAssemblies: {'TopiaForge.Mods.Testing': testingRuntime},
        runtimeSupportAssemblies: {
          'TopiaForge.Mods.Testing': [abstractions],
        },
        packageDependencies: {
          'TopiaForge.Mods.Testing': ['TopiaForge.Mods.Abstractions'],
        },
      );
      final package = pack.packages.singleWhere(
        (item) => item.id == 'TopiaForge.Mods.Testing',
      );
      final archive = ZipDecoder().decodeBytes(
        File(p.join(pack.root.path, package.path)).readAsBytesSync(),
      );
      expect(
        archive.files.map((file) => file.name),
        containsAll({
          'lib/netstandard2.1/TopiaForge.Mods.Testing.dll',
          'lib/netstandard2.1/TopiaForge.Mods.Abstractions.dll',
        }),
      );
      expect(
        package.runtimeAssemblyIds,
        containsAll({
          'TopiaForge.Mods.Testing',
          'TopiaForge.Mods.Abstractions',
        }),
      );
    },
  );

  test('rejects undeclared files in a reference pack', () {
    final assembly = fixtureFile(
      'input/TopiaForge.Mods.Abstractions.dll',
      'dll',
    );
    final pack = const SdkReferencePackWriter().write(
      destination: Directory(p.join(temp.path, 'sdk', '1.0.0')),
      sdkVersion: '1.0.0',
      dotnetSdkVersion: '10.0.301',
      references: {'TopiaForge.Mods.Abstractions': assembly},
      documentation: {
        'TopiaForge.Mods.Abstractions': fixtureFile(
          'input/TopiaForge.Mods.Abstractions.xml',
          '<doc />',
        ),
      },
    );
    File(p.join(pack.root.path, 'unexpected.dll')).writeAsStringSync('extra');

    expect(() => SdkReferencePack.load(pack.root), throwsStateError);
  });
}
