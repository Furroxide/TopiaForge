import { test } from "node:test";
import assert from "node:assert/strict";
import dgram from "node:dgram";
import crypto from "node:crypto";
import { TurnAllocation } from "./turn-probe.mjs";

function attribute(type, value) {
  const header = Buffer.alloc(4);
  header.writeUInt16BE(type);
  header.writeUInt16BE(value.length, 2);
  return Buffer.concat([
    header,
    value,
    Buffer.alloc((4 - (value.length % 4)) % 4),
  ]);
}
function signedReply(transaction, key, before = [], after = []) {
  const body = Buffer.concat(before),
    header = Buffer.alloc(20);
  header.writeUInt16BE(0x0108);
  header.writeUInt16BE(body.length + 24, 2);
  header.writeUInt32BE(0x2112a442, 4);
  transaction.copy(header, 8);
  const digest = crypto
    .createHmac("sha1", key)
    .update(Buffer.concat([header, body]))
    .digest();
  const packet = Buffer.concat([
    header,
    body,
    attribute(0x0008, digest),
    ...after,
  ]);
  packet.writeUInt16BE(packet.length - 20, 2);
  return packet;
}
function fixtureAllocation() {
  const a = new TurnAllocation(
    { address: "127.0.0.1", port: 3478 },
    "test-user",
    "test-credential",
  );
  a.realm = Buffer.from("test-realm");
  a.nonce = Buffer.from("test-nonce");
  a.key = Buffer.alloc(16, 7);
  return a;
}

test("authenticated response validation accepts the independent RFC 5769 IPv4 response", async () => {
  // Published data vector and short-term key, RFC5769 section2.2.
  const packet = Buffer.from(
    "0101003c2112a442b7e7a701bc34d686fa87dfae8022000b7465737420766563746f7220002000080001a147e112a643000800142b91f599fd9e90c38c7489f92af9ba53f06be7d780280004c07d4c96",
    "hex",
  );
  const a = fixtureAllocation();
  let accepted = null;
  a.pending.set("b7e7a701bc34d686fa87dfae", {
    key: Buffer.from("VOkJxbRl1RmTxUk/WvJxBt"),
    resolve: (reply) => {
      accepted = reply;
    },
    reject() {},
  });
  try {
    a.socket.emit("message", packet, {
      address: a.server.address,
      port: a.server.port,
    });
    assert.equal(accepted?.type, 0x0101);
  } finally {
    await a.release();
  }
});

test("authenticated TURN requests ignore success replies without valid integrity", async () => {
  const a = fixtureAllocation();
  let accepted = false,
    transaction;
  a.socket.send = (bytes) => {
    transaction = Buffer.from(bytes.subarray(8, 20));
    const forged = Buffer.alloc(20);
    forged.writeUInt16BE(0x0108);
    forged.writeUInt32BE(0x2112a442, 4);
    transaction.copy(forged, 8);
    a.socket.emit("message", forged, {
      address: a.server.address,
      port: a.server.port,
    });
  };
  const pending = a
    .permit({ address: "127.0.0.1", port: 20000 })
    .then((value) => {
      accepted = true;
      return value;
    });
  try {
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(accepted, false);
    const damaged = signedReply(transaction, a.key);
    damaged[damaged.length - 1] ^= 1;
    a.socket.emit("message", damaged, {
      address: a.server.address,
      port: a.server.port,
    });
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(accepted, false);
    a.socket.emit("message", signedReply(transaction, a.key), {
      address: a.server.address,
      port: a.server.port,
    });
    assert.equal(await pending, true);
  } finally {
    await a.release();
    await pending.catch(() => {});
  }
});

test("attributes appended after integrity cannot replace authenticated TURN addresses", async () => {
  const a = fixtureAllocation(),
    genuine = Buffer.from("000100012112a443", "hex"),
    tampered = Buffer.from("0001ffffffffffff", "hex");
  a.socket.send = (bytes) => {
    const reply = signedReply(
      bytes.subarray(8, 20),
      a.key,
      [attribute(0x0016, genuine)],
      [attribute(0x0016, tampered)],
    );
    a.socket.emit("message", reply, {
      address: a.server.address,
      port: a.server.port,
    });
  };
  try {
    const reply = await a.request(0x0008, []);
    assert.deepEqual(reply.attributes.get(0x0016), genuine);
  } finally {
    await a.release();
  }
});

test("the inherited TURN key derivation matches the RFC 5769 long-term authentication vector", async () => {
  // Data vector: https://www.rfc-editor.org/rfc/rfc5769.html#section-2.4
  // This establishes protocol compatibility; it does not waive security review.
  const allocation = new TurnAllocation(
    { address: "127.0.0.1", port: 9 },
    "マトリックス",
    "TheMatrIX",
  );
  let calls = 0;
  allocation.request = async () => {
    if (calls++ === 0)
      return {
        type: 0x0113,
        attributes: new Map([
          [0x0014, Buffer.from("example.org")],
          [0x0015, Buffer.from("f//499k954d6OL34oL9FSTvy64sA")],
        ]),
      };
    throw new Error("stop after derivation");
  };
  try {
    await assert.rejects(() => allocation.allocate(), /stop after derivation/);
    const message = Buffer.from(
      "000100602112a44278ad3433c6ad72c029da412e00060012e3839ee38388e383aae38383e382afe382b900000015001c662f2f3439396b39353464364f4c33346f4c394653547679363473410014000b6578616d706c652e6f726700",
      "hex",
    );
    assert.equal(
      crypto.createHmac("sha1", allocation.key).update(message).digest("hex"),
      "f67024656dd64a3e02b8e0712e85c9a28ca89666",
    );
  } finally {
    await allocation.release();
  }
});
test("UDP probe ignores malformed and foreign frames and cleans pending requests on socket error", async () => {
  const server = dgram.createSocket("udp4");
  await new Promise((resolve) => server.bind(0, "127.0.0.1", resolve));
  const allocation = new TurnAllocation(
    { address: "127.0.0.1", port: server.address().port },
    "test-user",
    "test-credential",
  );
  try {
    await allocation.bind("127.0.0.1");
    server.send(
      Buffer.from([0x40, 0, 0, 20]),
      allocation.socket.address().port,
      "127.0.0.1",
    );
    const request = allocation.request(3, [], false);
    allocation.socket.emit("error", new Error("test-only socket failure"));
    await assert.rejects(request, /TURN request failed/);
    assert.equal(allocation.pending.size, 0);
    assert.equal(allocation.received.length, 0);
    await allocation.release();
    await allocation.release();
    assert.equal(allocation.closed, true);
  } finally {
    await allocation.release();
    server.close();
  }
});
