import { describe, it, expect, vi } from "vitest";
import * as vscode from "vscode";
import { ConnectionsTreeProvider, type WorkerLike } from "./tree";
import { SchemaCache } from "./schema-cache";
import type { ConnectionDefinition } from "../protocol";

function createMockProvider(send: WorkerLike["send"]) {
  const schemaCache = new SchemaCache();
  const provider = new ConnectionsTreeProvider({
    getWorker: async () => ({ send }),
    schemaCache,
    log: () => {},
  });
  return { provider, schemaCache };
}

describe("Connections Tree View UI Elements", () => {
  const mockSqliteDef: ConnectionDefinition = {
    id: "conn-sqlite-1",
    name: "Local Inventory",
    kind: { Case: "Sqlite" as const },
    configuration: {},
    secretReference: null,
    isTrusted: false,
  };

  const mockPgDef: ConnectionDefinition = {
    id: "conn-pg-1",
    name: "Production Postgres",
    kind: { Case: "PostgreSql" as const },
    configuration: {},
    secretReference: null,
    isTrusted: true,
  };

  it("builds TreeItem for connection node with database icon, kind description and contextValue", () => {
    const { provider } = createMockProvider(async (req) => ({ id: req.id, result: { connections: [] } }));
    const item = provider.getTreeItem({ t: "connection", def: mockSqliteDef });

    expect(item.label).toBe("Local Inventory");
    expect(item.description).toBe("Sqlite");
    expect(item.contextValue).toBe("fsqsConnection");
    expect(item.collapsibleState).toBe(vscode.TreeItemCollapsibleState.Collapsed);
    expect(item.iconPath).toBeInstanceOf(vscode.ThemeIcon);
    expect((item.iconPath as vscode.ThemeIcon).id).toBe("database");
  });

  it("builds TreeItem for multi-schema folder node with symbol-namespace icon and table count", () => {
    const { provider } = createMockProvider(async (req) => ({ id: req.id, result: { connections: [] } }));
    const item = provider.getTreeItem({
      t: "schema",
      connId: "conn-pg-1",
      schemaName: "billing",
      tables: [
        { name: "billing.invoices", columns: [] },
        { name: "billing.subscriptions", columns: [] },
      ],
    });

    expect(item.label).toBe("billing");
    expect(item.description).toBe("2 tables");
    expect(item.collapsibleState).toBe(vscode.TreeItemCollapsibleState.Collapsed);
    expect(item.iconPath).toBeInstanceOf(vscode.ThemeIcon);
    expect((item.iconPath as vscode.ThemeIcon).id).toBe("symbol-namespace");
  });

  it("builds TreeItem for table node with table icon and displayName", () => {
    const { provider } = createMockProvider(async (req) => ({ id: req.id, result: { connections: [] } }));
    const item = provider.getTreeItem({
      t: "table",
      connId: "conn-pg-1",
      table: { name: "public.orders", columns: [] },
      displayName: "orders",
    });

    expect(item.label).toBe("orders");
    expect(item.collapsibleState).toBe(vscode.TreeItemCollapsibleState.Collapsed);
    expect(item.iconPath).toBeInstanceOf(vscode.ThemeIcon);
    expect((item.iconPath as vscode.ThemeIcon).id).toBe("table");
  });

  it("builds TreeItem for column node with symbol-field icon, dataType description and None collapsibleState", () => {
    const { provider } = createMockProvider(async (req) => ({ id: req.id, result: { connections: [] } }));
    const item = provider.getTreeItem({
      t: "column",
      column: { name: "created_at", dataType: "timestamptz" },
    });

    expect(item.label).toBe("created_at");
    expect(item.description).toBe("timestamptz");
    expect(item.collapsibleState).toBe(vscode.TreeItemCollapsibleState.None);
    expect(item.iconPath).toBeInstanceOf(vscode.ThemeIcon);
    expect((item.iconPath as vscode.ThemeIcon).id).toBe("symbol-field");
  });

  it("builds TreeItem for error node with warning icon and message", () => {
    const { provider } = createMockProvider(async (req) => ({ id: req.id, result: { connections: [] } }));
    const item = provider.getTreeItem({
      t: "error",
      message: "Authentication failed: invalid token",
    });

    expect(item.label).toBe("Authentication failed: invalid token");
    expect(item.collapsibleState).toBe(vscode.TreeItemCollapsibleState.None);
    expect(item.iconPath).toBeInstanceOf(vscode.ThemeIcon);
    expect((item.iconPath as vscode.ThemeIcon).id).toBe("warning");
  });
});
