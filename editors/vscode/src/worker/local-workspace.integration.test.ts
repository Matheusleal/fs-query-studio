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
      const fsqsDir = path.join(tempDir, ".fsqs");
      const wsJsonPath = path.join(fsqsDir, "workspace.json");
      const generatedDir = path.join(fsqsDir, "generated");
      expect(fs.existsSync(fsqsDir)).toBe(true);
      expect(fs.existsSync(wsJsonPath)).toBe(true);
      expect(fs.existsSync(generatedDir)).toBe(true);
      // Ensure root does NOT contain workspace.json or generated/
      expect(fs.existsSync(path.join(tempDir, "workspace.json"))).toBe(false);
      expect(fs.existsSync(path.join(tempDir, "generated"))).toBe(false);

      const initialWsContent = JSON.parse(fs.readFileSync(wsJsonPath, "utf-8"));
      expect(initialWsContent.schemaVersion).toBe(2);
      expect(initialWsContent.connections).toEqual([]);

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
      expect(updatedWs.schemaVersion).toBe(2);
      expect(updatedWs.connections).toHaveLength(1);
      expect(updatedWs.connections[0].name).toBe("LocalCsvConn");

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
      expect(afterRemoveWs.schemaVersion).toBe(2);
      expect(afterRemoveWs.connections).toHaveLength(0);
    } finally {
      client.dispose();
      try {
        fs.rmSync(tempDir, { recursive: true, force: true });
      } catch {
        // ignore cleanup error
      }
    }
  }, 30_000);

  it("automatically upgrades legacy project with root workspace.json to .fsqs and Schema v2 via workspace/upgrade", async () => {
    const dotnet = await resolveDotnet();
    const client = new WorkerClient({
      spawn: () => spawnWorkerProcess(dotnet.command, workerDll!),
    });

    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "fqs-upgrade-it-"));

    try {
      // 1. Create legacy root workspace.json with an array of connections
      const legacyWsPath = path.join(tempDir, "workspace.json");
      const connId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
      const legacyJson = JSON.stringify([
        {
          id: connId,
          name: "LegacyCsv",
          kind: { Case: "Csv" },
          configuration: { filePath: path.join(tempDir, "legacy.csv") },
          secretReference: null,
          isTrusted: true,
        },
      ]);
      fs.writeFileSync(legacyWsPath, legacyJson, "utf-8");

      // 2. Create legacy root generated/ directory
      const legacyGenDir = path.join(tempDir, "generated");
      fs.mkdirSync(legacyGenDir, { recursive: true });
      fs.writeFileSync(path.join(legacyGenDir, "GeneratedEntities.fs"), "// entities", "utf-8");

      // 3. Send workspace/upgrade request
      const upgradeRes = await client.send({
        id: newRequestId(),
        method: "workspace/upgrade",
        params: { workspaceDir: tempDir },
      });

      expect(upgradeRes.error ?? undefined).toBeUndefined();
      const report = upgradeRes.result as {
        upgraded: boolean;
        previousVersion: number;
        currentVersion: number;
        actions: string[];
      };

      expect(report.upgraded).toBe(true);
      expect(report.previousVersion).toBe(1);
      expect(report.currentVersion).toBe(2);
      expect(report.actions.length).toBeGreaterThanOrEqual(2);

      // Verify legacy files on root are gone
      expect(fs.existsSync(legacyWsPath)).toBe(false);
      expect(fs.existsSync(legacyGenDir)).toBe(false);

      // Verify files inside .fsqs exist
      const fsqsDir = path.join(tempDir, ".fsqs");
      const newWsPath = path.join(fsqsDir, "workspace.json");
      const newGenPath = path.join(fsqsDir, "generated", "GeneratedEntities.fs");

      expect(fs.existsSync(newWsPath)).toBe(true);
      expect(fs.existsSync(newGenPath)).toBe(true);

      const upgradedContent = JSON.parse(fs.readFileSync(newWsPath, "utf-8"));
      expect(upgradedContent.schemaVersion).toBe(2);
      expect(upgradedContent.connections).toHaveLength(1);
      expect(upgradedContent.connections[0].name).toBe("LegacyCsv");

      // Verify connection/list returns the upgraded connection
      const listRes = await client.send({
        id: newRequestId(),
        method: "connection/list",
        params: { workspaceDir: tempDir },
      });
      const list = (listRes.result as ConnectionListResult).connections;
      expect(list.some((c) => c.name === "LegacyCsv")).toBe(true);
    } finally {
      client.dispose();
      try {
        fs.rmSync(tempDir, { recursive: true, force: true });
      } catch {
        // ignore cleanup error
      }
    }
  }, 30_000);
});

