import * as assert from "node:assert";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import type { HealthCheckResult } from "../../src/extension";

const EXTENSION_ID = "fs-query-studio.fs-query-studio";
const COMMAND_ID = "fs-query-studio.checkWorker";

// out/test/integration -> editors/vscode
const extensionRoot = path.resolve(__dirname, "..", "..", "..");
const bundledWorker = path.join(
  extensionRoot,
  "dist",
  "worker",
  "FSharpQueryStudio.Worker.dll",
);
const hasWorker = fs.existsSync(bundledWorker);

describe("fs-query-studio extension host", () => {
  afterEach(() => {
    delete process.env.FSQS_WORKER_DLL;
  });

  it("activates", async () => {
    const ext = vscode.extensions.getExtension(EXTENSION_ID);
    assert.ok(ext, `extension ${EXTENSION_ID} not found`);
    await ext!.activate();
    assert.strictEqual(ext!.isActive, true);
  });

  it("registers the checkWorker command", async () => {
    const commands = await vscode.commands.getCommands(true);
    assert.ok(commands.includes(COMMAND_ID), `${COMMAND_ID} not registered`);
  });

  it("reports worker-missing for a bogus FSQS_WORKER_DLL", async () => {
    process.env.FSQS_WORKER_DLL = path.join(extensionRoot, "does-not-exist.dll");
    const result = await vscode.commands.executeCommand<HealthCheckResult>(COMMAND_ID);
    assert.ok(result);
    assert.strictEqual(result!.ok, false);
    assert.strictEqual(result!.ok === false && result!.kind, "worker-missing");
  });

  (hasWorker ? it : it.skip)(
    "completes a real worker/health check against the bundled worker",
    async function () {
      this.timeout(60000);
      const result = await vscode.commands.executeCommand<HealthCheckResult>(COMMAND_ID);
      assert.ok(result);
      if (!result!.ok) {
        assert.fail(`expected ok, got ${result!.kind}: ${result!.message}`);
      }
      assert.match(result!.version, /^\d+\./);
      assert.ok(Array.isArray(result!.capabilities));
    },
  );
});
