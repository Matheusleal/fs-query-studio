import { describe, it, expect } from "vitest";
import {
  deserializeFsqnb,
  serializeFsqnb,
  FsqnbError,
  FsqnbVersionError,
  type FsqnbDocument,
} from "./fsqnb";

describe("deserializeFsqnb", () => {
  it("parses a minimal document", () => {
    const doc = deserializeFsqnb(
      JSON.stringify({
        version: 1,
        cells: [{ kind: "code", language: "fsharp", value: "1 + 1" }],
        metadata: {},
      }),
    );
    expect(doc.cells).toEqual([{ kind: "code", language: "fsharp", value: "1 + 1" }]);
    expect(doc.metadata).toEqual({});
  });

  it("defaults a missing cell language to fsharp", () => {
    const doc = deserializeFsqnb(
      JSON.stringify({ version: 1, cells: [{ kind: "code", value: "x" }] }),
    );
    expect(doc.cells[0].language).toBe("fsharp");
  });

  it("normalises a markdown cell", () => {
    const doc = deserializeFsqnb(
      JSON.stringify({ version: 1, cells: [{ kind: "markdown", value: "# Hi" }] }),
    );
    expect(doc.cells[0]).toEqual({ kind: "markdown", language: "markdown", value: "# Hi" });
  });

  it("treats empty / whitespace input as an empty notebook", () => {
    expect(deserializeFsqnb("")).toEqual({ version: 1, cells: [], metadata: {} });
    expect(deserializeFsqnb("   \n ")).toEqual({ version: 1, cells: [], metadata: {} });
  });

  it("throws FsqnbVersionError for a future version, and carries the found value", () => {
    const err = (() => {
      try {
        deserializeFsqnb(JSON.stringify({ version: 2, cells: [] }));
      } catch (e) {
        return e as FsqnbVersionError;
      }
    })();
    expect(err).toBeInstanceOf(FsqnbVersionError);
    expect(err!.found).toBe(2);
    expect(err!.userMessage).toMatch(/version 1/);
  });

  it("throws FsqnbError for non-JSON", () => {
    expect(() => deserializeFsqnb("not json")).toThrow(FsqnbError);
  });

  it("throws FsqnbError when the root is not an object", () => {
    expect(() => deserializeFsqnb("[]")).toThrow(FsqnbError);
  });
});

describe("serializeFsqnb", () => {
  it("is stable across decode → encode → decode → encode", () => {
    const src: FsqnbDocument = {
      version: 1,
      cells: [
        { kind: "markdown", language: "markdown", value: "# Title" },
        { kind: "code", language: "fsharp", value: 'Connections.sql "db" "select 1"' },
      ],
      metadata: {},
    };
    const once = serializeFsqnb(src);
    const twice = serializeFsqnb(deserializeFsqnb(once));
    expect(twice).toBe(once);
  });

  it("drops language from a markdown cell and keeps it on a code cell", () => {
    const text = serializeFsqnb({
      version: 1,
      cells: [
        { kind: "markdown", language: "markdown", value: "m" },
        { kind: "code", language: "fsharp", value: "c" },
      ],
      metadata: {},
    });
    const parsed = JSON.parse(text);
    expect(parsed.cells[0]).toEqual({ kind: "markdown", value: "m" });
    expect(parsed.cells[1]).toEqual({ kind: "code", language: "fsharp", value: "c" });
  });

  it("ends with a trailing newline", () => {
    expect(serializeFsqnb({ version: 1, cells: [], metadata: {} }).endsWith("\n")).toBe(true);
  });
});
