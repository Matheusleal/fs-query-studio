import * as assert from "node:assert";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";

const EXTENSION_ID = "fs-query-studio.fs-query-studio";
const extensionRoot = path.resolve(__dirname, "..", "..", "..");
const fixture = path.join(
  extensionRoot,
  "test",
  "integration",
  "fixtures",
  "sample-intelligence.fsqnb",
);
const bundledWorker = path.join(extensionRoot, "dist", "worker", "FSharpQueryStudio.Worker.dll");
const hasWorker = fs.existsSync(bundledWorker);

async function delay(ms: number) {
  await new Promise((r) => setTimeout(r, ms));
}

describe("fsquery-notebook cell intelligence", () => {
  before(async () => {
    const ext = vscode.extensions.getExtension(EXTENSION_ID);
    assert.ok(ext, `extension ${EXTENSION_ID} not found`);
    await ext!.activate();
  });

  (hasWorker ? it : it.skip)(
    "offers a completion from an executed sibling cell",
    async function () {
      this.timeout(120000);
      const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
      await vscode.window.showNotebookDocument(doc);

      // Run cell 1 (`let answer = 42`) so it lands in the executed prefix.
      await vscode.commands.executeCommand("notebook.cell.execute", {
        ranges: [{ start: 1, end: 2 }],
        document: doc.uri,
      });
      for (let i = 0; i < 60 && doc.cellAt(1).outputs.length === 0; i++) {
        await delay(500);
      }

      const target = doc.cellAt(2).document; // "answ"
      const list = (await vscode.commands.executeCommand(
        "vscode.executeCompletionItemProvider",
        target.uri,
        new vscode.Position(0, 4),
      )) as vscode.CompletionList;

      const labels = list.items.map((i) =>
        typeof i.label === "string" ? i.label : i.label.label,
      );
      assert.ok(
        labels.includes("answer"),
        `expected 'answer' among completions, got: ${labels.slice(0, 20).join(", ")}`,
      );
    },
  );

  (hasWorker ? it : it.skip)("reports a diagnostic for a broken edit", async function () {
    this.timeout(120000);
    const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
    await vscode.window.showNotebookDocument(doc);

    const target = doc.cellAt(2).document;
    const edit = new vscode.WorkspaceEdit();
    edit.replace(target.uri, new vscode.Range(0, 0, target.lineCount, 0), "answer +");
    await vscode.workspace.applyEdit(edit);

    let diags: vscode.Diagnostic[] = [];
    for (let i = 0; i < 60; i++) {
      diags = vscode.languages.getDiagnostics(target.uri);
      if (diags.length > 0) break;
      await delay(500);
    }
    assert.ok(diags.length > 0, "expected at least one diagnostic on the broken cell");
    assert.strictEqual(diags[0].source, "F# Query Studio");
  });
});
