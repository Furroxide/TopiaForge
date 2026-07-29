part of '../local_developer_repository.dart';

abstract class _DeveloperDataRootRepository implements DeveloperRepository {
  _DeveloperDataRootRepository(this._dataRoot);

  final Directory _dataRoot;

  @override
  String get developerDataRoot => _dataRoot.path;
}
