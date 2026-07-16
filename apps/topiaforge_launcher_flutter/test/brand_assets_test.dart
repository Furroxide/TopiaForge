import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as image;
import 'package:path/path.dart' as p;

void main() {
  final appRoot = Directory.current.absolute;
  final repositoryRoot = Directory(
    p.normalize(p.join(appRoot.path, '..', '..')),
  );

  test('canonical pixel art keeps its dimensions and binary alpha', () {
    final brandRoot = p.join(
      repositoryRoot.path,
      'packages',
      'launcher_ui',
      'assets',
      'brand',
    );
    final wordmark = _decode(p.join(brandRoot, 'topiaforge-wordmark.png'));
    final icon = _decode(p.join(brandRoot, 'topiaforge-icon.png'));

    expect((wordmark.width, wordmark.height), (114, 18));
    expect((icon.width, icon.height), (144, 144));
    expect(_hasBinaryAlpha(wordmark), isTrue);
    expect(_hasBinaryAlpha(icon), isTrue);
    expect(_opaqueColors(wordmark).length, 6);
    expect(_opaqueColors(icon).length, 6);
  });

  test('generated desktop and website rasters use the new mark', () {
    final master = _decode(
      p.join(appRoot.path, 'assets', 'brand', 'topiaforge-app-icon.png'),
    );
    final snap = _decode(p.join(appRoot.path, 'snap', 'gui', 'topiaforge.png'));
    final favicon = _decode(
      p.join(repositoryRoot.path, 'website', 'public', 'favicon.png'),
    );
    final masterColors = _opaqueColors(master);

    expect((master.width, master.height), (1024, 1024));
    expect((snap.width, snap.height), (256, 256));
    expect((favicon.width, favicon.height), (64, 64));
    expect(masterColors, contains(0xff92e8c0));
    expect(masterColors, contains(0xffff5277));
    expect(masterColors, isNot(contains(0xffff8933)));
  });
}

image.Image _decode(String path) {
  final file = File(path);
  expect(file.existsSync(), isTrue, reason: 'Missing generated asset: $path');
  final decoded = image.decodeImage(file.readAsBytesSync());
  expect(decoded, isNotNull, reason: 'Unreadable generated asset: $path');
  return decoded!;
}

bool _hasBinaryAlpha(image.Image value) {
  for (final pixel in value) {
    if (pixel.a != 0 && pixel.a != pixel.maxChannelValue) {
      return false;
    }
  }
  return true;
}

Set<int> _opaqueColors(image.Image value) {
  final colors = <int>{};
  for (final pixel in value) {
    if (pixel.a == pixel.maxChannelValue) {
      colors.add(
        (0xff << 24) |
            (pixel.r.toInt() << 16) |
            (pixel.g.toInt() << 8) |
            pixel.b.toInt(),
      );
    }
  }
  return colors;
}
