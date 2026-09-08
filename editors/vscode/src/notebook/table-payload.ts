import type { ResultEnvelope } from "../protocol";
import type { TablePayload } from "../renderer/types";

/**
 * Projects a `Table`-kind `ResultEnvelope` onto the renderer's wire body.
 * Works for every tail-value shape the worker projects: a materialized
 * table (real columns, full rows retained for Export CSV, no continuation
 * token) and a streamed scalar `seq` (single `Value` column, preview rows,
 * a token when truncated). Rows are passed through positionally.
 */
export function buildTablePayload(env: ResultEnvelope, documentUri: string): TablePayload {
  return {
    documentUri,
    columns: (env.schema ?? []).map((c) => ({ name: c.name, displayType: c.displayType })),
    rows: env.rows ?? [],
    truncated: env.isTruncated === true,
    continuationToken: env.continuationToken ?? null,
    elapsedMs: env.elapsedMs,
  };
}
