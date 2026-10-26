import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart' as crypto;
import 'package:launcher_domain/launcher_domain.dart';

/// Authoring uses the public page's source-project scheme. Runtime resolution is
/// the game's own resolver; the two are checked together during game verification.
class MapProjectReader {
  MapProjectReader({HttpClient Function()? createClient})
    : _createClient = createClient ?? HttpClient.new;
  final HttpClient Function() _createClient;

  Future<MapProjectSurvey> read(MapReference reference) async {
    final client = _createClient()..autoUncompress = false;
    try {
      return await _download(
        client,
        reference,
      ).timeout(const Duration(seconds: 30));
    } on TimeoutException {
      throw StateError(
        'Downloading the map stopped. Check the connection and try again.',
      );
    } on SocketException {
      throw StateError(
        'The public map service could not be reached. Connect to the internet or use --offline with --scene and --name.',
      );
    } on HttpException {
      throw StateError(
        'The map download was incomplete. Check the connection and try again.',
      );
    } on TlsException {
      throw StateError(
        'The public map service could not be reached securely. Try again later.',
      );
    } finally {
      client.close(force: true);
    }
  }

  Future<MapProjectSurvey> _download(
    HttpClient client,
    MapReference reference,
  ) async {
    final uri = Uri.https(
      'ugc.tomatocake.dev',
      '/${reference.id}/project.roboworld',
    );
    final request = await client.getUrl(uri);
    request.followRedirects = false;
    final response = await request.close();
    if (response.statusCode == 404 || response.statusCode == 410) {
      throw StateError(
        'The map is no longer published. Ask its creator for an update.',
      );
    }
    if (response.statusCode != 200) {
      throw StateError(
        'The map service refused or redirected the download. Try again later.',
      );
    }
    final length = response.contentLength;
    if (length > MapWorldDocument.maximumPackedBytes || length == 0) {
      throw const FormatException(
        'The map is empty or exceeds the 8 MiB download limit.',
      );
    }
    final bytes = BytesBuilder(copy: false);
    await for (final chunk in response) {
      if (bytes.length + chunk.length > MapWorldDocument.maximumPackedBytes) {
        throw const FormatException(
          'The map exceeds the 8 MiB download limit.',
        );
      }
      bytes.add(chunk);
    }
    if (length >= 0 && bytes.length != length) {
      throw StateError(
        'The map download was incomplete. Check the connection and try again.',
      );
    }
    return MapProjectSurvey.read(bytes.takeBytes());
  }
}

class MapProjectSurvey {
  MapProjectSurvey._(this.document, this.sha256, this.packedSize);
  final MapWorldDocument document;
  final String sha256;
  final int packedSize;
  factory MapProjectSurvey.read(List<int> bytes) {
    if (bytes.isEmpty || bytes.length > MapWorldDocument.maximumPackedBytes) {
      throw const FormatException(
        'The map is empty or exceeds the 8 MiB download limit.',
      );
    }
    List<int> json = bytes;
    if (bytes.length > 1 && bytes[0] == 0x1f && bytes[1] == 0x8b) {
      final sink = _MapBytes();
      final decoder = gzip.decoder.startChunkedConversion(sink);
      try {
        for (var offset = 0; offset < bytes.length; offset += 16384) {
          decoder.add(
            bytes.sublist(offset, (offset + 16384).clamp(0, bytes.length)),
          );
        }
        decoder.close();
        json = sink.bytes.takeBytes();
      } on FormatException {
        throw const FormatException(
          'The map compression is invalid or exceeds the 32 MiB expanded limit.',
        );
      }
    }
    return MapProjectSurvey._(
      MapWorldDocument.read(json),
      crypto.sha256.convert(bytes).toString(),
      bytes.length,
    );
  }
  Map<String, Object?> snapshot(MapReference reference, MapWorldScene scene) =>
      {
        'schemaVersion': 1,
        'map': reference.id,
        'scene': scene.id,
        'sha256': sha256,
        'packedSize': packedSize,
        'modified': document.modified,
        'name': document.name,
        'entities': scene.entities,
      };
}

class _MapBytes implements Sink<List<int>> {
  final bytes = BytesBuilder(copy: false);
  @override
  void add(List<int> value) {
    if (bytes.length + value.length > MapWorldDocument.maximumJsonBytes) {
      throw const FormatException('Map expanded limit exceeded.');
    }
    bytes.add(value);
  }

  @override
  void close() {}
}
