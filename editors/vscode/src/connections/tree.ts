import * as vscode from "vscode";
import {
  newRequestId,
  readUnionTag,
  type ConnectionDefinition,
  type ConnectionListResult,
  type ConnectionSchemaResult,
  type SchemaColumn,
  type SchemaTable,
  type WorkerResponseFrame,
} from "../protocol";
import type { SchemaCache } from "./schema-cache";

export type WorkerLike = {
  send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame>;
};

export type ConnNode =
  | { t: "connection"; def: ConnectionDefinition }
  | { t: "table"; connId: string; table: SchemaTable }
  | { t: "column"; column: SchemaColumn }
  | { t: "error"; message: string };

export interface ConnectionsTreeDeps {
  getWorker: () => Promise<WorkerLike>;
  schemaCache: SchemaCache;
  log: (line: string) => void;
}

export class ConnectionsTreeProvider implements vscode.TreeDataProvider<ConnNode> {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.changed.event;

  constructor(private readonly deps: ConnectionsTreeDeps) {}

  refresh(): void {
    this.changed.fire();
  }

  getTreeItem(node: ConnNode): vscode.TreeItem {
    switch (node.t) {
      case "connection": {
        const item = new vscode.TreeItem(node.def.name, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = readUnionTag(node.def.kind) ?? "";
        item.contextValue = "fsqsConnection";
        item.iconPath = new vscode.ThemeIcon("database");
        return item;
      }
      case "table": {
        const item = new vscode.TreeItem(node.table.name, vscode.TreeItemCollapsibleState.Collapsed);
        item.iconPath = new vscode.ThemeIcon("table");
        return item;
      }
      case "column": {
        const item = new vscode.TreeItem(node.column.name, vscode.TreeItemCollapsibleState.None);
        item.description = node.column.dataType;
        item.iconPath = new vscode.ThemeIcon("symbol-field");
        return item;
      }
      case "error": {
        const item = new vscode.TreeItem(node.message, vscode.TreeItemCollapsibleState.None);
        item.iconPath = new vscode.ThemeIcon("warning");
        return item;
      }
    }
  }

  async getChildren(node?: ConnNode): Promise<ConnNode[]> {
    if (!node) {
      return this.listConnections();
    }
    if (node.t === "connection") {
      return this.tablesOf(node.def);
    }
    if (node.t === "table") {
      return node.table.columns.map((column) => ({ t: "column", column }));
    }
    return [];
  }

  private async listConnections(): Promise<ConnNode[]> {
    try {
      const worker = await this.deps.getWorker();
      const frame = await worker.send({ id: newRequestId(), method: "connection/list", params: {} });
      if (frame.error) {
        return [{ t: "error", message: `Could not list connections: ${frame.error.message}` }];
      }
      const result = frame.result as ConnectionListResult;
      return (result.connections ?? []).map((def) => ({ t: "connection", def }));
    } catch (err) {
      this.deps.log(`connection/list failed: ${(err as Error).message}`);
      return [{ t: "error", message: `Connections unavailable: ${(err as Error).message}` }];
    }
  }

  private async tablesOf(def: ConnectionDefinition): Promise<ConnNode[]> {
    const cached = this.deps.schemaCache.get(def.id);
    const tables = cached ?? (await this.fetchSchema(def));
    if (tables === undefined) {
      return [{ t: "error", message: this.lastSchemaError ?? "Schema discovery failed." }];
    }
    return tables.map((table) => ({ t: "table", connId: def.id, table }));
  }

  private lastSchemaError: string | undefined;

  private async fetchSchema(def: ConnectionDefinition): Promise<SchemaTable[] | undefined> {
    try {
      const worker = await this.deps.getWorker();
      const frame = await worker.send({
        id: newRequestId(),
        method: "connection/schema",
        params: { id: def.id },
      });
      if (frame.error) {
        this.lastSchemaError = `${def.name}: ${frame.error.message}`;
        return undefined;
      }
      const tables = (frame.result as ConnectionSchemaResult).tables ?? [];
      this.deps.schemaCache.set(def.id, tables);
      return tables;
    } catch (err) {
      this.lastSchemaError = `${def.name}: ${(err as Error).message}`;
      this.deps.log(`connection/schema failed: ${(err as Error).message}`);
      return undefined;
    }
  }
}
