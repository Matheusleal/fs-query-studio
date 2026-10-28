import { describe, it, expect } from "vitest";
// The shared vscode stub is aliased to "vscode" at runtime by vitest; import it
// directly here so `tsc` sees the stub's test-only surface (`languages._completion`
// et al.) instead of the real `@types/vscode` namespace.
import * as vscode from "../../test/vscode-stub";
import { registerLanguageFeatures } from "./providers";
import type { WorkerResponseFrame } from "../protocol";

function fsharpCellDoc(uri: string, text: string) {
  return {
    uri: { toString: () => uri, scheme: "vscode-notebook-cell" },
    languageId: "fsharp",
    getText: () => text,
    lineCount: text.split("\n").length,
    version: 1,
  };
}

/** Register one fake notebook containing `docs` as its cells. */
function stageNotebook(cellDocs: ReturnType<typeof fsharpCellDoc>[]) {
  (vscode.workspace as unknown as { notebookDocuments: unknown[] }).notebookDocuments = [
    {
      notebookType: "fsquery-notebook",
      getCells: () =>
        cellDocs.map((document, i) => ({
          kind: 2,
          document,
          executionSummary: i === 0 ? { executionOrder: 1 } : undefined,
        })),
    },
  ];
}

function worker(result: unknown): { getWorker: () => Promise<{ send: () => Promise<WorkerResponseFrame> }> } {
  return {
    getWorker: async () => ({ send: async () => ({ id: "1", result }) as WorkerResponseFrame }),
  };
}

const token = { isCancellationRequested: false } as never;

describe("registerLanguageFeatures", () => {
  it("registers the FSAC + connection-name completion, hover, signature-help, definition, and rename providers and a diagnostic collection", () => {
    const before = {
      c: vscode.languages._completion.length,
      h: vscode.languages._hover.length,
      s: vscode.languages._signature.length,
      def: vscode.languages._definition.length,
      ren: vscode.languages._rename.length,
      d: vscode.languages._collections.length,
    };
    const sub = registerLanguageFeatures({ getWorker: async () => ({ send: async () => ({ id: "1" }) }), log: () => {} });
    expect(vscode.languages._completion.length).toBe(before.c + 2);
    expect(vscode.languages._hover.length).toBe(before.h + 1);
    expect(vscode.languages._signature.length).toBe(before.s + 1);
    expect(vscode.languages._definition.length).toBe(before.def + 1);
    expect(vscode.languages._rename.length).toBe(before.ren + 1);
    expect(vscode.languages._collections.length).toBe(before.d + 1);
    sub.dispose();
  });

  it("completion maps analysis items to CompletionItems", async () => {
    const cell = fsharpCellDoc("c2", "answ");
    stageNotebook([fsharpCellDoc("c1", "let answer = 42"), cell]);
    registerLanguageFeatures({ ...worker({ items: ["answer"] }), log: () => {} });
    // -2: registerLanguageFeatures pushes the FSAC provider then the
    // connection-name provider; these tests exercise the FSAC one.
    const provider = vscode.languages._completion.at(-2)!.provider as {
      provideCompletionItems: (d: unknown, p: unknown, t: unknown) => Promise<unknown[]>;
    };
    const items = await provider.provideCompletionItems(cell, new vscode.Position(0, 4), token);
    expect(items.map((i) => (i as vscode.CompletionItem).label)).toEqual(["answer"]);
  });

  it("completion maps detailedItems with description and detail", async () => {
    const cell = fsharpCellDoc("c2", "answ");
    stageNotebook([fsharpCellDoc("c1", "let answer = 42"), cell]);
    registerLanguageFeatures({
      ...worker({ items: ["answer"], detailedItems: [{ label: "answer", detail: "int" }] }),
      log: () => {},
    });
    const provider = vscode.languages._completion.at(-2)!.provider as {
      provideCompletionItems: (d: unknown, p: unknown, t: unknown) => Promise<unknown[]>;
    };
    const items = await provider.provideCompletionItems(cell, new vscode.Position(0, 4), token);
    const first = items[0] as vscode.CompletionItem;
    expect(first.label).toEqual({ label: "answer", description: "int" });
    expect(first.detail).toBe("int");
  });

  it("completion sends a .fsx-suffixed documentUri to the worker", async () => {
    const cell = fsharpCellDoc("c2", "answ");
    stageNotebook([fsharpCellDoc("c1", "let answer = 42"), cell]);
    const requests: Array<{ params: { documentUri: string } }> = [];
    registerLanguageFeatures({
      getWorker: async () => ({
        send: async (req: unknown) => {
          requests.push(req as { params: { documentUri: string } });
          return { id: "1", result: { items: ["answer"] } } as WorkerResponseFrame;
        },
      }),
      log: () => {},
    });
    const provider = vscode.languages._completion.at(-2)!.provider as {
      provideCompletionItems: (d: unknown, p: unknown, t: unknown) => Promise<unknown[]>;
    };
    await provider.provideCompletionItems(cell, new vscode.Position(0, 4), token);
    expect(requests).toHaveLength(1);
    expect(requests[0].params.documentUri).toBe("c2.fsx");
    expect(requests[0].params.documentUri.endsWith(".fsx")).toBe(true);
  });

  it("completion returns undefined for a document that is not a cell in any notebook", async () => {
    (vscode.workspace as unknown as { notebookDocuments: unknown[] }).notebookDocuments = [];
    registerLanguageFeatures({ ...worker({ items: ["x"] }), log: () => {} });
    const provider = vscode.languages._completion.at(-2)!.provider as {
      provideCompletionItems: (d: unknown, p: unknown, t: unknown) => Promise<unknown>;
    };
    const out = await provider.provideCompletionItems(fsharpCellDoc("orphan", "x"), new vscode.Position(0, 1), token);
    expect(out).toBeUndefined();
  });

  it("hover builds a markdown Hover, or undefined when there is nothing", async () => {
    const cell = fsharpCellDoc("c2", "answer");
    stageNotebook([fsharpCellDoc("c1", "let answer = 42"), cell]);

    registerLanguageFeatures({
      ...worker({ hover: { displayName: "answer", type: "int", signature: null, xmlDoc: null } }),
      log: () => {},
    });
    let provider = vscode.languages._hover.at(-1)!.provider as {
      provideHover: (d: unknown, p: unknown, t: unknown) => Promise<vscode.Hover | undefined>;
    };
    const hover = await provider.provideHover(cell, new vscode.Position(0, 3), token);
    expect((hover!.contents as vscode.MarkdownString).value).toMatch(/answer : int/);

    registerLanguageFeatures({ ...worker({ hover: null }), log: () => {} });
    provider = vscode.languages._hover.at(-1)!.provider as typeof provider;
    expect(await provider.provideHover(cell, new vscode.Position(0, 3), token)).toBeUndefined();
  });

  it("signature help builds one signature with a parameter list", async () => {
    const cell = fsharpCellDoc("c2", "id (");
    stageNotebook([fsharpCellDoc("c1", "let id x = x"), cell]);
    registerLanguageFeatures({
      ...worker({ signatureHelp: { displayName: "id", parameters: ["x: 'a"], returnType: "'a" } }),
      log: () => {},
    });
    const provider = vscode.languages._signature.at(-1)!.provider as {
      provideSignatureHelp: (d: unknown, p: unknown, t: unknown) => Promise<vscode.SignatureHelp | undefined>;
    };
    const help = await provider.provideSignatureHelp(cell, new vscode.Position(0, 4), token);
    expect(help!.signatures[0].label).toBe("id(x: 'a) : 'a");
    expect(help!.signatures[0].parameters.map((p) => p.label)).toEqual(["x: 'a"]);
    expect(help!.activeParameter).toBe(0);
  });

  it("definition maps analysis location to a Location, or undefined when not found", async () => {
    const cell = fsharpCellDoc("c2", "answer");
    stageNotebook([fsharpCellDoc("c1", "let answer = 42"), cell]);

    registerLanguageFeatures({
      ...worker({ location: { filePath: "c2.fsx", startLine: 0, startColumn: 4, endLine: 0, endColumn: 10 } }),
      log: () => {},
    });
    let provider = vscode.languages._definition.at(-1)!.provider as {
      provideDefinition: (d: unknown, p: unknown, t: unknown) => Promise<vscode.Location | undefined>;
    };
    const loc = await provider.provideDefinition(cell, new vscode.Position(0, 2), token);
    expect(loc).toBeDefined();
    expect(loc!.range.start.line).toBe(0);
    expect(loc!.range.start.character).toBe(4);

    registerLanguageFeatures({ ...worker({ location: null }), log: () => {} });
    provider = vscode.languages._definition.at(-1)!.provider as typeof provider;
    expect(await provider.provideDefinition(cell, new vscode.Position(0, 2), token)).toBeUndefined();
  });

  it("definition maps external file URI properly using vscode.Uri.parse", async () => {
    const cell = fsharpCellDoc("c2", "Connections.sql");
    stageNotebook([cell]);

    registerLanguageFeatures({
      ...worker({
        location: {
          filePath: "file:///d:/repo/GeneratedEntities.fs",
          startLine: 12,
          startColumn: 4,
          endLine: 12,
          endColumn: 20,
        },
      }),
      log: () => {},
    });
    const provider = vscode.languages._definition.at(-1)!.provider as {
      provideDefinition: (d: unknown, p: unknown, t: unknown) => Promise<vscode.Location | undefined>;
    };
    const loc = await provider.provideDefinition(cell, new vscode.Position(0, 2), token);
    expect(loc).toBeDefined();
    const uri = loc!.uri as { scheme: string; fsPath: string };
    expect(uri.scheme).toBe("file");
    expect(uri.fsPath.replace(/\\/g, "/")).toContain("GeneratedEntities.fs");
    expect(loc!.range.start.line).toBe(12);
    expect(loc!.range.start.character).toBe(4);
  });

  it("rename maps edits to a WorkspaceEdit, or undefined when not found", async () => {
    const cell = fsharpCellDoc("c2", "answer + answer");
    stageNotebook([cell]);

    registerLanguageFeatures({
      ...worker({
        edits: [
          { startLine: 0, startColumn: 0, endLine: 0, endColumn: 6 },
          { startLine: 0, startColumn: 9, endLine: 0, endColumn: 15 },
        ],
      }),
      log: () => {},
    });
    let provider = vscode.languages._rename.at(-1)!.provider as {
      provideRenameEdits: (d: unknown, p: unknown, n: string, t: unknown) => Promise<vscode.WorkspaceEdit | undefined>;
    };
    const edit = await provider.provideRenameEdits(cell, new vscode.Position(0, 2), "solution", token);
    expect(edit).toBeDefined();
    const changes = edit!.get(cell.uri);
    expect(changes).toHaveLength(2);
    expect(changes[0].newText).toBe("solution");

    registerLanguageFeatures({ ...worker({ edits: null }), log: () => {} });
    provider = vscode.languages._rename.at(-1)!.provider as typeof provider;
    expect(await provider.provideRenameEdits(cell, new vscode.Position(0, 2), "solution", token)).toBeUndefined();
  });
});
