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
import { deserializeFsqnb } from "../notebook/fsqnb";

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

describe.skipIf(!workerDll)("sample notebook execution via worker", () => {
  it(
    "creates sample data and executes all cells of sample_demo.fsqnb and sample_demo_pt.fsqnb successfully",
    async () => {
      const dotnet = await resolveDotnet();
      const client = new WorkerClient({
        spawn: () => spawnWorkerProcess(dotnet.command, workerDll!),
      });

      const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "fqs-sample-vitest-"));

      try {
        // 1. Create sample data
        const createRes = await client.send({
          id: newRequestId(),
          method: "workspace/createSampleData",
          params: { targetDir: tempDir, targetDirectory: tempDir },
        });

        expect(createRes.error ?? undefined).toBeUndefined();
        const data = createRes.result as {
          sqlitePath: string;
          sqlite2Path?: string;
          csvPath: string;
          csv2Path?: string;
          excelPath?: string;
          excel2Path?: string;
          jsonPath?: string;
          xmlPath?: string;
          restUrl?: string;
          notebookPath: string;
          notebookPtPath?: string;
        };

        expect(fs.existsSync(data.sqlitePath)).toBe(true);
        expect(fs.existsSync(data.sqlite2Path!)).toBe(true);
        expect(fs.existsSync(data.csvPath)).toBe(true);
        expect(fs.existsSync(data.csv2Path!)).toBe(true);
        expect(fs.existsSync(data.excelPath!)).toBe(true);
        expect(fs.existsSync(data.excel2Path!)).toBe(true);
        expect(fs.existsSync(data.jsonPath!)).toBe(true);
        expect(fs.existsSync(data.xmlPath!)).toBe(true);
        expect(data.sqlitePath).toContain("data_sources");
        expect(data.excelPath).toContain("data_sources");
        expect(data.excel2Path).toContain("data_sources");
        expect(data.jsonPath).toContain("data_sources");
        expect(data.xmlPath).toContain("data_sources");
        expect(data.restUrl).toMatch(/^https:\/\/.*github\.com/i);
        expect(fs.existsSync(data.notebookPath)).toBe(true);
        expect(fs.existsSync(data.notebookPtPath!)).toBe(true);

        const generatedEntities = fs.existsSync(path.join(tempDir, ".fsqs", "generated", "GeneratedEntities.fs"))
          ? path.join(tempDir, ".fsqs", "generated", "GeneratedEntities.fs")
          : path.join(tempDir, "generated", "GeneratedEntities.fs");
        expect(fs.existsSync(generatedEntities)).toBe(true);

        // 2. Test sample_demo.fsqnb using VS Code Windows URI format (with %3A)
        const nbContent = fs.readFileSync(data.notebookPath, "utf-8");
        const doc = deserializeFsqnb(nbContent);
        const codeCells = doc.cells.filter((c) => c.kind === "code");
        expect(codeCells.length).toBe(17);

        const toVscodeUri = (filePath: string) => {
          const normalized = filePath.replace(/\\/g, "/");
          return normalized.startsWith("/")
            ? `file://${normalized}`
            : `file:///${normalized.replace(":", "%3A")}`;
        };

        // VS Code formats document URIs: file:///${path} on Windows (with %3A), file://${path} on Linux/macOS
        const vscodeUriEn = toVscodeUri(data.notebookPath);

        for (let i = 0; i < codeCells.length; i++) {
          const cell = codeCells[i];
          const evalRes = await client.send({
            id: newRequestId(),
            method: "execution/evaluate",
            params: {
              source: cell.value,
              sessionId: vscodeUriEn,
              mode: SESSION_MODE_PERSISTENT,
              documentUri: vscodeUriEn,
              isNotebookCell: true,
            },
          });

          expect(evalRes.error ?? undefined).toBeUndefined();
          const envelope = evalRes.result as ResultEnvelope;
          const kind = readUnionTag(envelope.kind);
          if (kind === "Error") {
            throw new Error(
              `sample_demo.fsqnb cell ${i + 1} failed: ${envelope.displayText}\nOutput: ${JSON.stringify(envelope.output)}`,
            );
          }
          expect(["Table", "Scalar"]).toContain(kind);
          if (kind === "Table") {
            expect(envelope.rows.length).toBeGreaterThanOrEqual(1);
          }
        }

        // 3. Test sample_demo_pt.fsqnb using VS Code Windows URI format (with %3A)
        const nbPtContent = fs.readFileSync(data.notebookPtPath!, "utf-8");
        const docPt = deserializeFsqnb(nbPtContent);
        const codeCellsPt = docPt.cells.filter((c) => c.kind === "code");
        expect(codeCellsPt.length).toBe(17);

        const vscodeUriPt = toVscodeUri(data.notebookPtPath!);

        for (let i = 0; i < codeCellsPt.length; i++) {
          const cell = codeCellsPt[i];
          const evalRes = await client.send({
            id: newRequestId(),
            method: "execution/evaluate",
            params: {
              source: cell.value,
              sessionId: vscodeUriPt,
              mode: SESSION_MODE_PERSISTENT,
              documentUri: vscodeUriPt,
              isNotebookCell: true,
            },
          });

          expect(evalRes.error ?? undefined).toBeUndefined();
          const envelope = evalRes.result as ResultEnvelope;
          const kind = readUnionTag(envelope.kind);
          if (kind === "Error") {
            throw new Error(
              `sample_demo_pt.fsqnb cell ${i + 1} failed: ${envelope.displayText}\nOutput: ${JSON.stringify(envelope.output)}`,
            );
          }
          expect(["Table", "Scalar"]).toContain(kind);
          if (kind === "Table") {
            expect(envelope.rows.length).toBeGreaterThanOrEqual(1);
          }
        }
      } finally {
        client.dispose();
        try {
          fs.rmSync(tempDir, { recursive: true, force: true });
        } catch {
          // ignore cleanup errors on Windows
        }
      }
    },
    120_000,
  );
});
