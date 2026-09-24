import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_candidate_contract.dart';
import 'package:topiaforge/src/release_git_contract.dart';
import 'package:topiaforge/src/release_readiness.dart';

void main() {
  for (final replacement in ['commit', 'blob']) {
    for (final reader in ['blob', 'candidate', 'readiness']) {
      test(
        '$reader reads frozen bytes despite $replacement replacement',
        () async {
          final fixture = _ReplacementFixture.create(replacement);
          addTearDown(() => fixture.directory.deleteSync(recursive: true));
          final expectedDigest = sha256.convert(fixture.original).toString();
          switch (reader) {
            case 'blob':
              final bytes = await readReleaseGitBlob(
                fixture.directory.path,
                fixture.commit,
                releaseReadinessPath,
                128 * 1024,
              );
              expect(bytes, fixture.original);
            case 'candidate':
              final contract = await ReleaseCandidateContract.load(
                fixture.directory.path,
                fixture.commit,
              );
              expect(contract.digest(releaseReadinessPath), expectedDigest);
            case 'readiness':
              final decision = await ReleaseReadinessDecision.loadAtGitSha(
                repositoryRoot: fixture.directory.path,
                targetSha: fixture.commit,
                expectedReleaseVersion:
                    (jsonDecode(utf8.decode(fixture.original))
                            as Map)['releaseVersion']
                        as String,
              );
              expect(decision.readinessBlobSha256, expectedDigest);
          }
        },
      );
    }
  }
}

final class _ReplacementFixture {
  _ReplacementFixture(this.directory, this.commit, this.original);
  final Directory directory;
  final String commit;
  final List<int> original;

  static _ReplacementFixture create(String replacement) {
    var source = Directory.current.absolute;
    while (!File(p.join(source.path, 'TopiaForge.slnx')).existsSync()) {
      if (source.parent.path == source.path) {
        throw StateError('Repository not found.');
      }
      source = source.parent;
    }
    final directory = Directory.systemTemp.createTempSync(
      'release-replace-test-',
    );
    try {
      for (final path in candidateContractPaths) {
        final target = File(p.join(directory.path, path));
        target.parent.createSync(recursive: true);
        target.writeAsBytesSync(
          File(p.join(source.path, path)).readAsBytesSync(),
        );
      }
      _git(directory, ['init', '--quiet']);
      _git(directory, ['config', 'user.name', 'Synthetic Release Reader Test']);
      _git(directory, ['config', 'user.email', 'reader@example.invalid']);
      _git(directory, ['config', 'core.autocrlf', 'false']);
      _git(directory, [
        'config',
        'core.hooksPath',
        p.join(directory.path, 'no-hooks'),
      ]);
      _git(directory, ['config', 'commit.gpgsign', 'false']);
      _git(directory, ['add', '--', ...candidateContractPaths]);
      _git(directory, [
        'commit',
        '--quiet',
        '-m',
        'test: freeze synthetic source',
      ]);
      final commit = _git(directory, ['rev-parse', 'HEAD']);
      final original = File(
        p.join(directory.path, releaseReadinessPath),
      ).readAsBytesSync();
      final oldBlob = _git(directory, [
        'rev-parse',
        '$commit:$releaseReadinessPath',
      ]);
      // Whitespace changes the immutable blob but keeps readiness semantically valid.
      File(
        p.join(directory.path, releaseReadinessPath),
      ).writeAsBytesSync([...original, 10]);
      _git(directory, ['add', '--', releaseReadinessPath]);
      _git(directory, ['commit', '--quiet', '-m', 'test: replacement source']);
      final otherCommit = _git(directory, ['rev-parse', 'HEAD']);
      final otherBlob = _git(directory, [
        'rev-parse',
        '$otherCommit:$releaseReadinessPath',
      ]);
      _git(directory, [
        'replace',
        replacement == 'commit' ? commit : oldBlob,
        replacement == 'commit' ? otherCommit : otherBlob,
      ]);
      // Prove the test has installed an effective replacement, not a no-op.
      final substituted = _git(directory, [
        'cat-file',
        'blob',
        '$commit:$releaseReadinessPath',
      ], trim: false);
      expect(utf8.encode(substituted), [...original, 10]);
      return _ReplacementFixture(directory, commit, original);
    } catch (_) {
      directory.deleteSync(recursive: true);
      rethrow;
    }
  }
}

String _git(Directory directory, List<String> arguments, {bool trim = true}) {
  final result = Process.runSync(
    'git',
    ['-C', directory.path, ...arguments],
    stdoutEncoding: utf8,
    stderrEncoding: utf8,
  );
  if (result.exitCode != 0) throw StateError('${result.stderr}');
  return trim ? (result.stdout as String).trim() : result.stdout as String;
}
