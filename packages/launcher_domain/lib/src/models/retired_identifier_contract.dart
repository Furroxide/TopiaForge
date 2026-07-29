part of '../models.dart';

// These code-unit sequences let validation reject retired ecosystem IDs
// without embedding those identities in shipped binaries.
final _retiredEcosystemIdPrefixes = <String>[
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
