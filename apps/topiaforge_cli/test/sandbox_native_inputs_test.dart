import 'dart:convert';
import 'dart:io';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_arguments.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_io.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_run_support.dart';

void main() {
  final root = p.join(Directory.systemTemp.path, 'test-only-sandbox');
  List<String> command(String operation) => [
    operation,
    for (final flag in [
      'isolation-record',
      'device-profile',
      'packages',
      'broker',
      'driver-manifest',
      'spec',
      if (operation == 'run') ...['source-root', 'output-root'] else 'annex',
    ]) ...['--$flag', p.join(root, flag)],
  ];
  test('run preserves all explicit inputs and bounded default', () {
    final parsed = SandboxNativeCommand.parse(command('run'));
    expect(parsed.command, 'run');
    expect(parsed.paths.repositoryRoot, p.join(root, 'source-root'));
    expect(parsed.paths.brokerPath, p.join(root, 'broker'));
    expect(parsed.paths.timeout, const Duration(minutes: 30));
    expect(parsed.paths.sourceWorkspacePath, isEmpty);
  });
  test('verify requires actual broker and no run defaults', () {
    final parsed = SandboxNativeCommand.parse(command('verify'));
    expect(parsed.paths.annexPath, p.join(root, 'annex'));
    expect(parsed.paths.repositoryRoot, isEmpty);
    expect(parsed.paths.outputRoot, isEmpty);
  });
  test('run accepts explicit snapshot and both timeout boundaries', () {
    for (final seconds in ['30', '14400']) {
      final parsed = SandboxNativeCommand.parse([
        ...command('run'),
        '--source-workspace',
        p.join(root, 'source.json'),
        '--timeout-seconds',
        seconds,
      ]);
      expect(parsed.paths.sourceWorkspacePath, p.join(root, 'source.json'));
      expect(parsed.paths.timeout.inSeconds, int.parse(seconds));
    }
  });
  for (final operation in ['run', 'verify']) {
    for (final mutation in <String, void Function(List<String>)>{
      'missing broker': (v) => v.removeRange(7, 9),
      'duplicate option': (v) => v.addAll(['--spec', p.join(root, 'other')]),
      'unknown option': (v) => v.addAll(['--automatic-approval', 'true']),
      'missing value': (v) => v.removeLast(),
      'blank value': (v) => v[2] = ' ',
      'relative path': (v) => v[2] = 'relative.json',
      'aliased path': (v) => v[2] = '$root${p.separator}..${p.separator}input',
    }.entries) {
      test('$operation refuses ${mutation.key}', () {
        final args = command(operation);
        mutation.value(args);
        expect(() => SandboxNativeCommand.parse(args), throwsFormatException);
      });
    }
  }
  for (final invalid in ['0', '29', '14401', '1.5', 'none']) {
    test('invalid native timeout $invalid refused', () {
      expect(
        () => SandboxNativeCommand.parse([
          ...command('run'),
          '--timeout-seconds',
          invalid,
        ]),
        throwsFormatException,
      );
    });
  }
  test('verify refuses run-only source and timing inputs', () {
    for (final flag in ['source-root', 'source-workspace', 'timeout-seconds']) {
      expect(
        () => SandboxNativeCommand.parse([
          ...command('verify'),
          '--$flag',
          p.join(root, 'extra'),
        ]),
        throwsFormatException,
      );
    }
  });
  test('unknown command cannot enter native execution', () {
    expect(() => SandboxNativeCommand.parse(['pass']), throwsFormatException);
    expect(() => SandboxNativeCommand.parse([]), throwsFormatException);
  });
  test('startup polling cap cannot exceed remaining run budget', () {
    expect(
      sandboxNativeRemainingBudget(
        const Duration(minutes: 30),
        const Duration(minutes: 1),
      ),
      const Duration(minutes: 2),
    );
    expect(
      sandboxNativeRemainingBudget(
        const Duration(seconds: 30),
        const Duration(seconds: 29),
      ),
      const Duration(seconds: 1),
    );
    for (final elapsed in [30, 31]) {
      expect(
        () => sandboxNativeRemainingBudget(
          const Duration(seconds: 30),
          Duration(seconds: elapsed),
        ),
        throwsStateError,
      );
    }
  });

  late Directory temporary;
  late String sourceRoot, manifestPath, sourceFile;
  late Map<String, Object?> snapshot;
  final revision = 'a' * 40;
  setUp(() {
    temporary = Directory.systemTemp.createTempSync(
      'sandbox-native-input-test-',
    );
    sourceRoot = (Directory(
      p.join(temporary.path, 'source'),
    )..createSync()).path;
    sourceFile = p.join(sourceRoot, 'main.dart');
    File(sourceFile).writeAsStringSync('void main() {}\n');
    manifestPath = p.join(temporary.path, 'source.json');
    final bytes = File(sourceFile).readAsBytesSync();
    snapshot = {
      'schemaVersion': 1,
      'kind': 'sandbox-source-workspace-v1',
      'sourceRevision': revision,
      'dirty': true,
      'statusSha256': 'b' * 64,
      'diffSha256': 'c' * 64,
      'files': [
        {
          'path': 'main.dart',
          'state': 'present',
          'sha256': nativeHash(bytes),
          'length': bytes.length,
        },
      ],
    };
  });
  tearDown(() => temporary.deleteSync(recursive: true));
  void writeSnapshot() =>
      File(manifestPath).writeAsStringSync(jsonEncode(snapshot));
  test('exact private source snapshot works without a Git checkout', () {
    writeSnapshot();
    expect(
      verifySandboxSourceSnapshot(sourceRoot, manifestPath),
      File(manifestPath).readAsBytesSync(),
    );
  });
  test('source snapshot detects changed bytes at same size', () {
    writeSnapshot();
    File(sourceFile).writeAsStringSync('void main() []\n');
    expect(
      () => verifySandboxSourceSnapshot(sourceRoot, manifestPath),
      throwsStateError,
    );
  });
  test('source snapshot rejects extra unreviewed code', () {
    writeSnapshot();
    File(p.join(sourceRoot, 'other.dart')).writeAsStringSync('extra');
    expect(
      () => verifySandboxSourceSnapshot(sourceRoot, manifestPath),
      throwsStateError,
    );
  });
  test('source snapshot rejects missing reviewed source', () {
    writeSnapshot();
    File(sourceFile).deleteSync();
    expect(
      () => verifySandboxSourceSnapshot(sourceRoot, manifestPath),
      throwsStateError,
    );
  });
  test('deleted source must stay absent', () {
    snapshot['files'] = [
      {'path': 'main.dart', 'state': 'deleted'},
    ];
    writeSnapshot();
    expect(
      () => verifySandboxSourceSnapshot(sourceRoot, manifestPath),
      throwsStateError,
    );
    File(sourceFile).deleteSync();
    expect(verifySandboxSourceSnapshot(sourceRoot, manifestPath), isNotEmpty);
  });
  for (final mutation in <String, void Function(Map<String, Object?>)>{
    'wrong revision': (v) => v['sourceRevision'] = 'd' * 40,
    'unknown property': (v) => v['approved'] = true,
    'noninteger version': (v) => v['schemaVersion'] = 1.0,
    'string dirty': (v) => v['dirty'] = 'true',
    'unbounded length': (v) =>
        ((v['files'] as List).first as Map)['length'] = 268435457,
    'unknown state': (v) =>
        ((v['files'] as List).first as Map)['state'] = 'trusted',
    'parent traversal': (v) =>
        ((v['files'] as List).first as Map)['path'] = '../main.dart',
    'Git internals': (v) =>
        ((v['files'] as List).first as Map)['path'] = '.git/config',
    'case collision': (v) => (v['files'] as List).insert(0, {
      'path': 'MAIN.dart',
      'state': 'deleted',
    }),
  }.entries) {
    test('private source grammar refuses ${mutation.key}', () {
      mutation.value(snapshot);
      expect(
        () => decodeNativeSourceWorkspace(
          utf8.encode(jsonEncode(snapshot)),
          revision,
        ),
        throwsA(isA<Object>()),
      );
    });
  }
  test('duplicate source JSON fields refused', () {
    expect(
      () => decodeNativeSourceWorkspace(
        utf8.encode('{"kind":"a","kind":"b"}'),
        revision,
      ),
      throwsA(isA<Object>()),
    );
  });
  test('native inputs reject oversized and empty files', () {
    File(manifestPath).writeAsBytesSync([1, 2, 3]);
    expect(
      () => readNativeFile(manifestPath, maximum: 2),
      throwsA(isA<Object>()),
    );
    File(manifestPath).writeAsBytesSync([]);
    expect(() => readNativeFile(manifestPath), throwsA(isA<Object>()));
    expect(readNativeFile(manifestPath, allowEmpty: true), isEmpty);
  });
  test('immutable output creation refuses an existing destination', () {
    writeNativeBytes(manifestPath, [1, 2]);
    expect(
      () => writeNativeBytes(manifestPath, [3]),
      throwsA(isA<FileSystemException>()),
    );
    expect(File(manifestPath).readAsBytesSync(), [1, 2]);
  });
  test('native child paths cannot escape or alias the evidence root', () {
    for (final invalid in [
      '../outside',
      '/absolute',
      'folder/../outside',
      r'folder\file',
      'CON',
      'file:stream',
      'trailing.',
    ]) {
      expect(
        () => nativeChild(temporary.path, invalid),
        throwsA(isA<Object>()),
      );
    }
    expect(
      nativeChild(temporary.path, 'nested/file.json'),
      p.join(temporary.path, 'nested', 'file.json'),
    );
  });
}
