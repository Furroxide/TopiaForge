import 'dart:io';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/cli_join_options.dart';
import '../bin/topiaforge.dart' as cli;

void main() {
  for (final target in [
    '01234-56789',
    'topiaforge://join/0123456789',
    'localhost:27250',
    '[::1]:27250',
  ]) {
    test(
      'parses $target',
      () => expect(CliJoinOptions([target]).target, isA<JoinTarget>()),
    );
  }
  for (final args in <List<String>>[
    [],
    ['--yes'],
    ['localhost', '--yes', '--yes'],
    ['localhost', '--wait-seconds', '0'],
    ['localhost', '--wait-seconds', '301'],
    ['localhost', '--game-dir'],
    ['localhost', '--unknown'],
    ['https://example.invalid/path'],
    ['topiaforge://join/bad?target=203.0.113.99'],
  ]) {
    test('refuses invalid shape ${args.length} ${args.join().hashCode}', () {
      expect(() => CliJoinOptions(args), throwsFormatException);
    });
  }
  test('join help is dispatched without touching game data', () async {
    final lines = <String>[];
    final result = await IOOverrides.runZoned(
      () => cli.runCli(['join', '--help']),
      stdout: () => _Output(lines),
    );
    expect(result, 0);
    expect(lines.join(), contains('Usage: topiaforge join'));
  });
}

final class _Output implements Stdout {
  _Output(this.lines);
  final List<String> lines;
  @override
  void writeln([Object? object = '']) => lines.add('$object');
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
