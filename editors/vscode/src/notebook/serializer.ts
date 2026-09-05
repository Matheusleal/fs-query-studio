import * as vscode from "vscode";
import {
  deserializeFsqnb,
  serializeFsqnb,
  type FsqnbCell,
  type FsqnbDocument,
} from "./fsqnb";

export const FSQNB_NOTEBOOK_TYPE = "fsquery-notebook";

/**
 * Adapts the pure `.fsqnb` codec to `vscode.NotebookSerializer`. Never
 * reads or writes cell outputs — the format does not carry them.
 */
export class FsqnbSerializer implements vscode.NotebookSerializer {
  deserializeNotebook(content: Uint8Array): vscode.NotebookData {
    const doc = deserializeFsqnb(new TextDecoder().decode(content));
    const cells = doc.cells.map((c) => {
      const kind =
        c.kind === "markdown" ? vscode.NotebookCellKind.Markup : vscode.NotebookCellKind.Code;
      const languageId = c.kind === "markdown" ? "markdown" : c.language || "fsharp";
      return new vscode.NotebookCellData(kind, c.value, languageId);
    });
    const data = new vscode.NotebookData(cells);
    data.metadata = doc.metadata;
    return data;
  }

  serializeNotebook(data: vscode.NotebookData): Uint8Array {
    const doc: FsqnbDocument = {
      version: 1,
      cells: data.cells.map<FsqnbCell>((c) =>
        c.kind === vscode.NotebookCellKind.Markup
          ? { kind: "markdown", language: "markdown", value: c.value }
          : { kind: "code", language: c.languageId || "fsharp", value: c.value },
      ),
      metadata: (data.metadata as Record<string, unknown>) ?? {},
    };
    return new TextEncoder().encode(serializeFsqnb(doc));
  }
}
