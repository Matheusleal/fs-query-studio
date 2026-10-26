/**
 * Wire types shared between the extension host and the results-grid
 * renderer. This module is imported by browser-side renderer code, so it
 * must stay dependency-free — no `import` of `../protocol` (which pulls
 * `node:crypto`) or `vscode`. The host side re-declares the matching
 * shapes via `import type` only.
 */

/** MIME type of the output item that carries a tabular result. */
export const FSQUERY_TABLE_MIME = "application/x-fsquery-table+json";

/** Must equal `contributes.notebookRenderer[].id` in `package.json`. */
export const FSQUERY_RENDERER_ID = "fs-query-studio-table";

/** Fixed row height the windowed grid lays rows out on. */
export const GRID_ROW_HEIGHT_PX = 24;

export interface TableColumn {
  name: string;
  displayType: string;
}

/** JSON body of an `application/x-fsquery-table+json` output item. */
export interface TablePayload {
  /** Notebook URI; echoed in renderer→host messages so the host can RPC. */
  documentUri: string;
  columns: TableColumn[];
  /** Preview rows, positional against `columns`. */
  rows: unknown[][];
  /** More rows exist on the worker behind `continuationToken`. */
  truncated: boolean;
  /** Opaque worker-issued paging token, or `null` when paging is unavailable. */
  continuationToken: string | null;
  elapsedMs: number;
}

// --- renderer -> host ---

export interface LoadMoreMessage {
  type: "loadMore";
  outputId: string;
  documentUri: string;
  token: string;
}

export interface ExportCsvMessage {
  type: "exportCsv";
  outputId: string;
  documentUri: string;
}

export type RendererToHostMessage = LoadMoreMessage | ExportCsvMessage;

// --- host -> renderer ---

export interface RowsMessage {
  type: "rows";
  outputId: string;
  rows: unknown[][];
  truncated: boolean;
  nextToken: string | null;
}

export interface HostErrorMessage {
  type: "error";
  outputId: string;
  scope: "loadMore" | "exportCsv";
  message: string;
}

export interface ExportDoneMessage {
  type: "exportDone";
  outputId: string;
  path: string;
}

export type HostToRendererMessage = RowsMessage | HostErrorMessage | ExportDoneMessage;

export function isRendererToHostMessage(value: unknown): value is RendererToHostMessage {
  if (value === null || typeof value !== "object") {
    return false;
  }
  const type = (value as { type?: unknown }).type;
  return type === "loadMore" || type === "exportCsv";
}
