import { readFile, writeFile } from 'node:fs/promises';

// Custom Domains are provisioned by the account owner. CI publishes Worker code
// with its single-Worker credential and preserves those existing domain bindings.
const source = await readFile(new URL('../wrangler.toml', import.meta.url), 'utf8');
const routing = /^[ \t]*routes[ \t]*=[^\r\n]*(?:\r?\n|$)/gm;
if ([...source.matchAll(routing)].length !== 2) throw new Error('Review changed deployment routing configuration.');
const deployment = source.replace(routing, '');
if (/^[ \t]*routes?[ \t]*=/m.test(deployment)) throw new Error('Unexpected deployment routing assignment.');
await writeFile(new URL('../wrangler.deploy.toml', import.meta.url), deployment);
