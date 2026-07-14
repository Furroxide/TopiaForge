import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import {
  establishPublisherSession,
  readSessionFile,
  SessionFileError,
  writeSessionFile,
} from '../session_file.mjs';

test('bounded session reads reject links, bad UTF-8, and oversized input', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-session-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const safe = path.join(root, 'safe.json');
  fs.writeFileSync(safe, '{"publisherLeaseToken":"token"}', { mode: 0o600 });
  assert.deepEqual(readSessionFile(safe), { publisherLeaseToken: 'token' });

  const linked = path.join(root, 'linked.json');
  fs.symlinkSync(safe, linked);
  assert.throws(
    () => readSessionFile(linked),
    (error) => error instanceof SessionFileError && error.code === 'unsafe-type',
  );

  const invalid = path.join(root, 'invalid.json');
  fs.writeFileSync(invalid, Buffer.from([0xff]), { mode: 0o600 });
  assert.throws(
    () => readSessionFile(invalid),
    (error) => error instanceof SessionFileError && error.code === 'invalid-utf8',
  );
  assert.throws(
    () => readSessionFile(safe, { maxBytes: 8 }),
    (error) => error instanceof SessionFileError && error.code === 'too-large',
  );
});

test('session reads detect same-name replacement races', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-session-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const target = path.join(root, 'session.json');
  const replacement = path.join(root, 'replacement.json');
  fs.writeFileSync(target, '{"publisherLeaseToken":"old"}', { mode: 0o600 });
  fs.writeFileSync(replacement, '{"publisherLeaseToken":"new"}', { mode: 0o600 });
  let replaced = false;
  const fileSystem = {
    ...fs,
    readSync(...arguments_) {
      const count = fs.readSync(...arguments_);
      if (!replaced && count > 0) {
        replaced = true;
        fs.renameSync(replacement, target);
      }
      return count;
    },
  };

  assert.throws(
    () => readSessionFile(target, { fileSystem }),
    (error) => error instanceof SessionFileError && error.code === 'changed',
  );
});

test('replaces an existing session file through the Windows retry path', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-session-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const target = path.join(root, 'session.json');
  fs.writeFileSync(target, '{"old":true}');

  let renameCalls = 0;
  const fileSystem = {
    ...fs,
    renameSync(source, destination) {
      renameCalls += 1;
      if (renameCalls === 1) {
        const error = new Error('destination exists');
        error.code = 'EPERM';
        throw error;
      }
      fs.renameSync(source, destination);
    },
  };

  const written = writeSessionFile(
    target,
    { documentUrl: 'automerge:test' },
    { fileSystem, platform: 'win32' },
  );

  assert.equal(written, true);
  assert.equal(renameCalls, 2);
  assert.deepEqual(JSON.parse(fs.readFileSync(target, 'utf8')), {
    documentUrl: 'automerge:test',
  });
  assert.deepEqual(fs.readdirSync(root), ['session.json']);
});

test('cleans the temporary file when replacement fails', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-session-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const target = path.join(root, 'session.json');
  const messages = [];

  const written = writeSessionFile(
    target,
    { documentUrl: 'automerge:test' },
    {
      fileSystem: {
        ...fs,
        renameSync() {
          const error = new Error('disk failure');
          error.code = 'EIO';
          throw error;
        },
      },
      reportError: (message) => messages.push(message),
    },
  );

  assert.equal(written, false);
  assert.equal(messages.length, 1);
  assert.deepEqual(fs.readdirSync(root), []);
});

test('announces a publisher only after its session lease is established', () => {
  const announcements = [];
  const publicSession = { documentUrl: 'automerge:public' };
  const privateSession = {
    ...publicSession,
    publisherLeaseToken: 'private-token',
  };

  assert.equal(
    establishPublisherSession('session.json', privateSession, publicSession, {
      writeSession: () => false,
      announce: (line) => announcements.push(line),
    }),
    false,
  );
  assert.deepEqual(announcements, []);

  assert.equal(
    establishPublisherSession('session.json', privateSession, publicSession, {
      writeSession: () => true,
      announce: (line) => announcements.push(line),
    }),
    true,
  );
  assert.equal(
    announcements[0],
    'TOPIAFORGE_UGC_SESSION {"documentUrl":"automerge:public"}\n',
  );
  assert.doesNotMatch(announcements[0], /private-token/);
});
