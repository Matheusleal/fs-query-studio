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
});
