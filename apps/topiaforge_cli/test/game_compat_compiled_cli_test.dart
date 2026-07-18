import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test(
    'compiled CLI selects and bounds its packaged sibling extractor',
    () async {
      final packageRoot = Directory.current.absolute.path;
      final temp = Directory.systemTemp.createTempSync(
        'topiaforge-compiled-game-compat-',
      );
      try {
        final cliName = Platform.isWindows
            ? 'topiaforge.exe'
            : Platform.isMacOS
            ? Platform.version.contains('arm64')
                  ? 'topiaforge-arm64'
                  : 'topiaforge-x64'
            : 'topiaforge';
        final extractorName = Platform.isWindows
            ? 'TopiaForge.GameCompat.Extractor.exe'
            : 'TopiaForge.GameCompat.Extractor';
        final cli = p.join(temp.path, cliName);
        final extractor = p.join(temp.path, extractorName);

        await _compile(
          packageRoot,
          p.join(packageRoot, 'bin', 'topiaforge.dart'),
          cli,
        );
        await _compile(
          packageRoot,
          p.join(packageRoot, 'test', 'fixtures', 'game_compat_probe.dart'),
          extractor,
        );

        final outsideRelease = Directory(p.join(temp.path, 'outside-release'))
          ..createSync();
        final managed = p.join(outsideRelease.path, 'Managed path with spaces');
        final result = await runBoundedProcess(
          cli,
          ['compat', '--managed', managed, '--json'],
          workingDirectory: outsideRelease.path,
          timeout: const Duration(minutes: 1),
          maxStdoutBytes: 1024 * 1024,
          maxStderrBytes: 1024 * 1024,
        );

        expect(result.exitCode, 0, reason: result.stderr);
        final report = jsonDecode(result.stdout) as Map<String, dynamic>;
        expect(report['probe'], 'packaged-game-compat');
        expect(report['arguments'], [
          'verify',
          '--managed',
          managed,
          '--format',
          'json',
        ]);
        final resolvedProbe = File(
          report['resolvedExecutable'] as String,
        ).resolveSymbolicLinksSync();
        final resolvedExtractor = File(extractor).resolveSymbolicLinksSync();
        expect(
          p.equals(resolvedProbe, resolvedExtractor),
          isTrue,
          reason:
              'the sibling executable itself must handle the request '
              '(reported $resolvedProbe, expected $resolvedExtractor)',
        );

        final overflow = await runBoundedProcess(
          cli,
          const ['compat', '--json'],
          workingDirectory: outsideRelease.path,
          environment: {
            ...Platform.environment,
            'TOPIAFORGE_GAME_COMPAT_PROBE_MODE': 'overflow',
          },
          timeout: const Duration(minutes: 1),
          maxStdoutBytes: 1024 * 1024,
          maxStderrBytes: 1024 * 1024,
        );
        expect(overflow.exitCode, 1);
        expect(overflow.stdout, isEmpty);
        expect(
          overflow.stderr,
          contains(
            'GameCompat extractor exceeded the 4 MiB combined output limit',
          ),
        );
      } finally {
        if (temp.existsSync()) {
          temp.deleteSync(recursive: true);
        }
      }
    },
    timeout: const Timeout(Duration(minutes: 3)),
  );
}

Future<void> _compile(String packageRoot, String source, String output) async {
  final result = await runBoundedProcess(
    Platform.resolvedExecutable,
    ['compile', 'exe', source, '-o', output],
    workingDirectory: packageRoot,
    timeout: const Duration(minutes: 1),
    maxStdoutBytes: 2 * 1024 * 1024,
    maxStderrBytes: 2 * 1024 * 1024,
  );
  if (result.exitCode != 0) {
    fail(
      'Could not compile ${p.basename(source)} (exit ${result.exitCode}).\n'
      '${result.stdout}\n${result.stderr}',
    );
  }
}
