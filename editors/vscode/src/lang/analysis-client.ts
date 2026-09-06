import {
  newRequestId,
  type AnalysisCheckParams,
  type AnalysisPositionParams,
  type HoverInfo,
  type SignatureHelpInfo,
  type WireDiagnostic,
  type WorkerResponseFrame,
} from "../protocol";

/**
 * The one `WorkerClient` method these wrappers need — injectable for
 * tests, and the same shape `notebook/controller.ts` and
 * `notebook/renderer-host.ts` already depend on.
 */
export interface WorkerLike {
  send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame>;
}

/**
 * Throws on a worker error (letting the caller handle error recovery),
 * returns empty on a null result. The DiagnosticsManager catches throws
 * to avoid publishing stale diagnostics; other callers may swallow the error.
 * Mirrors `App/AnalysisService.fs`.
 */
async function call<T>(
  worker: WorkerLike,
  method: string,
  params: unknown,
  pick: (result: Record<string, unknown>) => T,
  empty: T,
): Promise<T> {
  let response: WorkerResponseFrame;
  try {
    response = await worker.send({ id: newRequestId(), method, params });
  } catch (err) {
    throw err;
  }
  if (response.error || response.result == null) {
    return empty;
  }
  return pick(response.result as Record<string, unknown>);
}

export function checkAnalysis(
  worker: WorkerLike,
  params: AnalysisCheckParams,
): Promise<WireDiagnostic[]> {
  return call(
    worker,
    "analysis/check",
    params,
    (r) => (r.diagnostics as WireDiagnostic[] | undefined) ?? [],
    [],
  );
}

export function completeAnalysis(
  worker: WorkerLike,
  params: AnalysisPositionParams,
): Promise<string[]> {
  return call(worker, "analysis/complete", params, (r) => (r.items as string[] | undefined) ?? [], []);
}

export function hoverAnalysis(
  worker: WorkerLike,
  params: AnalysisPositionParams,
): Promise<HoverInfo | undefined> {
  return call(
    worker,
    "analysis/hover",
    params,
    (r) => (r.hover as HoverInfo | null) ?? undefined,
    undefined,
  );
}

export function signatureHelpAnalysis(
  worker: WorkerLike,
  params: AnalysisPositionParams,
): Promise<SignatureHelpInfo | undefined> {
  return call(
    worker,
    "analysis/signatureHelp",
    params,
    (r) => (r.signatureHelp as SignatureHelpInfo | null) ?? undefined,
    undefined,
  );
}
