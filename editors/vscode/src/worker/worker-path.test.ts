import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { resolveWorkerDll, WorkerDllNotFoundError } from "./worker-path";

let root: string;

beforeEach(() => {
  root = fs.mkdtempSync(path.join(os.tmpdir(), "fsqs-"));
});
afterEach(() => {
  fs.rmSync(root, { recursive: true, force: true });
});

describe("resolveWorkerDll", () => {
  it("finds the bundled dll under dist/worker", () => {
    const bundled = path.join(root, "dist", "worker");
    fs.mkdirSync(bundled, { recursive: true });
    const dll = path.join(bundled, "FSharpQueryStudio.Worker.dll");
    fs.writeFileSync(dll, "");
    expect(resolveWorkerDll(root, {})).toBe(dll);
  });

  it("prefers the FSQS_WORKER_DLL override", () => {
    const override = path.join(root, "custom.dll");
    fs.writeFileSync(override, "");
    expect(resolveWorkerDll(root, { FSQS_WORKER_DLL: override })).toBe(override);
  });

  it("throws WorkerDllNotFoundError when nothing exists", () => {
    expect(() => resolveWorkerDll(root, {})).toThrow(WorkerDllNotFoundError);
  });

  it("throws when FSQS_WORKER_DLL is set but the file is missing, ignoring the bundled fallback", () => {
    const bundled = path.join(root, "dist", "worker");
    fs.mkdirSync(bundled, { recursive: true });
    fs.writeFileSync(path.join(bundled, "FSharpQueryStudio.Worker.dll"), "");
    expect(() =>
      resolveWorkerDll(root, { FSQS_WORKER_DLL: path.join(root, "missing.dll") }),
    ).toThrow(WorkerDllNotFoundError);
  });
});
