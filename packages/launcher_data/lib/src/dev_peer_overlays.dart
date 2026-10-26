import 'dart:io';

import 'package:path/path.dart' as p;

/// One private game and launcher root. Native game content is shared read-only
/// by convention; runtime state and package installations stay in this root.
final class DevPeerOverlay {
  const DevPeerOverlay(this.index, this.gameRoot, this.launcherRoot);
  final int index;
  final String gameRoot;
  final String launcherRoot;
  String get playerLog => p.join(gameRoot, 'player.log');
}

/// Creates only owned children of dev-instances and removes directory links
/// before deleting any real content. Never follows a link during cleanup.
final class DevPeerOverlays {
  DevPeerOverlays(String dataRoot)
    : root = Directory(p.join(dataRoot, 'dev-instances'));

  final Directory root;
  final Set<String> _owned = {};
  String? _identity;

  Future<List<DevPeerOverlay>> create(String sourceGame, int count) async {
    if (count < 2 || count > 8) {
      throw ArgumentError('Use from 2 through 8 development peers.');
    }
    final source = Directory(sourceGame).resolveSymbolicLinksSync();
    root.createSync(recursive: true);
    final identity = root.resolveSymbolicLinksSync();
    if (_identity != null && !p.equals(identity, _identity!)) {
      throw StateError('The development instance root changed.');
    }
    _identity = identity;
    final run = Directory(identity).createTempSync('run-');
    _owned.add(run.path);
    final peers = <DevPeerOverlay>[];
    try {
      for (var index = 0; index < count; index++) {
        final peer = Directory(p.join(run.path, 'peer-$index'))..createSync();
        final game = Directory(
          p.join(peer.path, Platform.isMacOS ? 'Robotopia.app' : 'game'),
        );
        if (Platform.isMacOS) {
          // APFS clones are private copy-on-write files; never fall back to a
          // full copy of the game's data when the filesystem cannot clone.
          final clone = await Process.run('/bin/cp', [
            '-cR',
            source,
            game.path,
          ]);
          if (clone.exitCode != 0) {
            throw StateError(
              'The game must be on an APFS volume for peer clones.',
            );
          }
          _removeNativeRuntime(game);
        } else {
          game.createSync();
          for (final name in ['Robotopia.exe', 'UnityPlayer.dll']) {
            final file = File(p.join(source, name));
            if (!file.existsSync()) {
              throw StateError(
                'The game executable or Unity player is missing. Repair the installation.',
              );
            }
            file.copySync(p.join(game.path, name));
          }
          for (final name in [
            'UnityCrashHandler64.exe',
            'crashpad_handler.exe',
            'crashpad_wer.dll',
          ]) {
            final file = File(p.join(source, name));
            if (file.existsSync()) file.copySync(p.join(game.path, name));
          }
          final d3d = File(p.join(source, 'D3D12', 'D3D12Core.dll'));
          if (d3d.existsSync()) {
            Directory(p.join(game.path, 'D3D12')).createSync();
            d3d.copySync(p.join(game.path, 'D3D12', 'D3D12Core.dll'));
          }
          for (final name in ['Robotopia_Data', 'MonoBleedingEdge']) {
            final target = Directory(p.join(source, name));
            if (!target.existsSync()) {
              throw StateError(
                'The game data or Mono runtime is missing. Repair the installation.',
              );
            }
            await createDevDirectoryLink(p.join(game.path, name), target.path);
          }
        }
        final launcher = Directory(p.join(peer.path, 'launcher'))..createSync();
        peers.add(DevPeerOverlay(index, game.path, launcher.path));
      }
      return List.unmodifiable(peers);
    } on Object {
      cleanup(run.path);
      rethrow;
    }
  }

  String runRoot(DevPeerOverlay peer) => p.dirname(p.dirname(peer.gameRoot));

  void cleanup(String runRoot) {
    final target = p.normalize(p.absolute(runRoot));
    final identity = _identity;
    if (identity == null ||
        !_owned.contains(target) ||
        !p.equals(p.dirname(target), identity) ||
        !p.equals(root.resolveSymbolicLinksSync(), identity) ||
        FileSystemEntity.typeSync(target, followLinks: false) !=
            FileSystemEntityType.directory ||
        !p.equals(Directory(target).resolveSymbolicLinksSync(), target)) {
      throw StateError(
        'Cleanup requires an unchanged, owned child of dev-instances.',
      );
    }
    final directory = Directory(target);
    _unlinkTree(directory);
    // Enumerate again without following links. A newly inserted link is still
    // removed as a link, never handed to a recursive directory delete.
    _deleteTree(directory);
    _owned.remove(target);
  }

  static void _unlinkTree(Directory directory) {
    for (final entry in directory.listSync(followLinks: false)) {
      final type = FileSystemEntity.typeSync(entry.path, followLinks: false);
      if (type == FileSystemEntityType.link) {
        Link(entry.path).deleteSync();
      } else if (type == FileSystemEntityType.directory) {
        _unlinkTree(Directory(entry.path));
      }
    }
  }

  static void _deleteTree(Directory directory) {
    for (final entry in directory.listSync(followLinks: false)) {
      final type = FileSystemEntity.typeSync(entry.path, followLinks: false);
      if (type == FileSystemEntityType.link) {
        Link(entry.path).deleteSync();
      } else if (type == FileSystemEntityType.directory) {
        _deleteTree(Directory(entry.path));
      } else {
        File(entry.path).deleteSync();
      }
    }
    directory.deleteSync();
  }

  static void _removeNativeRuntime(Directory game) {
    for (final relative in ['BepInEx', 'Contents/BepInEx']) {
      final path = p.join(game.path, relative);
      final type = FileSystemEntity.typeSync(path, followLinks: false);
      if (type == FileSystemEntityType.link) {
        Link(path).deleteSync();
      } else if (type == FileSystemEntityType.directory) {
        _unlinkTree(Directory(path));
        _deleteTree(Directory(path));
      }
    }
  }
}

/// Windows directory junctions need no administrator privileges. Other hosts
/// use ordinary directory symlinks. Arguments are passed through environment
/// values, never interpolated into a shell command.
Future<void> createDevDirectoryLink(String link, String target) async {
  if (!Platform.isWindows) {
    Link(link).createSync(target);
    return;
  }
  final result = await Process.run(
    'powershell.exe',
    [
      '-NoProfile',
      '-NonInteractive',
      '-WindowStyle',
      'Hidden',
      '-Command',
      r'New-Item -ItemType Junction -Path $env:TOPIAFORGE_DEV_LINK -Target $env:TOPIAFORGE_DEV_TARGET -ErrorAction Stop | Out-Null',
    ],
    environment: {'TOPIAFORGE_DEV_LINK': link, 'TOPIAFORGE_DEV_TARGET': target},
  );
  if (result.exitCode != 0 ||
      FileSystemEntity.typeSync(link, followLinks: false) !=
          FileSystemEntityType.link) {
    throw StateError(
      'A development data link could not be created. Check directory permissions.',
    );
  }
}
