import assert from 'node:assert/strict';
import test from 'node:test';

import {
  toWebSocketUrl,
  waitForPeer,
  waitForSocketDrain,
  withTimeout,
} from '../network_policy.mjs';

test('sync URLs require encryption except on loopback', () => {
  assert.equal(
    toWebSocketUrl('https://sync.example.test/path', 'wss://unused.test'),
    'wss://sync.example.test/path',
  );
  assert.equal(
    toWebSocketUrl('ws://127.0.0.1:3030', 'wss://unused.test'),
    'ws://127.0.0.1:3030/',
  );
  assert.throws(
    () => toWebSocketUrl('http://sync.example.test', 'wss://unused.test'),
    /Plaintext ws:\/\//,
  );
  assert.throws(
    () => toWebSocketUrl('ftp://sync.example.test', 'wss://unused.test'),
    /must use wss:\/\//,
  );
  assert.throws(
    () => toWebSocketUrl('wss://secret@sync.example.test', 'wss://unused.test'),
    /must not contain credentials/,
  );
});

test('peer connection and document waits are bounded', async () => {
  await assert.rejects(
    waitForPeer({ socket: { readyState: 0 } }, { timeoutMs: 10, pollMs: 1 }),
    /Timed out connecting/,
  );
  await assert.rejects(
    withTimeout(new Promise(() => {}), 'Document lookup', 10),
    /Document lookup timed out/,
  );
});

test('one-shot publishing waits for the socket buffer to drain', async () => {
  const network = { socket: { readyState: 1, bufferedAmount: 4 } };
  const draining = waitForSocketDrain(network, {
    timeoutMs: 100,
    quietMs: 5,
    pollMs: 1,
  });
  setTimeout(() => {
    network.socket.bufferedAmount = 0;
  }, 5);
  await draining;

  await assert.rejects(
    waitForSocketDrain(
      { socket: { readyState: 1, bufferedAmount: 1 } },
      { timeoutMs: 10, quietMs: 2, pollMs: 1 },
    ),
    /Timed out flushing/,
  );
});
