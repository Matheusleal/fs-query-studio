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
