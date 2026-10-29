import { describe, it, expect, vi } from "vitest";
import { runAddConnection, runEditConnection, runRemoveConnection, type ConnectionUi } from "./add";
import type { ConnectionDefinition } from "../protocol";

function deps(overrides: Partial<Parameters<typeof runAddConnection>[0]> = {}) {
  const sent: Array<{ method: string; params: unknown }> = [];
  const stored: Array<[string, string]> = [];
  const deleted: string[] = [];
  const refresh = vi.fn();
  const base = {
    getWorker: async () => ({
      send: async (req: { id: string; method: string; params: unknown }) => {
        sent.push({ method: req.method, params: req.params });
        return { id: req.id, result: { connections: [] } };
      },
    }),
    ui: {
      pickKind: async () => "PostgreSql" as const,
      input: async (o: { prompt: string }) =>
        o.prompt.includes("name") ? "pg" : o.prompt.includes("assword") ? "hunter2" : "Host=h;Password={password}",
      confirmRemove: async () => true,
    } satisfies ConnectionUi,
    secrets: { store: async (k: string, v: string) => void stored.push([k, v]), delete: async (k: string) => void deleted.push(k) },
    refresh,
    log: () => {},
    ...overrides,
  };
  return { deps: base, sent, stored, deleted, refresh };
}

const existing: ConnectionDefinition = {
  id: "c1",
  name: "pg",
  kind: { Case: "PostgreSql" },
  configuration: { connectionString: "Host=h;Password={password}" },
  secretReference: "fqs-conn-old",
  isTrusted: false,
};

describe("runAddConnection", () => {
  it("stores the secret before connection/register and refreshes", async () => {
    const { deps: d, sent, stored, refresh } = deps();
    await runAddConnection(d);
    expect(stored).toHaveLength(1);
    expect(stored[0][1]).toBe("hunter2");
    const reg = sent.find((s) => s.method === "connection/register")!;
    expect((reg.params as ConnectionDefinition).secretReference).toBe(stored[0][0]);
    expect((reg.params as ConnectionDefinition).secretReference).not.toBeNull();
    expect(refresh).toHaveBeenCalledOnce();
  });

  it("aborts silently when the kind pick is cancelled", async () => {
    const { deps: d, sent, refresh } = deps({ ui: { ...deps().deps.ui, pickKind: async () => undefined } });
    await runAddConnection(d);
    expect(sent).toHaveLength(0);
    expect(refresh).not.toHaveBeenCalled();
  });

  it("passes workspaceDir when local scope is selected", async () => {
    const d = deps({
      getWorkspaceFolders: () => [{ name: "MyProj", path: "/workspace/my-proj" }],
      ui: {
        ...deps().deps.ui,
        pickScope: async () => ({ scope: "local", targetDir: "/workspace/my-proj" }),
      },
    });
    await runAddConnection(d.deps);
    const reg = d.sent.find((s) => s.method === "connection/register")!;
    expect((reg.params as Record<string, unknown>).workspaceDir).toBe("/workspace/my-proj");
    expect(d.refresh).toHaveBeenCalledOnce();
  });

  it("does not pass workspaceDir when global scope is selected", async () => {
    const d = deps({
      getWorkspaceFolders: () => [{ name: "MyProj", path: "/workspace/my-proj" }],
      ui: {
        ...deps().deps.ui,
        pickScope: async () => ({ scope: "global" }),
      },
    });
    await runAddConnection(d.deps);
    const reg = d.sent.find((s) => s.method === "connection/register")!;
    expect((reg.params as Record<string, unknown>).workspaceDir).toBeUndefined();
    expect(d.refresh).toHaveBeenCalledOnce();
  });

  it("aborts silently when scope selection is cancelled", async () => {
    const d = deps({
      getWorkspaceFolders: () => [{ name: "MyProj", path: "/workspace/my-proj" }],
      ui: {
        ...deps().deps.ui,
        pickScope: async () => undefined,
      },
    });
    await runAddConnection(d.deps);
    expect(d.sent).toHaveLength(0);
    expect(d.refresh).not.toHaveBeenCalled();
  });

  it("registers SQL Server with Windows Authentication without storing secrets", async () => {
    const d = deps({
      ui: {
        ...deps().deps.ui,
        pickKind: async () => "SqlServer" as const,
        pickSqlServerAuth: async () => "windows" as const,
        input: async (o: { prompt: string }) =>
          o.prompt.includes("name")
            ? "my-sql"
            : "Server=localhost;Database=mydb;",
      },
    });
    await runAddConnection(d.deps);
    expect(d.stored).toHaveLength(0);
    const reg = d.sent.find((s) => s.method === "connection/register")!;
    const def = reg.params as ConnectionDefinition;
    expect(def.name).toBe("my-sql");
    expect(def.secretReference).toBeNull();
    const config = def.configuration as { connectionString: string };
    expect(config.connectionString).toContain("Integrated Security=True");
    expect(config.connectionString).toContain("TrustServerCertificate=True");
    expect(d.refresh).toHaveBeenCalledOnce();
  });

  it("aborts silently when SQL Server auth pick is cancelled", async () => {
    const d = deps({
      ui: {
        ...deps().deps.ui,
        pickKind: async () => "SqlServer" as const,
        pickSqlServerAuth: async () => undefined,
      },
    });
    await runAddConnection(d.deps);
    expect(d.sent).toHaveLength(0);
    expect(d.refresh).not.toHaveBeenCalled();
  });

  it("renders and registers custom connector dynamically based on descriptor schema", async () => {
    const customDescriptor = {
      kindId: "MongoDb",
      displayName: "MongoDB (Custom)",
      category: "nosql",
      icon: "server",
      formFields: [
        { name: "connectionUri", label: "MongoDB Connection URI", type: "string" as const, required: true, secretHandling: "none" },
        { name: "databaseName", label: "Database Name", type: "string" as const, required: true, secretHandling: "none" },
      ],
    };
    const inputs = ["my-mongo", "mongodb://localhost:27017", "analytics_db"];
    let inputIdx = 0;
    const d = deps({
      getDescriptors: async () => [customDescriptor],
      ui: {
        ...deps().deps.ui,
        pickKind: async () => "MongoDb" as any,
        input: async () => inputs[inputIdx++],
      },
    });
    await runAddConnection(d.deps);
    const reg = d.sent.find((s) => s.method === "connection/register")!;
    const def = reg.params as ConnectionDefinition;
    expect(def.name).toBe("my-mongo");
    expect(def.kind).toEqual({ Case: "MongoDb" });
    const config = def.configuration as Record<string, unknown>;
    expect(config.connectionUri).toBe("mongodb://localhost:27017");
    expect(config.databaseName).toBe("analytics_db");
    expect(d.refresh).toHaveBeenCalledOnce();
  });
});


describe("runEditConnection", () => {
  it("does not store a secret when the password box is left blank", async () => {
    const d = deps({
      ui: {
        pickKind: async () => "PostgreSql" as const,
        input: async (o: { prompt: string }) => (o.prompt.includes("assword") ? "" : "pg"),
        confirmRemove: async () => true,
      },
    });
    await runEditConnection(d.deps, existing);
    expect(d.stored).toHaveLength(0);
    const reg = d.sent.find((s) => s.method === "connection/register")!;
    expect((reg.params as ConnectionDefinition).id).toBe("c1");
    expect((reg.params as ConnectionDefinition).secretReference).toBe("fqs-conn-old");
  });
});

describe("runRemoveConnection", () => {
  it("removes then deletes the secret and refreshes", async () => {
    const d = deps();
    await runRemoveConnection(d.deps, existing);
    expect(d.sent.find((s) => s.method === "connection/remove")?.params).toEqual({ id: "c1" });
    expect(d.deleted).toEqual(["fqs-conn-old"]);
    expect(d.refresh).toHaveBeenCalledOnce();
  });

  it("sends nothing when the confirm is declined", async () => {
    const d = deps({ ui: { ...deps().deps.ui, confirmRemove: async () => false } });
    await runRemoveConnection(d.deps, existing);
    expect(d.sent).toHaveLength(0);
    expect(d.deleted).toHaveLength(0);
  });
});
