import { describe, it, expect } from "vitest";
import { isWorkerVersionSupported, newRequestId } from "./protocol";

describe("isWorkerVersionSupported", () => {
  it("accepts a v0 worker", () => {
    expect(isWorkerVersionSupported("0.0.0")).toBe(true);
  });
  it("accepts a v1 worker", () => {
    expect(isWorkerVersionSupported("1.4.2.0")).toBe(true);
  });
  it("rejects a v2 worker", () => {
    expect(isWorkerVersionSupported("2.0.0")).toBe(false);
  });
  it("rejects a non-numeric version", () => {
    expect(isWorkerVersionSupported("")).toBe(false);
    expect(isWorkerVersionSupported("nope")).toBe(false);
  });
});

describe("newRequestId", () => {
  it("returns distinct values", () => {
    expect(newRequestId()).not.toEqual(newRequestId());
  });
});

import {
  readUnionTag,
  makeUnionTag,
  SESSION_MODE_PERSISTENT,
  readSeverity,
} from "./protocol";

describe("union tag helpers", () => {
  it("reads a Case tag from a union-shaped value", () => {
    expect(readUnionTag({ Case: "Table" })).toBe("Table");
  });

  it("returns undefined for values that are not union-shaped", () => {
    expect(readUnionTag(null)).toBeUndefined();
    expect(readUnionTag(undefined)).toBeUndefined();
    expect(readUnionTag({})).toBeUndefined();
    expect(readUnionTag("Table")).toBeUndefined();
    expect(readUnionTag({ Case: 3 })).toBeUndefined();
  });

  it("round-trips through make/read", () => {
    expect(readUnionTag(makeUnionTag("Persistent"))).toBe("Persistent");
  });

  it("SESSION_MODE_PERSISTENT serializes to the worker's adjacent-tag shape", () => {
    expect(JSON.stringify(SESSION_MODE_PERSISTENT)).toBe('{"Case":"Persistent"}');
  });
});

describe("readSeverity", () => {
  it("decodes each F# DU severity tag", () => {
    expect(readSeverity({ Case: "Error" })).toBe("Error");
    expect(readSeverity({ Case: "Warning" })).toBe("Warning");
    expect(readSeverity({ Case: "Information" })).toBe("Information");
  });

  it("falls back to Error for anything unrecognized", () => {
    expect(readSeverity({ Case: "Nonsense" })).toBe("Error");
    expect(readSeverity(null)).toBe("Error");
    expect(readSeverity(undefined)).toBe("Error");
    expect(readSeverity("Warning")).toBe("Error"); // bare string, not a tag
    expect(readSeverity({})).toBe("Error");
  });
});
