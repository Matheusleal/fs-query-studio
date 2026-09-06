import { describe, it, expect } from "vitest";
import { runHealthCheck, type HealthCheckDeps, activate, deactivate } from "./extension";
import * as vscodeStub from "../test/vscode-stub";
import { DotnetNotFoundError, DotnetTooOldError } from "./worker/dotnet";
import { WorkerDllNotFoundError } from "./worker/worker-path";

function baseDeps(overrides: Partial<HealthCheckDeps>): HealthCheckDeps {
  return {
    extensionRoot: "/ext",
    env: {},
    log: () => {},
    resolveDotnet: async () => ({ command: "dotnet", version: "10.0.100" }),
    resolveWorkerDll: () => "/ext/dist/worker/FSharpQueryStudio.Worker.dll",
    makeClient: () => ({
      send: async () => ({ id: "1", result: { version: "0.1.0", capabilities: ["fsi"] } }),
      dispose: () => {},
    }),
    ...overrides,
  };
}

describe("runHealthCheck", () => {
  it("returns ok with the worker version and capabilities", async () => {
    const result = await runHealthCheck(baseDeps({}));
    expect(result).toEqual({ ok: true, version: "0.1.0", capabilities: ["fsi"] });
  });

  it("reports dotnet-missing", async () => {
    const result = await runHealthCheck(
      baseDeps({
        resolveDotnet: async () => {
          throw new DotnetNotFoundError();
        },
      }),
    );
    expect(result).toMatchObject({ ok: false, kind: "dotnet-missing" });
  });

  it("reports dotnet-old", async () => {
    const result = await runHealthCheck(
      baseDeps({
        resolveDotnet: async () => {
          throw new DotnetTooOldError("8.0.404");
        },
      }),
    );
    expect(result).toMatchObject({ ok: false, kind: "dotnet-old" });
  });

  it("reports worker-missing", async () => {
    const result = await runHealthCheck(
      baseDeps({
        resolveWorkerDll: () => {
          throw new WorkerDllNotFoundError(["/ext/dist/worker/FSharpQueryStudio.Worker.dll"]);
        },
      }),
    );
    expect(result).toMatchObject({ ok: false, kind: "worker-missing" });
  });

  it("reports version-unsupported for a v2 worker", async () => {
    const result = await runHealthCheck(
      baseDeps({
        makeClient: () => ({
          send: async () => ({ id: "1", result: { version: "2.0.0", capabilities: [] } }),
          dispose: () => {},
        }),
      }),
    );
    expect(result).toMatchObject({ ok: false, kind: "version-unsupported" });
  });

  it("reports error when the worker returns an error frame", async () => {
    const result = await runHealthCheck(
      baseDeps({
        makeClient: () => ({
          send: async () => ({ id: "1", error: { code: "boom", message: "nope" } }),
          dispose: () => {},
        }),
      }),
    );
    expect(result).toMatchObject({ ok: false, kind: "error" });
  });

  it("logs the outcome", async () => {
    const lines: string[] = [];
    await runHealthCheck(baseDeps({ log: (l) => lines.push(l) }));
    expect(lines.join("\n")).toMatch(/0\.1\.0/);
  });

  it("disposes the client it creates", async () => {
    let disposed = false;
    await runHealthCheck(
      baseDeps({
        makeClient: () => ({
          send: async () => ({ id: "1", result: { version: "0.1.0", capabilities: [] } }),
          dispose: () => {
            disposed = true;
          },
        }),
      }),
    );
    expect(disposed).toBe(true);
  });
});

describe("activate — notebook wiring", () => {
  function fakeContext() {
    return {
      extensionPath: "/ext",
      subscriptions: [] as Array<{ dispose: () => void }>,
    } as never;
  }

  it("registers a NotebookSerializer for fsquery-notebook", async () => {
    const registered: string[] = [];
    (vscodeStub.workspace as unknown as {
      registerNotebookSerializer: (t: string, s: unknown) => { dispose: () => void };
    }).registerNotebookSerializer = (type) => {
      registered.push(type);
      return { dispose: () => {} };
    };

    await activate(fakeContext());
    expect(registered).toContain("fsquery-notebook");
    await deactivate();
  });

  it("registers the restartKernel command", async () => {
    await activate(fakeContext());
    const commands = await vscodeStub.commands.executeCommand("fs-query-studio.restartKernel");
    // handler exists (returns undefined without an active notebook); no throw
    expect(commands).toBeUndefined();
    await deactivate();
  });

  it("creates a notebook controller", async () => {
    await activate(fakeContext());
    const made = (vscodeStub.notebooks as unknown as { _controllers: unknown[] })._controllers;
    expect(made.length).toBeGreaterThan(0);
    await deactivate();
  });

  it("creates renderer messaging for the results grid", async () => {
    (vscodeStub.notebooks as unknown as { _rendererMessagingIds: string[] })._rendererMessagingIds =
      [];
    await activate(fakeContext());
    expect(
      (vscodeStub.notebooks as unknown as { _rendererMessagingIds: string[] })._rendererMessagingIds,
    ).toContain("fs-query-studio-table");
    await deactivate();
  });
});
