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
    const frame = written.find((f) => f.id === "host-1")!;
    // Every reply frame carries BOTH keys — a missing `error`/`result` key
    // throws `JsonException` in the worker's `routeResponse` and kills it.
    expect("error" in frame && "result" in frame).toBe(true);
    expect(frame).toEqual({ id: "host-1", result: { secret: "hunter2" }, error: null });
  });

  it("replies host/unhandled when no handler is set", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });
    client.send({ id: "warm", method: "worker/health", params: {} });

    emitHostRequest(child, { id: "host-2", method: "secret/get", params: {} });
    await flush();

    const written = child.rawStdinLines.map((l) => JSON.parse(l));
    const frame = written.find((f) => f.id === "host-2")!;
    expect("error" in frame && "result" in frame).toBe(true);
    expect(frame.result).toBeNull();
    expect(frame.error?.code).toBe("host/unhandled");
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
    const f = written.find((x) => x.id === "host-3")!;
    expect("error" in f && "result" in f).toBe(true);
    expect(f).toEqual({ id: "host-3", result: null, error: { code: "host/threw", message: "boom" } });
  });

  it("emits both keys when the handler returns { error }", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });
    client.setHostRequestHandler(async () => ({
      error: { code: "host/secretReadFailed", message: "keychain locked" },
    }));
    client.send({ id: "warm", method: "worker/health", params: {} });

    emitHostRequest(child, { id: "host-4", method: "secret/get", params: {} });
    await flush();

    const written = child.rawStdinLines.map((l) => JSON.parse(l));
    const f = written.find((x) => x.id === "host-4")!;
    expect("error" in f && "result" in f).toBe(true);
    expect(f).toEqual({
      id: "host-4",
      result: null,
      error: { code: "host/secretReadFailed", message: "keychain locked" },
    });
  });

  it("serialises a non-Error throw with String(err), not undefined", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });
    client.setHostRequestHandler(async () => {
      throw "plain string";
    });
    client.send({ id: "warm", method: "worker/health", params: {} });

    emitHostRequest(child, { id: "host-5", method: "secret/get", params: {} });
    await flush();

    const written = child.rawStdinLines.map((l) => JSON.parse(l));
    const f = written.find((x) => x.id === "host-5")!;
    expect("error" in f && "result" in f).toBe(true);
    expect(f.error.message).toBe("plain string");
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

describe("WorkerClient logging and stderr tracking", () => {
  it("pipes stderr chunks into log callback with [Worker stderr] prefix", async () => {
    const logs: string[] = [];
    const child = createFakeChild();
    const client = new WorkerClient({
      spawn: () => child.process,
      log: (l) => logs.push(l),
    });

    client.send({ id: "log-1", method: "worker/health", params: {} });
    await flush();

    child.process.stderr.emit("data", Buffer.from("First error line\nSecond error line\n"));
    await flush();

    expect(logs).toContain("[Worker] Started process with PID 4242.");
    expect(logs).toContain("[Host -> Worker] worker/health (log-1)");
    expect(logs).toContain("[Worker stderr] First error line");
    expect(logs).toContain("[Worker stderr] Second error line");

    child.reply({ id: "log-1", result: { ok: true } });
    await flush();
    expect(logs).toContain("[Worker -> Host] Result on worker/health (log-1)");
  });

  it("logs errors returned in response frames", async () => {
    const logs: string[] = [];
    const child = createFakeChild();
    const client = new WorkerClient({
      spawn: () => child.process,
      log: (l) => logs.push(l),
    });

    client.send({ id: "err-1", method: "test/method", params: {} });
    await flush();

    child.reply({
      id: "err-1",
      result: null,
      error: { code: "test/failed", message: "something blew up" },
    });
    await flush();

    expect(logs).toContain(
      "[Worker -> Host] Error on test/method (err-1): [test/failed] something blew up",
    );
  });
});

