/// Sequences the STUN transactions that produce a PII-free [NatObservation].
library;

import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';

import 'stun_message.dart';
import 'stun_transport.dart';

/// Runs the RFC 5780 behaviour-discovery sequence and reduces it to booleans.
///
/// Addresses live only inside [run]. Every comparison happens here and only the results are returned, so the
/// privacy claim in `launcher_domain` holds by construction rather than by discipline.
class ReachabilityProbeRunner {
  const ReachabilityProbeRunner();

  /// Probes [servers] in order, using the first that answers.
  ///
  /// The sequence is:
  ///
  /// 1. A plain binding request to the primary server yields the reflexive endpoint `M1` and, when the server
  ///    advertises one, an alternate server address.
  /// 2. Two CHANGE-REQUEST probes observe filtering: whether a reply is accepted from an unsolicited address, and
  ///    from an unsolicited port.
  /// 3. Two further binding requests observe mapping: `M2` from the alternate address on the original port, and
  ///    `M3` from the alternate address and port. `M1 == M2` means the mapping ignores the destination address;
  ///    `M2 == M3` means it ignores the destination port.
  ///
  /// A server that advertises no alternate address leaves mapping undetermined, which the classifier reports as
  /// [HostReachability.unknown] rather than guessing.
  Future<NatObservation> run(
    StunTransport transport,
    List<StunEndpoint> servers,
  ) async {
    StunEndpoint? primary;
    StunBindingResponse? first;
    for (final server in servers) {
      final response = await transport.request(server);
      if (response != null) {
        primary = server;
        first = response;
        break;
      }
    }
    if (primary == null || first == null) return const NatObservation();

    final mappedMatchesLocal = transport.matchesLocalEndpoint(first.mapped);

    final unsolicitedAddress = await transport.request(
      primary,
      changeAddress: true,
      changePort: true,
    );
    final unsolicitedPort = await transport.request(primary, changePort: true);

    var completedMappingTransactions = 1;
    var sameAcrossAddresses = false;
    var sameAcrossPorts = false;

    final alternate = first.otherAddress;
    if (alternate != null) {
      final alternateAddressOriginalPort = StunEndpoint(
        alternate.address,
        primary.port,
      );
      final second = await transport.request(alternateAddressOriginalPort);
      if (second != null) {
        completedMappingTransactions++;
        sameAcrossAddresses = second.mapped == first.mapped;

        final third = await transport.request(alternate);
        if (third != null) {
          completedMappingTransactions++;
          sameAcrossPorts = third.mapped == second.mapped;
        }
      }
    }

    return NatObservation(
      respondedAtAll: true,
      mappedMatchesLocalEndpoint: mappedMatchesLocal,
      sameMappingAcrossServerAddresses: sameAcrossAddresses,
      sameMappingAcrossServerPorts: sameAcrossPorts,
      acceptedFromUnsolicitedAddress: unsolicitedAddress != null,
      acceptedFromUnsolicitedPort: unsolicitedPort != null,
      completedMappingTransactions: completedMappingTransactions,
    );
  }
}

/// Parses `host:port` probe server entries into endpoints.
///
/// Only literal IPv4 or IPv6 addresses are accepted. Hostname resolution is deliberately not performed here: DNS is
/// a separate failure mode and a separate privacy surface, and the probe is configuration-driven rather than
/// shipping a default server list.
class StunServerList {
  const StunServerList();

  /// Returns the parsable entries in [entries], skipping anything malformed.
  List<StunEndpoint> parse(Iterable<String> entries) {
    final servers = <StunEndpoint>[];
    for (final entry in entries) {
      final endpoint = parseOne(entry);
      if (endpoint != null) servers.add(endpoint);
    }
    return servers;
  }

  /// Parses one `address:port` or `[v6address]:port` entry, or returns `null`.
  StunEndpoint? parseOne(String entry) {
    final trimmed = entry.trim();
    if (trimmed.isEmpty) return null;

    String host;
    String portText;
    if (trimmed.startsWith('[')) {
      final close = trimmed.indexOf(']');
      if (close < 0 || close + 2 >= trimmed.length) return null;
      if (trimmed[close + 1] != ':') return null;
      host = trimmed.substring(1, close);
      portText = trimmed.substring(close + 2);
    } else {
      final separator = trimmed.lastIndexOf(':');
      if (separator <= 0 || separator == trimmed.length - 1) return null;
      host = trimmed.substring(0, separator);
      portText = trimmed.substring(separator + 1);
    }

    final port = int.tryParse(portText);
    if (port == null || port < 1 || port > 65535) return null;

    final address = _parseAddress(host);
    if (address == null) return null;
    return StunEndpoint(address, port);
  }

  Uint8List? _parseAddress(String host) {
    if (host.contains(':')) return _parseIPv6(host);
    return _parseIPv4(host);
  }

  Uint8List? _parseIPv4(String host) {
    final parts = host.split('.');
    if (parts.length != 4) return null;
    final bytes = Uint8List(4);
    for (var index = 0; index < 4; index++) {
      final value = int.tryParse(parts[index]);
      if (value == null || value < 0 || value > 255) return null;
      bytes[index] = value;
    }
    return bytes;
  }

  Uint8List? _parseIPv6(String host) {
    final compression = host.indexOf('::');
    if (compression != host.lastIndexOf('::')) return null;

    List<int>? groups(String text) {
      if (text.isEmpty) return const [];
      final values = <int>[];
      for (final part in text.split(':')) {
        if (part.isEmpty || part.length > 4) return null;
        final value = int.tryParse(part, radix: 16);
        if (value == null || value < 0 || value > 0xFFFF) return null;
        values.add(value);
      }
      return values;
    }

    List<int> words;
    if (compression < 0) {
      final parsed = groups(host);
      if (parsed == null || parsed.length != 8) return null;
      words = parsed;
    } else {
      final head = groups(host.substring(0, compression));
      final tail = groups(host.substring(compression + 2));
      if (head == null || tail == null) return null;
      final missing = 8 - head.length - tail.length;
      if (missing < 1) return null;
      words = [...head, ...List<int>.filled(missing, 0), ...tail];
    }

    final bytes = Uint8List(16);
    for (var index = 0; index < 8; index++) {
      bytes[index * 2] = (words[index] >> 8) & 0xFF;
      bytes[(index * 2) + 1] = words[index] & 0xFF;
    }
    return bytes;
  }
}
