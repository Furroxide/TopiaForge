import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;

  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-dotnet-sdk-');
  });

  tearDown(() {
    if (root.existsSync()) root.deleteSync(recursive: true);
  });

  test('selects only a host reporting the exact global.json SDK', () async {
    _writeGlobalJson(root, '10.0.301');
    final probed = <String>[];

    final selected = await resolveRepositoryDotnetSdk(
      root,
      candidateExecutables: const ['/wrong/dotnet', '/exact/dotnet'],
      versionProbe: (executable, workingDirectory) async {
        probed.add(executable);
        expect(workingDirectory, root.absolute.path);
        return BoundedProcessResult(
          exitCode: 0,
          stdout: executable.contains('exact') ? '10.0.301\n' : '10.0.101\n',
          stderr: '',
        );
      },
    );

    expect(selected.executable, '/exact/dotnet');
    expect(selected.version, '10.0.301');
    expect(selected.requiredVersion, '10.0.301');
    expect(probed, ['/wrong/dotnet', '/exact/dotnet']);
  });

  test('does not treat an empty or failed version probe as usable', () async {
    _writeGlobalJson(root, '10.0.301');

    await expectLater(
      resolveRepositoryDotnetSdk(
        root,
        candidateExecutables: const ['/empty/dotnet', '/failed/dotnet'],
        versionProbe: (executable, _) async => BoundedProcessResult(
          exitCode: executable.contains('failed') ? 1 : 0,
          stdout: '',
          stderr: 'compatible SDK was not found',
        ),
      ),
      throwsA(
        isA<StateError>()
            .having(
              (error) => error.message,
              'message',
              contains('.NET SDK 10.0.301'),
            )
            .having(
              (error) => error.message,
              'message',
              contains('TOPIAFORGE_DOTNET_PATH'),
            ),
      ),
    );
  });

  test('fails clearly when the repository has no global.json pin', () async {
    await expectLater(
      resolveRepositoryDotnetSdk(
        root,
        candidateExecutables: const ['/otherwise-valid/dotnet'],
        versionProbe: (_, _) async => const BoundedProcessResult(
          exitCode: 0,
          stdout: '10.0.301',
          stderr: '',
        ),
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          allOf(contains('global.json was not found'), contains(root.path)),
        ),
      ),
    );
  });

  test('rejects linked global.json instead of following it', () async {
    if (Platform.isWindows) return;
    final outside = File(p.join(root.parent.path, '${root.path.hashCode}.json'))
      ..writeAsStringSync(jsonEncode(_globalJson('10.0.301')));
    addTearDown(() {
      if (outside.existsSync()) outside.deleteSync();
    });
    Link(p.join(root.path, 'global.json')).createSync(outside.path);

    await expectLater(
      resolveRepositoryDotnetSdk(
        root,
        candidateExecutables: const ['/exact/dotnet'],
        versionProbe: (_, _) async => const BoundedProcessResult(
          exitCode: 0,
          stdout: '10.0.301',
          stderr: '',
        ),
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('regular file'),
        ),
      ),
    );
  });

  test('doctor reports an exact-SDK resolution failure as blocking', () async {
    final repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
      dotnetSdkResolver: (_) async =>
          throw StateError('Required .NET SDK 10.0.301 was not found.'),
    );

    final environment = await repository.checkEnvironment();
    final sdk = environment.checks.singleWhere(
      (check) => check.name == '.NET SDK',
    );
    expect(sdk.status, ToolStatus.missing);
    expect(sdk.detail, contains('10.0.301'));

    final doctor = await repository.runDoctor();
    expect(
      doctor.issues,
      contains(
        isA<LauncherIssue>()
            .having((issue) => issue.isBlocking, 'blocking', isTrue)
            .having((issue) => issue.message, 'message', contains('10.0.301')),
      ),
    );
  });
}

void _writeGlobalJson(Directory root, String version) {
  File(
    p.join(root.path, 'global.json'),
  ).writeAsStringSync(jsonEncode(_globalJson(version)));
}

Map<String, Object?> _globalJson(String version) => {
  'sdk': {
    'version': version,
    'rollForward': 'disable',
    'allowPrerelease': false,
  },
};
