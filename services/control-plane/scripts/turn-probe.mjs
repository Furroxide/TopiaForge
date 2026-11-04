// Shared bounded TURN probe transport. No credentials or endpoint addresses are logged.
import crypto from "node:crypto";
import dgram from "node:dgram";
const MAGIC = 0x2112a442;
function stunMessage(type, attributes, key) {
  const transaction = crypto.randomBytes(12);
  const encode = (list) =>
    Buffer.concat(
      list.map(([attrType, value]) => {
        const header = Buffer.alloc(4);
        header.writeUInt16BE(attrType, 0);
        header.writeUInt16BE(value.length, 2);
        const pad = Buffer.alloc((4 - (value.length % 4)) % 4);
        return Buffer.concat([header, value, pad]);
      }),
    );
  let body = encode(attributes);
  const header = (length) => {
    const h = Buffer.alloc(20);
    h.writeUInt16BE(type, 0);
    h.writeUInt16BE(length, 2);
    h.writeUInt32BE(MAGIC, 4);
    transaction.copy(h, 8);
    return h;
  };
  if (key) {
    const forHmac = Buffer.concat([header(body.length + 24), body]);
    const integrity = crypto.createHmac("sha1", key).update(forHmac).digest();
    body = Buffer.concat([body, encode([[0x0008, integrity]])]);
  }
  return { bytes: Buffer.concat([header(body.length), body]), transaction };
}
function parseStun(buffer) {
  if (
    buffer.length < 20 ||
    (buffer[0] & 0xc0) !== 0 ||
    buffer.readUInt32BE(4) !== MAGIC
  )
    return null;
  const type = buffer.readUInt16BE(0),
    length = buffer.readUInt16BE(2);
  if (length % 4 || length + 20 !== buffer.length) return null;
  const attributes = new Map();
  let integrityOffset = null,
    integrity = null;
  for (let at = 20; at + 4 <= 20 + length; ) {
    const attrType = buffer.readUInt16BE(at),
      size = buffer.readUInt16BE(at + 2);
    if (at + 4 + size > 20 + length) return null;
    if (integrityOffset === null) {
      if (attributes.has(attrType)) return null;
      attributes.set(attrType, buffer.subarray(at + 4, at + 4 + size));
      if (attrType === 0x0008) {
        if (size !== 20) return null;
        integrityOffset = at;
        integrity = buffer.subarray(at + 4, at + 24);
      }
    } else if (attrType === 0x0008) return null;
    // RFC8489: attributes after MESSAGE-INTEGRITY cannot affect authenticated
    // semantics. In particular, never overwrite XOR-RELAYED-ADDRESS after it.
    at += 4 + size + ((4 - (size % 4)) % 4);
  }
  return {
    type,
    transaction: buffer.subarray(8, 20),
    attributes,
    integrityOffset,
    integrity,
  };
}
function authenticResponse(packet, parsed, key) {
  if (parsed.integrityOffset === null || parsed.integrity === null)
    return false;
  const authenticated = Buffer.from(packet.subarray(0, parsed.integrityOffset));
  authenticated.writeUInt16BE(parsed.integrityOffset + 4, 2);
  const expected = crypto
    .createHmac("sha1", key)
    .update(authenticated)
    .digest();
  return crypto.timingSafeEqual(expected, parsed.integrity);
}
function xorAddress(value, transaction) {
  if (!value || value.length < 8) throw new Error("Malformed relay address");
  const family = value[1];
  const port = value.readUInt16BE(2) ^ (MAGIC >>> 16);
  if (family === 1) {
    const raw = value.readUInt32BE(4) ^ MAGIC;
    return {
      family: 4,
      address: [
        raw >>> 24,
        (raw >>> 16) & 255,
        (raw >>> 8) & 255,
        raw & 255,
      ].join("."),
      port,
    };
  }
  if (family !== 2 || value.length !== 20)
    throw new Error("Malformed relay address");
  const mask = Buffer.concat([
    Buffer.from([0x21, 0x12, 0xa4, 0x42]),
    transaction,
  ]);
  const bytes = Buffer.from(value.subarray(4, 20)).map((b, i) => b ^ mask[i]);
  return {
    family: 6,
    address: Buffer.from(bytes).toString("hex").match(/.{4}/g).join(":"),
    port,
  };
}
function xorPeer(address, port) {
  const value = Buffer.alloc(8);
  value[1] = 1;
  value.writeUInt16BE(port ^ (MAGIC >>> 16), 2);
  const raw = address
    .split(".")
    .reduce((n, part) => ((n << 8) | Number(part)) >>> 0, 0);
  value.writeUInt32BE((raw ^ MAGIC) >>> 0, 4);
  return value;
}

export class TurnAllocation {
  constructor(server, username, credential) {
    this.server = server;
    this.username = username;
    this.credential = credential;
    this.socket = dgram.createSocket("udp4");
    this.pending = new Map();
    this.received = [];
    this.bound = false;
    this.closed = false;
    this.socket.on("error", () => {
      for (const pending of this.pending.values())
        pending.reject(new Error("Relay socket failed"));
    });
    this.socket.on("message", (message, sender) => {
      if (
        sender.address !== this.server.address ||
        sender.port !== this.server.port ||
        this.received.length >= 64
      )
        return;
      if (message.length >= 4 && message[0] >= 0x40 && message[0] <= 0x4f) {
        if (message.readUInt16BE(2) > message.length - 4) return;
        this.received.push({
          kind: "channel",
          channel: message.readUInt16BE(0),
          data: message.subarray(4, 4 + message.readUInt16BE(2)).toString(),
        });
        return;
      }
      const parsed = parseStun(message);
      if (!parsed) return;
      if (parsed.type === 0x0017) {
        // Data indication
        this.received.push({
          kind: "indication",
          data: parsed.attributes.get(0x0013)?.toString() ?? "",
        });
        return;
      }
      const waiter = this.pending.get(parsed.transaction.toString("hex"));
      if (
        waiter &&
        (!waiter.key || authenticResponse(message, parsed, waiter.key))
      )
        waiter.resolve(parsed);
    });
  }
  bind(address = "0.0.0.0") {
    return new Promise((resolve, reject) => {
      this.socket.once("error", reject);
      this.socket.bind(0, address, () => {
        this.bound = true;
        this.socket.removeListener("error", reject);
        resolve();
      });
    });
  }
  request(type, attributes, signed = true) {
    const withAuth =
      signed && this.key
        ? [
            ...attributes,
            [0x0006, Buffer.from(this.username)],
            [0x0014, this.realm],
            [0x0015, this.nonce],
          ]
        : attributes;
    const { bytes, transaction } = stunMessage(
      type,
      withAuth,
      signed ? this.key : undefined,
    );
    return new Promise((resolve, reject) => {
      const id = transaction.toString("hex");
      let tries = 0;
      const finish = (callback, value) => {
        clearInterval(timer);
        this.pending.delete(id);
        callback(value);
      };
      const fail = () => finish(reject, new Error("TURN request failed"));
      const send = () => {
        try {
          this.socket.send(
            bytes,
            this.server.port,
            this.server.address,
            (error) => {
              if (error) fail();
            },
          );
        } catch {
          fail();
        }
      };
      const timer = setInterval(() => {
        if (++tries > 6) fail();
        else send();
      }, 500);
      this.pending.set(id, {
        resolve: (parsed) => finish(resolve, parsed),
        reject: fail,
        key: signed && this.key ? Buffer.from(this.key) : null,
      });
      send();
    });
  }
  async allocate() {
    const transport = Buffer.from([17, 0, 0, 0]);
    const first = await this.request(0x0003, [[0x0019, transport]], false);
    if (first.type !== 0x0113)
      throw new Error(
        `expected 401 to the first Allocate, got type 0x${first.type.toString(16)}`,
      );
    this.realm = first.attributes.get(0x0014);
    this.nonce = first.attributes.get(0x0015);
    this.key = crypto
      .createHash("md5")
      .update(`${this.username}:${this.realm.toString()}:${this.credential}`)
      .digest();
    const answer = await this.request(0x0003, [
      [0x0019, transport],
      [0x000d, Buffer.from([0, 0, 0x02, 0x58])],
    ]);
    if (answer.type !== 0x0103)
      throw new Error(
        `Allocate failed, type 0x${answer.type.toString(16)} code ${answer.attributes.get(0x0009)?.readUInt8(2)}${answer.attributes.get(0x0009)?.readUInt8(3)}`,
      );
    this.relayed = xorAddress(
      answer.attributes.get(0x0016),
      answer.transaction,
    );
    return this.relayed;
  }
  async permit(peer) {
    const answer = await this.request(0x0008, [
      [0x0012, xorPeer(peer.address, peer.port)],
    ]);
    return answer.type === 0x0108;
  }
  async channel(number, peer) {
    const value = Buffer.alloc(4);
    value.writeUInt16BE(number, 0);
    const answer = await this.request(0x0009, [
      [0x000c, value],
      [0x0012, xorPeer(peer.address, peer.port)],
    ]);
    return answer.type === 0x0109;
  }
  sendChannel(number, text) {
    const data = Buffer.from(text);
    const header = Buffer.alloc(4);
    header.writeUInt16BE(number, 0);
    header.writeUInt16BE(data.length, 2);
    const pad = Buffer.alloc((4 - (data.length % 4)) % 4);
    this.socket.send(
      Buffer.concat([header, data, pad]),
      this.server.port,
      this.server.address,
    );
  }
  async release() {
    if (this.closed) return;
    try {
      if (this.bound && this.key)
        await this.request(0x0004, [[0x000d, Buffer.alloc(4)]]);
    } catch {
      /* best effort */
    } finally {
      this.closed = true;
      for (const pending of [...this.pending.values()])
        pending.reject(new Error("Relay closed"));
      try {
        this.socket.close();
      } catch {
        /* Never bound. */
      }
    }
  }
}
