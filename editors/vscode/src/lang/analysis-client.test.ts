import { describe, it, expect, vi } from "vitest";
import type { WorkerResponseFrame } from "../protocol";
import {
  checkAnalysis,
  completeAnalysis,
  hoverAnalysis,
  signatureHelpAnalysis,
  type WorkerLike,
} from "./analysis-client";

function fakeWorker(response: WorkerResponseFrame | (() => Promise<WorkerResponseFrame>)): {
  worker: WorkerLike;
  calls: Array<{ method: string; params: unknown }>;
} {
  const calls: Array<{ method: string; params: unknown }> = [];
  const worker: WorkerLike = {
    send: (req) => {
      calls.push({ method: req.method, params: req.params });
      return typeof response === "function" ? response() : Promise.resolve(response);
    },
  };
  return { worker, calls };
}

const checkParams = { documentUri: "c1", version: 3, source: "let x = 1" };
const posParams = { documentUri: "c1", version: 3, source: "x", line: 1, column: 1 };

describe("checkAnalysis", () => {
  it("sends analysis/check with the params and returns the diagnostics", async () => {
    const diag = {
      message: "m",
      severity: { Case: "Error" as const },
      startLine: 0,
      startColumn: 0,
      endLine: 0,
      endColumn: 1,
      code: null,
    };
    const { worker, calls } = fakeWorker({ id: "1", result: { diagnostics: [diag] } });
    const out = await checkAnalysis(worker, checkParams);
    expect(calls[0]).toEqual({ method: "analysis/check", params: checkParams });
    expect(out).toEqual([diag]);
  });

  it("returns [] on an error frame or null result, throws on worker send error", async () => {
    expect(
      await checkAnalysis(fakeWorker({ id: "1", error: { code: "x", message: "y" } }).worker, checkParams),
    ).toEqual([]);
    expect(await checkAnalysis(fakeWorker({ id: "1", result: null }).worker, checkParams)).toEqual([]);
    await expect(
      checkAnalysis(
        fakeWorker(() => Promise.reject(new Error("boom"))).worker,
        checkParams,
      ),
    ).rejects.toThrow("boom");
  });
});

describe("completeAnalysis", () => {
  it("sends analysis/complete and returns items", async () => {
    const { worker, calls } = fakeWorker({ id: "1", result: { items: ["foo", "bar"] } });
    expect(await completeAnalysis(worker, posParams)).toEqual(["foo", "bar"]);
    expect(calls[0].method).toBe("analysis/complete");
  });

  it("returns [] on error / null / throw", async () => {
    expect(
      await completeAnalysis(fakeWorker({ id: "1", error: { code: "x", message: "y" } }).worker, posParams),
    ).toEqual([]);
    expect(await completeAnalysis(fakeWorker({ id: "1", result: null }).worker, posParams)).toEqual([]);
  });
});

describe("hoverAnalysis", () => {
  it("sends analysis/hover and returns the hover, or undefined", async () => {
    const hover = { displayName: "x", type: "int", signature: null, xmlDoc: null };
    const { worker, calls } = fakeWorker({ id: "1", result: { hover } });
    expect(await hoverAnalysis(worker, posParams)).toEqual(hover);
    expect(calls[0].method).toBe("analysis/hover");
    expect(await hoverAnalysis(fakeWorker({ id: "1", result: { hover: null } }).worker, posParams)).toBeUndefined();
    expect(
      await hoverAnalysis(fakeWorker({ id: "1", error: { code: "x", message: "y" } }).worker, posParams),
    ).toBeUndefined();
  });
});

describe("signatureHelpAnalysis", () => {
  it("sends analysis/signatureHelp and returns it, or undefined", async () => {
    const sh = { displayName: "f", parameters: ["a: int"], returnType: "int" };
    const { worker, calls } = fakeWorker({ id: "1", result: { signatureHelp: sh } });
    expect(await signatureHelpAnalysis(worker, posParams)).toEqual(sh);
    expect(calls[0].method).toBe("analysis/signatureHelp");
    expect(
      await signatureHelpAnalysis(fakeWorker({ id: "1", result: { signatureHelp: null } }).worker, posParams),
    ).toBeUndefined();
  });
});
