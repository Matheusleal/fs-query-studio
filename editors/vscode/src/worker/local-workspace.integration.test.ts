import { describe, it, expect } from "vitest";
import * as path from "node:path";
import * as fs from "node:fs";
import * as os from "node:os";
import { resolveDotnet } from "./dotnet";
import { resolveWorkerDll } from "./worker-path";
import { WorkerClient } from "./client";
import { spawnWorkerProcess } from "./spawn";
import {
  newRequestId,
  type ConnectionListResult,
  type ConnectionSchemaResult,
} from "../protocol";

const extensionRoot = path.resolve(__dirname, "..", "..");

let workerDll: string | undefined;
try {
  workerDll = resolveWorkerDll(extensionRoot, process.env);
} catch {
  workerDll = undefined;
}

describe.skipIf(!workerDll)("local workspace project integration via worker", () => {
  it("initializes empty project, registers connection in local workspace, discovers schema, and removes it", async () => {
    const dotnet = await resolveDotnet();
    const client = new WorkerClient({
      spawn: () => spawnWorkerProcess(dotnet.command, workerDll!),
    });

    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "fqs-local-proj-"));

    try {
      // 1. Initialize empty project
      const initRes = await client.send({
        id: newRequestId(),
        method: "workspace/initEmptyProject",
        params: { targetDir: tempDir },
      });

      expect(initRes.error ?? undefined).toBeUndefined();
      const wsJsonPath = path.join(tempDir, "workspace.json");
      const generatedDir = path.join(tempDir, "generated");
      expect(fs.existsSync(wsJsonPath)).toBe(true);
      expect(fs.existsSync(generatedDir)).toBe(true);

      const initialWsContent = JSON.parse(fs.readFileSync(wsJsonPath, "utf-8"));
      expect(initialWsContent).toEqual([]);

      // 2. Register connection targeted to local workspace
      const connId = "11111111-2222-3333-4444-555555555555";
      const regRes = await client.send({
        id: newRequestId(),
        method: "connection/register",
        params: {
          id: connId,
          name: "LocalCsvConn",
          kind: { Case: "Csv" },
          configuration: { filePath: path.join(tempDir, "test.csv") },
          secretReference: null,
          isTrusted: true,
          workspaceDir: tempDir,
        },
      });

      expect(regRes.error ?? undefined).toBeUndefined();

      // Check local workspace.json was written
      const updatedWs = JSON.parse(fs.readFileSync(wsJsonPath, "utf-8"));
      expect(updatedWs).toHaveLength(1);
      expect(updatedWs[0].name).toBe("LocalCsvConn");

      // 3. List connections with workspaceDir
      const listRes = await client.send({
        id: newRequestId(),
        method: "connection/list",
        params: { workspaceDir: tempDir },
      });

      expect(listRes.error ?? undefined).toBeUndefined();
      const list = (listRes.result as ConnectionListResult).connections;
      const found = list.find((c) => c.name === "LocalCsvConn");
      expect(found).toBeDefined();
      expect(found?.id).toBe(connId);

      // 4. Remove connection with workspaceDir
      const removeRes = await client.send({
        id: newRequestId(),
        method: "connection/remove",
        params: { id: connId, workspaceDir: tempDir },
      });

      expect(removeRes.error ?? undefined).toBeUndefined();

      const afterRemoveWs = JSON.parse(fs.readFileSync(wsJsonPath, "utf-8"));
      expect(afterRemoveWs).toHaveLength(0);
    } finally {
      client.dispose();
      try {
        fs.rmSync(tempDir, { recursive: true, force: true });
      } catch {
        // ignore cleanup error
      }
    }
  });
});
