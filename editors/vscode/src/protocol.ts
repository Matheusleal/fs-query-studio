import { randomUUID } from "node:crypto";

export interface WorkerRequestFrame {
  id: string;
  method: string;
  params: unknown;
}

export interface WorkerErrorInfo {
  code: string;
  message: string;
}

export interface WorkerResponseFrame {
  id: string;
  // The worker (FSharp.SystemTextJson) serializes an absent value as an
  // explicit JSON `null`, not by omitting the key — so both `null` and
  // `undefined` mean "not present". Consumers must treat them the same.
  result?: unknown;
  error?: WorkerErrorInfo | null;
}

export interface HealthInfo {
  version: string;
  capabilities: string[];
}

export const SUPPORTED_WORKER_MAJOR = 0;
export const SUPPORTED_WORKER_MAJOR_MAX = 1;

export function isWorkerVersionSupported(version: string): boolean {
  const match = /^(\d+)/.exec(version.trim());
  if (!match) {
    return false;
  }
  const major = Number(match[1]);
  return major >= SUPPORTED_WORKER_MAJOR && major <= SUPPORTED_WORKER_MAJOR_MAX;
}

export function newRequestId(): string {
  return randomUUID();
}

// --- F2: discriminated-union wire encoding ---

/**
 * An F# discriminated-union case as it appears on the wire.
 * FSharp.SystemTextJson's default (AdjacentTag, fieldless cases not
 * unwrapped) serializes a union case as `{ "Case": "<Name>" }`.
 * Verified against tests/FSharpQueryStudio.Integration.Tests/Tests.fs.
 */
export interface UnionTag<T extends string = string> {
  Case: T;
}

export function makeUnionTag<T extends string>(name: T): UnionTag<T> {
  return { Case: name };
}

export function readUnionTag(value: unknown): string | undefined {
  if (value !== null && typeof value === "object" && "Case" in value) {
    const tag = (value as { Case: unknown }).Case;
    return typeof tag === "string" ? tag : undefined;
  }
  return undefined;
}

// --- F2: execution/evaluate + session/reset request payloads ---

export type SessionModeName = "Isolated" | "Persistent";

export const SESSION_MODE_PERSISTENT: UnionTag<"Persistent"> = { Case: "Persistent" };

export interface EvaluateParams {
  source: string;
  sessionId: string | null;
  mode: UnionTag<SessionModeName>;
  documentUri: string;
}

export interface SessionResetParams {
  sessionId: string;
}

// --- F2: execution/evaluate response payload (ResultEnvelope) ---

export type ResultKindName = "Scalar" | "Text" | "Table" | "Object" | "Error" | "NoValue";

export type OutputStreamName = "Stdout" | "Stderr";

export interface Column {
  name: string;
  displayType: string;
}

export interface OutputLine {
  stream: UnionTag<OutputStreamName>;
  text: string;
}

/**
 * Mirror of `FSharpQueryStudio.Protocol.ResultEnvelope`. `displayText` and
 * `continuationToken` are F# `string option`, which FSharp.SystemTextJson
 * unwraps to the bare string or JSON `null`.
 */
export interface ResultEnvelope {
  kind: UnionTag<ResultKindName>;
  displayText: string | null;
  schema: Column[];
  rows: unknown[][];
  isTruncated: boolean;
  continuationToken: string | null;
  elapsedMs: number;
  output: OutputLine[];
}

// --- F4: analysis/* request + response payloads ---

export type WireSeverity = "Error" | "Warning" | "Information";

/**
 * `Diagnostic.Severity` is a fieldless F# DU — FSharp.SystemTextJson
 * writes it as `{ "Case": "Error" }`. Anything we don't recognize is
 * treated as an error so a surprising value is never silently dropped.
 */
export function readSeverity(value: unknown): WireSeverity {
  switch (readUnionTag(value)) {
    case "Warning":
      return "Warning";
    case "Information":
      return "Information";
    default:
      return "Error";
  }
}

/** `analysis/check` request — mirror of `Protocol.AnalysisCheckParams`. */
export interface AnalysisCheckParams {
  documentUri: string;
  version: number;
  source: string;
}

/**
 * `analysis/complete` / `analysis/hover` / `analysis/signatureHelp`
 * request. `line` is **1-based**, `column` is **0-based** — the worker's
 * `Prologue.shiftUp` contract.
 */
export interface AnalysisPositionParams {
  documentUri: string;
  version: number;
  source: string;
  line: number;
  column: number;
}

/** One diagnostic as it arrives from `analysis/check`. Lines/columns are
 *  0-based against the *unprefixed* source we sent (the worker already
 *  shifted its FSAC prologue back out). */
export interface WireDiagnostic {
  message: string;
  severity: UnionTag<WireSeverity>;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  code: string | null;
}

export interface AnalysisCheckResult {
  diagnostics: WireDiagnostic[];
}

export interface AnalysisCompletionResult {
  items: string[];
}

export interface HoverInfo {
  displayName: string;
  type: string;
  signature: string | null;
  xmlDoc: string | null;
}

export interface AnalysisHoverResult {
  hover: HoverInfo | null;
}

export interface SignatureHelpInfo {
  displayName: string;
  parameters: string[];
  returnType: string;
}

export interface AnalysisSignatureHelpResult {
  signatureHelp: SignatureHelpInfo | null;
}
