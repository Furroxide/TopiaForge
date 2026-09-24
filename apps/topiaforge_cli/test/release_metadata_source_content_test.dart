import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_metadata_source_content.dart';

void main() {
  test('declared text preserves legitimate Git newline conversion', () async {
    final fixture = _Fixture.create(
      '*.txt text eol=lf',
      'source.txt',
      utf8.encode('first\nsecond\n'),
    );
    addTearDown(fixture.dispose);
    fixture.file.writeAsStringSync('first\r\nsecond\r\n');
    await fixture.verify();
  });

  test(
    'frozen -text refuses rewritten bytes despite working attributes',
    () async {
      final fixture = _Fixture.create(
        '*.txt -text',
        'source.txt',
        utf8.encode('first\r\nsecond\r\n'),
      );
      addTearDown(fixture.dispose);
      File(
        p.join(fixture.root.path, '.gitattributes'),
      ).writeAsStringSync('*.txt text eol=lf\n');
      fixture.file.writeAsStringSync('first\nsecond\n');
      await expectLater(fixture.verify(), throwsStateError);
    },
  );

  test('auto text keeps binary source bytes exact', () async {
    final fixture = _Fixture.create('*.txt text=auto', 'source.txt', [
      0,
      13,
      10,
      65,
    ]);
    addTearDown(fixture.dispose);
    fixture.file.writeAsBytesSync([0, 10, 65]);
    await expectLater(fixture.verify(), throwsStateError);
  });

  test(
    'hydrated LFS content is verified against exact pointer oid and size',
    () async {
      final content = utf8.encode('Synthetic LFS bundle content.');
      final fixture = _Fixture.lfs(content);
      addTearDown(fixture.dispose);
      fixture.file.writeAsBytesSync(content);
      await fixture.verify();
      // A clean index hint never authorizes different same-size bundle bytes.
      fixture.file.writeAsBytesSync([...content]..[0] = 0);
      await expectLater(fixture.verify(), throwsStateError);
    },
  );

  test('unhydrated LFS pointers cannot be consumed as a bundle', () async {
    final fixture = _Fixture.lfs(utf8.encode('Synthetic bundle.'));
    addTearDown(fixture.dispose);
    await expectLater(fixture.verify(), throwsStateError);
  });

  test(
    'custom Git filters cannot transform publication metadata inputs',
    () async {
      final fixture = _Fixture.create(
        '*.txt filter=custom',
        'source.txt',
        utf8.encode('Synthetic custom-filter input.'),
      );
      addTearDown(fixture.dispose);
      await expectLater(fixture.verify(), throwsStateError);
    },
  );
}

final class _Fixture {
  _Fixture(this.root, this.name, this.sha);
  final Directory root;
  final String name;
  final String sha;
  File get file => File(p.join(root.path, name));
  Future<void> verify() => verifyMetadataSourceContents(root.path, sha, [name]);
  void dispose() => root.deleteSync(recursive: true);

  static _Fixture lfs(List<int> content) => create(
    '*.bundle filter=lfs -text',
    'source.bundle',
    utf8.encode(
      'version https://git-lfs.github.com/spec/v1\n'
      'oid sha256:${sha256.convert(content)}\nsize ${content.length}\n',
    ),
  );

  static _Fixture create(String attrs, String name, List<int> bytes) {
    final root = Directory.systemTemp.createTempSync('metadata-content-');
    File(p.join(root.path, '.gitattributes')).writeAsStringSync('$attrs\n');
    File(p.join(root.path, name)).writeAsBytesSync(bytes);
    String git(List<String> args) {
      final result = Process.runSync('git', args, workingDirectory: root.path);
      if (result.exitCode != 0) throw StateError('${result.stderr}');
      return '${result.stdout}'.trim();
    }

    git(['init', '--quiet']);
    git(['config', 'core.autocrlf', 'false']);
    git(['config', 'user.name', 'Metadata Content Test']);
    git(['config', 'user.email', 'metadata@example.invalid']);
    // Commit a synthetic pointer without invoking or downloading through LFS.
    git(['config', 'filter.lfs.clean', '']);
    git(['config', 'filter.lfs.smudge', '']);
    git(['config', 'filter.lfs.required', 'false']);
    git(['add', '--', '.']);
    git([
      '-c',
      'commit.gpgsign=false',
      'commit',
      '--quiet',
      '-m',
      'test: freeze synthetic content',
    ]);
    git(['update-index', '--skip-worktree', '--', name]);
    return _Fixture(root, name, git(['rev-parse', 'HEAD']));
  }
}
