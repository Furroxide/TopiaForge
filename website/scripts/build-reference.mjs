import { rmSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const websiteRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

// DocFX updates its output in place and otherwise leaves pages for API types
// removed by a breaking SDK revision. Always build the reference from an
// empty metadata/output tree so published search and xref indexes cannot
// advertise obsolete contracts.
rmSync(resolve(websiteRoot, 'obj', 'api'), { recursive: true, force: true });
rmSync(resolve(websiteRoot, 'dist', 'api', 'csharp'), {
  recursive: true,
  force: true,
});

const result = spawnSync('dotnet', ['tool', 'run', 'docfx', 'docfx.json'], {
  cwd: websiteRoot,
  encoding: 'utf8',
  stdio: 'inherit',
});

if (result.error) {
  console.error(`Unable to start DocFX: ${result.error.message}`);
  process.exit(1);
}
process.exit(result.status ?? 1);
