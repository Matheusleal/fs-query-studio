import * as assert from "node:assert";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import * as vscode from "vscode";

const EXTENSION_ID = "fs-query-studio.fs-query-studio";

// out/test/integration -> editors/vscode
const extensionRoot = path.resolve(__dirname, "..", "..", "..");
const bundledWorker = path.join(
  extensionRoot,
  "dist",
  "worker",
  "FSharpQueryStudio.Worker.dll",
);
const hasWorker = fs.existsSync(bundledWorker);

// Mirrors the F2 spec's worker gate: the whole worker-backed block is skipped
// green when no worker dll is bundled (no `dotnet publish` in this env).
const maybe = hasWorker ? describe : describe.skip;

maybe("F3 renderer — table-json output item", () => {
  before(async () => {
    const ext = vscode.extensions.getExtension(EXTENSION_ID);
    assert.ok(ext, `extension ${EXTENSION_ID} not found`);
    await ext!.activate();
  });

  it("a truncatable sequence cell produces an application/x-fsquery-table+json item", async function () {
    this.timeout(120000);

    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "fsqnb-f3-"));
    const file = path.join(dir, "grid.fsqnb");
    fs.writeFileSync(
      file,
      JSON.stringify({
        version: 1,
        cells: [{ kind: "code", language: "fsharp", value: "seq { 1 .. 600 }" }],
        metadata: {},
      }),
    );

    const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(file));
    await vscode.window.showNotebookDocument(doc);

    await vscode.commands.executeCommand("notebook.cell.execute", {
      ranges: [{ start: 0, end: 1 }],
      document: doc.uri,
    });

    // Poll until the execution has produced output (the controller resolves async).
    const cell = doc.cellAt(0);
    for (let i = 0; i < 60 && cell.outputs.length === 0; i++) {
      await new Promise((r) => setTimeout(r, 500));
    }

    const mimes = cell.outputs.flatMap((o) => o.items).map((it) => it.mime);
    assert.ok(
      mimes.includes("application/x-fsquery-table+json"),
      `expected a table-json item, got: ${mimes.join(", ")}`,
    );
    assert.ok(
      mimes.includes("text/plain"),
      `expected the text/plain fallback alongside it, got: ${mimes.join(", ")}`,
    );
  });
});
