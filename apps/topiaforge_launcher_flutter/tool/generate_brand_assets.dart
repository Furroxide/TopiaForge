import 'dart:io';

import 'package:image/image.dart' as image;
import 'package:path/path.dart' as p;

const _canonicalWordmarkWidth = 114;
const _canonicalWordmarkHeight = 18;
const _canonicalIconSize = 144;
const _masterSize = 1024;
const _scaledIconSize = 864;
const _scaledIconOffset = 80;
const _snapIconSize = 256;
const _faviconSize = 64;

void main() {
  final appRoot = Directory.current.absolute;
  _requireAppRoot(appRoot);

  final repositoryRoot = Directory(
    p.normalize(p.join(appRoot.path, '..', '..')),
  );
  final sharedBrandRoot = Directory(
    p.join(repositoryRoot.path, 'packages', 'launcher_ui', 'assets', 'brand'),
  );
  final websiteRoot = Directory(p.join(repositoryRoot.path, 'website'));

  final canonicalIconFile = File(
    p.join(sharedBrandRoot.path, 'topiaforge-icon.png'),
  );
  final canonicalWordmarkFile = File(
    p.join(sharedBrandRoot.path, 'topiaforge-wordmark.png'),
  );
  final canonicalIcon = _decodePng(canonicalIconFile);
  final canonicalWordmark = _decodePng(canonicalWordmarkFile);

  _requireDimensions(
    canonicalIconFile,
    canonicalIcon,
    _canonicalIconSize,
    _canonicalIconSize,
  );
  _requireDimensions(
    canonicalWordmarkFile,
    canonicalWordmark,
    _canonicalWordmarkWidth,
    _canonicalWordmarkHeight,
  );
  _requireBinaryAlpha(canonicalIconFile, canonicalIcon);
  _requireBinaryAlpha(canonicalWordmarkFile, canonicalWordmark);

  final master = _buildDesktopMaster(canonicalIcon);
  final masterFile = File(
    p.join(appRoot.path, 'assets', 'brand', 'topiaforge-app-icon.png'),
  );
  _writePng(masterFile, master);

  final snapIcon = image.copyResize(
    master,
    width: _snapIconSize,
    height: _snapIconSize,
    interpolation: image.Interpolation.average,
  );
  final snapIconFile = File(
    p.join(appRoot.path, 'snap', 'gui', 'topiaforge.png'),
  );
  _writePng(snapIconFile, snapIcon);

  final websiteAssetDirectory = Directory(
    p.join(websiteRoot.path, 'src', 'assets'),
  )..createSync(recursive: true);
  canonicalWordmarkFile.copySync(
    p.join(websiteAssetDirectory.path, 'topiaforge-wordmark.png'),
  );

  final favicon = image.copyResize(
    master,
    width: _faviconSize,
    height: _faviconSize,
    interpolation: image.Interpolation.average,
  );
  _writePng(File(p.join(websiteRoot.path, 'public', 'favicon.png')), favicon);

  stdout.writeln('Generated ${p.relative(masterFile.path)}');
  stdout.writeln('Generated ${p.relative(snapIconFile.path)}');
  stdout.writeln('Synchronized website/src/assets/topiaforge-wordmark.png');
  stdout.writeln('Generated website/public/favicon.png');
}

image.Image _buildDesktopMaster(image.Image canonicalIcon) {
  final master = image.Image(
    width: _masterSize,
    height: _masterSize,
    numChannels: 4,
  );

  // These dimensions and colors preserve the shell from the previous
  // 256x256 SVG mark at exactly 4x scale: rx=56, stroke=4.
  image.fillRect(
    master,
    x1: 0,
    y1: 0,
    x2: _masterSize - 1,
    y2: _masterSize - 1,
    radius: 232,
    color: image.ColorUint8.rgba(32, 46, 60, 255),
  );
  image.fillRect(
    master,
    x1: 16,
    y1: 16,
    x2: _masterSize - 17,
    y2: _masterSize - 17,
    radius: 216,
    color: image.ColorUint8.rgba(44, 62, 80, 255),
  );

  final scaledIcon = image.copyResize(
    canonicalIcon,
    width: _scaledIconSize,
    height: _scaledIconSize,
    interpolation: image.Interpolation.nearest,
  );
  image.compositeImage(
    master,
    scaledIcon,
    dstX: _scaledIconOffset,
    dstY: _scaledIconOffset,
  );
  return master;
}

image.Image _decodePng(File file) {
  if (!file.existsSync()) {
    throw StateError('Required brand source is missing: ${file.path}');
  }
  final decoded = image.decodePng(file.readAsBytesSync());
  if (decoded == null) {
    throw StateError('Brand source is not a readable PNG: ${file.path}');
  }
  return decoded;
}

void _requireDimensions(
  File file,
  image.Image decoded,
  int expectedWidth,
  int expectedHeight,
) {
  if (decoded.width != expectedWidth || decoded.height != expectedHeight) {
    throw StateError(
      '${file.path} must be ${expectedWidth}x$expectedHeight, found '
      '${decoded.width}x${decoded.height}.',
    );
  }
}

void _requireBinaryAlpha(File file, image.Image decoded) {
  for (final pixel in decoded) {
    if (pixel.a != 0 && pixel.a != pixel.maxChannelValue) {
      throw StateError('${file.path} must use binary pixel-art alpha.');
    }
  }
}

void _writePng(File file, image.Image value) {
  file.parent.createSync(recursive: true);
  file.writeAsBytesSync(image.encodePng(value));
}

void _requireAppRoot(Directory appRoot) {
  final config = File(p.join(appRoot.path, 'icons_launcher.yaml'));
  final pubspec = File(p.join(appRoot.path, 'pubspec.yaml'));
  if (!config.existsSync() || !pubspec.existsSync()) {
    throw StateError('Run this tool from apps/topiaforge_launcher_flutter.');
  }
}
