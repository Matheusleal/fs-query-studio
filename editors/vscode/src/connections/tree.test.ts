import { describe, it, expect, vi } from "vitest";
import { ConnectionsTreeProvider, type WorkerLike } from "./tree";
import { SchemaCache } from "./schema-cache";

const conn = (id: string, name: string) => ({
  id,
  name,
  kind: { Case: "Sqlite" as const },
  configuration: {},
  secretReference: null,
  isTrusted: false,
});

function provider(send: WorkerLike["send"]) {
  const schemaCache = new SchemaCache();
  const p = new ConnectionsTreeProvider({
    getWorker: async () => ({ send }),
    schemaCache,
    log: () => {},
  });
  return { p, schemaCache };
}

describe("ConnectionsTreeProvider", () => {
  it("lists connections from connection/list", async () => {
    const { p } = provider(async (req) => {
      expect(req.method).toBe("connection/list");
      return { id: req.id, result: { connections: [conn("1", "Sales"), conn("2", "Ops")] } };
    });
    const roots = await p.getChildren();
    expect(roots.map((n) => (n.t === "connection" ? n.def.name : n.t))).toEqual(["Sales", "Ops"]);
  });

  it("shows one error node when connection/list fails", async () => {
    const { p } = provider(async (req) => ({ id: req.id, error: { code: "worker/crashed", message: "gone" } }));
    const roots = await p.getChildren();
    expect(roots).toEqual([{ t: "error", message: expect.stringContaining("gone") }]);
  });

  it("fetches schema once and serves the second expand from cache", async () => {
    const send = vi.fn(async (req: { id: string; method: string; params: unknown }) => {
      if (req.method === "connection/schema") {
        return { id: req.id, result: { tables: [{ name: "orders", columns: [{ name: "id", dataType: "int" }] }] } };
      }
      return { id: req.id, result: { connections: [] } };
    });
    const { p } = provider(send);
    const node = { t: "connection" as const, def: conn("1", "Sales") };
    const first = await p.getChildren(node);
    const second = await p.getChildren(node);
    expect(first.map((n) => (n.t === "table" ? n.table.name : n.t))).toEqual(["orders"]);
    expect(second).toEqual(first);
    expect(send.mock.calls.filter((c) => c[0].method === "connection/schema")).toHaveLength(1);
  });

  it("shows an error child when connection/schema fails", async () => {
    const { p } = provider(async (req) => ({ id: req.id, error: { code: "connection/schemaFailed", message: "bad creds" } }));
    const kids = await p.getChildren({ t: "connection", def: conn("1", "Sales") });
    expect(kids).toEqual([{ t: "error", message: expect.stringContaining("bad creds") }]);
  });

  it("expands a table into columns", async () => {
    const { p } = provider(async (req) => ({ id: req.id, result: { connections: [] } }));
    const kids = await p.getChildren({
      t: "table",
      connId: "1",
      table: { name: "orders", columns: [{ name: "id", dataType: "int" }, { name: "total", dataType: "money" }] },
    });
    expect(kids.map((n) => (n.t === "column" ? `${n.column.name}:${n.column.dataType}` : n.t))).toEqual([
      "id:int",
      "total:money",
    ]);
  });

  it("refresh fires onDidChangeTreeData", () => {
    const { p } = provider(async (req) => ({ id: req.id, result: { connections: [] } }));
    const fn = vi.fn();
    p.onDidChangeTreeData(fn);
    p.refresh();
    expect(fn).toHaveBeenCalledOnce();
  });
});
