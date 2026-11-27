import 'dart:io';
import 'package:launcher_domain/launcher_domain.dart';
import 'acceptance_isolation_context.dart';
import 'multiplayer/descriptor_client.dart';

/// Resolves only the admitted pair's loopback host, pinned to its V5 identity.
/// The network client and socket types remain private to the data package.
Future<LaunchDirectTarget> verifyAcceptanceHostTarget(
  AcceptanceIsolationContext context, {
  required int port,
  required String serverId,
}) async {
  context.verify();
  final fetched = await DescriptorClient().fetchFrom(
    InternetAddress.loopbackIPv4,
    port,
    allowDirect: false,
    expectedServerId: serverId,
  );
  context.verify();
  if (fetched is! DescriptorFetched) {
    throw StateError('The acceptance host descriptor could not be verified.');
  }
  final descriptor = fetched.descriptor;
  return LaunchDirectTarget(
    host: '127.0.0.1',
    port: port,
    serverId: descriptor.serverId,
    descriptorSha256: descriptor.descriptorSha256,
    packageDigest: descriptor.packageDigest,
  );
}
