part of 'release_spdx_third_party.dart';

/// Release-catalog components that are themselves vendored third-party
/// software: the licence their authors declare and the upstream source.
///
/// THIRD_PARTY_NOTICES.md records BepInEx itself as MIT. The UnityDoorstop
/// loader stub and the other runtime dependencies inside the same bundle carry
/// their own terms, so they are separate packages below, not part of this
/// declaration. Tag and repository: third_party/BepInEx/provenance.json.
const releaseVendoredComponents =
    <String, ({String license, String downloadLocation})>{
      'bepInEx': (
        license: 'MIT',
        downloadLocation: 'git+https://github.com/BepInEx/BepInEx@v5.4.23.5',
      ),
    };

/// Upstream licences the repository records for exact third-party releases,
/// keyed by `name@version`. A shipped release absent from this table is
/// declared NOASSERTION: a licence recorded for one version is never assumed
/// for another.
const releaseSpdxRecordedLicenses = <String, String>{
  // THIRD_PARTY_NOTICES.md, the table of the standalone executable's runtime.
  'Dart SDK@3.12.2': 'BSD-3-Clause',
  // archive's own LICENSE is MIT; its LICENSE-other.md adds the terms of the
  // zlib.js (MIT), JZLib (BSD-3-Clause), bzip2 (bzip2-1.0.6) and Bouncy
  // Castle (MIT) code it derives from.
  'archive@4.2.0': 'MIT AND BSD-3-Clause AND bzip2-1.0.6',
  'async@2.13.1': 'BSD-3-Clause',
  'boolean_selector@2.1.2': 'BSD-3-Clause',
  'collection@1.19.1': 'BSD-3-Clause',
  'crypto@3.0.7': 'BSD-3-Clause',
  'cryptography@2.9.0': 'Apache-2.0',
  'ffi@2.2.0': 'BSD-3-Clause',
  'http@1.6.0': 'BSD-3-Clause',
  'http_parser@4.1.2': 'BSD-3-Clause',
  'json_schema@5.2.2': 'BSD-3-Clause',
  'logging@1.3.0': 'BSD-3-Clause',
  'matcher@0.12.20': 'BSD-3-Clause',
  'meta@1.18.3': 'BSD-3-Clause',
  'path@1.9.1': 'BSD-3-Clause',
  'posix@6.5.0': 'MIT',
  'quiver@3.2.2': 'Apache-2.0',
  'rfc_6901@0.2.1': 'MIT',
  'source_span@1.10.2': 'BSD-3-Clause',
  'stack_trace@1.12.1': 'BSD-3-Clause',
  'stream_channel@2.1.4': 'BSD-3-Clause',
  'string_scanner@1.4.1': 'BSD-3-Clause',
  'term_glyph@1.2.2': 'BSD-3-Clause',
  'test_api@0.7.13': 'BSD-3-Clause',
  'typed_data@1.4.0': 'BSD-3-Clause',
  'unorm_dart@0.3.2': 'MIT',
  'uri@1.0.0': 'BSD-3-Clause',
  'web@1.1.1': 'BSD-3-Clause',
  // THIRD_PARTY_NOTICES.md, the GameCompat extractor paragraphs: the runtime
  // pack's LICENSE.TXT is the .NET Foundation MIT text, and the
  // MetadataLoadContext nuspec declares MIT, which packaging verifies.
  '.NET Runtime@10.0.9': 'MIT',
  'System.Reflection.MetadataLoadContext@10.0.9': 'MIT',
};

/// Licences without an SPDX License List identifier, as the SBOM records them.
const _licenseReferences = <String, Map<String, String>>{
  // THIRD_PARTY_NOTICES.md, the TextMesh Pro essential resources entry.
  'LicenseRef-Unity-Companion-License': {
    'name': 'Unity Companion License',
    'extractedText':
        'Unity Companion License. THIRD_PARTY_NOTICES.md records the '
        'TextMesh Pro essential resources as distributed under this licence '
        'as part of the Unity Editor package. The licence has no SPDX '
        'License List identifier, and this repository does not carry its '
        'text.',
  },
};

const _vendoredPackages = <ReleaseSpdxThirdPartyPackage>[
  // THIRD_PARTY_NOTICES.md, the BepInEx 5.4.23.5 runtime dependency table;
  // UnityDoorstop's source archive URL is third_party/BepInEx/provenance.json.
  //
  // UnityDoorstop is LGPL-2.1-only, not -or-later. The vendored source
  // archive (third_party/BepInEx/UnityDoorstop-4.5.0-source-*.zip) states
  // "Doorstop 4 is licensed under LGPLv2.1" in its README without an
  // "any later version" grant, its own sources carry no per-file notice, and
  // its LICENSE is the unmodified LGPL 2.1 text, byte-identical to
  // third_party/BepInEx/LICENSES/UnityDoorstop-LGPL-2.1.txt. Under section 13
  // of that licence, a later version is optional only when the library says
  // so. Its BSD-licensed plthook files are compiled only into the non-Windows
  // stubs, so _requireRecordedPins refuses a macOS release until they are
  // recorded too.
  ReleaseSpdxThirdPartyPackage(
    name: 'UnityDoorstop',
    version: '4.5.0',
    licenseDeclared: 'LGPL-2.1-only',
    shippedBy: {'bepInEx'},
    downloadLocation:
        'https://github.com/NeighTools/UnityDoorstop/archive/'
        '33dab9a6733862eb81869ff08431d9478b28784b.zip',
    comment:
        'BepInEx loader stub (winhttp.dll on Windows); its corresponding '
        'source ships under third_party/BepInEx.',
  ),
  ReleaseSpdxThirdPartyPackage(
    name: 'HarmonyX',
    version: '2.7.0',
    licenseDeclared: 'MIT',
    shippedBy: {'bepInEx'},
  ),
  ReleaseSpdxThirdPartyPackage(
    name: 'Harmony',
    version: null,
    licenseDeclared: 'MIT',
    shippedBy: {'bepInEx'},
    comment: 'Recorded as the HarmonyX upstream base, without a version.',
  ),
  ReleaseSpdxThirdPartyPackage(
    name: 'MonoMod',
    version: '21.12.13.01',
    licenseDeclared: 'MIT',
    shippedBy: {'bepInEx'},
  ),
  ReleaseSpdxThirdPartyPackage(
    name: 'Mono.Cecil',
    version: '0.10.4',
    licenseDeclared: 'MIT',
    shippedBy: {'bepInEx'},
  ),
  // THIRD_PARTY_NOTICES.md, the Quicksand, Audiowide, TextMesh Pro, and
  // Liberation Sans entries. The two brand fonts ship as files in launcherUi
  // (packages/launcher_ui/pubspec.yaml) and as SDF glyph data in the bundle
  // TopiaForge.Mods.UnityUi.csproj embeds; TextMesh Pro and Liberation Sans
  // ship in that bundle's project, tools/unity-ui-bundle, whose README records
  // that the build pulls the TextMesh Pro shader into the bundle.
  ReleaseSpdxThirdPartyPackage(
    name: 'Quicksand',
    version: null,
    licenseDeclared: 'OFL-1.1',
    shippedBy: {'launcherUi', 'unityUi'},
    downloadLocation:
        'https://github.com/google/fonts/raw/main/ofl/quicksand/'
        'Quicksand%5Bwght%5D.ttf',
    comment: _brandFontComment,
  ),
  ReleaseSpdxThirdPartyPackage(
    name: 'Audiowide',
    version: null,
    licenseDeclared: 'OFL-1.1',
    shippedBy: {'launcherUi', 'unityUi'},
    downloadLocation:
        'https://github.com/google/fonts/raw/main/ofl/audiowide/'
        'Audiowide-Regular.ttf',
    comment: _brandFontComment,
  ),
  ReleaseSpdxThirdPartyPackage(
    name: 'Liberation Sans',
    version: null,
    licenseDeclared: 'OFL-1.1',
    shippedBy: {'unityUi'},
    comment: 'Redistributed with the TextMesh Pro essential resources.',
  ),
  ReleaseSpdxThirdPartyPackage(
    name: 'TextMesh Pro essential resources',
    version: null,
    licenseDeclared: 'LicenseRef-Unity-Companion-License',
    shippedBy: {'unityUi'},
    comment:
        'Shaders, materials, style sheets, and line-breaking data in '
        'tools/unity-ui-bundle; the bundle build also pulls the TextMesh Pro '
        'shader of the brand font materials into topiaforge-ui.bundle.',
  ),
  // THIRD_PARTY_NOTICES.md, the CC0 launcher artwork entry.
  ReleaseSpdxThirdPartyPackage(
    name: 'City Mega Pack',
    version: null,
    licenseDeclared: 'CC0-1.0',
    shippedBy: {'launcherUi'},
    downloadLocation: 'https://opengameart.org/content/city-mega-pack',
    comment: 'By GrafxKid; source of topiaforge-city-header.webp.',
  ),
  ReleaseSpdxThirdPartyPackage(
    name: 'Gum Bot sprites',
    version: null,
    licenseDeclared: 'CC0-1.0',
    shippedBy: {'launcherUi'},
    downloadLocation: 'https://opengameart.org/content/gum-bot-sprites',
    comment: 'By GrafxKid; source of baby-stitch.webp and sheriff.webp.',
  ),
  // No repository file records a licence for the Material Icons font.
  ReleaseSpdxThirdPartyPackage(
    name: 'Material Icons',
    version: null,
    licenseDeclared: 'NOASSERTION',
    shippedBy: {'launcher'},
    comment:
        'Icon font Flutter bundles because the launcher sets '
        'uses-material-design.',
  ),
  // THIRD_PARTY_NOTICES.md and third_party/SPDX_LICENSE_LIST_PROVENANCE.json.
  ReleaseSpdxThirdPartyPackage(
    name: 'SPDX License List Data',
    version: '3.28.0',
    licenseDeclared: 'CC0-1.0',
    shippedBy: {'cli'},
    downloadLocation: 'git+https://github.com/spdx/license-list-data@v3.28.0',
    comment: 'Identifier allowlist generated into spdx_ids_3_28.g.dart.',
  ),
];

const _brandFontComment =
    'launcherUi ships the font file; unityUi ships SDF glyph data derived '
    'from it inside the topiaforge-ui.bundle its assembly embeds.';
