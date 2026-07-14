const DEFAULT_CONNECT_TIMEOUT_MS = 15_000;
const DEFAULT_FLUSH_TIMEOUT_MS = 10_000;

export function toWebSocketUrl(raw, defaultUrl) {
  const value = (raw || defaultUrl).trim();
  const candidate = /^[a-z][a-z0-9+.-]*:\/\//i.test(value)
    ? value
    : `wss://${value}`;
  let url;
  try {
    url = new URL(candidate);
  } catch {
    throw new Error(`Invalid sync server URL: ${value}`);
  }

  if (url.username || url.password) {
    throw new Error('Sync server URLs must not contain credentials.');
  }
  if (url.protocol === 'https:') url.protocol = 'wss:';
  if (url.protocol === 'http:') url.protocol = 'ws:';
  if (url.protocol !== 'wss:' && url.protocol !== 'ws:') {
    throw new Error('Sync server URL must use wss:// (or ws:// for loopback development).');
  }
  if (url.protocol === 'ws:' && !isLoopbackHost(url.hostname)) {
    throw new Error('Plaintext ws:// is allowed only for localhost or loopback addresses.');
  }
  return url.toString();
}

export async function waitForPeer(
  network,
  { timeoutMs = DEFAULT_CONNECT_TIMEOUT_MS, pollMs = 25 } = {},
) {
  await waitUntil(
    () => network.socket?.readyState === 1 && Boolean(network.remotePeerId),
    timeoutMs,
    pollMs,
    'Timed out connecting to the Automerge sync server.',
  );
}

export async function withTimeout(
  promise,
  label,
  timeoutMs = DEFAULT_CONNECT_TIMEOUT_MS,
) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(
          () => reject(new Error(`${label} timed out after ${timeoutMs} ms.`)),
          timeoutMs,
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

export async function waitForSocketDrain(
  network,
  { timeoutMs = DEFAULT_FLUSH_TIMEOUT_MS, quietMs = 250, pollMs = 25 } = {},
) {
  const deadline = Date.now() + timeoutMs;
  let quietSince = 0;
  while (Date.now() < deadline) {
    const socket = network.socket;
    if (socket?.readyState !== 1) {
      quietSince = 0;
    } else if ((socket.bufferedAmount ?? 0) === 0) {
      quietSince ||= Date.now();
      if (Date.now() - quietSince >= quietMs) return;
    } else {
      quietSince = 0;
    }
    await delay(pollMs);
  }
  throw new Error('Timed out flushing the Automerge update to the sync server.');
}

function isLoopbackHost(hostname) {
  const host = hostname.toLowerCase().replace(/^\[|\]$/g, '');
  if (host === 'localhost' || host === '::1') return true;
  const match = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(host);
  return Boolean(match && match.slice(1).every((part) => Number(part) <= 255) && match[1] === '127');
}

async function waitUntil(predicate, timeoutMs, pollMs, message) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await delay(pollMs);
  }
  throw new Error(message);
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
