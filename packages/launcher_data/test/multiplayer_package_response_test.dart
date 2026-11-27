import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:launcher_data/src/multiplayer/package_response.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

/// The `TFD1 package` reader on its own, fed chunk by chunk, so where the
/// network splits an answer is under the test's control. It must read what
/// the game's `DescriptorEndpoint` writes: `TFD1 200 <length>`, the length as
/// .NET writes a 64-bit integer, then the archive.
void main() {
  Uint8List bytesOf(String text) => Uint8List.fromList(latin1.encode(text));

  /// Feeds [chunks] until the reader finishes, then closes it as a dropped
  /// connection would. Returns the answer and every body byte handed out.
  (Tfd1PackageAnswer, List<int>) read(
    List<Uint8List> chunks, {
    int maximum = ModRegistryFormat.maxPackageBytes,
  }) {
    final reader = Tfd1PackageReader(maximumBodyBytes: maximum);
    final body = <int>[];
    for (final chunk in chunks) {
      body.addAll(reader.add(chunk));
      if (reader.isDone) return (reader.result!, body);
    }
    return (reader.close(), body);
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

  group('an archive', () {
    final archive = Uint8List.fromList(
      List<int>.generate(70000, (index) => (index * 31 + 7) & 0xFF),
    );
    final header = bytesOf('TFD1 200 ${archive.length}\n');
    final trailing = bytesOf('bytes after the archive are never handed out');
    final answer = Uint8List.fromList([...header, ...archive, ...trailing]);
    final end = header.length + archive.length;

    for (final (about, cuts) in [
      ('in one chunk', <int>[]),
      ('with the trailing bytes in a later chunk', [end]),
      ('with its end and the trailing bytes together', [end - 5]),
      ('with the header apart', [header.length]),
      ('split inside the status line', [4, 11]),
      (
        'in 64 KiB chunks, as the game writes it',
        [for (var at = 65536; at < answer.length; at += 65536) at],
      ),
      ('in uneven chunks', [3, 13, 4000, 4001, 65537, end - 1]),
    ]) {
      test('is handed out up to its announced length, $about', () {
        final (result, body) = read(cut(answer, cuts));
        expect((result as Tfd1PackageComplete).length, archive.length);
        expect(body, archive);
      });
    }

    test('byte by byte too', () {
      final small = Uint8List.fromList(List<int>.generate(300, (i) => i % 7));
      final whole = Uint8List.fromList([
        ...bytesOf('TFD1 200 300\n'),
        ...small,
        ...trailing,
      ]);
      final (result, body) = read(
        cut(whole, [for (var at = 1; at < whole.length; at++) at]),
      );
      expect(result, isA<Tfd1PackageComplete>());
      expect(body, small);
    });

    test('reports its length and progress as it arrives', () {
      final reader = Tfd1PackageReader();
      expect(reader.announcedLength, isNull);
      expect(reader.add(Uint8List.sublistView(header, 0, 5)), isEmpty);
      expect(reader.announcedLength, isNull);
      final first = reader.add(
        Uint8List.fromList([...header.sublist(5), ...archive.sublist(0, 10)]),
      );
      expect(first, archive.sublist(0, 10));
      expect(reader.announcedLength, archive.length);
      expect(reader.receivedBytes, 10);
      expect(reader.isDone, isFalse);
    });

    test('stays finished whatever arrives after it', () {
      final reader = Tfd1PackageReader();
      reader.add(Uint8List.fromList([...header, ...archive]));
      expect(reader.isDone, isTrue);
      expect(reader.add(trailing), isEmpty);
      expect(reader.add(bytesOf('TFD1 404\n')), isEmpty);
      expect(reader.close(), same(reader.result));
    });

    test('that ends short is refused when the connection ends', () {
      final (result, body) = read([Uint8List.sublistView(answer, 0, end - 1)]);
      expect(
        (result as Tfd1PackageMalformed).problem,
        Tfd1PackageProblem.endedEarly,
      );
      expect(body, hasLength(archive.length - 1));
    });

    test('announced as shorter than it is ends where announced', () {
      // A wrong length is the host's word: the bytes past it are not taken,
      // and the SHA-256 the caller checks tells the archive is not whole.
      final whole = Uint8List.fromList([
        ...bytesOf('TFD1 200 1000\n'),
        ...archive,
      ]);
      final (result, body) = read([whole]);
      expect((result as Tfd1PackageComplete).length, 1000);
      expect(body, archive.sublist(0, 1000));
    });
  });

  group('the length', () {
    test('is the game\'s 64-bit integer, up to the reader\'s bound', () {
      for (final (length, maximum) in [
        (1, 1),
        (536870912, ModRegistryFormat.maxPackageBytes),
        (65536, 65536),
      ]) {
        final reader = Tfd1PackageReader(maximumBodyBytes: maximum);
        reader.add(bytesOf('TFD1 200 $length\n'));
        expect(reader.announcedLength, length);
        expect(reader.isDone, isFalse);
      }
    });

    test('past the bound is refused before any of the body is taken', () {
      for (final line in [
        'TFD1 200 ${ModRegistryFormat.maxPackageBytes + 1}\n',
        'TFD1 200 9223372036854775807\n',
        'TFD1 200 9999999999999999999\n',
      ]) {
        final reader = Tfd1PackageReader();
        final body = reader.add(
          Uint8List.fromList([...bytesOf(line), 1, 2, 3]),
        );
        expect(body, isEmpty, reason: line);
        expect(
          (reader.result! as Tfd1PackageMalformed).problem,
          Tfd1PackageProblem.tooLarge,
          reason: line,
        );
        expect(reader.receivedBytes, 0);
      }
    });

    test('a small bound holds too', () {
      final reader = Tfd1PackageReader(maximumBodyBytes: 100);
      reader.add(bytesOf('TFD1 200 101\n'));
      expect(
        (reader.result! as Tfd1PackageMalformed).problem,
        Tfd1PackageProblem.tooLarge,
      );
    });

    test('that the game cannot write is impossible', () {
      for (final line in [
        'TFD1 200 0\n',
        'TFD1 200 00\n',
        'TFD1 200 0100\n',
        'TFD1 200 -5\n',
        'TFD1 200 +5\n',
        'TFD1 200  5\n',
        'TFD1 200 5 \n',
        'TFD1 200 5\r\n',
        'TFD1 200 1e6\n',
        'TFD1 200 0x10\n',
        'TFD1 200 \n',
        'TFD1 200 12345678901234567890\n',
      ]) {
        final reader = Tfd1PackageReader();
        expect(reader.add(bytesOf(line)), isEmpty, reason: line);
        expect(
          (reader.result! as Tfd1PackageMalformed).problem,
          Tfd1PackageProblem.impossibleLength,
          reason: line,
        );
      }
    });
  });

  group('statuses', () {
    test('without a body are read as the game reads them', () {
      for (final status in ['404', '429', '503', '400', '200', 'xyz']) {
        final (result, body) = read([bytesOf('TFD1 $status\n')]);
        expect((result as Tfd1PackageStatus).status, status);
        expect(body, isEmpty);
      }
    });

    test('of another shape are not statuses', () {
      for (final line in [
        'TFD1 4040\n',
        'TFD1 40\n',
        'TFD2 404\n',
        'tfd1 404\n',
        'HTTP/1.1 200 OK\r\n',
        '\n',
        'TFD1 201 5\n',
      ]) {
        final (result, _) = read([bytesOf(line)]);
        expect(
          (result as Tfd1PackageMalformed).problem,
          Tfd1PackageProblem.badStatusLine,
          reason: line,
        );
      }
    });

    test('must end within 64 bytes', () {
      final long = bytesOf('TFD1 200 ${'1' * 60}');
      final (result, _) = read([long]);
      expect(
        (result as Tfd1PackageMalformed).problem,
        Tfd1PackageProblem.noStatusLine,
      );
      // 63 bytes and the newline fit; 64 without one do not.
      final fits = bytesOf('${'x' * 63}\n');
      expect(
        (read([fits]).$1 as Tfd1PackageMalformed).problem,
        Tfd1PackageProblem.badStatusLine,
      );
      final over = bytesOf('${'x' * 64}\n');
      expect(
        (read([over]).$1 as Tfd1PackageMalformed).problem,
        Tfd1PackageProblem.noStatusLine,
      );
    });

    test('a connection that ends first has no status line', () {
      for (final bytes in ['', 'TFD1 2', 'TFD1 200 5']) {
        final (result, _) = read([bytesOf(bytes)]);
        expect(
          (result as Tfd1PackageMalformed).problem,
          Tfd1PackageProblem.noStatusLine,
          reason: bytes,
        );
      }
    });
  });

  test('hostile bytes never throw', () {
    final random = Random(20261022);
    for (var round = 0; round < 3000; round++) {
      final reader = Tfd1PackageReader(maximumBodyBytes: 4096);
      final prefix = switch (round % 4) {
        0 => 'TFD1 200 ${random.nextInt(9000)}\n',
        1 => 'TFD1 ${random.nextInt(1000)}\n',
        2 => 'TFD1 200 ${'9' * random.nextInt(30)}\n',
        _ => '',
      };
      final noise = List<int>.generate(
        random.nextInt(600),
        (_) => random.nextInt(256),
      );
      final bytes = Uint8List.fromList([...latin1.encode(prefix), ...noise]);
      var at = 0;
      var handed = 0;
      while (at < bytes.length) {
        final next = min(bytes.length, at + 1 + random.nextInt(64));
        handed += reader.add(Uint8List.sublistView(bytes, at, next)).length;
        at = next;
      }
      final result = reader.isDone ? reader.result! : reader.close();
      expect(handed, reader.receivedBytes);
      if (result is Tfd1PackageComplete) {
        expect(handed, result.length);
      }
      expect(reader.receivedBytes, lessThanOrEqualTo(4096));
    }
  });

  test('a reader bound past what a package may be is refused', () {
    expect(
      () => Tfd1PackageReader(
        maximumBodyBytes: ModRegistryFormat.maxPackageBytes + 1,
      ),
      throwsRangeError,
    );
    expect(() => Tfd1PackageReader(maximumBodyBytes: 0), throwsRangeError);
  });

  test('ascii and Latin-1 alike: a status keeps its own bytes', () {
    final (result, _) = read([
      Uint8List.fromList([...ascii.encode('TFD1 '), 0xE9, 0x00, 0x7F, 0x0A]),
    ]);
    expect((result as Tfd1PackageStatus).status, 'é\u0000\u007f');
  });
}
