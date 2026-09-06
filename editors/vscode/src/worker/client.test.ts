import { describe, it, expect } from "vitest";
import { WorkerClient } from "./client";
import { createFakeChild } from "../../test/fake-child";
import type { WorkerResponseFrame } from "../protocol";

const flush = () => new Promise((r) => setTimeout(r, 10));

// The fake child's `reply` is typed for response frames; a host-bound request
// frame carries a `method`, so route it through the same stdout writer.
const emitHostRequest = (
  child: ReturnType<typeof createFakeChild>,
  frame: { id: string; method: string; params: unknown },
) => child.reply(frame as unknown as WorkerResponseFrame);

describe("WorkerClient send + correlation", () => {
  it("resolves a send with the matching response", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });

    const pending = client.send({ id: "req-1", method: "worker/health", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    child.reply({ id: "req-1", result: { version: "0.1.0", capabilities: [] } });

    const response = await pending;
    expect(response).toEqual({ id: "req-1", result: { version: "0.1.0", capabilities: [] } });
  });

  it("matches responses that arrive out of order", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });

    const a = client.send({ id: "a", method: "m", params: {} });
    const b = client.send({ id: "b", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    child.reply({ id: "b", result: "B" });
    child.reply({ id: "a", result: "A" });

    expect((await a).result).toBe("A");
    expect((await b).result).toBe("B");
  });

  it("writes each request as one whole NDJSON line", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });

    client.send({ id: "a", method: "m", params: {} });
    client.send({ id: "b", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 10));

    expect(child.rawStdinLines).toHaveLength(2);
    expect(JSON.parse(child.rawStdinLines[0]).id).toBe("a");
    expect(JSON.parse(child.rawStdinLines[1]).id).toBe("b");
  });

  it("spawns the process only once for multiple sends", async () => {
    let spawnCount = 0;
    const child = createFakeChild();
    const client = new WorkerClient({
      spawn: () => {
        spawnCount++;
        return child.process;
      },
    });
    client.send({ id: "a", method: "m", params: {} });
    client.send({ id: "b", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    expect(spawnCount).toBe(1);
  });
});

describe("WorkerClient crash + restart", () => {
  it("settles pending requests with worker/crashed on exit", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });

    const pending = client.send({ id: "x", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    child.emitExit();

    const response = await pending;
    expect(response).toEqual({
      id: "x",
      error: { code: "worker/crashed", message: "The worker process exited before responding." },
    });
  });

  it("spawns a fresh process on the next send after a crash", async () => {
    let spawnCount = 0;
    const children = [createFakeChild(), createFakeChild()];
    const client = new WorkerClient({
      spawn: () => children[spawnCount++].process,
    });

    client.send({ id: "a", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    children[0].emitExit();
    await new Promise((r) => setTimeout(r, 5));

    client.send({ id: "b", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    expect(spawnCount).toBe(2);
  });

  it("invokes onCrash once", async () => {
    const child = createFakeChild();
    let crashes = 0;
    const client = new WorkerClient({ spawn: () => child.process, onCrash: () => crashes++ });
    client.send({ id: "a", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    child.emitExit();
    await new Promise((r) => setTimeout(r, 5));
    expect(crashes).toBe(1);
  });
});

describe("WorkerClient cancel", () => {
  it("does not kill the process when the request settles within the grace period", async () => {
    const child = createFakeChild();
    let killed = false;
    const proc = { ...child.process, kill: () => (killed = true) };
    const client = new WorkerClient({ spawn: () => proc });

    const pending = client.send({ id: "run-1", method: "execution/evaluate", params: {} });
    await new Promise((r) => setTimeout(r, 5));

    const cancelling = client.cancel("run-1", 200);
    child.reply({ id: "run-1", error: { code: "execution/cancelled", message: "Cancelled." } });
    await cancelling;
    await pending;

    expect(killed).toBe(false);
  });

  it("kills the process and force-settles when the grace period elapses", async () => {
    const child = createFakeChild();
    let killed = false;
    const proc = { ...child.process, kill: () => (killed = true) };
    const client = new WorkerClient({ spawn: () => proc });

    const pending = client.send({ id: "run-2", method: "execution/evaluate", params: {} });
    await new Promise((r) => setTimeout(r, 5));

    await client.cancel("run-2", 20);
    const response = await pending;

    expect(killed).toBe(true);
    expect(response.error?.code).toBe("execution/forciblyStopped");
  });

  it("sends an execution/cancel frame", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });
    client.send({ id: "run-3", method: "execution/evaluate", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    void client.cancel("run-3", 20);
    await new Promise((r) => setTimeout(r, 5));
    const methods = child.sentFrames.map((f) => (f as { method: string }).method);
    expect(methods).toContain("execution/cancel");
  });
});

describe("reverse channel (host-bound requests)", () => {
  it("answers a host request with the handler result", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });
    client.setHostRequestHandler(async (method, params) => {
      expect(method).toBe("secret/get");
      expect(params).toEqual({ reference: "fqs-conn-1" });
      return { result: { secret: "hunter2" } };
    });
    client.send({ id: "warm", method: "worker/health", params: {} }); // force ensureProcess

    emitHostRequest(child, {
      id: "host-1",
      method: "secret/get",
      params: { reference: "fqs-conn-1" },
    });
    await flush();

    const written = child.rawStdinLines.map((l) => JSON.parse(l));
    expect(written).toContainEqual({ id: "host-1", result: { secret: "hunter2" } });
  });

  it("replies host/unhandled when no handler is set", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });
    client.send({ id: "warm", method: "worker/health", params: {} });

    emitHostRequest(child, { id: "host-2", method: "secret/get", params: {} });
    await flush();

    const written = child.rawStdinLines.map((l) => JSON.parse(l));
    expect(written.find((f) => f.id === "host-2")?.error?.code).toBe("host/unhandled");
  });

  it("replies host/threw when the handler rejects", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });
    client.setHostRequestHandler(async () => {
      throw new Error("boom");
    });
    client.send({ id: "warm", method: "worker/health", params: {} });

    emitHostRequest(child, { id: "host-3", method: "secret/get", params: {} });
    await flush();

    const written = child.rawStdinLines.map((l) => JSON.parse(l));
    const f = written.find((x) => x.id === "host-3");
    expect(f?.error).toEqual({ code: "host/threw", message: "boom" });
  });

  it("still correlates a normal response frame by id", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });
    const p = client.send({ id: "r1", method: "worker/health", params: {} });
    await flush();
    child.reply({ id: "r1", result: { version: "0.1.0" } });
    expect((await p).result).toEqual({ version: "0.1.0" });
  });
});
