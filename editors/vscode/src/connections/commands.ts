import * as vscode from "vscode";
import {
  newRequestId,
  type ConnectionDefinition,
  type ConnectionListResult,
  type ConnectionSchemaResult,
  type SchemaTable,
} from "../protocol";
import { SchemaCache } from "./schema-cache";
import { ConnectionsTreeProvider, type WorkerLike } from "./tree";
import {
  buildConnectionDefinition,
  type ConnectionFormInput,
} from "./definition";
import {
  runAddConnection,
  runEditConnection,
  runRemoveConnection,
  type ConnectionUi,
} from "./add";

// `connectorKind` (not `kind`) — `vscode.QuickPickItem` reserves `kind` for
// `QuickPickItemKind`, so a `kind: ConnectorKindName` field breaks the overload.
const KIND_PICKS: Array<{ label: string; connectorKind: import("../protocol").ConnectorKindName }> = [
  { label: "SQL Server", connectorKind: "SqlServer" },
  { label: "PostgreSQL", connectorKind: "PostgreSql" },
  { label: "SQLite", connectorKind: "Sqlite" },
  { label: "CSV", connectorKind: "Csv" },
  { label: "Excel", connectorKind: "Excel" },
  { label: "REST (JSON)", connectorKind: "RestJson" },
];

export interface ConnectionFeatureDeps {
  getWorker: () => Promise<WorkerLike>;
  secrets: vscode.SecretStorage;
  log: (line: string) => void;
}

export interface ConnectionsApi {
  list(): Promise<ConnectionDefinition[]>;
  schema(id: string): Promise<SchemaTable[]>;
  addFromInput(input: ConnectionFormInput, existing?: ConnectionDefinition): Promise<void>;
  remove(id: string): Promise<void>;
}

export function registerConnectionFeatures(deps: ConnectionFeatureDeps): {
  disposable: vscode.Disposable;
  api: ConnectionsApi;
} {
  const schemaCache = new SchemaCache();
  const getPrimaryWorkspaceDir = () => vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;

  const tree = new ConnectionsTreeProvider({
    getWorker: deps.getWorker,
    schemaCache,
    log: deps.log,
    getWorkspaceDir: getPrimaryWorkspaceDir,
  });
  const refresh = () => {
    schemaCache.clear();
    tree.refresh();
  };

  let watcherDisposable: vscode.Disposable | undefined;
  if (typeof vscode.workspace?.createFileSystemWatcher === "function") {
    const watcher = vscode.workspace.createFileSystemWatcher(
      "**/{.fsqs/workspace.json,workspace.json}",
    );
    watcher.onDidCreate(() => refresh());
    watcher.onDidChange(() => refresh());
    watcher.onDidDelete(() => refresh());
    watcherDisposable = watcher;
  }

  const getWorkspaceFolders = (): import("./add").WorkspaceFolderInfo[] => {
    return (vscode.workspace.workspaceFolders ?? []).map((f) => ({
      name: f.name,
      path: f.uri.fsPath,
    }));
  };

  const ui: ConnectionUi = {
    async pickKind() {
      const picked = await vscode.window.showQuickPick(KIND_PICKS, {
        placeHolder: "Connector type",
      });
      return picked?.connectorKind;
    },
    async pickSqlServerAuth() {
      interface AuthPickItem extends vscode.QuickPickItem {
        authType: import("./add").SqlServerAuthType;
      }
      const items: AuthPickItem[] = [
        {
          label: "$(person) Windows Authentication (Integrated Security)",
          description: "Use current Windows user account",
          detail: "Connects using Integrated Security=True (no password required)",
          authType: "windows",
        },
        {
          label: "$(key) SQL Server Authentication",
          description: "Standard SQL Server login",
          detail: "Requires username (User Id) and password",
          authType: "sql",
        },
      ];
      const picked = await vscode.window.showQuickPick(items, {
        placeHolder: "Select SQL Server authentication method",
      });
      return picked?.authType;
    },
    async pickScope(folders) {
      if (folders.length === 0) {
        return { scope: "global" };
      }
      interface ScopePickItem extends vscode.QuickPickItem {
        scope: "local" | "global";
        targetDir?: string;
      }
      const items: ScopePickItem[] = [];
      if (folders.length === 1) {
        items.push({
          label: "$(folder) Local (Current Project)",
          description: folders[0].path,
          detail: "Saves connection in .fsqs/workspace.json within this project directory",
          scope: "local",
          targetDir: folders[0].path,
        });
      } else {
        for (const f of folders) {
          items.push({
            label: `$(folder) Local Project: ${f.name}`,
            description: f.path,
            detail: "Saves connection in .fsqs/workspace.json within this project directory",
            scope: "local",
            targetDir: f.path,
          });
        }
      }
      items.push({
        label: "$(globe) Global (User Profile)",
        detail: "Saves connection in user profile across all projects and windows",
        scope: "global",
      });

      const picked = await vscode.window.showQuickPick(items, {
        placeHolder: "Select connection storage scope (Local project or Global)",
      });
      if (!picked) return undefined;
      return { scope: picked.scope, targetDir: picked.targetDir };
    },
    async input(opts) {
      return vscode.window.showInputBox({
        prompt: opts.prompt,
        value: opts.value,
        password: opts.password ?? false,
        placeHolder: opts.placeHolder,
        ignoreFocusOut: true,
      });
    },
    async confirmRemove(name) {
      const answer = await vscode.window.showWarningMessage(
        `Remove connection "${name}"? This deletes its stored credential.`,
        { modal: true },
        "Remove",
      );
      return answer === "Remove";
    },
  };

  const flowDeps = {
    getWorker: deps.getWorker,
    ui,
    secrets: deps.secrets,
    refresh,
    log: deps.log,
    getWorkspaceFolders,
  };

  const view = vscode.window.registerTreeDataProvider("fsQueryStudioConnections", tree);
  const add = vscode.commands.registerCommand("fs-query-studio.connections.add", () =>
    runAddConnection(flowDeps),
  );
  const edit = vscode.commands.registerCommand(
    "fs-query-studio.connections.edit",
    (node?: { t?: string; def?: ConnectionDefinition }) =>
      node?.def ? runEditConnection(flowDeps, node.def) : undefined,
  );
  const remove = vscode.commands.registerCommand(
    "fs-query-studio.connections.remove",
    (node?: { t?: string; def?: ConnectionDefinition }) =>
      node?.def ? runRemoveConnection(flowDeps, node.def) : undefined,
  );
  const refreshCmd = vscode.commands.registerCommand("fs-query-studio.connections.refresh", refresh);

  const send = async (method: string, params: unknown) => {
    const worker = await deps.getWorker();
    return worker.send({ id: newRequestId(), method, params });
  };

  const api: ConnectionsApi = {
    async list() {
      const wsDir = getPrimaryWorkspaceDir();
      const params = wsDir ? { workspaceDir: wsDir } : {};
      const frame = await send("connection/list", params);
      if (frame.error) throw new Error(frame.error.message);
      return (frame.result as ConnectionListResult).connections ?? [];
    },
    async schema(id) {
      const wsDir = getPrimaryWorkspaceDir();
      const params = wsDir ? { id, workspaceDir: wsDir } : { id };
      const frame = await send("connection/schema", params);
      if (frame.error) throw new Error(frame.error.message);
      return (frame.result as ConnectionSchemaResult).tables ?? [];
    },
    async addFromInput(input, existing) {
      const built = buildConnectionDefinition(input, existing);
      if (built.secret && built.secretReference) {
        await deps.secrets.store(built.secretReference, built.secret);
      }
      const wsDir = getPrimaryWorkspaceDir();
      const params: Record<string, unknown> = { ...built.definition };
      if (wsDir) {
        params.workspaceDir = wsDir;
      }
      const frame = await send("connection/register", params);
      if (frame.error) throw new Error(frame.error.message);
      refresh();
    },
    async remove(id) {
      const wsDir = getPrimaryWorkspaceDir();
      const params: Record<string, unknown> = { id };
      if (wsDir) {
        params.workspaceDir = wsDir;
      }
      const frame = await send("connection/remove", params);
      if (frame.error) throw new Error(frame.error.message);
      refresh();
    },
  };

  const disposables: vscode.Disposable[] = [view, add, edit, remove, refreshCmd];
  if (watcherDisposable) disposables.push(watcherDisposable);
  return {
    disposable: vscode.Disposable.from(...disposables),
    api,
  };
}
