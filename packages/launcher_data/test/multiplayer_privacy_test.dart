import 'dart:io';
import 'dart:mirrors';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/descriptor_client.dart';
import 'package:launcher_data/src/multiplayer/descriptor_response.dart';
import 'package:launcher_data/src/multiplayer/join_descriptor_resolver.dart';
import 'package:launcher_data/src/multiplayer/lan_datagrams.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_data/src/multiplayer/lan_sockets.dart';
import 'package:launcher_data/src/multiplayer/multiplayer_store_salvage.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// One declaration from every library in `lib/src/multiplayer/`, so each is
/// loaded and checked below. A new library must be added here, or the
/// coverage test fails.
final _covered = <Object>[
  ControlPlaneClient,
  DescriptorClient,
  Tfd1ResponseReader,
  Ed25519DescriptorSignatureVerifier,
  JoinDescriptorResolver,
  LanInfoReply,
  LanDiscoveryClient,
  LanBrowseSnapshot,
  LanDatagramSocket,
  LocalMultiplayerRepository,
  MultiplayerStore,
  salvageMultiplayerSettings,
  MultiplayerNetworkReport,
];

const _prefix = 'package:launcher_data/src/multiplayer/';

/// Types whose values are, or carry, a network address or a socket.
const _addressTypes = {
  'dart.io.InternetAddress',
  'dart.io.Datagram',
  'dart.io.RawDatagramSocket',
  'dart.io.Socket',
  'dart.io.RawSocket',
  'dart.io.SecureSocket',
  'dart.io.ServerSocket',
  'dart.io.RawServerSocket',
  'dart.io.ConnectionTask',
  'dart.io.NetworkInterface',
};

/// Words that name an address in a member's name.
const _addressWords = {
  'address',
  'addresses',
  'ip',
  'ips',
  'endpoint',
  'endpoints',
  'sender',
  'senders',
  'remote',
  'peer',
  'peers',
};

/// The socket layer's own seams. They are how sockets and name lookups are
/// injected, so their signatures mention addresses; none may be exported
/// from the package.
const _socketSeams = {
  'LanSocketBinder',
  'DescriptorConnector',
  'HostLookup',
  'LanDatagramSocket',
  'bindLanDatagramSocket',
};

Iterable<LibraryMirror> _multiplayerLibraries() => currentMirrorSystem()
    .libraries
    .values
    .where((library) => library.uri.toString().startsWith(_prefix));

bool _mentionsAddress(TypeMirror type, [Set<TypeMirror>? seen]) {
  final visited = seen ?? <TypeMirror>{};
  if (!visited.add(type)) return false;
  if (_addressTypes.contains(MirrorSystem.getName(type.qualifiedName))) {
    return true;
  }
  if (type is TypedefMirror) return _mentionsAddress(type.referent, visited);
  if (type is FunctionTypeMirror) {
    return _mentionsAddress(type.returnType, visited) ||
        type.parameters.any((item) => _mentionsAddress(item.type, visited));
  }
  if (type is ClassMirror) {
    return type.typeArguments.any((item) => _mentionsAddress(item, visited));
  }
  return false;
}

List<String> _words(String name) => RegExp(
  r'[A-Z]?[a-z0-9]+|[A-Z]+(?![a-z])',
).allMatches(name).map((match) => match.group(0)!.toLowerCase()).toList();

/// Why [member] exposes an address, or null. Only what a caller can read is
/// judged: fields, getters and what methods return. Constructor and method
/// parameters are inputs, such as the address a socket binds or the target
/// a player typed.
String? _exposure(String owner, DeclarationMirror member) {
  final name = MirrorSystem.getName(member.simpleName);
  TypeMirror? type;
  if (member is VariableMirror) {
    type = member.type;
  } else if (member is MethodMirror) {
    if (member.isConstructor || member.isSetter) return null;
    type = member.returnType;
  } else {
    return null;
  }
  if (_mentionsAddress(type)) {
    return '$owner.$name has an address type, '
        '${MirrorSystem.getName(type.qualifiedName)}';
  }
  final words = _words(name);
  if (words.any(_addressWords.contains)) return '$owner.$name names an address';
  // A host held as text or bytes is an address too.
  final typeName = MirrorSystem.getName(type.qualifiedName);
  if (words.any((word) => word == 'host' || word == 'hosts') &&
      {'String', 'List', 'Uint8List'}.contains(typeName)) {
    return '$owner.$name holds a host as $typeName';
  }
  return null;
}

void main() {
  test('covers every library in the folder', () {
    expect(_covered, isNotEmpty);
    final folder = Directory(
      p.join(Directory.current.path, 'lib', 'src', 'multiplayer'),
    );
    // A part file belongs to the library that names it, and is checked
    // with it.
    final files = {
      for (final entity in folder.listSync())
        if (entity is File &&
            entity.path.endsWith('.dart') &&
            !entity.readAsStringSync().startsWith('part of '))
          '$_prefix${p.basename(entity.path)}',
    };
    final loaded = {
      for (final library in _multiplayerLibraries()) library.uri.toString(),
    };
    expect(loaded, files);
  });

  test('no public type of the folder exposes an address', () {
    final problems = <String>[];
    for (final library in _multiplayerLibraries()) {
      for (final declaration in library.declarations.values) {
        if (declaration.isPrivate) continue;
        final name = MirrorSystem.getName(declaration.simpleName);
        if (_socketSeams.contains(name)) continue;
        if (declaration is ClassMirror) {
          for (final member in declaration.declarations.values) {
            if (member.isPrivate) continue;
            final problem = _exposure(name, member);
            if (problem != null) problems.add(problem);
          }
        } else if (declaration is TypedefMirror) {
          if (_mentionsAddress(declaration)) {
            problems.add('typedef $name mentions an address type');
          }
        } else {
          final problem = _exposure(library.uri.pathSegments.last, declaration);
          if (problem != null) problems.add(problem);
        }
      }
    }
    expect(problems, isEmpty);
  });

  test('the package exports no socket seam and no network client', () {
    final barrel = currentMirrorSystem()
        .libraries[Uri.parse('package:launcher_data/launcher_data.dart')]!;
    final exported = {
      for (final dependency in barrel.libraryDependencies)
        if (dependency.isExport &&
            dependency.targetLibrary!.uri.toString().startsWith(_prefix))
          dependency.targetLibrary!.uri.pathSegments.last,
    };
    expect(exported, {
      'control_plane.dart',
      'descriptor_signature_verifier.dart',
      'lan_discovery_state.dart',
      'multiplayer_repository.dart',
      'multiplayer_store.dart',
      'network_diagnostics.dart',
    });
    final names = {
      for (final library in _multiplayerLibraries())
        if (exported.contains(library.uri.pathSegments.last))
          for (final declaration in library.declarations.values)
            MirrorSystem.getName(declaration.simpleName),
    };
    expect(names.intersection(_socketSeams), isEmpty);
  });

  test('nothing in the folder logs or prints', () {
    final folder = Directory(
      p.join(Directory.current.path, 'lib', 'src', 'multiplayer'),
    );
    final printing = RegExp(
      r'\bprint\(|\bstdout\b|\bstderr\b|\bdeveloper\.log',
    );
    for (final file in folder.listSync().whereType<File>()) {
      expect(
        printing.hasMatch(file.readAsStringSync()),
        isFalse,
        reason: p.basename(file.path),
      );
    }
  });
}
