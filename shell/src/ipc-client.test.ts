import assert from "node:assert/strict";
import { test } from "node:test";
import { FrameReader } from "./ipc-client";

// What is covered is reassembly, because it is the one place in this file where a wrong answer is
// silent rather than loud: a socket chooses its own chunk boundaries, and a reader that assumes one
// chunk is one frame corrupts a response instead of failing. The transport itself is exercised by
// the app; the arithmetic in between is not exercised by anything else.

function framed(...payloads: string[]): Buffer {
  return Buffer.concat(
    payloads.flatMap((payload) => {
      const body = Buffer.from(payload, "utf8");
      const header = Buffer.allocUnsafe(4);
      header.writeUInt32LE(body.length, 0);
      return [header, body];
    }),
  );
}

function collect(): { reader: FrameReader; frames: string[] } {
  const frames: string[] = [];
  return { reader: new FrameReader((frame) => frames.push(frame.toString("utf8"))), frames };
}

test("a frame split across chunks is delivered once, whole", () => {
  const { reader, frames } = collect();
  const wire = framed('{"id":1}');

  // Split mid-header, the worst boundary: fewer than four bytes means the length is not even
  // readable yet, so nothing may be emitted and nothing may be discarded.
  reader.push(wire.subarray(0, 2));
  assert.deepEqual(frames, []);

  reader.push(wire.subarray(2));
  assert.deepEqual(frames, ['{"id":1}']);
});

test("two frames arriving in one chunk are delivered as two", () => {
  const { reader, frames } = collect();

  reader.push(framed('{"id":1}', '{"id":2}'));

  assert.deepEqual(frames, ['{"id":1}', '{"id":2}']);
});

test("a string chunk is read as UTF-8 rather than dropped", () => {
  // Unreachable while no encoding is set on the socket, which is the case today. It is pinned
  // because `@types/node` 25 widened the `data` listener to `string | Buffer`, and the branch that
  // answers that widening has to actually work if it is ever reached.
  const { reader, frames } = collect();

  // Written out byte by byte rather than decoded from a Buffer, so the test states the wire it
  // means: a 2-byte little-endian length, then the payload.
  reader.push(String.fromCharCode(2, 0, 0, 0) + "hi");

  assert.deepEqual(frames, ["hi"]);
});

test("a length past the cap throws instead of buffering forever", () => {
  const { reader } = collect();
  const header = Buffer.allocUnsafe(4);
  header.writeUInt32LE(64 * 1024 * 1024 + 1, 0);

  assert.throws(() => reader.push(header), /exceeds the limit/);
});
