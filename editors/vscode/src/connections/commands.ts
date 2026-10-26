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
  const tree = new ConnectionsTreeProvider({
    getWorker: deps.getWorker,
    schemaCache,
    log: deps.log,
  });
  const refresh = () => {
    schemaCache.clear();
    tree.refresh();
  };

  const ui: ConnectionUi = {
    async pickKind() {
      const picked = await vscode.window.showQuickPick(KIND_PICKS, {
        placeHolder: "Connector type",
      });
      return picked?.connectorKind;
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

  const flowDeps = { getWorker: deps.getWorker, ui, secrets: deps.secrets, refresh, log: deps.log };

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
      const frame = await send("connection/list", {});
      if (frame.error) throw new Error(frame.error.message);
      return (frame.result as ConnectionListResult).connections ?? [];
    },
    async schema(id) {
      const frame = await send("connection/schema", { id });
      if (frame.error) throw new Error(frame.error.message);
      return (frame.result as ConnectionSchemaResult).tables ?? [];
    },
    async addFromInput(input, existing) {
      const built = buildConnectionDefinition(input, existing);
      if (built.secret && built.secretReference) {
        await deps.secrets.store(built.secretReference, built.secret);
      }
      const frame = await send("connection/register", built.definition);
      if (frame.error) throw new Error(frame.error.message);
      refresh();
    },
    async remove(id) {
      const frame = await send("connection/remove", { id });
      if (frame.error) throw new Error(frame.error.message);
      refresh();
    },
  };

  return {
    disposable: vscode.Disposable.from(view, add, edit, remove, refreshCmd),
    api,
  };
}
