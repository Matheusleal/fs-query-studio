import { describe, it, expect } from "vitest";
import { buildConnectionDefinition } from "./definition";
import type { ConnectionDefinition } from "../protocol";

describe("buildConnectionDefinition", () => {
  it("builds a Sqlite file connection with no secret", () => {
    const { definition, secret, secretReference } = buildConnectionDefinition({
      kind: "Sqlite",
      name: "Sales",
      filePath: "/data/sales.db",
    });
    expect(definition.kind).toEqual({ Case: "Sqlite" });
    expect(definition.configuration).toEqual({ filePath: "/data/sales.db" });
    expect(definition.secretReference).toBeNull();
    expect(definition.isTrusted).toBe(false);
    expect(typeof definition.id).toBe("string");
    expect(secret).toBeUndefined();
    expect(secretReference).toBeUndefined();
  });

  it("mints a fqs-conn- reference when a SQL connection gets a password", () => {
    const { definition, secret, secretReference } = buildConnectionDefinition({
      kind: "PostgreSql",
      name: "pg",
      connectionString: "Host=h;Port=5432;Password={password}",
      password: "hunter2",
    });
    expect(definition.configuration).toEqual({ connectionString: "Host=h;Port=5432;Password={password}" });
    expect(definition.secretReference).toMatch(/^fqs-conn-[0-9a-f]{32}$/);
    expect(secretReference).toBe(definition.secretReference);
    expect(secret).toBe("hunter2");
  });

  it("builds a RestJson configuration matching the Avalonia shape", () => {
    const { definition } = buildConnectionDefinition({ kind: "RestJson", name: "api", url: "https://x/api" });
    expect(definition.configuration).toEqual({
      url: "https://x/api",
      method: "GET",
      headers: [],
      queryParams: [],
      body: null,
    });
  });

  it("edit keeps id, isTrusted and the existing secretReference when password is blank", () => {
    const existing: ConnectionDefinition = {
      id: "keep-me",
      name: "old",
      kind: { Case: "SqlServer" },
      configuration: { connectionString: "old" },
      secretReference: "fqs-conn-existing",
      isTrusted: true,
    };
    const { definition, secret } = buildConnectionDefinition(
      { kind: "SqlServer", name: "new", connectionString: "Server=.;Password={password}", password: "  " },
      existing,
    );
    expect(definition.id).toBe("keep-me");
    expect(definition.isTrusted).toBe(true);
    expect(definition.secretReference).toBe("fqs-conn-existing");
    expect(secret).toBeUndefined();
  });

  it("edit with a new password on a previously secretless connection mints a reference", () => {
    const existing: ConnectionDefinition = {
      id: "c1",
      name: "n",
      kind: { Case: "PostgreSql" },
      configuration: { connectionString: "x" },
      secretReference: null,
      isTrusted: false,
    };
    const { definition, secret } = buildConnectionDefinition(
      { kind: "PostgreSql", name: "n", connectionString: "Host=h;Password={password}", password: "new" },
      existing,
    );
    expect(definition.secretReference).toMatch(/^fqs-conn-[0-9a-f]{32}$/);
    expect(secret).toBe("new");
  });
});
