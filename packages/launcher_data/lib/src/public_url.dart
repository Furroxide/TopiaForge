/// Returns whether [uri] is a bounded absolute public HTTPS URL.
///
/// Credentials, queries, and fragments are rejected so secrets and mutable
/// signed references cannot enter persisted registries or redirect chains.
bool isPublicHttpsUri(Uri uri) =>
    uri.toString().length <= 4096 &&
    uri.scheme.toLowerCase() == 'https' &&
    uri.host.isNotEmpty &&
    uri.userInfo.isEmpty &&
    !uri.hasQuery &&
    !uri.hasFragment;

Uri requirePublicHttpsUri(Uri uri, String label) {
  if (uri.toString().length > 4096 ||
      uri.scheme.toLowerCase() != 'https' ||
      uri.host.isEmpty) {
    throw StateError('$label must use an absolute HTTPS URL.');
  }
  if (uri.userInfo.isNotEmpty) {
    throw StateError('$label URL must not contain credentials.');
  }
  if (uri.hasQuery || uri.hasFragment) {
    throw StateError('$label URL must not contain a query or fragment.');
  }
  return uri.replace(scheme: 'https');
}
