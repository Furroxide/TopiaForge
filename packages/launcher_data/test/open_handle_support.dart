/// What a test still holds open in its temporary folder, read from
/// `/proc/self/fd`, and the work it handed a launcher repository: the check
/// that closing the repository left nothing running and nothing open before
/// the folder is deleted.
///
/// Windows will not delete a folder while this process holds a file in it
/// open ("being used by another process", errno 32). That is how a launch
/// still preparing past a test's end showed there. Linux deletes the folder
/// anyway, so there the check lists the process's open handles instead.
library;

import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// The paths in [folder] that this process holds open, from
/// `/proc/self/fd`, or null where that cannot be read: anywhere but Linux.
/// Empty once [folder] is gone.
List<String>? openHandlesUnder(Directory folder) {
  final handles = Directory('/proc/self/fd');
  if (!Platform.isLinux || !handles.existsSync()) return null;
  if (!folder.existsSync()) return const [];
  final root = folder.resolveSymbolicLinksSync();
  final open = <String>[];
  for (final handle in handles.listSync(followLinks: false)) {
    try {
      final target = Link(handle.path).targetSync();
      if (p.equals(target, root) || p.isWithin(root, target)) {
        open.add(p.relative(target, from: root));
      }
    } on FileSystemException {
      // Closed while the list was read.
    }
  }
  return open..sort();
}

/// Fails while this process holds anything in [folder] open. Where that
/// cannot be listed it checks nothing: on Windows, deleting the folder at
/// the end of the test is the check.
void expectNothingOpenUnder(Directory folder) {
  final open = openHandlesUnder(folder);
  if (open == null) return;
  expect(
    open,
    isEmpty,
    reason:
        'Nothing may stay open in a test folder once its launcher '
        'repository is closed: Windows cannot delete the folder then.',
  );
}

/// The operations a test hands a launcher repository, each counted until it
/// completes.
final class RepositoryWork {
  var _running = 0;

  /// [operation], counted until it completes.
  Future<T> track<T>(Future<T> operation) {
    _running++;
    return operation.whenComplete(() => _running--);
  }

  /// Whether every tracked operation has completed.
  bool get finished => _running == 0;

  /// Checks at the end of the test that every tracked operation finished and
  /// that nothing in [folder] is open. Call it after registering the folder's
  /// deletion and before registering the repository's close: tear-downs run
  /// in reverse, so the check runs once the repository is closed and before
  /// the folder is deleted.
  void checkAtEnd(Directory folder) => addTearDown(() {
    expect(
      finished,
      isTrue,
      reason:
          'Every operation the test started has finished once its '
          'repository is closed.',
    );
    expectNothingOpenUnder(folder);
  });
}
