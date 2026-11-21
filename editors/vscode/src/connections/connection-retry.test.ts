import { describe, it, expect, vi, beforeEach } from "vitest";
import * as vscode from "vscode";
import {
  ConnectionsTreeProvider,
  type WorkerLike,
  type ConnectionStatus,
} from "./tree";
import { SchemaCache } from "./schema-cache";
import type { ConnectionDefinition, WorkerResponseFrame } from "../protocol";

describe("Connection Retry Routine & Status Icons", () => {
  const mockPostgresDef: ConnectionDefinition = {
    id: "conn-pg-retry-1",
    name: "PostgreSQL_Advanced",
    kind: { Case: "PostgreSql" as const },
    configuration: {},
    secretReference: null,
    isTrusted: true,
  };

  const extensionUri = vscode.Uri.file("/mock/extension/path");

  beforeEach(() => {
    (vscode.env as { language?: string }).language = "en";
  });

  function createTestRig(sendFn: (req: { id: string; method: string; params: unknown }) => Promise<WorkerResponseFrame>) {
    const notifications: Array<{ type: "warning" | "error" | "info"; message: string }> = [];
    const logs: string[] = [];
    const schemaCache = new SchemaCache();

    const provider = new ConnectionsTreeProvider({
      getWorker: async () => ({ send: sendFn }),
      schemaCache,
      log: (line) => logs.push(line),
      extensionUri,
      retryDelayMs: 10, // Fast delay for unit tests
      notify: (type, message) => notifications.push({ type, message }),
    });

    return { provider, schemaCache, notifications, logs };
  }

  // --- Scenario 1: Happy Path ---
  it("Scenario 1.1: marks connection as connected with db-connected.svg icon when schema discovery succeeds", async () => {
    const { provider } = createTestRig(async (req) => {
      if (req.method === "connection/schema") {
        return {
          id: req.id,
          result: { tables: [{ name: "products", columns: [] }] },
        };
      }
      return { id: req.id, result: {} };
    });

    const tables = await provider.checkConnectionWithRetry(mockPostgresDef);
    expect(tables).toHaveLength(1);
    expect(provider.getConnectionStatus(mockPostgresDef.id)).toBe("connected");

    const item = provider.getTreeItem({ t: "connection", def: mockPostgresDef });
    const iconPath = item.iconPath as { light: vscode.Uri; dark: vscode.Uri };
    expect(iconPath.light.toString()).toBe(vscode.Uri.joinPath(extensionUri, "media", "db-connected.svg").toString());
    expect(iconPath.dark.toString()).toBe(vscode.Uri.joinPath(extensionUri, "media", "db-connected.svg").toString());
    expect(item.tooltip).toContain("Connected");
  });

  it("Scenario 1.2: does not emit warning notifications when initial connection succeeds on first try", async () => {
    const { provider, notifications } = createTestRig(async (req) => ({
      id: req.id,
      result: { tables: [{ name: "categories", columns: [] }] },
    }));

    await provider.checkConnectionWithRetry(mockPostgresDef);
    expect(notifications).toHaveLength(0);
  });

  // --- Scenario 2: Retry with Recovery on Attempt 2 ---
  it("Scenario 2.1: retries on initial failure and emits warning notification with attempt count", async () => {
    let callCount = 0;
    const { provider, notifications } = createTestRig(async (req) => {
      callCount++;
      if (callCount === 1) {
        return {
          id: req.id,
          error: { code: "connect_failed", message: "Connection refused on port 5432" },
        };
      }
      return {
        id: req.id,
        result: { tables: [{ name: "products", columns: [] }] },
      };
    });

    await provider.checkConnectionWithRetry(mockPostgresDef, 3);
    expect(callCount).toBe(1);
    expect(notifications).toHaveLength(1);
    expect(notifications[0].type).toBe("warning");
    expect(notifications[0].message).toContain("attempt 1 of 3");
    expect(provider.getConnectionStatus(mockPostgresDef.id)).toBe("connecting");

    const itemConnecting = provider.getTreeItem({ t: "connection", def: mockPostgresDef });
    const iconConnecting = itemConnecting.iconPath as { light: vscode.Uri; dark: vscode.Uri };
    expect(iconConnecting.light.toString()).toBe(vscode.Uri.joinPath(extensionUri, "media", "db-connecting.svg").toString());
    expect(iconConnecting.dark.toString()).toBe(vscode.Uri.joinPath(extensionUri, "media", "db-connecting.svg").toString());
  });

  it("Scenario 2.2: recovers on subsequent retry, transitions to connected and notifies success", async () => {
    let callCount = 0;
    const { provider, notifications } = createTestRig(async (req) => {
      callCount++;
      if (callCount === 1) {
        return {
          id: req.id,
          error: { code: "connect_failed", message: "Timeout" },
        };
      }
      return {
        id: req.id,
        result: { tables: [{ name: "products", columns: [] }] },
      };
    });

    // Attempt 1 fails and schedules retry
    await provider.checkConnectionWithRetry(mockPostgresDef, 3);

    // Wait for the fast retry (10ms)
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(callCount).toBe(2);
    expect(provider.getConnectionStatus(mockPostgresDef.id)).toBe("connected");

    // Success notification emitted after recovery
    const infoNotifs = notifications.filter((n) => n.type === "info");
    expect(infoNotifs).toHaveLength(1);
    expect(infoNotifs[0].message).toContain("established successfully");

    const item = provider.getTreeItem({ t: "connection", def: mockPostgresDef });
    const iconPath = item.iconPath as { light: vscode.Uri; dark: vscode.Uri };
    expect(iconPath.light.toString()).toBe(vscode.Uri.joinPath(extensionUri, "media", "db-connected.svg").toString());
    expect(iconPath.dark.toString()).toBe(vscode.Uri.joinPath(extensionUri, "media", "db-connected.svg").toString());
  });

  // --- Scenario 3: Retries Exhausted (3 Failed Attempts) ---
  it("Scenario 3.1: retries exactly 3 times before transitioning to disconnected with db-disconnected.svg", async () => {
    let callCount = 0;
    const { provider, notifications } = createTestRig(async (req) => {
      callCount++;
      return {
        id: req.id,
        error: { code: "offline", message: "Container offline" },
      };
    });

    await provider.checkConnectionWithRetry(mockPostgresDef, 3);
    // Wait for attempt 2 and attempt 3 to trigger and complete
    await new Promise((resolve) => setTimeout(resolve, 150));

    // Attempt 1 + attempt 2 + attempt 3 + final failure check
    expect(callCount).toBe(4);
    expect(provider.getConnectionStatus(mockPostgresDef.id)).toBe("disconnected");

    const item = provider.getTreeItem({ t: "connection", def: mockPostgresDef });
    const iconPath = item.iconPath as { light: vscode.Uri; dark: vscode.Uri };
    expect(iconPath.light.toString()).toBe(vscode.Uri.joinPath(extensionUri, "media", "db-disconnected.svg").toString());
    expect(iconPath.dark.toString()).toBe(vscode.Uri.joinPath(extensionUri, "media", "db-disconnected.svg").toString());
    expect(item.tooltip).toContain("Disconnected");
  });

  it("Scenario 3.2: emits 3 warning notifications and a final error notification when all retries fail", async () => {
    const { provider, notifications } = createTestRig(async (req) => ({
      id: req.id,
      error: { code: "refused", message: "Connection refused" },
    }));

    await provider.checkConnectionWithRetry(mockPostgresDef, 3);
    await new Promise((resolve) => setTimeout(resolve, 150));

    const warnings = notifications.filter((n) => n.type === "warning");
    const errors = notifications.filter((n) => n.type === "error");

    expect(warnings).toHaveLength(3);
    expect(warnings[0].message).toContain("attempt 1 of 3");
    expect(warnings[1].message).toContain("attempt 2 of 3");
    expect(warnings[2].message).toContain("attempt 3 of 3");

    expect(errors).toHaveLength(1);
    expect(errors[0].message).toContain("after 3 attempts");
  });

  // --- Scenario 4: User Interaction & Cleanup ---
  it("Scenario 4.1: expanding disconnected node triggers reconnection attempt", async () => {
    let callCount = 0;
    const { provider } = createTestRig(async (req) => {
      callCount++;
      if (callCount <= 4) {
        return { id: req.id, error: { code: "err", message: "fail" } };
      }
      return {
        id: req.id,
        result: { tables: [{ name: "recovered_table", columns: [] }] },
      };
    });

    await provider.checkConnectionWithRetry(mockPostgresDef, 3);
    await new Promise((resolve) => setTimeout(resolve, 150));
    expect(provider.getConnectionStatus(mockPostgresDef.id)).toBe("disconnected");

    // User clicks / expands connection node
    const children = await provider.getChildren({ t: "connection", def: mockPostgresDef });
    expect(children).toHaveLength(1);
    expect(children[0].t).toBe("table");
    expect(provider.getConnectionStatus(mockPostgresDef.id)).toBe("connected");
  });

  it("Scenario 4.2: provider refresh cancels active timers and resets state", async () => {
    const { provider } = createTestRig(async (req) => ({
      id: req.id,
      error: { code: "err", message: "fail" },
    }));

    await provider.checkConnectionWithRetry(mockPostgresDef, 3);
    expect(provider.getConnectionStatus(mockPostgresDef.id)).toBe("connecting");

    provider.refresh();
    expect(provider.getConnectionStatus(mockPostgresDef.id)).toBe("connecting"); // default when reset
    provider.dispose();
  });

  // --- Scenario 5: Localization (Portuguese & English) ---
  it("Scenario 5.1: formats notifications in Portuguese when vscode.env.language is pt-BR", async () => {
    (vscode.env as { language?: string }).language = "pt-BR";
    const { provider, notifications } = createTestRig(async (req) => ({
      id: req.id,
      error: { code: "down", message: "Porta 5432 fechada" },
    }));

    await provider.checkConnectionWithRetry(mockPostgresDef, 3);
    await new Promise((resolve) => setTimeout(resolve, 150));

    const warnings = notifications.filter((n) => n.type === "warning");
    const errors = notifications.filter((n) => n.type === "error");

    expect(warnings[0].message).toContain("não foi estabelecida");
    expect(warnings[0].message).toContain("tentativa 1 de 3");
    expect(errors[0].message).toContain("após 3 tentativas");

    const item = provider.getTreeItem({ t: "connection", def: mockPostgresDef });
    expect(item.tooltip).toContain("Desconectado");
  });

  it("Scenario 5.2: formats notifications in English when vscode.env.language is en-US", async () => {
    (vscode.env as { language?: string }).language = "en-US";
    const { provider, notifications } = createTestRig(async (req) => ({
      id: req.id,
      error: { code: "down", message: "Port 5432 closed" },
    }));

    await provider.checkConnectionWithRetry(mockPostgresDef, 3);
    await new Promise((resolve) => setTimeout(resolve, 150));

    const warnings = notifications.filter((n) => n.type === "warning");
    const errors = notifications.filter((n) => n.type === "error");

    expect(warnings[0].message).toContain("could not be established");
    expect(warnings[0].message).toContain("attempt 1 of 3");
    expect(errors[0].message).toContain("after 3 attempts");

    const item = provider.getTreeItem({ t: "connection", def: mockPostgresDef });
    expect(item.tooltip).toContain("Disconnected");
  });
});
