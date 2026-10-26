import { describe, it, expect } from "vitest";
import { PassThrough } from "node:stream";
import { writeFrame, readFrames } from "./ndjson";

describe("writeFrame / readFrames", () => {
  it("round-trips multiple frames", async () => {
    const stream = new PassThrough();
    const got: unknown[] = [];
    let ended = false;
    readFrames(stream, { onFrame: (f) => got.push(f), onEnd: () => (ended = true) });

    await writeFrame(stream, { id: "1", method: "a", params: {} });
    await writeFrame(stream, { id: "2", method: "b", params: { x: 1 } });
    stream.end();
    await new Promise((r) => setTimeout(r, 10));

    expect(got).toEqual([
      { id: "1", method: "a", params: {} },
      { id: "2", method: "b", params: { x: 1 } },
    ]);
    expect(ended).toBe(true);
  });

  it("reassembles a frame split across two chunks", async () => {
    const stream = new PassThrough();
    const got: unknown[] = [];
    readFrames(stream, { onFrame: (f) => got.push(f), onEnd: () => {} });

    stream.write('{"id":"1",');
    stream.write('"method":"a","params":{}}\n');
    await new Promise((r) => setTimeout(r, 10));

    expect(got).toEqual([{ id: "1", method: "a", params: {} }]);
  });

  it("surfaces an unparseable line without throwing", async () => {
    const stream = new PassThrough();
    const got: unknown[] = [];
    readFrames(stream, { onFrame: (f) => got.push(f), onEnd: () => {} });

    stream.write("not json\n");
    await new Promise((r) => setTimeout(r, 10));

    expect(got).toEqual([{ __parseError: true, raw: "not json" }]);
  });
});
