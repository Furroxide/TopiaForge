import 'dart:convert';
import 'dart:io';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  Future<ProcessResult> cli(List<String> args) => Process.run(
    Platform.resolvedExecutable,
    [p.join('bin', 'topiaforge.dart'), ...args],
    environment: {'DART_SUPPRESS_ANALYTICS': 'true'},
  );
  test('help and command-specific help expose both verbs', () async {
    final help = await cli(['help']);
    expect(help.exitCode, 0);
    expect(help.stdout, contains('topiaforge servers'));
    expect(help.stdout, contains('topiaforge host'));
    for (final verb in ['host', 'servers']) {
      final result = await cli([verb, '--help']);
      expect(result.exitCode, 0);
      expect(result.stdout, contains('Usage: topiaforge $verb'));
    }
  });
  test('nonterminal host refuses before discovery', () async {
    final result = await cli(['host']);
    expect(result.exitCode, 2);
    expect(result.stderr, contains('TFHOST110:'));
    expect(result.stderr, contains('--yes'));
  });
  test('invalid host options have stable 100 and no raw option', () async {
    final result = await cli(['host', '--private-endpoint']);
    expect(result.exitCode, 2);
    expect(result.stderr, contains('TFHOST100:'));
    expect(result.stderr, isNot(contains('--private-endpoint')));
  });
  test(
    'multiplayer suite dry run has no live evidence or process actions',
    () async {
      final result = await cli([
        'acceptance',
        'run',
        '--suite',
        'multiplayer',
        '--dry-run',
      ]);
      expect(result.exitCode, 0, reason: '${result.stderr}');
      final document = jsonDecode(result.stdout as String) as Map;
      expect(document['topology'], 'listen-pair');
      expect(document['liveEvidence'], isFalse);
      expect((document['cases'] as List).length, 25);
    },
  );
  test(
    'a missing multiplayer probe is refused clearly without its path',
    () async {
      final result = await cli([
        'acceptance',
        'run',
        '--suite',
        'multiplayer',
        '--game-dir',
        'absent-game',
        '--isolation-record',
        'absent-host-record.json',
        '--guest-isolation-record',
        'absent-guest-record.json',
        '--package',
        'absent-probe.topiaforgemod',
        '--output',
        'absent-evidence',
      ]);
      expect(result.exitCode, 1);
      expect(result.stderr, contains('TFMPACCEPT110:'));
      expect(
        result.stderr,
        contains('Build tests/TopiaForge.MultiplayerAcceptanceMod'),
      );
      expect(result.stderr, isNot(contains('absent-probe')));
      expect(Directory('absent-evidence').existsSync(), isFalse);
    },
  );
  test('an unknown multiplayer case is a usage error', () async {
    final result = await cli([
      'acceptance',
      'run',
      '--suite',
      'multiplayer',
      '--dry-run',
      '--case',
      'multiplayer.unknown',
    ]);
    expect(result.exitCode, 2);
    expect(result.stderr, contains('TFMPACCEPT100:'));
    expect(result.stderr, isNot(contains('FormatException')));
  });
  test(
    'unknown suite cannot fall through to single-instance acceptance',
    () async {
      final result = await cli([
        'acceptance',
        'run',
        '--suite',
        'unknown',
        '--dry-run',
      ]);
      expect(result.exitCode, 2);
    },
  );
  test('join stays deferred', () async {
    final result = await cli(['join', '--help']);
    expect(result.exitCode, 2);
    expect(result.stderr, contains('Unknown command: join'));
  });
}
