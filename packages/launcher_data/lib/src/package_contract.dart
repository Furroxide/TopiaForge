import 'package:path/path.dart' as p;

const _topiaForgePackageExtension = '.topiaforgemod';

void requireCanonicalTopiaForgePackageReference(String reference) {
  final trimmed = reference.trim();
  final windowsPath =
      RegExp(r'^[A-Za-z]:[\\/]').hasMatch(trimmed) || trimmed.startsWith(r'\\');
  final uri = windowsPath ? null : Uri.tryParse(trimmed);
  final path = uri != null && uri.hasScheme ? uri.path : trimmed;
  if (!p.basename(path).toLowerCase().endsWith(_topiaForgePackageExtension)) {
    throw const FormatException(
      'TopiaForge package references must end with .topiaforgemod.',
    );
  }
}
