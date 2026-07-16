import { extname } from 'node:path';

export const pages = [
  page('website/content/index.md', 'index.md', false),
  page('docs/YourFirstMod.md', 'getting-started/first-mod.md'),
  page('docs/Modding.md', 'reference/sdk-overview.md'),
  page('docs/CoreServices.md', 'guides/core-services.md'),
  page('docs/UiKit.md', 'guides/ui.md'),
  page('docs/Modules.md', 'guides/modules.md'),
  page('docs/RobotKit.md', 'guides/robotkit.md'),
  page('docs/CustomWorlds.md', 'guides/custom-worlds.md'),
  page('docs/TestingMods.md', 'guides/testing.md'),
  page('docs/CliDevLoop.md', 'guides/cli-dev.md'),
  page('docs/UnityInterop.md', 'guides/interop.md'),
  page('docs/ManifestV4.md', 'reference/manifest-v4.md'),
  page('docs/Diagnostics.md', 'reference/diagnostics.md'),
  page('docs/PrivacyAndCapabilities.md', 'reference/capabilities.md'),
  page(
    'docs/CapabilityMatrix.md',
    'reference/capability-matrix.md',
    true,
    generatedFrontmatter(
      'V1 capability coverage',
      'Trace every promised Robotopia modding goal to its API, sample, guide, testing fake, and live acceptance case.',
    ),
  ),
  page(
    'docs/LiveGameAcceptance.md',
    'reference/live-game-acceptance.md',
    true,
    generatedFrontmatter(
      'Live Robotopia acceptance',
      'Run the safe SDK acceptance suite on an authorized Robotopia installation.',
    ),
  ),
  page('docs/ModPackaging.md', 'reference/package-format.md'),
  page('docs/CompatibilityPolicy.md', 'reference/compatibility.md'),
  page('docs/Troubleshooting.md', 'reference/troubleshooting.md'),
  page('docs/PublishingYourMod.md', 'guides/publishing.md'),
];

export const forbiddenPublicContractTerms = [
  [/(?<![A-Za-z])ITopiaForgeMod(?![A-Za-z])/giu, 'retired mod interface'],
  [/(?<![A-Za-z])GetService(?![A-Za-z])/giu, 'retired global service lookup'],
  [/(?<![A-Za-z])vpmDependencies(?![A-Za-z])/giu, 'retired dependency field'],
  [/(?<![A-Za-z])permissions(?![A-Za-z])/giu, 'retired manifest terminology'],
  [/(?<![0-9.])0\.(?:x|1(?:\.[0-9]+)?)(?![0-9])/giu, 'pre-V1 SDK or package version'],
  [/(?<![A-Za-z])UnityEngine(?![A-Za-z])/gu, 'native engine consumer type'],
  [/(?<![A-Za-z])GameCode(?![A-Za-z])/gu, 'game implementation assembly'],
  [/(?<![A-Za-z])Harmony(?![A-Za-z])/gu, 'patch-library consumer API'],
  [/(?<![A-Za-z])ProjectReference(?![A-Za-z])/giu, 'source-checkout project reference'],
];

export function routeForOutput(outputPath) {
  if (outputPath === 'index.md') {
    return '/';
  }

  return `/${outputPath.slice(0, -extname(outputPath).length)}/`;
}

function page(sourcePath, outputPath, enforceV1Contract = true, frontmatter = '') {
  return { sourcePath, outputPath, enforceV1Contract, frontmatter };
}

function generatedFrontmatter(title, description) {
  return `---\ntitle: ${title}\ndescription: ${description}\n---`;
}
