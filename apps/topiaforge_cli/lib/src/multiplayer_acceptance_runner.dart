import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

import 'live_acceptance_log_reader.dart';
import 'bounded_file_reader.dart';
import 'live_acceptance_models.dart';
import 'live_acceptance_session.dart';
import 'live_acceptance_trust.dart';
import 'multiplayer_acceptance_peer.dart';
import 'multiplayer_acceptance_spec.dart';

typedef MultiplayerPeerLauncher =
    Future<LiveAcceptanceSession> Function(
      String repositoryRoot,
      AcceptanceIsolationContext context,
      String challenge,
      String peer,
    );

final class MultiplayerAcceptanceOptions {
  const MultiplayerAcceptanceOptions({
    required this.repositoryRoot,
    required this.sourceGameRoot,
    required this.hostRecord,
    required this.guestRecord,
    required this.outputRoot,
    required this.packagePath,
    this.timeout = const Duration(minutes: 10),
    this.caseIds = const [],
  });
  final String repositoryRoot,
      sourceGameRoot,
      hostRecord,
      guestRecord,
      outputRoot,
      packagePath;
  final Duration timeout;
  final List<String> caseIds;
}

/// Two private runtime/launcher roots under an admitted QA identity. Native
/// persistence remains the OS-isolated QA user's, never an environment override.
final class MultiplayerAcceptanceRunner {
  MultiplayerAcceptanceRunner({
    required this.command,
    MultiplayerPeerLauncher? launchPeer,
    DateTime Function()? clock,
    Future<void> Function(Duration)? delay,
    this.admitPeer,
  }) : launchPeer = launchPeer ?? launchMultiplayerAcceptancePeer,
       clock = clock ?? DateTime.now,
       delay = delay ?? Future<void>.delayed;

  final Future<int> Function(List<String>, AcceptanceIsolationContext) command;
  final MultiplayerPeerLauncher launchPeer;
  final DateTime Function() clock;
  final Future<void> Function(Duration) delay;
  final AcceptanceIsolationContext Function(
    String peer,
    MultiplayerAcceptanceOptions options,
  )?
  admitPeer;

  Future<Map<String, Object?>> run(
    MultiplayerAcceptanceOptions options,
    MultiplayerAcceptanceSpec spec,
  ) async {
    final selected = spec.select(options.caseIds);
    if (options.timeout.inSeconds < 30 || options.timeout.inSeconds > 3600) {
      throw StateError(
        'TFMPACCEPT100: Use a timeout from 30 through 3600 seconds.',
      );
    }
    // Check both admissions and the probe before any write or process starts.
    final host =
        admitPeer?.call('host', options) ?? _admitPeer('host', options);
    final guest =
        admitPeer?.call('guest', options) ?? _admitPeer('guest', options);
    requireSeparateMultiplayerRoots([
      host.gameRoot,
      host.launcherRoot,
      host.outputRoot,
      guest.gameRoot,
      guest.launcherRoot,
      guest.outputRoot,
    ]);
    final package = File(options.packagePath);
    if (!package.existsSync()) {
      throw StateError(
        'TFMPACCEPT110: The multiplayer acceptance probe package is missing. Build tests/TopiaForge.MultiplayerAcceptanceMod and supply --package. No game was started.',
      );
    }
    _requireProbe(package);
    final expected = readLiveAcceptancePackageReceipt(package.path);
    final contexts = {'host': host, 'guest': guest};
    for (final context in contexts.values) {
      context.verify();
      for (final args in [
        ['check', 'package', package.path],
        ['dev-install', '--game-dir', context.gameRoot],
        ['install', package.path, '--game-dir', context.gameRoot],
      ]) {
        if (await command(args, context) != 0) {
          throw StateError(
            'TFMPACCEPT110: A peer could not be prepared. Repair the QA runtime and probe package before retrying.',
          );
        }
      }
    }
    final random = Random.secure();
    final challenge = List.generate(
      32,
      (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
    ).join();
    final observations = MultiplayerAcceptanceObservations(challenge, selected);
    final readers = <String, AcceptanceIncrementalLogReader>{};
    final sessions = <String, LiveAcceptanceSession>{};
    final results = <String, LiveAcceptanceLastRun>{};
    final started = clock().toUtc();
    final closes = <String, bool>{};
    try {
      for (final peer in contexts.keys) {
        final context = contexts[peer]!;
        context.verify();
        final config = File(
          p.join(
            context.managerRoot,
            'config',
            '${MultiplayerAcceptanceSpec.packageId}.json',
          ),
        );
        requireAcceptanceUnlinkedPath(config.path);
        config.parent.createSync(recursive: true);
        config.writeAsStringSync(
          jsonEncode({
            'schemaVersion': 1,
            'value': {
              'challenge': challenge,
              'peer': peer,
              'topology': 'listen-pair',
              'controlRoot': host.outputRoot,
              'requiredCases': selected.map((c) => c.id).toList(),
            },
          }),
          flush: true,
        );
        readers[peer] = AcceptanceIncrementalLogReader(
          File(p.join(context.managerRoot, 'logs', 'manager.log')),
        );
      }
      for (final peer in contexts.keys) {
        final session = await launchPeer(
          options.repositoryRoot,
          contexts[peer]!,
          challenge,
          peer,
        );
        sessions[peer] = session;
        await session.confirm(options.timeout);
      }
      final deadline = clock().toUtc().add(options.timeout);
      while (clock().toUtc().isBefore(deadline)) {
        for (final peer in contexts.keys) {
          contexts[peer]!.verify();
          for (final line in await readers[peer]!.readNewLines()) {
            final parsed = tryParseLiveAcceptanceManagerLine(line);
            if (parsed != null) {
              observations.observe(
                peer: peer,
                source: parsed.source,
                level: parsed.level,
                message: parsed.message,
              );
            }
          }
          final file = File(
            p.join(contexts[peer]!.managerRoot, 'logs', 'last-run.json'),
          );
          if (file.existsSync() && file.lengthSync() <= 16 * 1024 * 1024) {
            final result = LiveAcceptanceLastRun.tryParse(
              readBoundedTextFileSync(file, maxBytes: 16 * 1024 * 1024),
            );
            if (result != null && !result.completedAtUtc.isBefore(started)) {
              results[peer] = result;
            }
          }
        }
        if (observations.failures.isNotEmpty ||
            (observations.complete && results.length == 2)) {
          break;
        }
        await delay(const Duration(milliseconds: 500));
      }
    } finally {
      // Try both even if the first cleanup fails. Never stop by a found PID.
      for (final peer in sessions.keys.toList().reversed) {
        try {
          await sessions[peer]!.close();
          closes[peer] = true;
        } on Object {
          closes[peer] = false;
        }
      }
      if (closes.values.any((closed) => !closed)) {
        throw StateError(
          'TFMPACCEPT140: Owned process exit is unconfirmed. Preserve both QA layouts and inspect the processes before retrying.',
        );
      }
    }
    final receiptsMatch = contexts.keys.every((peer) {
      final result = results[peer];
      final loaded = result?.package(MultiplayerAcceptanceSpec.packageId);
      return result?.rootError == '' &&
          loaded?.valid == true &&
          loaded?.status == 'loaded' &&
          loaded?.matchesReceipt(expected) == true;
    });
    final isolation = {
      for (final peer in sessions.keys) peer: sessions[peer]!.isolationEvidence,
    };
    final evidence = <String, Object?>{
      'schemaVersion': 1,
      'suite': 'multiplayer',
      'topology': 'listen-pair',
      'dryRun': false,
      'challenge': challenge,
      'packageReceipt': expected.toJson(),
      'cases': selected.map((c) => c.toJson()).toList(),
      'missing': observations.missing,
      'failures': observations.failures,
      'receiptsMatch': receiptsMatch,
      'isolation': isolation,
      'processExitConfirmed':
          closes.length == 2 && closes.values.every((v) => v),
      'succeeded': observations.complete && receiptsMatch && closes.length == 2,
    };
    host.verify();
    final output = File(
      p.join(host.outputRoot, 'multiplayer-acceptance-result.json'),
    );
    requireAcceptanceUnlinkedPath(output.path);
    output.parent.createSync(recursive: true);
    output.writeAsStringSync(jsonEncode(evidence), flush: true);
    if (evidence['succeeded'] != true) {
      throw StateError(
        'TFMPACCEPT130: Multiplayer acceptance is incomplete. Review missing observations and receipts in the private result, then rerun.',
      );
    }
    return evidence;
  }
}

void requireSeparateMultiplayerRoots(List<String> roots) {
  if (roots.length != 6) {
    throw StateError('Two complete peer layouts are required.');
  }
  for (var i = 0; i < roots.length; i++) {
    for (var j = i + 1; j < roots.length; j++) {
      if (acceptancePathsOverlap(roots[i], roots[j])) {
        throw StateError(
          'TFMPACCEPT100: Peer writable roots overlap. Provision separate game, launcher and evidence roots for both peers.',
        );
      }
    }
  }
}

AcceptanceIsolationContext _admitPeer(
  String peer,
  MultiplayerAcceptanceOptions options,
) {
  final record = peer == 'host' ? options.hostRecord : options.guestRecord;
  final output = peer == 'host'
      ? options.outputRoot
      : acceptancePath(
          decodeAcceptanceObject(readAcceptanceRecord(File(record))),
          'outputRoot',
        );
  return AcceptanceIsolationContext.admit(
    recordPath: record,
    sourceGameRoot: options.sourceGameRoot,
    outputRoot: output,
  );
}

void _requireProbe(File file) {
  requireAcceptanceUnlinkedPath(file.absolute.path);
  if (file.lengthSync() > 512 * 1024 * 1024) {
    throw StateError('Probe package is too large.');
  }
  final archive = SafeZipArchive.decode(
    readBoundedRegularFileSync(file, maxBytes: CliFileLimits.package),
    label: 'Multiplayer acceptance probe',
  );
  final entry = archive.entryNamed('topiaforge.mod.json');
  final manifest = entry == null
      ? null
      : jsonDecode(
          utf8.decode(
            entry.readBytes(
              maxBytes: 2 * 1024 * 1024,
              label: 'Multiplayer acceptance manifest',
            ),
          ),
        );
  if (manifest is! Map ||
      manifest['name'] != MultiplayerAcceptanceSpec.packageId) {
    throw StateError(
      'TFMPACCEPT110: This is not the multiplayer acceptance probe. Supply the reviewed probe package.',
    );
  }
}
