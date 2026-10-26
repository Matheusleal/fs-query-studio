import { describe, it, expect } from "vitest";
import * as vscode from "../../test/vscode-stub";
import {
  isInsideConnectionNameArgument,
  registerConnectionNameCompletion,
} from "./connection-name-completion";

const token = { isCancellationRequested: false } as never;

describe("isInsideConnectionNameArgument", () => {
  it.each([
    'Connections.sql "',
    'Connections.csv "',
    'Connections.excel "',
    'Connections.rest "',
    'Connections.connect "',
    'Connections.connectCsv "',
    'Connections.connectExcel "',
    'Connections.connectRest "',
    'Connections.queryCsv "',
    'Connections.queryExcel "',
    'Connections.queryJson "',
    'Connections.get "',
    'Connections.query "',
    'Connections.sql("',
    'Connections.connect("',
    'Connections.connectCsv("',
    'Connections.sql ( "',
    '  let x = Connections.sql "prod',
    '  let c = Connections.connect "prod',
    '  let c2 = Connections.connectCsv "feed',
    'Connections.rest   "partial name with spaces',
  ])("is true for prefix %j", (prefix) => {
    expect(isInsideConnectionNameArgument(prefix)).toBe(true);
  });

  it.each([
    "",
    "let x = 1",
    'let x = "not a connection',
    'Connections.sql "closed" ',
    'Connections.sql "closed"',
    'Connections.table "',
    'Connections.other "',
    'Connections.sqlite "',
    "Connections.sql ",
    "Connections.sql",
  ])("is false for prefix %j", (prefix) => {
    expect(isInsideConnectionNameArgument(prefix)).toBe(false);
  });
});

function cellDoc(text: string) {
  return {
    uri: { toString: () => "cell-1", scheme: "vscode-notebook-cell" },
    languageId: "fsharp",
    getText: () => text,
  } as never;
}

describe("registerConnectionNameCompletion", () => {
  it("registers one completion provider triggered on the double quote", () => {
    const before = vscode.languages._completion.length;
    const sub = registerConnectionNameCompletion({
      listConnectionNames: async () => [],
    });
    const added = vscode.languages._completion[vscode.languages._completion.length - 1];
    expect(vscode.languages._completion.length).toBe(before + 1);
    expect(added.triggers).toContain('"');
    sub.dispose();
  });

  it("offers the registered connection names inside a Connections.sql string argument", async () => {
    const sub = registerConnectionNameCompletion({
      listConnectionNames: async () => ["prod-db", "analytics"],
    });
    const provider = vscode.languages._completion[vscode.languages._completion.length - 1]
      .provider as {
      provideCompletionItems(doc: unknown, pos: unknown, tok: unknown): Promise<unknown[] | undefined>;
    };

    const line = 'let db = Connections.sql "';
    const items = (await provider.provideCompletionItems(
      cellDoc(line),
      new vscode.Position(0, line.length),
      token,
    )) as Array<{ label: string; kind: number }>;

    expect(items.map((i) => i.label)).toEqual(["prod-db", "analytics"]);
    expect(items.every((i) => i.kind === vscode.CompletionItemKind.Value)).toBe(true);
    sub.dispose();
  });

  it("returns undefined when the cursor is not inside a connection-name argument", async () => {
    const sub = registerConnectionNameCompletion({
      listConnectionNames: async () => ["prod-db"],
    });
    const provider = vscode.languages._completion[vscode.languages._completion.length - 1]
      .provider as {
      provideCompletionItems(doc: unknown, pos: unknown, tok: unknown): Promise<unknown[] | undefined>;
    };

    const line = "let x = 1";
    const items = await provider.provideCompletionItems(
      cellDoc(line),
      new vscode.Position(0, line.length),
      token,
    );

    expect(items).toBeUndefined();
    sub.dispose();
  });

  it("reads the prefix on the cursor's own line in a multi-line cell", async () => {
    const sub = registerConnectionNameCompletion({
      listConnectionNames: async () => ["prod-db"],
    });
    const provider = vscode.languages._completion[vscode.languages._completion.length - 1]
      .provider as {
      provideCompletionItems(doc: unknown, pos: unknown, tok: unknown): Promise<unknown[] | undefined>;
    };

    const text = ["let a = 1", 'let db = Connections.csv "'].join("\n");
    const items = (await provider.provideCompletionItems(
      cellDoc(text),
      new vscode.Position(1, 'let db = Connections.csv "'.length),
      token,
    )) as unknown[] | undefined;

    expect(items).toHaveLength(1);
    sub.dispose();
  });

  it("does not query names when the predicate does not match", async () => {
    let called = 0;
    const sub = registerConnectionNameCompletion({
      listConnectionNames: async () => {
        called += 1;
        return [];
      },
    });
    const provider = vscode.languages._completion[vscode.languages._completion.length - 1]
      .provider as {
      provideCompletionItems(doc: unknown, pos: unknown, tok: unknown): Promise<unknown[] | undefined>;
    };

    await provider.provideCompletionItems(cellDoc("let x = 1"), new vscode.Position(0, 9), token);
    expect(called).toBe(0);
    sub.dispose();
  });
});
