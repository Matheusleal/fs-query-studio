import { describe, it, expect, vi } from "vitest";
import * as vscode from "vscode";
import {
  emptyNotebookData,
  emptyFsqnbBytes,
  openNewUntitledNotebook,
  createNotebookFileInFolder,
  NotebookFileExistsError,
  type CreateNotebookFileDeps,
} from "./new-notebook";
import { FSQNB_NOTEBOOK_TYPE } from "./serializer";

describe("emptyNotebookData", () => {
  it("is one empty F# code cell with empty metadata", () => {
    const data = emptyNotebookData();
    expect(data.cells).toHaveLength(1);
    const [cell] = data.cells;
    expect(cell.kind).toBe(vscode.NotebookCellKind.Code);
    expect(cell.languageId).toBe("fsharp");
    expect(cell.value).toBe("");
    expect(data.metadata).toEqual({});
  });
});

describe("emptyFsqnbBytes", () => {
  it("decodes to a valid v1 document with one empty code cell", () => {
    const doc = JSON.parse(new TextDecoder().decode(emptyFsqnbBytes()));
    expect(doc.version).toBe(1);
    expect(doc.cells).toEqual([{ kind: "code", language: "fsharp", value: "" }]);
    expect(doc.metadata).toEqual({});
  });
});

describe("openNewUntitledNotebook", () => {
  it("opens an fsquery-notebook from emptyNotebookData, then shows it", async () => {
    const opened: Array<{ type: string; data: vscode.NotebookData }> = [];
    const shown: unknown[] = [];
    const doc = { id: "untitled-1" };

    await openNewUntitledNotebook({
      openNotebookDocument: async (type, data) => {
        opened.push({ type, data });
        return doc;
      },
      showNotebookDocument: async (d) => {
        shown.push(d);
      },
    });

    expect(opened).toHaveLength(1);
    expect(opened[0].type).toBe(FSQNB_NOTEBOOK_TYPE);
    expect(opened[0].data.cells).toHaveLength(1);
    expect(shown).toEqual([doc]);
  });
});

function createDeps(overrides: Partial<CreateNotebookFileDeps> = {}) {
  const written: Array<{ path: string; bytes: Uint8Array }> = [];
  const openedPaths: string[] = [];
  const base: CreateNotebookFileDeps = {
    showInputBox: async () => "report",
    fileExists: async () => false,
    writeFile: async (path, bytes) => void written.push({ path, bytes }),
    openNotebook: async (path) => void openedPaths.push(path),
    joinPath: (dir, name) => `${dir}/${name}`,
    ...overrides,
  };
  return { deps: base, written, openedPaths };
}

describe("createNotebookFileInFolder", () => {
  it("appends .fsqnb, writes the empty document, and opens it", async () => {
    const { deps, written, openedPaths } = createDeps({ showInputBox: async () => "report" });
    const result = await createNotebookFileInFolder("/ws/queries", deps);

    expect(result).toBe("/ws/queries/report.fsqnb");
    expect(written).toHaveLength(1);
    expect(written[0].path).toBe("/ws/queries/report.fsqnb");
    const doc = JSON.parse(new TextDecoder().decode(written[0].bytes));
    expect(doc.version).toBe(1);
    expect(doc.cells).toEqual([{ kind: "code", language: "fsharp", value: "" }]);
    expect(openedPaths).toEqual(["/ws/queries/report.fsqnb"]);
  });

  it("keeps an existing .fsqnb extension", async () => {
    const { deps, written } = createDeps({ showInputBox: async () => "a.fsqnb" });
    const result = await createNotebookFileInFolder("/ws", deps);
    expect(result).toBe("/ws/a.fsqnb");
    expect(written[0].path).toBe("/ws/a.fsqnb");
  });

  it("trims surrounding whitespace from the typed name", async () => {
    const { deps, written } = createDeps({ showInputBox: async () => "  spaced  " });
    await createNotebookFileInFolder("/ws", deps);
    expect(written[0].path).toBe("/ws/spaced.fsqnb");
  });

  it("returns undefined and writes nothing when the prompt is cancelled", async () => {
    const { deps, written, openedPaths } = createDeps({ showInputBox: async () => undefined });
    const result = await createNotebookFileInFolder("/ws", deps);
    expect(result).toBeUndefined();
    expect(written).toHaveLength(0);
    expect(openedPaths).toHaveLength(0);
  });

  it("throws NotebookFileExistsError and writes nothing when the target exists", async () => {
    const { deps, written } = createDeps({
      showInputBox: async () => "dup",
      fileExists: async () => true,
    });
    await expect(createNotebookFileInFolder("/ws", deps)).rejects.toBeInstanceOf(
      NotebookFileExistsError,
    );
    expect(written).toHaveLength(0);
  });

  it("passes a validateInput that rejects empty and path-separator names", async () => {
    let captured: ((v: string) => string | undefined) | undefined;
    const { deps } = createDeps({
      showInputBox: async (opts) => {
        captured = opts.validateInput;
        return "ok";
      },
    });
    await createNotebookFileInFolder("/ws", deps);
    expect(captured).toBeTypeOf("function");
    expect(captured!("")).toBeTypeOf("string");
    expect(captured!("   ")).toBeTypeOf("string");
    expect(captured!("a/b")).toBeTypeOf("string");
    expect(captured!("a\\b")).toBeTypeOf("string");
    expect(captured!("fine")).toBeUndefined();
    expect(captured!("fine.fsqnb")).toBeUndefined();
  });
});
