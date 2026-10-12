import 'package:unorm_dart/unorm_dart.dart' as unicode;

import 'models.dart';
import 'versioning.dart';

part 'multiplayer_admission_comparisons.dart';
part 'multiplayer_admission_modes.dart';
part 'multiplayer_admission_paths.dart';
part 'multiplayer_admission_validation.dart';

/// Mismatch codes in the C# `MultiplayerAdmissionMismatchCode` order. New
/// codes are appended so the ordinal sort stays identical in both languages.
enum MultiplayerAdmissionMismatchCode {
  invalidProfile,
  gameBuildMismatch,
  topiaForgeProtocolMismatch,
  standaloneOnlyMod,
  missingRequiredMod,
  modProtocolMismatch,
  synchronizedContentMismatch,
  serverOnlyModOnClient,
  exactProfileMismatch,
  clientLocalModOnServer,
  optionalSessionModUnavailable,
  implicitSharedContentMismatch,
}

enum MultiplayerAdmissionPolicy { compatible, exactProfile }

/// Describes the process that hosts the logical server side of a session.
enum MultiplayerAdmissionServerRole {
  /// A server without a local player. Client-local mods have no place in its
  /// profile.
  dedicatedServer,

  /// A player-hosted listen server whose process is also an interactive
  /// client, so the host's own client-local and server-only mods are both
  /// legitimate.
  listenHost,
}

class MultiplayerAdmissionMod {
  const MultiplayerAdmissionMod({
    required this.manifest,
    this.packageSha256 = '',
  });

  final ModManifest manifest;
  final String packageSha256;
}

class MultiplayerAdmissionProfile {
  MultiplayerAdmissionProfile({
    required this.peerId,
    required this.gameBuild,
    required this.topiaForgeProtocolVersion,
    String? topiaForgePeerVersionRange,
    required List<MultiplayerAdmissionMod> mods,
  }) : topiaForgePeerVersionRangeIsPresent =
           topiaForgePeerVersionRange?.trim().isNotEmpty == true,
       topiaForgePeerVersionRange = _effectivePeerVersionRange(
         topiaForgeProtocolVersion,
         topiaForgePeerVersionRange,
       ),
       mods = List.unmodifiable(mods) {
    if (peerId.trim().isEmpty ||
        gameBuild.trim().isEmpty ||
        topiaForgeProtocolVersion.trim().isEmpty) {
      throw ArgumentError(
        'Peer, Robotopia, and protocol identities are required.',
      );
    }
  }

  final String peerId;
  final String gameBuild;
  final String topiaForgeProtocolVersion;

  /// Whether a range was advertised instead of exact protocol equality.
  final bool topiaForgePeerVersionRangeIsPresent;
  final String topiaForgePeerVersionRange;
  final List<MultiplayerAdmissionMod> mods;
}

class MultiplayerAdmissionMismatch {
  const MultiplayerAdmissionMismatch({
    required this.code,
    required this.message,
    this.modId = '',
    this.serverValue = '',
    this.clientValue = '',
  });

  final MultiplayerAdmissionMismatchCode code;
  final String message;
  final String modId;
  final String serverValue;
  final String clientValue;
}

class MultiplayerAdmissionReport {
  const MultiplayerAdmissionReport({
    required this.activeSessionMods,
    required this.mismatches,
    required this.inactiveSessionReasons,
  });

  bool get isAdmitted => mismatches.isEmpty;
  final List<String> activeSessionMods;
  final List<MultiplayerAdmissionMismatch> mismatches;

  /// Non-fatal reasons why optional session mods did not activate.
  final List<MultiplayerAdmissionMismatch> inactiveSessionReasons;
}

/// Applies server-canonical V6 admission rules: implicit sharing for mods
/// without a multiplayer block, presence, protocol, and content negotiation
/// for declared session mods, and logical-side rules for the rest.
class MultiplayerAdmissionPlanner {
  const MultiplayerAdmissionPlanner();

  MultiplayerAdmissionReport evaluate({
    required MultiplayerAdmissionProfile server,
    required MultiplayerAdmissionProfile client,
    MultiplayerAdmissionPolicy policy = MultiplayerAdmissionPolicy.compatible,
    MultiplayerAdmissionServerRole serverRole =
        MultiplayerAdmissionServerRole.dedicatedServer,
  }) {
    final mismatches = <MultiplayerAdmissionMismatch>[];
    final inactiveSessionReasons = <MultiplayerAdmissionMismatch>[];
    if (server.gameBuild != client.gameBuild) {
      mismatches.add(
        MultiplayerAdmissionMismatch(
          code: MultiplayerAdmissionMismatchCode.gameBuildMismatch,
          message: 'Robotopia builds must match exactly.',
          serverValue: server.gameBuild,
          clientValue: client.gameBuild,
        ),
      );
    }

    final serverProtocolIsValid = _validateProfileProtocol(
      server,
      'server',
      mismatches,
    );
    final clientProtocolIsValid = _validateProfileProtocol(
      client,
      'client',
      mismatches,
    );
    if (serverProtocolIsValid &&
        clientProtocolIsValid &&
        !_mutuallyCompatible(
          server.topiaForgeProtocolVersion,
          server.topiaForgePeerVersionRange,
          server.topiaForgePeerVersionRangeIsPresent,
          client.topiaForgeProtocolVersion,
          client.topiaForgePeerVersionRange,
          client.topiaForgePeerVersionRangeIsPresent,
        )) {
      mismatches.add(
        MultiplayerAdmissionMismatch(
          code: MultiplayerAdmissionMismatchCode.topiaForgeProtocolMismatch,
          message:
              'The TopiaForge multiplayer protocol ranges are not mutually compatible.',
          serverValue: _protocolDisplay(
            server.topiaForgeProtocolVersion,
            server.topiaForgePeerVersionRange,
            server.topiaForgePeerVersionRangeIsPresent,
          ),
          clientValue: _protocolDisplay(
            client.topiaForgeProtocolVersion,
            client.topiaForgePeerVersionRange,
            client.topiaForgePeerVersionRangeIsPresent,
          ),
        ),
      );
    }

    final serverMods = _indexAdmissionProfile(server, 'server', mismatches);
    final clientMods = _indexAdmissionProfile(client, 'client', mismatches);
    _rejectStandaloneOnly(serverMods, 'server', mismatches);
    _rejectStandaloneOnly(clientMods, 'client', mismatches);
    _rejectWrongSideMods(serverMods, clientMods, serverRole, mismatches);

    final active = <String>{};
    final ids = <String>{...serverMods.keys, ...clientMods.keys}.toList()
      ..sort();
    for (final id in ids) {
      final serverMod = serverMods[id];
      final clientMod = clientMods[id];
      if (serverMod?.manifest.isImplicitlyShared == true ||
          clientMod?.manifest.isImplicitlyShared == true) {
        _compareImplicitShared(id, serverMod, clientMod, active, mismatches);
        continue;
      }

      final serverMetadata = serverMod?.manifest.multiplayer;
      final clientMetadata = clientMod?.manifest.multiplayer;
      final serverSession = serverMetadata?.mode == ModMultiplayerMode.session;
      final clientSession = clientMetadata?.mode == ModMultiplayerMode.session;
      final requiredByServer =
          serverSession &&
          serverMetadata?.presence == ModMultiplayerPresence.required;
      final requiredByClient =
          clientSession &&
          clientMetadata?.presence == ModMultiplayerPresence.required;
      var missingRequiredCopy = false;

      if (requiredByServer && !clientSession) {
        mismatches.add(
          _missing(
            id,
            'The server requires a session-compatible copy of this mod on every client.',
            'session/required',
            _modeDisplay(clientMod),
          ),
        );
        missingRequiredCopy = true;
      }
      if (requiredByClient && !serverSession) {
        mismatches.add(
          _missing(
            id,
            'The client requires a session-compatible copy of this mod on the server.',
            _modeDisplay(serverMod),
            'session/required',
          ),
        );
        missingRequiredCopy = true;
      }
      if (missingRequiredCopy) continue;

      if (!serverSession || !clientSession) {
        if (policy == MultiplayerAdmissionPolicy.exactProfile &&
            (serverSession || clientSession)) {
          mismatches.add(
            MultiplayerAdmissionMismatch(
              code: MultiplayerAdmissionMismatchCode.exactProfileMismatch,
              message:
                  'The exact-profile policy requires the same session-mod inventory on both peers.',
              modId: id,
              serverValue: _modeDisplay(serverMod),
              clientValue: _modeDisplay(clientMod),
            ),
          );
        } else if (serverSession || clientSession) {
          inactiveSessionReasons.add(
            MultiplayerAdmissionMismatch(
              code: MultiplayerAdmissionMismatchCode
                  .optionalSessionModUnavailable,
              message:
                  'The optional session mod is present on only one peer and remains inactive.',
              modId: id,
              serverValue: _modeDisplay(serverMod),
              clientValue: _modeDisplay(clientMod),
            ),
          );
        }
        continue;
      }

      final pairMismatches = <MultiplayerAdmissionMismatch>[];
      _compareProtocol(
        id,
        serverMetadata!.protocol!,
        clientMetadata!.protocol!,
        pairMismatches,
      );
      _compareContent(
        id,
        serverMod!.manifest,
        clientMod!.manifest,
        pairMismatches,
      );
      if (policy == MultiplayerAdmissionPolicy.exactProfile) {
        _compareExactProfile(id, serverMod, clientMod, pairMismatches);
      }

      if (pairMismatches.isEmpty) {
        active.add(id);
      } else if (requiredByServer ||
          requiredByClient ||
          policy == MultiplayerAdmissionPolicy.exactProfile) {
        mismatches.addAll(pairMismatches);
      } else {
        inactiveSessionReasons.addAll(pairMismatches);
      }
    }

    mismatches.sort(_compareMismatches);
    inactiveSessionReasons.sort(_compareMismatches);
    final activeList = active.toList()..sort();
    return MultiplayerAdmissionReport(
      activeSessionMods: List.unmodifiable(activeList),
      mismatches: List.unmodifiable(mismatches),
      inactiveSessionReasons: List.unmodifiable(inactiveSessionReasons),
    );
  }
}

String _effectivePeerVersionRange(String version, String? range) =>
    range?.trim().isNotEmpty == true ? range!.trim() : version;
