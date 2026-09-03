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
