import 'dart:convert';
import 'dart:typed_data';

import 'package:launcher_data/src/multiplayer/descriptor_response.dart';
import 'package:test/test.dart';

/// The TFD1 reader on its own, fed chunk by chunk, so where the network
/// splits an answer is under the test's control.
void main() {
  Uint8List bytesOf(String text) => Uint8List.fromList(latin1.encode(text));

  /// Feeds [chunks] until the reader finishes, then closes it as a dropped
  /// connection would.
  Tfd1Response read(List<Uint8List> chunks) {
    final reader = Tfd1ResponseReader();
    for (final chunk in chunks) {
      if (reader.add(chunk)) return reader.result!;
    }
    return reader.close();
  }

  /// [bytes] cut at each of [cuts].
  List<Uint8List> cut(Uint8List bytes, List<int> cuts) {
    final chunks = <Uint8List>[];
    var start = 0;
    for (final end in [...cuts, bytes.length]) {
      chunks.add(Uint8List.sublistView(bytes, start, end));
      start = end;
    }
    return chunks;
  }

  group('a body', () {
    final body = bytesOf('{"descriptor": "bytes"}');
    final header = bytesOf('TFD1 200 ${body.length}\n');
    final trailing = bytesOf('trailing bytes the reader never takes');
    final answer = Uint8List.fromList([...header, ...body, ...trailing]);
    final end = header.length + body.length;

    for (final (about, cuts) in [
      ('in one chunk', <int>[]),
      ('with the trailing bytes in a later chunk', [end]),
      ('with its end and the trailing bytes together', [header.length + 3]),
      ('with the header apart', [header.length]),
      ('byte by byte', [for (var at = 1; at < answer.length; at++) at]),
    ]) {
      test('is read up to its announced length, $about', () {
        final response = read(cut(answer, cuts));
        expect(response, isA<Tfd1Body>());
        expect((response as Tfd1Body).bytes, body);
      });
    }

    test('stays finished whatever arrives after it', () {
      final reader = Tfd1ResponseReader();
      expect(reader.add(Uint8List.fromList([...header, ...body])), isTrue);
      expect(reader.add(trailing), isTrue);
      expect(reader.add(bytesOf('TFD1 404\n')), isTrue);
      expect((reader.result! as Tfd1Body).bytes, body);
      expect(reader.close(), same(reader.result));
    });

    test('that ends early is malformed', () {
      final response = read([Uint8List.sublistView(answer, 0, end - 1)]);
      expect((response as Tfd1Malformed).problem, Tfd1Problem.endedEarly);
    });

    test('of an impossible length is refused before any of it', () {
      for (final line in [
        'TFD1 200 0\n',
        'TFD1 200 0100\n',
        'TFD1 200 262145\n',
        'TFD1 200 99999999\n',
        'TFD1 200 abc\n',
        'TFD1 200 \n',
        'TFD1 200 -5\n',
      ]) {
        final reader = Tfd1ResponseReader();
        expect(reader.add(bytesOf(line)), isTrue, reason: line);
        expect(
          (reader.result! as Tfd1Malformed).problem,
          Tfd1Problem.impossibleLength,
          reason: line,
        );
      }
    });
  });

  group('a status', () {
    test('is any eight bytes that start with TFD1, as the game reads it', () {
      for (final (line, status) in [
        ('TFD1 404\n', '404'),
        ('TFD1 400\n', '400'),
        ('TFD1 999\n', '999'),
        ('TFD1 200\n', '200'),
        ('TFD1 4é0\n', '4é0'),
        ('TFD1 40\r\n', '40\r'),
      ]) {
        final response = read([bytesOf('${line}trailing')]);
        expect((response as Tfd1Status).status, status, reason: line);
      }
    });

    test('of another shape is no status at all', () {
      for (final (line, problem) in [
        ('TFD1 4040\n', Tfd1Problem.badStatusLine),
        ('TFD1 40\n', Tfd1Problem.badStatusLine),
        ('HTTP/1.1 200 OK\r\n', Tfd1Problem.badStatusLine),
        ('\n', Tfd1Problem.badStatusLine),
        ('${'A' * 64}\n', Tfd1Problem.noStatusLine),
        ('', Tfd1Problem.noStatusLine),
      ]) {
        final response = read([bytesOf(line)]);
        expect((response as Tfd1Malformed).problem, problem, reason: line);
      }
    });

    test('fills at most 64 bytes with its newline', () {
      final longest = 'TFD1 200 ${' ' * 54}\n';
      expect(longest.length, Tfd1ResponseReader.maximumHeaderBytes);
      final response = read([bytesOf(longest)]);
      expect((response as Tfd1Malformed).problem, Tfd1Problem.impossibleLength);
    });
  });
}
