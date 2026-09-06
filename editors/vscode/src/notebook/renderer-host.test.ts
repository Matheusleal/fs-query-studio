import { describe, it, expect } from "vitest";
import { registerRendererHost, type RendererHostDeps, type WorkerSendLike } from "./renderer-host";
import type { WorkerResponseFrame } from "../protocol";
import type { HostToRendererMessage } from "../renderer/types";

function harness(overrides: Partial<RendererHostDeps> = {}) {
  let listener: ((e: { editor: unknown; message: unknown }) => unknown) | undefined;
  const posted: Array<{ message: HostToRendererMessage; editor: unknown }> = [];
  const messaging = {
    onDidReceiveMessage(fn: (e: { editor: unknown; message: unknown }) => unknown) {
      listener = fn;
      return { dispose: () => (listener = undefined) };
    },
    postMessage(message: HostToRendererMessage, editor?: unknown) {
      posted.push({ message, editor });
      return Promise.resolve(true);
    },
  };
  const deps: RendererHostDeps = {
    messaging,
    getWorker: async () => ({ send: async (r) => ({ id: r.id, result: {} }) }) as WorkerSendLike,
    pickSaveFile: async () => "/tmp/results.csv",
    log: () => {},
    ...overrides,
  };
  const host = registerRendererHost(deps);
  return {
    host,
    posted,
    send: (message: unknown, editor: unknown = { id: "ed1" }) => listener!({ editor, message }),
  };
}

describe("registerRendererHost — loadMore", () => {
  it("calls results/page with the token and posts the returned rows", async () => {
    const calls: Array<{ method: string; params: unknown }> = [];
    const h = harness({
      getWorker: async () => ({
        send: async (r) => {
          calls.push({ method: r.method, params: r.params });
          return {
            id: r.id,
            result: { rows: [[4], [5]], isTruncated: true, continuationToken: "next" },
          } as WorkerResponseFrame;
        },
      }),
    });
    await h.send({ type: "loadMore", outputId: "o1", documentUri: "file:///q.fsqnb", token: "t0" });

    expect(calls[0].method).toBe("results/page");
    expect(calls[0].params).toEqual({ sessionId: "file:///q.fsqnb", continuationToken: "t0" });
    expect(h.posted[0].message).toEqual({
      type: "rows",
      outputId: "o1",
      rows: [[4], [5]],
      truncated: true,
      nextToken: "next",
    });
    expect(h.posted[0].editor).toEqual({ id: "ed1" });
  });

  it("maps results/unknownToken to a friendly loadMore error", async () => {
    const h = harness({
      getWorker: async () => ({
        send: async (r) => ({
          id: r.id,
          error: { code: "results/unknownToken", message: "expired" },
        }),
      }),
    });
    await h.send({ type: "loadMore", outputId: "o1", documentUri: "u", token: "t" });
    expect(h.posted[0].message).toMatchObject({ type: "error", outputId: "o1", scope: "loadMore" });
    expect((h.posted[0].message as { message: string }).message).toMatch(/re-?run/i);
  });

  it("reports a worker-unavailable error with its userMessage", async () => {
    const h = harness({
      getWorker: async () => {
        throw Object.assign(new Error("x"), { userMessage: "No .NET 10 on PATH." });
      },
    });
    await h.send({ type: "loadMore", outputId: "o1", documentUri: "u", token: "t" });
    expect(h.posted[0].message).toEqual({
      type: "error",
      outputId: "o1",
      scope: "loadMore",
      message: "No .NET 10 on PATH.",
    });
  });
});

describe("registerRendererHost — exportCsv", () => {
  it("prompts for a path, calls results/exportCsv, and posts exportDone", async () => {
    const calls: Array<{ method: string; params: unknown }> = [];
    const h = harness({
      pickSaveFile: async (name, uri) => {
        expect(name).toBe("results.csv");
        expect(uri).toBe("file:///q.fsqnb");
        return "/home/me/out.csv";
      },
      getWorker: async () => ({
        send: async (r) => {
          calls.push({ method: r.method, params: r.params });
          return { id: r.id, result: { acknowledged: true } };
        },
      }),
    });
    await h.send({ type: "exportCsv", outputId: "o1", documentUri: "file:///q.fsqnb" });

    expect(calls[0].method).toBe("results/exportCsv");
    expect(calls[0].params).toEqual({
      documentUri: "file:///q.fsqnb",
      filePath: "/home/me/out.csv",
    });
    expect(h.posted[0].message).toEqual({ type: "exportDone", outputId: "o1", path: "/home/me/out.csv" });
  });

  it("stays silent when the save dialog is cancelled", async () => {
    const h = harness({ pickSaveFile: async () => undefined });
    await h.send({ type: "exportCsv", outputId: "o1", documentUri: "u" });
    expect(h.posted).toHaveLength(0);
  });

  it("explains results/nothingToExport as a limitation, not a raw error", async () => {
    const h = harness({
      getWorker: async () => ({
        send: async (r) => ({
          id: r.id,
          error: { code: "results/nothingToExport", message: "no retained table" },
        }),
      }),
    });
    await h.send({ type: "exportCsv", outputId: "o1", documentUri: "u" });
    expect(h.posted[0].message).toMatchObject({ type: "error", scope: "exportCsv" });
    expect((h.posted[0].message as { message: string }).message).toMatch(/Results\.table/);
  });
});

describe("registerRendererHost — hygiene", () => {
  it("ignores messages that are not renderer→host shapes", async () => {
    const h = harness();
    await h.send({ type: "rows", outputId: "x" });
    await h.send("nope");
    await h.send(null);
    expect(h.posted).toHaveLength(0);
  });

  it("dispose() unsubscribes", async () => {
    const h = harness();
    h.host.dispose();
    expect(() => h.send({ type: "exportCsv", outputId: "o", documentUri: "u" })).toThrow();
  });
});
