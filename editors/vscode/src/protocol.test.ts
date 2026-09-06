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

import { isHostRequestFrame, type ConnectionDefinition } from "./protocol";

describe("F5 connection wire types", () => {
  it("decodes a { Case } connector kind and re-encodes it", () => {
    const def = {
      id: "1",
      name: "Sales",
      kind: { Case: "Sqlite" },
      configuration: { filePath: "/x.db" },
      secretReference: null,
      isTrusted: false,
    } as unknown as ConnectionDefinition;
    expect(readUnionTag(def.kind)).toBe("Sqlite");
    expect(makeUnionTag("PostgreSql")).toEqual({ Case: "PostgreSql" });
  });

  it("recognises an inbound host-bound request frame", () => {
    expect(isHostRequestFrame({ id: "host-abc", method: "secret/get", params: {} })).toBe(true);
    expect(isHostRequestFrame({ id: "x", result: { secret: "y" } })).toBe(false);
    expect(isHostRequestFrame({ id: "x", method: "" })).toBe(false);
    expect(isHostRequestFrame(null)).toBe(false);
  });
});
