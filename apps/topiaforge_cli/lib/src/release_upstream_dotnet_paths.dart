import 'dart:typed_data';

/// Public native assertion strings in Microsoft's pinned macOS singlefilehost.
/// Exact member/package hashes and byte offsets are recorded in
/// release/dotnet-upstream-source-paths-v1.json (runtime 10.0.9, upstream commit
/// 901ca941248413c79832d2fdbd709da0c4386353). This never approves an account,
/// prefix, filename, UTF16 spelling, or the rest of any executable.
class UpstreamDotnetSourcePaths {
  static const runtimeVersion = '10.0.9';
  static const sourceFiles = <String>{
    'coreclr/debug/ee/rcthread.cpp',
    'coreclr/dlls/mscoree/exports.cpp',
    'coreclr/jit/codegenarm64.cpp',
    'coreclr/jit/codegenarmarch.cpp',
    'coreclr/jit/codegencommon.cpp',
    'coreclr/jit/codegenxarch.cpp',
    'coreclr/jit/emitarm64.cpp',
    'coreclr/jit/instr.cpp',
    'coreclr/jit/lclvars.cpp',
    'coreclr/jit/lsraarm64.cpp',
    'coreclr/jit/morph.cpp',
    'coreclr/jit/unwindarm64.cpp',
    'coreclr/vm/amd64/cgenamd64.cpp',
    'coreclr/vm/gcenv.ee.common.cpp',
    'coreclr/vm/jithelpers.cpp',
    'coreclr/vm/threads.cpp',
    'coreclr/vm/writebarriermanager.cpp',
    'native/containers/dn-simdhash-ght-compatible.c',
    'native/containers/dn-simdhash-specialization.h',
    'native/containers/dn-simdhash-string-ptr.c',
    'native/containers/dn-simdhash.c',
  };

  // Do not embed an entire home-shaped literal into the compiled CLI itself.
  // These runtime joins reconstruct only the exact reviewed upstream paths.
  static final _prefix = [
    '/Users',
    'runner',
    'work',
    '1',
    's',
    'src',
    'runtime',
    'src',
  ].join('/');
  static final literals = Set<String>.unmodifiable({
    for (final suffix in sourceFiles) '$_prefix/$suffix',
  });
  // Keep both original NUL boundaries. The trailing lookahead permits adjacent
  // approved literals while requiring the whole public filename to match.
  static final _completeLiteral = RegExp(
    '\u0000(?:${literals.map(RegExp.escape).join('|')})(?=\u0000)',
  );

  /// Replaces only approved ASCII bytes, before either privacy-scan encoding
  /// pass. Keeping the length and NULs preserves neighboring private paths.
  static Uint8List mask(Uint8List bytes) {
    Uint8List? masked;
    for (final match in _completeLiteral.allMatches(
      String.fromCharCodes(bytes),
    )) {
      masked ??= Uint8List.fromList(bytes);
      masked.fillRange(match.start + 1, match.end, 0x20);
    }
    return masked ?? bytes;
  }
}
