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
import { ConnectionHealthService, type ConnectionStatus } from "./health";

export { ConnectionStatus } from "./health";

export type WorkerLike = {
  send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame>;
};

export type ConnNode =
  | { t: "connection"; def: ConnectionDefinition }
  | { t: "schema"; connId: string; schemaName: string; fullPrefix?: string; tables: SchemaTable[] }
  | { t: "table"; connId: string; table: SchemaTable; displayName?: string }
  | { t: "column"; column: SchemaColumn }
  | { t: "error"; message: string };

export interface ConnectionsTreeDeps {
  getWorker: () => Promise<WorkerLike>;
  schemaCache: SchemaCache;
  log: (line: string) => void;
  getWorkspaceDir?: () => string | undefined;
  extensionUri?: vscode.Uri;
  retryDelayMs?: number;
  notify?: (type: "warning" | "error" | "info", message: string) => void;
  healthService?: ConnectionHealthService;
}

export class ConnectionsTreeProvider implements vscode.TreeDataProvider<ConnNode> {
  private readonly changed = new vscode.EventEmitter<ConnNode | void>();
  readonly onDidChangeTreeData = this.changed.event;

  readonly healthService: ConnectionHealthService;
  private readonly connNodes = new Map<string, ConnNode>();
  private readonly disposables: vscode.Disposable[] = [];
  private lastSchemaError: string | undefined;

  constructor(private readonly deps: ConnectionsTreeDeps) {
    this.healthService =
      deps.healthService ??
      new ConnectionHealthService({
        retryDelayMs: deps.retryDelayMs,
        log: deps.log,
      });

    this.disposables.push(
      this.healthService.onStatusChanged(({ id }) => {
        this.fireNodeChanged(id);
      }),
      this.healthService.onNotify(({ type, message }) => {
        this.notify(type, message);
      }),
    );
  }

  getConnectionStatus(id: string): ConnectionStatus {
    return this.healthService.getConnectionStatus(id);
  }

  setConnectionStatus(id: string, status: ConnectionStatus): void {
    this.healthService.setConnectionStatus(id, status);
  }

  cancelRetries(connId: string): void {
    this.healthService.cancelRetries(connId);
  }

  private fireNodeChanged(id?: string): void {
    if (!id) {
      this.changed.fire();
      return;
    }
    const node = this.connNodes.get(id);
    if (node) {
      this.changed.fire(node);
    } else {
      this.changed.fire();
    }
  }

  refresh(): void {
    this.healthService.clear();
    this.connNodes.clear();
    this.changed.fire();
  }

  dispose(): void {
    this.healthService.dispose();
    for (const d of this.disposables) {
      d.dispose();
    }
    this.changed.dispose();
  }

  getTreeItem(node: ConnNode): vscode.TreeItem {
    switch (node.t) {
      case "connection": {
        const item = new vscode.TreeItem(node.def.name, vscode.TreeItemCollapsibleState.Collapsed);
        const status = this.getConnectionStatus(node.def.id);
        const kind = readUnionTag(node.def.kind) ?? "";
        const isPt = vscode.env?.language?.startsWith("pt");
        const statusText =
          status === "connected"
            ? (isPt ? "Conectado" : "Connected")
            : status === "connecting"
              ? (isPt ? "Conectando..." : "Connecting...")
              : (isPt ? "Desconectado" : "Disconnected");

        item.description = kind;
        item.tooltip = `${node.def.name} - ${statusText}\nKind: ${kind}`;
        item.contextValue = "fsqsConnection";

        if (this.deps.extensionUri) {
          item.iconPath = {
            light: vscode.Uri.joinPath(this.deps.extensionUri, "media", "light", `db-${status}.svg`),
            dark: vscode.Uri.joinPath(this.deps.extensionUri, "media", "dark", `db-${status}.svg`),
          };
        } else {
          item.iconPath = new vscode.ThemeIcon("database");
        }
        return item;
      }
      case "schema": {
        const item = new vscode.TreeItem(node.schemaName, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = `${node.tables.length} table${node.tables.length === 1 ? "" : "s"}`;
        item.iconPath = new vscode.ThemeIcon("symbol-namespace");
        return item;
      }
      case "table": {
        const label = node.displayName ?? node.table.name;
        const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.Collapsed);
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
    if (node.t === "schema") {
      const prefix = (node.fullPrefix ?? node.schemaName) + ".";
      const subSchemaMap = new Map<string, SchemaTable[]>();
      const directTables: ConnNode[] = [];

      for (const table of node.tables) {
        const remainder = table.name.startsWith(prefix) ? table.name.slice(prefix.length) : table.name;
        const dotIdx = remainder.indexOf(".");
        if (dotIdx > 0) {
          const subSchema = remainder.slice(0, dotIdx);
          const list = subSchemaMap.get(subSchema) ?? [];
          list.push(table);
          subSchemaMap.set(subSchema, list);
        } else {
          directTables.push({ t: "table", connId: node.connId, table, displayName: remainder });
        }
      }

      const subSchemaNodes: ConnNode[] = Array.from(subSchemaMap.entries())
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([subSchema, subTables]) => ({
          t: "schema",
          connId: node.connId,
          schemaName: subSchema,
          fullPrefix: `${node.fullPrefix ?? node.schemaName}.${subSchema}`,
          tables: subTables,
        }));

      return [...subSchemaNodes, ...directTables];
    }
    if (node.t === "table") {
      return node.table.columns.map((column) => ({ t: "column", column }));
    }
    return [];
  }

  private notify(type: "warning" | "error" | "info", message: string): void {
    this.deps.log(`[${type.toUpperCase()}] ${message}`);
    if (this.deps.notify) {
      this.deps.notify(type, message);
      return;
    }
    if (type === "warning") {
      void vscode.window.showWarningMessage(message);
    } else if (type === "error") {
      void vscode.window.showErrorMessage(message);
    } else {
      void vscode.window.showInformationMessage(message);
    }
  }

  async checkConnectionWithRetry(
    def: ConnectionDefinition,
    maxRetries = 3,
  ): Promise<SchemaTable[] | undefined> {
    const existing = this.deps.schemaCache.get(def.id);
    if (existing !== undefined) {
      this.healthService.setConnectionStatus(def.id, "connected");
      this.healthService.cancelRetries(def.id);
      return existing;
    }

    return this.healthService.checkWithRetry({
      connId: def.id,
      connName: def.name,
      maxRetries,
      checkFn: () => this.fetchSchema(def),
      getErrorMessage: () => this.lastSchemaError,
    });
  }

  private async listConnections(): Promise<ConnNode[]> {
    try {
      const worker = await this.deps.getWorker();
      const wsDir = this.deps.getWorkspaceDir?.();
      const params: Record<string, unknown> = {};
      if (wsDir) {
        params.workspaceDir = wsDir;
      }
      const frame = await worker.send({ id: newRequestId(), method: "connection/list", params });
      if (frame.error) {
        return [{ t: "error", message: `Could not list connections: ${frame.error.message}` }];
      }
      const result = frame.result as ConnectionListResult;
      const connections = result.connections ?? [];
      const nodes: ConnNode[] = [];
      for (const def of connections) {
        const node: ConnNode = { t: "connection", def };
        this.connNodes.set(def.id, node);
        nodes.push(node);
        if (this.deps.schemaCache.get(def.id)) {
          this.healthService.setConnectionStatus(def.id, "connected");
        }
      }

      // Check connections in background asynchronously without blocking tree display
      setTimeout(() => {
        for (const def of connections) {
          if (!this.deps.schemaCache.get(def.id)) {
            void this.checkConnectionWithRetry(def);
          }
        }
      }, 50);

      return nodes;
    } catch (err) {
      this.deps.log(`connection/list failed: ${(err as Error).message}`);
      return [{ t: "error", message: `Connections unavailable: ${(err as Error).message}` }];
    }
  }

  private async tablesOf(def: ConnectionDefinition): Promise<ConnNode[]> {
    let tables = this.deps.schemaCache.get(def.id);
    if (tables === undefined) {
      tables = await this.fetchSchema(def);
      if (tables !== undefined) {
        this.healthService.setConnectionStatus(def.id, "connected");
      } else {
        this.healthService.setConnectionStatus(def.id, "disconnected");
      }
    }
    if (tables === undefined) {
      return [{ t: "error", message: this.lastSchemaError ?? "Schema discovery failed." }];
    }

    const hasSchemas = tables.some((t) => t.name.includes("."));
    if (!hasSchemas) {
      return tables.map((table) => ({ t: "table", connId: def.id, table }));
    }

    const schemaMap = new Map<string, SchemaTable[]>();
    for (const table of tables) {
      const dotIdx = table.name.indexOf(".");
      const schemaName = dotIdx > 0 ? table.name.slice(0, dotIdx) : "public";
      const list = schemaMap.get(schemaName) ?? [];
      list.push(table);
      schemaMap.set(schemaName, list);
    }

    return Array.from(schemaMap.entries())
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([schemaName, schemaTables]) => ({
        t: "schema",
        connId: def.id,
        schemaName,
        tables: schemaTables,
      }));
  }

  private async fetchSchema(def: ConnectionDefinition): Promise<SchemaTable[] | undefined> {
    try {
      const worker = await this.deps.getWorker();
      const wsDir = this.deps.getWorkspaceDir?.();
      const params: Record<string, unknown> = { id: def.id };
      if (wsDir) {
        params.workspaceDir = wsDir;
      }
      const frame = await worker.send({
        id: newRequestId(),
        method: "connection/schema",
        params,
      });
      if (frame.error) {
        this.lastSchemaError = `${def.name}: ${frame.error.message}`;
        this.deps.log(`connection/schema for '${def.name}' failed: [${frame.error.code}] ${frame.error.message}`);
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
