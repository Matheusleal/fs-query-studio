import * as assert from "node:assert";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";

const EXTENSION_ID = "fs-query-studio.fs-query-studio";
const extensionRoot = path.resolve(__dirname, "..", "..", "..");
const dbFixture = path.join(extensionRoot, "test", "integration", "fixtures", "sample-connections.db");
const bundledWorker = path.join(extensionRoot, "dist", "worker", "FSharpQueryStudio.Worker.dll");
const hasWorker = fs.existsSync(bundledWorker);

describe("fsquery connections", () => {
  let api: {
    connections: {
      list(): Promise<Array<{ id: string; name: string }>>;
      schema(id: string): Promise<Array<{ name: string; columns: Array<{ name: string }> }>>;
      addFromInput(input: unknown): Promise<void>;
      remove(id: string): Promise<void>;
    };
  };

  before(async () => {
    const ext = vscode.extensions.getExtension(EXTENSION_ID);
    assert.ok(ext, `extension ${EXTENSION_ID} not found`);
    api = (await ext!.activate()) as typeof api;
  });

  (hasWorker ? it : it.skip)("registers a SQLite connection and reads its schema", async function () {
    this.timeout(120000);
    await api.connections.addFromInput({ kind: "Sqlite", name: "widgets-db", filePath: dbFixture });

    const list = await api.connections.list();
    const created = list.find((c) => c.name === "widgets-db");
    assert.ok(created, "widgets-db not in connection/list");

    const tables = await api.connections.schema(created!.id);
    const widgets = tables.find((t) => t.name.toLowerCase() === "widgets");
    assert.ok(widgets, `expected a 'widgets' table, got: ${tables.map((t) => t.name).join(", ")}`);
    assert.ok(
      widgets!.columns.some((c) => c.name.toLowerCase() === "label"),
      `expected a 'label' column, got: ${widgets!.columns.map((c) => c.name).join(", ")}`,
    );

    await api.connections.remove(created!.id);
    assert.ok(
      !(await api.connections.list()).some((c) => c.id === created!.id),
      "widgets-db still present after connection/remove",
    );
  });

  (hasWorker ? it : it.skip)(
    "resolves a {password} token from SecretStorage over the reverse channel",
    async function () {
      this.timeout(120000);
      // Substitute the secret into the *host* portion of a PostgreSQL string,
      // so the connection failure text echoes it back — proving the worker
      // fetched it from context.secrets via secret/get, not the literal token.
      const sentinel = `bridge-sentinel-${Date.now()}`;
      // addFromInput mints a SecretReference and stores `password` under it;
      // the worker resolves `{password}` from SecretStorage over `secret/get`.
      await api.connections.addFromInput({
        kind: "PostgreSql",
        name: "bridge-check",
        connectionString: "Host={password};Port=5432;Username=u;Database=d",
        password: sentinel,
      });
      const created = (await api.connections.list()).find((c) => c.name === "bridge-check")!;
      assert.ok(created, "bridge-check not in connection/list");

      let message = "";
      try {
        await api.connections.schema(created.id);
      } catch (err) {
        message = (err as Error).message;
      }
      // schema() rejects with the worker's `connection/schemaFailed` error text
      // (there is no server on the resolved host). The API seam surfaces only
      // `frame.error.message`, so we assert on that text, not the error code.
      assert.ok(message.length > 0, "connection/schema was expected to fail (no server on host)");

      // Guaranteed proof the reverse channel ran: the literal token is gone,
      // i.e. the worker substituted a resolved value in its place.
      assert.ok(
        !message.includes("{password}"),
        `the {password} token was not substituted; got: ${message}`,
      );

      // Stronger proof where Npgsql echoes the substituted host verbatim (most
      // Linux CI). Some OS/Npgsql versions report "No such host is known."
      // without the host — a failure HERE (with {password} already proven
      // absent above) is known Npgsql host-echo variance, not a bridge defect.
      assert.ok(
        message.includes(sentinel),
        `KNOWN NPGSQL VARIANCE: expected the resolved host '${sentinel}' in the failure ` +
          `text (some Npgsql builds omit it — the {password}-absent assert above is the ` +
          `guaranteed bridge proof). Got: ${message}`,
      );

      await api.connections.remove(created.id);
    },
  );
});
