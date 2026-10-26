import { describe, it, expect } from "vitest";
import * as path from "node:path";
import { resolveDotnet } from "./dotnet";
import { resolveWorkerDll } from "./worker-path";
import { WorkerClient } from "./client";
import { spawnWorkerProcess } from "./spawn";
import { newRequestId, type HealthInfo } from "../protocol";

const extensionRoot = path.resolve(__dirname, "..", "..");

let workerDll: string | undefined;
try {
  workerDll = resolveWorkerDll(extensionRoot, process.env);
} catch {
  workerDll = undefined;
}

describe.skipIf(!workerDll)("real worker handshake", () => {
  it(
    "completes a worker/health round-trip",
    async () => {
      const dotnet = await resolveDotnet();
      const client = new WorkerClient({
        spawn: () => spawnWorkerProcess(dotnet.command, workerDll!),
      });
      try {
        const response = await client.send({
          id: newRequestId(),
          method: "worker/health",
          params: {},
        });
        expect(response.error ?? undefined).toBeUndefined();
        const health = response.result as HealthInfo;
        expect(typeof health.version).toBe("string");
        expect(health.version).toMatch(/^\d+\./);
        expect(Array.isArray(health.capabilities)).toBe(true);
      } finally {
        client.dispose();
      }
    },
    30_000,
  );
});
