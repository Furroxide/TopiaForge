part of 'multiplayer_admission.dart';

void _compareProtocol(
  String id,
  ModMultiplayerProtocol server,
  ModMultiplayerProtocol client,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  final serverHasRange = _hasDeclaredPeerRange(server);
  final clientHasRange = _hasDeclaredPeerRange(client);
  final serverRange = serverHasRange
      ? server.peerVersionRange!
      : server.version;
  final clientRange = clientHasRange
      ? client.peerVersionRange!
      : client.version;
  if (_mutuallyCompatible(
    server.version,
    serverRange,
    serverHasRange,
    client.version,
    clientRange,
    clientHasRange,
  )) {
    return;
  }
  mismatches.add(
    MultiplayerAdmissionMismatch(
      code: MultiplayerAdmissionMismatchCode.modProtocolMismatch,
      message: 'The mod protocol ranges are not mutually compatible.',
      modId: id,
      serverValue: _protocolDisplay(
        server.version,
        serverRange,
        serverHasRange,
      ),
      clientValue: _protocolDisplay(
        client.version,
        clientRange,
        clientHasRange,
      ),
    ),
  );
}

void _compareContent(
  String id,
  ModManifest server,
  ModManifest client,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  final serverPaths = [...server.multiplayer!.synchronizedFiles]..sort();
  final clientPaths = [...client.multiplayer!.synchronizedFiles]..sort();
  if (!_equalStrings(serverPaths, clientPaths)) {
    mismatches.add(
      MultiplayerAdmissionMismatch(
        code: MultiplayerAdmissionMismatchCode.synchronizedContentMismatch,
        message: 'The synchronized-file inventories differ.',
        modId: id,
        serverValue: serverPaths.join(','),
        clientValue: clientPaths.join(','),
      ),
    );
    return;
  }
  for (final path in serverPaths) {
    final serverHash = server.hashes[path] ?? '';
    final clientHash = client.hashes[path] ?? '';
    if (serverHash.toLowerCase() != clientHash.toLowerCase()) {
      mismatches.add(
        MultiplayerAdmissionMismatch(
          code: MultiplayerAdmissionMismatchCode.synchronizedContentMismatch,
          message: "Synchronized content differs at '$path'.",
          modId: id,
          serverValue: serverHash,
          clientValue: clientHash,
        ),
      );
    }
  }
}

void _compareExactProfile(
  String id,
  MultiplayerAdmissionMod server,
  MultiplayerAdmissionMod client,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  if (server.manifest.version == client.manifest.version &&
      _admissionSha256Pattern.hasMatch(server.packageSha256) &&
      _admissionSha256Pattern.hasMatch(client.packageSha256) &&
      server.packageSha256.toLowerCase() ==
          client.packageSha256.toLowerCase()) {
    return;
  }
  mismatches.add(
    MultiplayerAdmissionMismatch(
      code: MultiplayerAdmissionMismatchCode.exactProfileMismatch,
      message:
          'The exact-profile policy requires equal package versions and archive hashes.',
      modId: id,
      serverValue: '${server.manifest.version}@${server.packageSha256}',
      clientValue: '${client.manifest.version}@${client.packageSha256}',
    ),
  );
}

bool _mutuallyCompatible(
  String leftVersion,
  String leftRange,
  bool leftHasRange,
  String rightVersion,
  String rightRange,
  bool rightHasRange,
) =>
    _accepts(leftVersion, leftRange, leftHasRange, rightVersion) &&
    _accepts(rightVersion, rightRange, rightHasRange, leftVersion);

bool _accepts(
  String localVersion,
  String localRange,
  bool hasRange,
  String remoteVersion,
) {
  if (!hasRange) return localVersion == remoteVersion;
  try {
    return VersionRange.parse(localRange).allows(remoteVersion);
  } on FormatException {
    return false;
  }
}

bool _hasDeclaredPeerRange(ModMultiplayerProtocol protocol) =>
    protocol.peerVersionRangeIsPresent || protocol.peerVersionRange != null;

void _addInvalid(
  String side,
  String id,
  String message,
  String value,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  mismatches.add(
    MultiplayerAdmissionMismatch(
      code: MultiplayerAdmissionMismatchCode.invalidProfile,
      message: message,
      modId: id,
      serverValue: side == 'server' ? value : '',
      clientValue: side == 'client' ? value : '',
    ),
  );
}

int _compareMismatches(
  MultiplayerAdmissionMismatch left,
  MultiplayerAdmissionMismatch right,
) {
  var comparison = left.code.index.compareTo(right.code.index);
  if (comparison != 0) return comparison;
  comparison = left.modId.compareTo(right.modId);
  if (comparison != 0) return comparison;
  comparison = left.message.compareTo(right.message);
  if (comparison != 0) return comparison;
  comparison = left.serverValue.compareTo(right.serverValue);
  return comparison != 0
      ? comparison
      : left.clientValue.compareTo(right.clientValue);
}

String _protocolDisplay(String version, String range, bool hasRange) =>
    '$version accepts ${hasRange ? range : 'exactly $version'}';

bool _equalStrings(List<String> left, List<String> right) {
  if (left.length != right.length) return false;
  for (var index = 0; index < left.length; index++) {
    if (left[index] != right[index]) return false;
  }
  return true;
}
