import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

const _version = '0.1.0-rc.1';
const _nonGame = ['P0-IP-01', 'P0-OSS-01', 'P0-PRIV-01', 'P0-CRED-01'];

void main() {
  test(
    'four approvals permit private preparation with gameplay deferred',
    () async {
      final fixture = _Fixture.create(_nonGame);
      try {
        final result = await fixture.run('validate-prerequisites');
        expect(result.exitCode, 0, reason: '${result.stderr}');
        final summary = jsonDecode(result.stdout as String) as Map;
        expect(summary['status'], 'eligible-for-private-build');
        expect(summary['targetSha'], fixture.sha);
        expect(summary['gates'], hasLength(12));
        expect(summary['deferredGateIds'], ['P0-GAME-01']);
        expect(
          (summary['gates'] as List)
              .where((gate) => (gate as Map)['id'] == 'P0-GAME-01')
              .single['status'],
          'blocked',
        );
      } finally {
        fixture.dispose();
      }
    },
  );

  for (final omitted in _nonGame) {
    test('private preparation refuses missing $omitted approval', () async {
      final fixture = _Fixture.create(_nonGame.where((id) => id != omitted));
      try {
        final result = await fixture.run('validate-prerequisites');
        expect(result.exitCode, 1);
        expect(result.stderr, contains(omitted));
        expect(result.stdout, isNot(contains('"status": "ready"')));
      } finally {
        fixture.dispose();
      }
    });
  }

  test(
    'tracked-ready gates alone cannot authorize publication without assets',
    () async {
      final fixture = _Fixture.create([..._nonGame, 'P0-GAME-01']);
      try {
        final result = await fixture.run('validate-readiness');
        expect(result.exitCode, 2);
        expect(result.stderr, contains('--assets'));
      } finally {
        fixture.dispose();
      }
    },
  );

  test(
    'private eligibility uses the frozen decision despite working tree drift',
    () async {
      final fixture = _Fixture.create(_nonGame);
      try {
        File(
          p.join(fixture.root.path, 'release', 'release-readiness.json'),
        ).writeAsStringSync('{}');
        final result = await fixture.run('validate-prerequisites');
        expect(result.exitCode, 0, reason: '${result.stderr}');
        expect(jsonDecode(result.stdout as String)['targetSha'], fixture.sha);
      } finally {
        fixture.dispose();
      }
    },
  );
}

class _Fixture {
  _Fixture(this.root, this.source, this.sha);
  final Directory root;
  final Directory source;
  final String sha;

  static _Fixture create(Iterable<String> approvals) {
    var source = Directory.current.absolute;
    while (!File(p.join(source.path, 'TopiaForge.slnx')).existsSync()) {
      if (source.parent.path == source.path) {
        throw StateError('Missing repository');
      }
      source = source.parent;
    }
    final root = Directory.systemTemp.createTempSync(
      'topiaforge-prerequisites-',
    );
    try {
      final readiness =
          jsonDecode(
                File(
                  p.join(source.path, 'release', 'release-readiness.json'),
                ).readAsStringSync(),
              )
              as Map;
      for (final gate in (readiness['gates'] as List).cast<Map>()) {
        if (!approvals.contains(gate['id'])) continue;
        gate['status'] = 'approved';
        gate.remove('reasonCode');
        gate['evidenceIds'] = ['EVID-${gate['id']}-0001'];
      }
      readiness['status'] =
          (readiness['gates'] as List).cast<Map>().any(
            (gate) =>
                gate['enforcement'] == 'blocking' &&
                gate['status'] != 'approved',
          )
          ? 'blocked'
          : 'ready';
      final file = File(p.join(root.path, 'release', 'release-readiness.json'));
      file.parent.createSync(recursive: true);
      file.writeAsStringSync(jsonEncode(readiness));
      final schema = File(
        p.join(
          root.path,
          'schemas',
          'topiaforge.release-readiness-v1.schema.json',
        ),
      );
      schema.parent.createSync(recursive: true);
      File(
        p.join(
          source.path,
          'schemas',
          'topiaforge.release-readiness-v1.schema.json',
        ),
      ).copySync(schema.path);
      File(
        p.join(root.path, 'TopiaForge.slnx'),
      ).writeAsStringSync('<Solution />');
      _git(root, ['init', '--quiet']);
      _git(root, ['config', 'user.name', 'Synthetic prerequisite test']);
      _git(root, ['config', 'user.email', 'fixture@example.invalid']);
      _git(root, [
        'add',
        '--',
        'release/release-readiness.json',
        'schemas/topiaforge.release-readiness-v1.schema.json',
        'TopiaForge.slnx',
      ]);
      _git(root, [
        '-c',
        'commit.gpgsign=false',
        'commit',
        '--quiet',
        '-m',
        'test: freeze synthetic prerequisite decision',
      ]);
      return _Fixture(root, source, _git(root, ['rev-parse', 'HEAD']).trim());
    } catch (_) {
      root.deleteSync(recursive: true);
      rethrow;
    }
  }

  Future<ProcessResult> run(String command) => Process.run(
    Platform.resolvedExecutable,
    [
      p.join(source.path, 'apps', 'topiaforge_cli', 'bin', 'topiaforge.dart'),
      'release',
      command,
      '--version',
      _version,
      '--target-sha',
      sha,
    ],
    workingDirectory: root.path,
    stdoutEncoding: utf8,
    stderrEncoding: utf8,
  );

  void dispose() => root.deleteSync(recursive: true);
}

String _git(Directory root, List<String> args) {
  final result = Process.runSync(
    'git',
    ['-C', root.path, ...args],
    stdoutEncoding: utf8,
    stderrEncoding: utf8,
  );
  if (result.exitCode != 0) {
    throw StateError('Synthetic git setup failed: ${result.stderr}');
  }
  return result.stdout as String;
}
