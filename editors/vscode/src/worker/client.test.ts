import { describe, it, expect } from "vitest";
import { WorkerClient } from "./client";
import { createFakeChild } from "../../test/fake-child";

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
