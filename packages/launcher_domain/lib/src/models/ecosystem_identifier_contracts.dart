part of '../models.dart';

abstract final class VpmPackageId {
  static final _pattern = RegExp(r'^[a-z0-9]+(?:[._-][a-z0-9]+)+$');

  static bool isValid(String value) =>
      value == value.trim() &&
      value.length <= 214 &&
      _pattern.hasMatch(value) &&
      !_retiredEcosystemPrefixes.any(value.startsWith);
}

abstract final class PackageSourceId {
  static final _pattern = RegExp(r'^[a-z0-9]+(?:[._-][a-z0-9]+)*$');

  static bool isValid(String value) =>
      value == value.trim() &&
      value.length <= 128 &&
      _pattern.hasMatch(value) &&
      !_retiredEcosystemPrefixes.any(value.startsWith);
}

// Constructed at runtime so retired names are rejected without embedding
// those identifiers in release binaries.
final _retiredEcosystemPrefixes = <String>[
  String.fromCharCodes(const [114, 111, 98, 111, 116, 111, 112, 105, 97, 46]),
  String.fromCharCodes(const [
    99,
    111,
    109,
    46,
    114,
    111,
    98,
    111,
    116,
    111,
    112,
    105,
    97,
    46,
  ]),
  String.fromCharCodes(const [
    113,
    117,
    97,
    110,
    116,
    117,
    109,
    119,
    111,
    114,
    107,
    115,
    46,
  ]),
];
