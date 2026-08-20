import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:json_schema/json_schema.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';
import 'release_archive_metadata.dart';
import 'release_policy.dart';

const launcherUpdatePayloadAssetName = 'topiaforge-update-v1.json';
const launcherUpdateSignatureAssetName = 'topiaforge-update-v1.json.sig';
const launcherUpdatePrivateKeyEnvironment =
    'TOPIAFORGE_UPDATE_ED25519_PRIVATE_KEY_B64';

final class ReleaseUpdateMetadataBuilder {
  const ReleaseUpdateMetadataBuilder();

  Future<void> generateKey({
    required String repositoryRoot,
    required String privateOutput,
  }) async {
    final privateFile = File(p.normalize(p.absolute(privateOutput)));
    if (FileSystemEntity.typeSync(privateFile.path, followLinks: false) !=
        FileSystemEntityType.notFound) {
      throw StateError(
        'Refusing to replace existing update private key: ${privateFile.path}',
      );
    }
    final material = await LauncherUpdateKeyMaterial.generate();
    privateFile.parent.createSync(recursive: true);
    privateFile.createSync(exclusive: true);
    privateFile.writeAsStringSync(
      '${base64Encode(material.privateSeed)}\n',
      flush: true,
    );
    if (!await _restrictPrivateKey(privateFile)) {
      privateFile.deleteSync();
      throw StateError('Could not restrict update private-key permissions.');
    }
    final trustFile = File(
      p.join(repositoryRoot, 'release', 'update-keys.json'),
    );
    if (FileSystemEntity.typeSync(trustFile.path, followLinks: false) !=
        FileSystemEntityType.notFound) {
      privateFile.deleteSync();
      throw StateError(
        'Refusing to replace existing update trust roots: ${trustFile.path}',
      );
    }
    try {
      _writeJsonAtomic(trustFile, {
        r'$schema':
            'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.update-keys.schema.json',
        'formatVersion': 1,
        'keys': [material.publicKey.toJson()],
      });
    } on Object {
      privateFile.deleteSync();
      rethrow;
    }
  }

  Future<({String payload, String signature})> build({
    required String repositoryRoot,
    required String version,
    required String assetsDirectory,
    Map<String, String>? environment,
  }) async {
    final policy = TopiaForgeReleasePolicy.load(repositoryRoot);
    final release = TopiaForgeReleaseCatalog.load(
      repositoryRoot,
    ).release(version);
    final issues = await const ReleasePolicyValidator().validate(
      policy: policy,
      release: release,
      verifyArchiveHashes: false,
    );
    if (issues.isNotEmpty) {
      throw StateError(
        'Release policy validation failed:\n- ${issues.join('\n- ')}',
      );
    }
    final seed = _privateSeed(environment ?? Platform.environment);
    final key = await LauncherUpdateKeyMaterial.fromSeed(seed);
    final trustStoreJson = _readObject(
      File(p.join(repositoryRoot, 'release', 'update-keys.json')),
    );
    final trusted = LauncherUpdateTrustStore.fromJson(trustStoreJson);

    final assets = Directory(assetsDirectory);
    final platforms = <String, Object?>{};
    for (final platform in policy.targetPlatforms) {
      final asset = releaseArchiveForPlatform(platform);
      final layout =
          releasePlatformInstallLayouts[platform] ??
          (throw StateError(
            'No launcher update install layout exists for $platform.',
          ));
      final archive = File(p.join(assets.path, asset));
      final bytes = readBoundedRegularFileSync(
        archive,
        maxBytes: ReleaseZipMetadataPolicy.maxCompressedBytes,
      );
      final inspection = const ReleaseZipMetadataPolicy().inspect(bytes);
      _validateInstallLayout(bytes, platform: platform, installLayout: layout);
      platforms[platform] = {
        'assetName': asset,
        'url':
            'https://github.com/furroxide/TopiaForge/releases/download/'
            '${release.tag}/$asset',
        'sha256': sha256.convert(bytes).toString(),
        'size': bytes.length,
        'entryCount': inspection.entryCount,
        'expandedSize': inspection.expandedSize,
        'installLayout': layout,
      };
    }

    final payloadJson = <String, Object?>{
      r'$schema':
          'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.launcher-update-v1.schema.json',
      'formatVersion': launcherUpdatePayloadFormatVersion,
      'product': 'TopiaForge',
      'version': release.version,
      'tag': release.tag,
      'channel': release.prerelease ? 'beta' : 'release',
      'minimumUpdaterVersion': '1.0.0-rc.1',
      'releaseUrl':
          'https://github.com/furroxide/TopiaForge/releases/tag/${release.tag}',
      'platforms': platforms,
    };
    final payloadBytes = utf8.encode(
      '${const JsonEncoder.withIndent('  ').convert(payloadJson)}\n',
    );
    _validateUpdateSchema(repositoryRoot, payloadJson);
    final signatureBytes = await key.sign(payloadBytes);
    await trusted.verify(
      payloadBytes: payloadBytes,
      signatureBytes: signatureBytes,
    );

    final payloadFile = File(
      p.join(assets.path, launcherUpdatePayloadAssetName),
    );
    final signatureFile = File(
      p.join(assets.path, launcherUpdateSignatureAssetName),
    );
    _writeBytesAtomic(payloadFile, payloadBytes);
    _writeBytesAtomic(signatureFile, signatureBytes);
    await verify(
      repositoryRoot: repositoryRoot,
      version: version,
      assetsDirectory: assetsDirectory,
    );
    return (payload: payloadFile.path, signature: signatureFile.path);
  }

  Future<void> verify({
    required String repositoryRoot,
    required String version,
    required String assetsDirectory,
  }) async {
    final policy = TopiaForgeReleasePolicy.load(repositoryRoot);
    final release = TopiaForgeReleaseCatalog.load(
      repositoryRoot,
    ).release(version);
    final assets = Directory(assetsDirectory);
    final payloadFile = File(
      p.join(assets.path, launcherUpdatePayloadAssetName),
    );
    final signatureFile = File(
      p.join(assets.path, launcherUpdateSignatureAssetName),
    );
    final payloadBytes = readBoundedRegularFileSync(
      payloadFile,
      maxBytes: 1024 * 1024,
    );
    final signatureBytes = readBoundedRegularFileSync(
      signatureFile,
      maxBytes: 16 * 1024,
    );
    final trustStore = LauncherUpdateTrustStore.fromJson(
      _readObject(File(p.join(repositoryRoot, 'release', 'update-keys.json'))),
    );
    final verified = await trustStore.verify(
      payloadBytes: payloadBytes,
      signatureBytes: signatureBytes,
    );
    final candidate = LauncherUpdateCandidate.fromVerifiedJson(
      json: verified.payload,
      signingKeyId: verified.keyId,
      payloadSha256: verified.sha256,
    );
    if (policy.productVersion != release.version ||
        candidate.version != release.version ||
        candidate.tag != release.tag ||
        !_samePlatformSet(
          candidate.platforms.keys.toSet(),
          policy.targetPlatforms.toSet(),
        ) ||
        candidate.releaseUrl !=
            'https://github.com/furroxide/TopiaForge/releases/tag/'
                '${release.tag}' ||
        candidate.channel !=
            (release.prerelease
                ? LauncherUpdateChannel.beta
                : LauncherUpdateChannel.release)) {
      throw StateError(
        'Signed update metadata does not match the release catalog.',
      );
    }
    for (final artifact in candidate.platforms.values) {
      final file = File(p.join(assets.path, artifact.assetName));
      final bytes = readBoundedRegularFileSync(
        file,
        maxBytes: ReleaseZipMetadataPolicy.maxCompressedBytes,
      );
      final inspection = const ReleaseZipMetadataPolicy().inspect(bytes);
      if (bytes.length != artifact.size ||
          sha256.convert(bytes).toString() != artifact.sha256 ||
          inspection.entryCount != artifact.entryCount ||
          inspection.expandedSize != artifact.expandedSize ||
          artifact.url !=
              'https://github.com/furroxide/TopiaForge/releases/download/'
                  '${release.tag}/${artifact.assetName}') {
        throw StateError(
          'Signed update metadata does not match ${artifact.assetName}.',
        );
      }
      _validateInstallLayout(
        bytes,
        platform: artifact.platform,
        installLayout: artifact.installLayout,
      );
    }
  }

  List<int> _privateSeed(Map<String, String> environment) {
    final encoded =
        environment[launcherUpdatePrivateKeyEnvironment]?.trim() ?? '';
    if (encoded.isEmpty) {
      throw StateError(
        '$launcherUpdatePrivateKeyEnvironment is required to sign launcher update metadata.',
      );
    }
    try {
      final seed = base64Decode(encoded);
      if (seed.length != 32 || base64Encode(seed) != encoded) {
        throw const FormatException('not canonical base64');
      }
      return seed;
    } on FormatException catch (error) {
      throw StateError(
        '$launcherUpdatePrivateKeyEnvironment is invalid: $error',
      );
    }
  }
}

bool _samePlatformSet(Set<String> left, Set<String> right) =>
    left.length == right.length && left.containsAll(right);

void _validateInstallLayout(
  List<int> bytes, {
  required String platform,
  required String installLayout,
}) {
  final archive = SafeZipArchive.decode(
    bytes,
    policy: const SafeArchivePolicy(
      maxArchiveBytes: ReleaseZipMetadataPolicy.maxCompressedBytes,
      maxEntries: ReleaseZipMetadataPolicy.maxEntries,
      maxEntryBytes: ReleaseZipMetadataPolicy.maxUncompressedEntryBytes,
      maxExpandedBytes: ReleaseZipMetadataPolicy.maxTotalUncompressedBytes,
    ),
    label: '$platform release archive',
    allowContainedLinks: installLayout == 'app-bundle',
  );
  final names = archive.entries.map((entry) => entry.name).toSet();
  final required = switch (platform) {
    'windows-x64' => const {
      'topiaforge.exe',
      'launcher/topiaforge_launcher.exe',
    },
    'linux-x64' => const {'topiaforge', 'launcher/topiaforge_launcher'},
    'macos-universal' => const {
      'TopiaForge.app/Contents/MacOS/topiaforge_launcher',
      'TopiaForge.app/Contents/Resources/TopiaForge/topiaforge',
      'TopiaForge.app/Contents/Resources/TopiaForge/topiaforge-arm64',
      'TopiaForge.app/Contents/Resources/TopiaForge/topiaforge-x64',
    },
    _ => throw StateError('Unsupported launcher update platform: $platform'),
  };
  if (!names.containsAll(required)) {
    throw StateError(
      '$platform release archive has an invalid install layout.',
    );
  }
  if (installLayout == 'app-bundle' &&
      names.any(
        (name) =>
            name != 'TopiaForge.app' &&
            !name.startsWith('TopiaForge.app/') &&
            name != 'topiaforge',
      )) {
    throw StateError('macOS release archive has an invalid root layout.');
  }
}

Future<bool> _restrictPrivateKey(File file) async {
  if (!Platform.isWindows) {
    final chmod = await Process.run('/bin/chmod', ['600', file.path]);
    return chmod.exitCode == 0;
  }
  final identity = await Process.run('whoami', const []);
  final account = identity.stdout.toString().trim();
  if (identity.exitCode != 0 || account.isEmpty) return false;
  final acl = await Process.run('icacls', [
    file.path,
    '/inheritance:r',
    '/grant:r',
    '$account:(F)',
  ]);
  return acl.exitCode == 0;
}

void _validateUpdateSchema(
  String repositoryRoot,
  Map<String, Object?> payload,
) {
  final schema = JsonSchema.create(
    _readObject(
      File(
        p.join(
          repositoryRoot,
          'schemas',
          'topiaforge.launcher-update-v1.schema.json',
        ),
      ),
    ),
  );
  final result = schema.validate(payload);
  if (!result.isValid) {
    throw StateError(
      'Signed launcher update metadata is schema-invalid:\n'
      '${result.errors.join('\n')}',
    );
  }
}

Map<String, Object?> _readObject(File file) {
  final decoded = jsonDecode(
    utf8.decode(
      readBoundedRegularFileSync(file, maxBytes: 1024 * 1024),
      allowMalformed: false,
    ),
  );
  if (decoded is! Map) {
    throw StateError('${file.path} must contain a JSON object.');
  }
  return Map<String, Object?>.from(decoded);
}

void _writeJsonAtomic(File file, Map<String, Object?> value) {
  _writeBytesAtomic(
    file,
    utf8.encode('${const JsonEncoder.withIndent('  ').convert(value)}\n'),
  );
}

void _writeBytesAtomic(File file, List<int> bytes) {
  final type = FileSystemEntity.typeSync(file.path, followLinks: false);
  if (type == FileSystemEntityType.file) {
    final current = readBoundedRegularFileSync(file, maxBytes: 1024 * 1024);
    if (!_sameBytes(current, bytes)) {
      throw StateError('Refusing to replace existing metadata: ${file.path}');
    }
    return;
  }
  if (type != FileSystemEntityType.notFound) {
    throw StateError('Metadata output path is unsafe: ${file.path}');
  }
  file.parent.createSync(recursive: true);
  final temporary = File(
    '${file.path}.tmp-$pid-${DateTime.now().microsecondsSinceEpoch}',
  );
  try {
    temporary.writeAsBytesSync(bytes, flush: true);
    temporary.renameSync(file.path);
  } finally {
    if (temporary.existsSync()) {
      temporary.deleteSync();
    }
  }
}

bool _sameBytes(List<int> left, List<int> right) {
  if (left.length != right.length) return false;
  for (var index = 0; index < left.length; index++) {
    if (left[index] != right[index]) return false;
  }
  return true;
}
