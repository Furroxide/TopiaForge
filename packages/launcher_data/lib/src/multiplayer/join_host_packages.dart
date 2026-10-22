part of 'join_descriptor_resolver.dart';

/// Where a direct link or typed address served the descriptor a join
/// verified: the private handle its host-served downloads go through.
final class _HostContact {
  const _HostContact(this.address, this.port, this.serverId);

  final InternetAddress address;
  final int port;
  final String serverId;
}

/// The packages one join takes from its host in developer mode
/// ([JoinHostPackageSource]), from where its descriptor came and nowhere
/// else:
///
/// - a LAN listing, from its beacon's entry, which [prepare] keeps alive
///   until [release], starting browsing again when it had stopped and the
///   player's settings allow it;
/// - a direct link or typed address, from the address that served the
///   descriptor the join verified;
/// - an invite, server link or Internet listing, from nowhere: the service
///   found that host, and the launcher does not know where it is.
///
/// The session's privacy is applied to the host's address again before
/// every package, so under relay-only nothing beyond the local network is
/// contacted ([HostPackageClient]). Nothing here names an address.
final class JoinHostPackages implements JoinHostPackageSource {
  JoinHostPackages._(this._resolver, this._request, this._mayBrowse);

  final JoinDescriptorResolver _resolver;
  final JoinDownloadRequest _request;
  final Future<bool> Function()? _mayBrowse;
  LanBrowseHold? _hold;
  var _released = false;

  @override
  Future<JoinHostPackageProblem?> prepare({
    CancellationToken? cancellation,
  }) async {
    final target = _request.target;
    if (target.resolvesThroughService) {
      return JoinHostPackageProblem.hostUnknown;
    }
    if (target is! JoinListingTarget) {
      return _contact() == null ? JoinHostPackageProblem.unreachable : null;
    }
    final lan = _resolver._lan;
    if (lan == null || _released) return JoinHostPackageProblem.notHeard;
    final hold = _hold ??= await lan.hold(mayStart: _mayBrowse);
    if (_released) hold.release();
    final heard = await lan.hearsServer(
      target.serverId,
      cancellation: cancellation,
    );
    if (cancellation?.isCancelled ?? false) {
      return JoinHostPackageProblem.cancelled;
    }
    return heard ? null : JoinHostPackageProblem.notHeard;
  }

  @override
  Future<JoinHostPackageResult> download(
    DescriptorPackage package,
    String path, {
    void Function(int bytesReceived, int bytesTotal)? onProgress,
    CancellationToken? cancellation,
  }) async {
    final target = _request.target;
    if (target.resolvesThroughService) {
      return const JoinHostPackageFailed(JoinHostPackageProblem.hostUnknown);
    }
    final file = File(path);
    if (target is JoinListingTarget) {
      final lan = _resolver._lan;
      if (lan == null) {
        return const JoinHostPackageFailed(JoinHostPackageProblem.notHeard);
      }
      return lan.fetchPackage(
        _request,
        package.sha256,
        file,
        onProgress: onProgress,
        cancellation: cancellation,
      );
    }
    final contact = _contact();
    if (contact == null) {
      return const JoinHostPackageFailed(JoinHostPackageProblem.unreachable);
    }
    return _resolver._packages.fetchFrom(
      contact.address,
      contact.port,
      package.sha256,
      file,
      allowDirect: _request.allowsDirect,
      onProgress: onProgress,
      cancellation: cancellation,
    );
  }

  /// Lets LAN browsing stop again if this join's downloads were all that
  /// kept it running. Harmless twice.
  void release() {
    _released = true;
    _hold?.release();
  }

  /// Where this join's descriptor came from, when it was this join's server
  /// that answered there.
  _HostContact? _contact() {
    final contact = _resolver._served[_request.target];
    return contact != null && contact.serverId == _request.serverId
        ? contact
        : null;
  }
}
