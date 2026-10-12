part of 'multiplayer_admission.dart';

void _rejectStandaloneOnly(
  Map<String, MultiplayerAdmissionMod> mods,
  String side,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  for (final entry in mods.entries.where(
    (entry) => entry.value.manifest.isStandaloneOnly,
  )) {
    mismatches.add(
      MultiplayerAdmissionMismatch(
        code: MultiplayerAdmissionMismatchCode.standaloneOnlyMod,
        message:
            'The $side enables a mod that declares itself standalone. Disable it in an explicitly confirmed derived profile to play together.',
        modId: entry.key,
      ),
    );
  }
}

void _rejectWrongSideMods(
  Map<String, MultiplayerAdmissionMod> serverMods,
  Map<String, MultiplayerAdmissionMod> clientMods,
  MultiplayerAdmissionServerRole serverRole,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  for (final entry in serverMods.entries.where(
    (entry) =>
        serverRole == MultiplayerAdmissionServerRole.dedicatedServer &&
        entry.value.manifest.multiplayer?.mode ==
            ModMultiplayerMode.clientLocal,
  )) {
    mismatches.add(
      MultiplayerAdmissionMismatch(
        code: MultiplayerAdmissionMismatchCode.clientLocalModOnServer,
        message: 'A client-local mod is enabled in the logical server profile.',
        modId: entry.key,
        serverValue: ModMultiplayerMode.clientLocal.wireName,
        clientValue: 'absent',
      ),
    );
  }
  for (final entry in clientMods.entries.where(
    (entry) =>
        entry.value.manifest.multiplayer?.mode == ModMultiplayerMode.serverOnly,
  )) {
    mismatches.add(
      MultiplayerAdmissionMismatch(
        code: MultiplayerAdmissionMismatchCode.serverOnlyModOnClient,
        message:
            'A server-only mod is enabled in the interactive client profile.',
        modId: entry.key,
        serverValue: 'absent',
        clientValue: ModMultiplayerMode.serverOnly.wireName,
      ),
    );
  }
}

/// A mod without a multiplayer block is required of every peer as the
/// identical package, whichever peer enables it, so a mismatched or missing
/// copy always blocks admission.
void _compareImplicitShared(
  String id,
  MultiplayerAdmissionMod? server,
  MultiplayerAdmissionMod? client,
  Set<String> active,
  List<MultiplayerAdmissionMismatch> mismatches,
) {
  if (server == null) {
    mismatches.add(
      _missing(
        id,
        'The client enables this implicitly shared mod, so the server must enable the identical package.',
        'absent',
        _modeDisplay(client),
      ),
    );
    return;
  }
  if (client == null) {
    mismatches.add(
      _missing(
        id,
        'The server requires this implicitly shared mod on every client as the identical package.',
        _modeDisplay(server),
        'absent',
      ),
    );
    return;
  }
  if (_admissionSha256Pattern.hasMatch(server.packageSha256) &&
      _admissionSha256Pattern.hasMatch(client.packageSha256) &&
      server.packageSha256.toLowerCase() ==
          client.packageSha256.toLowerCase()) {
    active.add(id);
    return;
  }
  mismatches.add(
    MultiplayerAdmissionMismatch(
      code: MultiplayerAdmissionMismatchCode.implicitSharedContentMismatch,
      message:
          'Implicitly shared mods must be the identical package on every peer.',
      modId: id,
      serverValue: '${server.manifest.version}@${server.packageSha256}',
      clientValue: '${client.manifest.version}@${client.packageSha256}',
    ),
  );
}

MultiplayerAdmissionMismatch _missing(
  String id,
  String message,
  String serverValue,
  String clientValue,
) => MultiplayerAdmissionMismatch(
  code: MultiplayerAdmissionMismatchCode.missingRequiredMod,
  message: message,
  modId: id,
  serverValue: serverValue,
  clientValue: clientValue,
);

String _modeDisplay(MultiplayerAdmissionMod? item) {
  if (item == null) return 'absent';
  if (!item.manifest.multiplayerIsPresent) return 'implicit';
  return item.manifest.multiplayer?.modeName ?? 'invalid';
}
