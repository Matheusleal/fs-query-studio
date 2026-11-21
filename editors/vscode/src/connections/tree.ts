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

export type ConnectionStatus = "connected" | "disconnected" | "connecting";

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
}

export class ConnectionsTreeProvider implements vscode.TreeDataProvider<ConnNode> {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.changed.event;

  private readonly connectionStatus = new Map<string, ConnectionStatus>();
  private readonly retryCounts = new Map<string, number>();
  private readonly retryTimers = new Map<string, NodeJS.Timeout>();
  private lastSchemaError: string | undefined;

  constructor(private readonly deps: ConnectionsTreeDeps) {}

  getConnectionStatus(id: string): ConnectionStatus {
    return this.connectionStatus.get(id) ?? "connecting";
  }

  setConnectionStatus(id: string, status: ConnectionStatus): void {
    this.connectionStatus.set(id, status);
    this.changed.fire();
  }

  refresh(): void {
    for (const timer of this.retryTimers.values()) {
      clearTimeout(timer);
    }
    this.retryTimers.clear();
    this.retryCounts.clear();
    this.connectionStatus.clear();
    this.changed.fire();
  }

  dispose(): void {
    for (const timer of this.retryTimers.values()) {
      clearTimeout(timer);
    }
    this.retryTimers.clear();
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
            light: vscode.Uri.joinPath(this.deps.extensionUri, "media", `db-${status}.svg`),
            dark: vscode.Uri.joinPath(this.deps.extensionUri, "media", `db-${status}.svg`),
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
      this.connectionStatus.set(def.id, "connected");
      this.retryCounts.delete(def.id);
      return existing;
    }

    this.connectionStatus.set(def.id, "connecting");
    this.changed.fire();

    const tables = await this.fetchSchema(def);
    if (tables !== undefined) {
      const hadPreviousRetries = (this.retryCounts.get(def.id) ?? 0) > 0;
      this.connectionStatus.set(def.id, "connected");
      this.retryCounts.delete(def.id);
      const activeTimer = this.retryTimers.get(def.id);
      if (activeTimer) {
        clearTimeout(activeTimer);
        this.retryTimers.delete(def.id);
      }
      if (hadPreviousRetries) {
        const isPt = vscode.env?.language?.startsWith("pt");
        const msg = isPt
          ? `Conexão com '${def.name}' estabelecida com sucesso.`
          : `Connection to '${def.name}' established successfully.`;
        this.notify("info", msg);
      }
      this.changed.fire();
      return tables;
    }

    const currentAttempt = (this.retryCounts.get(def.id) ?? 0) + 1;
    const isPt = vscode.env?.language?.startsWith("pt");
    const errMsg = this.lastSchemaError ?? "Connection error";

    if (currentAttempt <= maxRetries) {
      this.retryCounts.set(def.id, currentAttempt);
      this.connectionStatus.set(def.id, "connecting");
      const warnMsg = isPt
        ? `A conexão com '${def.name}' não foi estabelecida (${errMsg}). Tentando reconectar (tentativa ${currentAttempt} de ${maxRetries})...`
        : `Connection to '${def.name}' could not be established (${errMsg}). Retrying (attempt ${currentAttempt} of ${maxRetries})...`;
      this.notify("warning", warnMsg);
      this.changed.fire();

      const delay = this.deps.retryDelayMs ?? 3000;
      const timer = setTimeout(() => {
        this.retryTimers.delete(def.id);
        void this.checkConnectionWithRetry(def, maxRetries);
      }, delay);
      this.retryTimers.set(def.id, timer);
      return undefined;
    } else {
      this.retryCounts.delete(def.id);
      this.connectionStatus.set(def.id, "disconnected");
      const failMsg = isPt
        ? `Não foi possível estabelecer conexão com '${def.name}' após ${maxRetries} tentativas. Verifique se o serviço está em execução.`
        : `Could not establish connection to '${def.name}' after ${maxRetries} attempts. Please verify if the database service is running.`;
      this.notify("error", failMsg);
      this.changed.fire();
      return undefined;
    }
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
      for (const def of connections) {
        if (!this.deps.schemaCache.get(def.id)) {
          void this.checkConnectionWithRetry(def);
        } else {
          this.connectionStatus.set(def.id, "connected");
        }
      }
      return connections.map((def) => ({ t: "connection", def }));
    } catch (err) {
      this.deps.log(`connection/list failed: ${(err as Error).message}`);
      return [{ t: "error", message: `Connections unavailable: ${(err as Error).message}` }];
    }
  }

  private async tablesOf(def: ConnectionDefinition): Promise<ConnNode[]> {
    const cached = this.deps.schemaCache.get(def.id);
    let tables = cached;
    if (tables === undefined) {
      tables = await this.checkConnectionWithRetry(def);
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
