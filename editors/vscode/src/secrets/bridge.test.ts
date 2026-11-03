import { describe, it, expect, vi } from "vitest";
import { registerSecretBridge } from "./bridge";

function harness(secretsImpl: { get: (k: string) => Promise<string | undefined> }) {
  let handler!: (m: string, p: unknown) => Promise<{ result?: unknown; error?: { code: string; message: string } }>;
  registerSecretBridge({
    client: { setHostRequestHandler: (fn) => (handler = fn) },
    secrets: secretsImpl,
  });
  return (m: string, p: unknown) => handler(m, p);
}

describe("registerSecretBridge", () => {
  it("answers secret/get from the store", async () => {
    const call = harness({ get: async (k) => (k === "fqs-conn-1" ? "hunter2" : undefined) });
    expect(await call("secret/get", { reference: "fqs-conn-1" })).toEqual({ result: { secret: "hunter2" } });
  });

  it("returns { secret: null } for a missing key", async () => {
    const call = harness({ get: async () => undefined });
    expect(await call("secret/get", { reference: "nope" })).toEqual({ result: { secret: null } });
  });

  it("rejects an unknown method", async () => {
    const call = harness({ get: async () => undefined });
    expect((await call("secret/set", {})).error?.code).toBe("host/unknownMethod");
  });

  it("rejects a non-string reference", async () => {
    const call = harness({ get: async () => undefined });
    expect((await call("secret/get", { reference: 5 })).error?.code).toBe("host/badParams");
  });

  it("maps a throwing store to an error reply", async () => {
    const call = harness({
      get: async () => {
        throw new Error("keychain locked");
      },
    });
    expect(await call("secret/get", { reference: "x" })).toEqual({
      error: { code: "host/secretReadFailed", message: "keychain locked" },
    });
  });

  it("maps a non-Error throw to an error reply via String(err)", async () => {
    // A non-Error throw leaves `(err as Error).message` undefined → the key
    // is dropped by JSON.stringify → the worker's non-optional
    // `WorkerErrorInfo.Message` throws `JsonException` and exits.
    const call = harness({
      get: async () => {
        throw "locked";
      },
    });
    expect(await call("secret/get", { reference: "x" })).toEqual({
      error: { code: "host/secretReadFailed", message: "locked" },
    });
  });

  it("handles entityMapping/status notifications and calls callback", async () => {
    let handler!: (m: string, p: unknown) => Promise<{ result?: unknown; error?: { code: string; message: string } }>;
    const statusCalls: string[] = [];
    registerSecretBridge({
      client: { setHostRequestHandler: (fn) => (handler = fn) },
      secrets: { get: async () => undefined },
      onEntityMappingStatus: (st) => statusCalls.push(st),
    });

    const resStarted = await handler("entityMapping/status", { status: "started" });
    expect(resStarted).toEqual({ result: { acknowledged: true } });
    expect(statusCalls).toEqual(["started"]);

    const resCompleted = await handler("entityMapping/status", { status: "completed" });
    expect(resCompleted).toEqual({ result: { acknowledged: true } });
    expect(statusCalls).toEqual(["started", "completed"]);

    const resBad = await handler("entityMapping/status", { status: "invalid" });
    expect(resBad.error?.code).toBe("host/badParams");
  });

  it("handles staging/progress notifications and calls onStagingProgress callback", async () => {
    let handler!: (m: string, p: unknown) => Promise<{ result?: unknown; error?: { code: string; message: string } }>;
    const progressEvents: unknown[] = [];
    registerSecretBridge({
      client: { setHostRequestHandler: (fn) => (handler = fn) },
      secrets: { get: async () => undefined },
      onStagingProgress: (ev) => progressEvents.push(ev),
    });

    const resReading = await handler("staging/progress", {
      phase: "Reading",
      tableName: "Customers",
      currentIndex: 1,
      totalCount: 3,
      message: "Reading customers.csv...",
    });
    expect(resReading).toEqual({ result: { acknowledged: true } });

    const resWriting = await handler("staging/progress", {
      phase: "Writing",
      tableName: "Customers",
      rowCount: 5000,
      currentIndex: 1,
      totalCount: 3,
    });
    expect(resWriting).toEqual({ result: { acknowledged: true } });

    expect(progressEvents).toHaveLength(2);
    expect(progressEvents[0]).toMatchObject({ phase: "Reading", tableName: "Customers" });
    expect(progressEvents[1]).toMatchObject({ phase: "Writing", rowCount: 5000 });

    const resBad = await handler("staging/progress", {});
    expect(resBad.error?.code).toBe("host/badParams");
  });
});
