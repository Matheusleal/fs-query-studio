import * as assert from "node:assert";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";

const EXTENSION_ID = "fs-query-studio.fs-query-studio";

// out/test/integration -> editors/vscode
const extensionRoot = path.resolve(__dirname, "..", "..", "..");
const fixture = path.join(extensionRoot, "test", "integration", "fixtures", "sample.fsqnb");
const bundledWorker = path.join(
  extensionRoot,
  "dist",
  "worker",
  "FSharpQueryStudio.Worker.dll",
);
const hasWorker = fs.existsSync(bundledWorker);

function outputText(cell: vscode.NotebookCell): string {
  return cell.outputs
    .flatMap((o) => o.items)
    .map((i) => Buffer.from(i.data).toString("utf8"))
    .join("\n");
}

describe("fsquery-notebook integration", () => {
  before(async () => {
    const ext = vscode.extensions.getExtension(EXTENSION_ID);
    assert.ok(ext, `extension ${EXTENSION_ID} not found`);
    await ext!.activate();
  });

  it("fs-query-studio.newNotebook opens an untitled fsquery-notebook with one empty F# cell", async () => {
    await vscode.commands.executeCommand("fs-query-studio.newNotebook");
    const opened = vscode.workspace.notebookDocuments.find(
      (d) => d.notebookType === "fsquery-notebook" && d.uri.scheme === "untitled",
    );
    assert.ok(opened, "an untitled fsquery-notebook should be open");
    assert.strictEqual(opened!.cellCount, 1);
    assert.strictEqual(opened!.cellAt(0).kind, vscode.NotebookCellKind.Code);
    assert.strictEqual(opened!.cellAt(0).document.languageId, "fsharp");
    assert.strictEqual(opened!.cellAt(0).document.getText(), "");
  });

  it("opens an .fsqnb file as a notebook with the right cells", async () => {
    const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
    assert.strictEqual(doc.notebookType, "fsquery-notebook");
    assert.strictEqual(doc.cellCount, 3);
    assert.strictEqual(doc.cellAt(0).kind, vscode.NotebookCellKind.Markup);
    assert.strictEqual(doc.cellAt(1).kind, vscode.NotebookCellKind.Code);
    assert.strictEqual(doc.cellAt(1).document.languageId, "fsharp");
  });

  (hasWorker ? it : it.skip)(
    "runs a code cell and captures stdout + the tail value",
    async function () {
      this.timeout(120000);
      const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
      await vscode.window.showNotebookDocument(doc);

      await vscode.commands.executeCommand("notebook.cell.execute", {
        ranges: [{ start: 1, end: 2 }],
        document: doc.uri,
      });

      // Poll until the execution has produced output.
      const cell = doc.cellAt(1);
      for (let i = 0; i < 60 && cell.outputs.length === 0; i++) {
        await new Promise((r) => setTimeout(r, 500));
      }
      const text = outputText(cell);
      assert.match(text, /hello from fsi/);
      assert.match(text, /2/);
    },
  );

  (hasWorker ? it : it.skip)(
    "renders a table-shaped result as a text grid",
    async function () {
      this.timeout(120000);
      const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
      await vscode.window.showNotebookDocument(doc);

      await vscode.commands.executeCommand("notebook.cell.execute", {
        ranges: [{ start: 2, end: 3 }],
        document: doc.uri,
      });

      const cell = doc.cellAt(2);
      for (let i = 0; i < 60 && cell.outputs.length === 0; i++) {
        await new Promise((r) => setTimeout(r, 500));
      }
      const text = outputText(cell);
      assert.match(text, /\(3 rows · \d+ms\)/);
    },
  );

  (hasWorker ? it : it.skip)("restarts the session without error", async function () {
    this.timeout(60000);
    const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
    await vscode.window.showNotebookDocument(doc);
    await vscode.commands.executeCommand("fs-query-studio.restartKernel");
  });
});
