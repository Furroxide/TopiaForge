import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/src/ugc_sidecar_runtime.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory sidecar;

  setUp(() {
    root = Directory.systemTemp.createTempSync('ugc-sidecar-runtime-');
    sidecar = _writeSidecar(root);
  });

  tearDown(() => root.deleteSync(recursive: true));

  test('requires a matching lockfile and exposes exact safe npm ci flags', () {
    final inspected = TrustedUgcSidecar.inspectDirectory(sidecar);

    expect(inspected.lockDigest, hasLength(64));
    expect(ugcNpmCiArguments, [
      'ci',
      '--ignore-scripts',
      '--no-audit',
      '--no-fund',
      '--omit=dev',
    ]);

    final lock = File(p.join(sidecar.path, 'package-lock.json'));
    final json = jsonDecode(lock.readAsStringSync()) as Map<String, Object?>;
    ((json['packages'] as Map)[''] as Map)['version'] = '9.9.9';
    lock.writeAsStringSync(jsonEncode(json));
    expect(
      () => TrustedUgcSidecar.inspectDirectory(sidecar),
      throwsA(
        predicate((error) => error.toString().contains('does not match')),
      ),
    );
  });

  test('rejects linked sidecar source files', () {
    if (Platform.isWindows) return;
    final script = File(p.join(sidecar.path, 'index.mjs'))..deleteSync();
    final outside = File(p.join(root.path, 'outside.mjs'))
      ..writeAsStringSync('secret');
    Link(script.path).createSync(outside.path);

    expect(
      () => TrustedUgcSidecar.inspectDirectory(sidecar),
      throwsA(
        predicate((error) => error.toString().contains('cannot be a link')),
      ),
    );
  });

  test('rejects an end-of-life Node engine floor', () {
    final manifest = File(p.join(sidecar.path, 'package.json'));
    final json =
        jsonDecode(manifest.readAsStringSync()) as Map<String, Object?>;
    json['engines'] = {'node': '>=20'};
    manifest.writeAsStringSync(jsonEncode(json));

    expect(
      () => TrustedUgcSidecar.inspectDirectory(sidecar),
      throwsA(predicate((error) => error.toString().contains('>=24.16.0'))),
    );
  });
}

Directory _writeSidecar(Directory root) {
  final directory = Directory(p.join(root.path, 'sidecar'))..createSync();
  const package = {
    'name': 'topiaforge-sidecar',
    'version': '1.0.0',
    'engines': {'node': '>=24.16.0'},
    'dependencies': {'safe-package': '1.0.0'},
  };
  final lock = {
    'name': 'topiaforge-sidecar',
    'version': '1.0.0',
    'lockfileVersion': 3,
    'requires': true,
    'packages': {
      '': {
        'name': 'topiaforge-sidecar',
        'version': '1.0.0',
        'engines': {'node': '>=24.16.0'},
        'dependencies': {'safe-package': '1.0.0'},
      },
    },
  };
  File(p.join(directory.path, 'index.mjs')).writeAsStringSync('// trusted');
  File(
    p.join(directory.path, 'package.json'),
  ).writeAsStringSync(jsonEncode(package));
  File(
    p.join(directory.path, 'package-lock.json'),
  ).writeAsStringSync(jsonEncode(lock));
  return directory;
}
