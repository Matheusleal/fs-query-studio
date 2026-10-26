import { describe, it, expect, afterEach } from "vitest";
import {
  resolveWorkerSession,
  getWorkerSession,
  disposeWorkerSession,
  WorkerUnavailableError,
  type ResolveWorkerSessionDeps,
} from "./session";
import { DotnetNotFoundError, DotnetTooOldError } from "./dotnet";
import { WorkerDllNotFoundError } from "./worker-path";

function fakeClient(send: (req: { id: string; method: string; params: unknown }) => Promise<unknown>) {
  let disposed = false;
  return {
    client: {
      send,
      dispose: () => {
        disposed = true;
      },
    } as never,
    wasDisposed: () => disposed,
  };
}

function baseDeps(overrides: Partial<ResolveWorkerSessionDeps>): ResolveWorkerSessionDeps {
  return {
    extensionRoot: "/ext",
    env: {},
    log: () => {},
    resolveDotnet: async () => ({ command: "dotnet", version: "10.0.100" }),
    resolveWorkerDll: () => "/ext/dist/worker/FSharpQueryStudio.Worker.dll",
    makeClient: () =>
      fakeClient(async (req) => ({ id: req.id, result: { version: "1.0.0", capabilities: [] } }))
        .client,
    ...overrides,
  };
}

afterEach(() => {
  disposeWorkerSession();
});

describe("resolveWorkerSession", () => {
  it("returns a session with the worker health on success", async () => {
    const session = await resolveWorkerSession(baseDeps({}));
    expect(session.health.version).toBe("1.0.0");
  });

  it("maps a missing dotnet to WorkerUnavailableError(dotnet-missing)", async () => {
    const err: WorkerUnavailableError = await resolveWorkerSession(
      baseDeps({
        resolveDotnet: async () => {
          throw new DotnetNotFoundError();
        },
      }),
    ).catch((e) => e);
    expect(err).toBeInstanceOf(WorkerUnavailableError);
    expect(err.kind).toBe("dotnet-missing");
  });

  it("maps an old dotnet to dotnet-old", async () => {
    const err: WorkerUnavailableError = await resolveWorkerSession(
      baseDeps({
        resolveDotnet: async () => {
          throw new DotnetTooOldError("8.0.404");
        },
      }),
    ).catch((e) => e);
    expect(err.kind).toBe("dotnet-old");
  });

  it("maps a missing worker dll to worker-missing", async () => {
    const err: WorkerUnavailableError = await resolveWorkerSession(
      baseDeps({
        resolveWorkerDll: () => {
          throw new WorkerDllNotFoundError(["/ext/dist/worker/FSharpQueryStudio.Worker.dll"]);
        },
      }),
    ).catch((e) => e);
    expect(err.kind).toBe("worker-missing");
  });

  it("maps an unsupported worker version to version-unsupported and disposes the client", async () => {
    const fc = fakeClient(async (req) => ({ id: req.id, result: { version: "2.0.0", capabilities: [] } }));
    const err: WorkerUnavailableError = await resolveWorkerSession(
      baseDeps({ makeClient: () => fc.client }),
    ).catch((e) => e);
    expect(err.kind).toBe("version-unsupported");
    expect(fc.wasDisposed()).toBe(true);
  });

  it("maps a worker error frame to error and disposes the client", async () => {
    const fc = fakeClient(async (req) => ({ id: req.id, error: { code: "boom", message: "no" } }));
    const err: WorkerUnavailableError = await resolveWorkerSession(
      baseDeps({ makeClient: () => fc.client }),
    ).catch((e) => e);
    expect(err.kind).toBe("error");
    expect(fc.wasDisposed()).toBe(true);
  });
});

describe("getWorkerSession", () => {
  it("resolves the worker only once across concurrent calls", async () => {
    let made = 0;
    const deps = baseDeps({
      makeClient: () => {
        made++;
        return fakeClient(async (req) => ({
          id: req.id,
          result: { version: "1.0.0", capabilities: [] },
        })).client;
      },
    });
    const [a, b] = await Promise.all([getWorkerSession(deps), getWorkerSession(deps)]);
    expect(a).toBe(b);
    expect(made).toBe(1);
  });

  it("retries after a failed resolution", async () => {
    let attempt = 0;
    const deps = baseDeps({
      resolveDotnet: async () => {
        attempt++;
        if (attempt === 1) {
          throw new DotnetNotFoundError();
        }
        return { command: "dotnet", version: "10.0.100" };
      },
    });
    await expect(getWorkerSession(deps)).rejects.toBeInstanceOf(WorkerUnavailableError);
    const session = await getWorkerSession(deps);
    expect(session.health.version).toBe("1.0.0");
  });
});
