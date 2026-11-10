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
  readUnionTag,
  SESSION_MODE_PERSISTENT,
  type ResultEnvelope,
} from "../protocol";

const extensionRoot = path.resolve(__dirname, "..", "..");
const repoRoot = path.resolve(extensionRoot, "..", "..");

function findLatestWorkerDll(): string | undefined {
  if (process.env.FSQS_WORKER_DLL && fs.existsSync(process.env.FSQS_WORKER_DLL)) {
    return process.env.FSQS_WORKER_DLL;
  }
  const candidates = [
    path.join(repoRoot, "src", "FSharpQueryStudio.Worker", "bin", "Release", "net10.0", "FSharpQueryStudio.Worker.dll"),
    path.join(repoRoot, "src", "FSharpQueryStudio.Worker", "bin", "Debug", "net10.0", "FSharpQueryStudio.Worker.dll"),
    path.join(extensionRoot, "dist", "worker", "FSharpQueryStudio.Worker.dll"),
  ].filter((p) => fs.existsSync(p));

  if (candidates.length === 0) {
    try {
      return resolveWorkerDll(extensionRoot, process.env);
    } catch {
      return undefined;
    }
  }

  candidates.sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs);
  return candidates[0];
}

const workerDll = findLatestWorkerDll();

const toVscodeUri = (filePath: string) => {
  const normalized = filePath.replace(/\\/g, "/");
  return normalized.startsWith("/")
    ? `file://${normalized}`
    : `file:///${normalized.replace(":", "%3A")}`;
};

function normalizeFsPath(p: string): string {
  try {
    let resolved = typeof fs.realpathSync.native === "function" ? fs.realpathSync.native(p) : fs.realpathSync(p);
    if (resolved.startsWith("\\\\?\\")) {
      resolved = resolved.slice(4);
    }
    return path.resolve(resolved).toLowerCase();
  } catch {
    return path.resolve(p).toLowerCase();
  }
}

describe.skipIf(!workerDll)("working directory and CSV export integration", () => {
  it("evaluates in workspace dir, creates and disposes files without locks, and exports CSV relatively", async () => {
    const dotnet = await resolveDotnet();
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "fqs-workdir-test-"));
    const notebookPath = path.join(tempDir, "test.fsqnb");
    fs.writeFileSync(notebookPath, "{}");
    const notebookUri = toVscodeUri(notebookPath);

    const client = new WorkerClient({
      spawn: () => spawnWorkerProcess(dotnet.command, workerDll!, tempDir),
    });

    try {
      // 1. Verify System.Environment.CurrentDirectory points to workspace directory
      const evalDirRes = await client.send({
        id: newRequestId(),
        method: "execution/evaluate",
        params: {
          source: "System.Environment.CurrentDirectory",
          sessionId: notebookUri,
          mode: SESSION_MODE_PERSISTENT,
          documentUri: notebookUri,
          isNotebookCell: true,
        },
      });

      expect(evalDirRes.error ?? undefined).toBeUndefined();
      const dirEnvelope = evalDirRes.result as ResultEnvelope;
      const returnedDir = dirEnvelope.displayText?.trim().replace(/"/g, "") ?? "";
      expect(normalizeFsPath(returnedDir)).toBe(normalizeFsPath(tempDir));

      // 2. File creation via File.Create, write, Dispose, verify, and immediate deletion
      const fileCode = [
        'let testFile = "sample_created_file.txt"',
        'let fs = System.IO.File.Create(testFile)',
        'fs.Write([| 65uy; 66uy; 67uy |], 0, 3)',
        'fs.Dispose()',
        'System.IO.File.Exists(testFile)',
      ].join("\n");

      const evalFileRes = await client.send({
        id: newRequestId(),
        method: "execution/evaluate",
        params: {
          source: fileCode,
          sessionId: notebookUri,
          mode: SESSION_MODE_PERSISTENT,
          documentUri: notebookUri,
          isNotebookCell: true,
        },
      });

      expect(evalFileRes.error ?? undefined).toBeUndefined();
      const createdFilePath = path.join(tempDir, "sample_created_file.txt");
      expect(fs.existsSync(createdFilePath)).toBe(true);
      expect(fs.readFileSync(createdFilePath, "utf-8")).toBe("ABC");

      // Verify the file can be deleted immediately from host process (not locked by worker)
      expect(() => fs.unlinkSync(createdFilePath)).not.toThrow();
      expect(fs.existsSync(createdFilePath)).toBe(false);

      // 3. Evaluate tabular data to retain in session
      const tableCode = '[ {| Id = 1; Name = "Alice" |}; {| Id = 2; Name = "Bob" |} ]';
      const evalTableRes = await client.send({
        id: newRequestId(),
        method: "execution/evaluate",
        params: {
          source: tableCode,
          sessionId: notebookUri,
          mode: SESSION_MODE_PERSISTENT,
          documentUri: notebookUri,
          isNotebookCell: true,
        },
      });

      expect(evalTableRes.error ?? undefined).toBeUndefined();
      const tableEnvelope = evalTableRes.result as ResultEnvelope;
      expect(readUnionTag(tableEnvelope.kind)).toBe("Table");

      // 4. Export CSV with relative path
      const exportRelativeRes = await client.send({
        id: newRequestId(),
        method: "results/exportCsv",
        params: {
          documentUri: notebookUri,
          filePath: "exported_relative.csv",
        },
      });

      expect(exportRelativeRes.error ?? undefined).toBeUndefined();
      const relativeCsvPath = path.join(tempDir, "exported_relative.csv");
      expect(fs.existsSync(relativeCsvPath)).toBe(true);
      const relativeContent = fs.readFileSync(relativeCsvPath, "utf-8");
      expect(relativeContent).toContain("Id");
      expect(relativeContent).toContain("Alice");
      expect(relativeContent).toContain("Bob");

      // 5. Export CSV with nested path (parent dirs auto-created)
      const nestedPath = path.join(tempDir, "nested", "reports", "exported_nested.csv");
      const exportNestedRes = await client.send({
        id: newRequestId(),
        method: "results/exportCsv",
        params: {
          documentUri: notebookUri,
          filePath: nestedPath,
        },
      });

      expect(exportNestedRes.error ?? undefined).toBeUndefined();
      expect(fs.existsSync(nestedPath)).toBe(true);
      const nestedContent = fs.readFileSync(nestedPath, "utf-8");
      expect(nestedContent).toContain("Alice");
    } finally {
      client.dispose();
      try {
        fs.rmSync(tempDir, { recursive: true, force: true });
      } catch {
        // temp cleanup
      }
    }
  }, 30000);
});

