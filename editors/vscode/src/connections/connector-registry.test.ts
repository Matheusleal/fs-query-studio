import { describe, it, expect } from "vitest";
import { ConnectorFrontRegistry } from "./connector-registry";
import type { FrontendConnectorModule } from "./connector-shared";

describe("ConnectorFrontRegistry & Per-Connector Frontend Modules", () => {
  it("exposes default descriptors for all 6 built-in connectors", () => {
    const descriptors = ConnectorFrontRegistry.defaultDescriptors;
    const kindIds = descriptors.map((d) => d.kindId);
    expect(kindIds).toEqual(["SqlServer", "PostgreSql", "Sqlite", "Csv", "Excel", "RestJson"]);
  });

  describe("SQL Server module rules", () => {
    it("detects and normalizes Windows Authentication connection strings (1 and 2 args)", () => {
      expect(ConnectorFrontRegistry.supportsWindowsAuthPrompt("SqlServer", "connectionString")).toBe(true);
      expect(ConnectorFrontRegistry.supportsWindowsAuthPrompt("PostgreSql", "connectionString")).toBe(false);

      const defaultWin = ConnectorFrontRegistry.getDefaultWindowsAuthConnectionString("SqlServer");
      expect(defaultWin).toContain("Integrated Security=True");

      const norm2 = ConnectorFrontRegistry.normalizeWindowsAuthConnectionString(
        "SqlServer",
        "Server=localhost;Database=Orders;",
      );
      expect(norm2).toContain("Integrated Security=True");
      expect(norm2).toContain("TrustServerCertificate=True");

      const norm1 = ConnectorFrontRegistry.normalizeWindowsAuthConnectionString(
        "Server=localhost;Database=Orders;",
      );
      expect(norm1).toContain("Integrated Security=True");
      expect(norm1).toContain("TrustServerCertificate=True");

      expect(ConnectorFrontRegistry.isWindowsAuthentication("SqlServer", norm2, false)).toBe(true);
      expect(ConnectorFrontRegistry.isWindowsAuthentication("SqlServer", norm2, true)).toBe(false);
    });

    it("extracts inline Password from SQL Server connection string and replaces with {password}", () => {
      const res = ConnectorFrontRegistry.sanitizeConnectionStringAndPassword(
        "SqlServer",
        "Server=.;Database=App;User Id=sa;Password=MySecret123;",
      );
      expect(res.password).toBe("MySecret123");
      expect(res.connectionString).toBe("Server=.;Database=App;User Id=sa;Password={password};");
    });

    it("validates SQL Server configuration", () => {
      expect(ConnectorFrontRegistry.validateConfiguration("SqlServer", { connectionString: "   " })).toContain(
        "connectionString",
      );
      expect(
        ConnectorFrontRegistry.validateConfiguration("SqlServer", {
          connectionString: "Server=.;Database=App;",
        }),
      ).toBeUndefined();
    });
  });

  describe("PostgreSQL module rules", () => {
    it("sanitizes both URI and key-value PostgreSQL connection strings", () => {
      const uriRes = ConnectorFrontRegistry.sanitizeConnectionStringAndPassword(
        "PostgreSql",
        "postgresql://postgres:pgPass99@localhost:5432/mydb",
      );
      expect(uriRes.password).toBe("pgPass99");
      expect(uriRes.connectionString).toBe("postgresql://postgres:{password}@localhost:5432/mydb");

      const kvRes = ConnectorFrontRegistry.sanitizeConnectionStringAndPassword(
        "PostgreSql",
        "Host=localhost;Database=mydb;Username=postgres",
        "explicitPass",
      );
      expect(kvRes.password).toBe("explicitPass");
      expect(kvRes.connectionString).toContain("Password={password}");
    });

    it("validates PostgreSQL configuration", () => {
      expect(ConnectorFrontRegistry.validateConfiguration("PostgreSql", {})).toContain("connectionString");
      expect(
        ConnectorFrontRegistry.validateConfiguration("PostgreSql", {
          connectionString: "Host=localhost;Database=mydb",
        }),
      ).toBeUndefined();
    });
  });

  describe("SQLite, CSV, Excel, and REST JSON module rules", () => {
    it("builds and validates SQLite configuration", () => {
      const cfg = ConnectorFrontRegistry.configurationFor({
        kind: "Sqlite",
        name: "LocalDb",
        filePath: ' "C:/data/store.db" ',
      }) as { filePath: string };
      expect(cfg.filePath).toBe("C:/data/store.db");
      expect(ConnectorFrontRegistry.validateConfiguration("Sqlite", cfg)).toBeUndefined();
      expect(ConnectorFrontRegistry.validateConfiguration("Sqlite", { filePath: "" })).toContain("filePath");
    });

    it("builds and validates CSV configuration with optional delimiter", () => {
      const cfg = ConnectorFrontRegistry.configurationFor({
        kind: "Csv",
        name: "SalesCsv",
        filePath: "/data/sales.csv",
        delimiter: ";",
      }) as { filePath: string; delimiter?: string };
      expect(cfg.filePath).toBe("/data/sales.csv");
      expect(cfg.delimiter).toBe(";");
      expect(ConnectorFrontRegistry.validateConfiguration("Csv", cfg)).toBeUndefined();
    });

    it("builds and validates Excel configuration", () => {
      const cfg = ConnectorFrontRegistry.configurationFor({
        kind: "Excel",
        name: "Book1",
        filePath: "/data/book.xlsx",
      }) as { filePath: string };
      expect(cfg.filePath).toBe("/data/book.xlsx");
      expect(ConnectorFrontRegistry.validateConfiguration("Excel", cfg)).toBeUndefined();
    });

    it("builds and validates REST JSON configuration", () => {
      const cfg = ConnectorFrontRegistry.configurationFor({
        kind: "RestJson",
        name: "OrdersApi",
        url: " https://api.example.com/orders ",
      }) as { url: string; method: string };
      expect(cfg.url).toBe("https://api.example.com/orders");
      expect(cfg.method).toBe("GET");
      expect(ConnectorFrontRegistry.validateConfiguration("RestJson", cfg)).toBeUndefined();
      expect(ConnectorFrontRegistry.validateConfiguration("RestJson", { url: "" })).toContain("url");
    });
  });

  describe("Dynamic plugin registration", () => {
    it("allows registering a custom connector module at runtime without changing shared files", () => {
      const duckDbModule: FrontendConnectorModule = {
        descriptor: {
          kindId: "DuckDb",
          displayName: "DuckDB",
          category: "sql",
          icon: "database",
          formFields: [
            {
              name: "filePath",
              label: "DuckDB Path",
              type: "file",
              required: true,
              secretHandling: "none",
            },
          ],
        },
        autocompleteCategory: "sql",
        completionTriggerMembers: ["duck"],
        buildConfiguration: (input) => ({ filePath: input.filePath ?? ":memory:" }),
        validateConfiguration: (config) =>
          typeof config.filePath === "string" && config.filePath.length > 0
            ? undefined
            : "DuckDB requires filePath",
      };

      ConnectorFrontRegistry.register(duckDbModule);
      expect(ConnectorFrontRegistry.getModule("duckdb")?.descriptor.displayName).toBe("DuckDB");
      expect(ConnectorFrontRegistry.getTargetConnectorCategory('Connections.duck("')).toBe("sql");
      expect(ConnectorFrontRegistry.isInCategory("DuckDb", "sql")).toBe(true);
      expect(ConnectorFrontRegistry.validateConfiguration("DuckDb", { filePath: "" })).toBe(
        "DuckDB requires filePath",
      );
    });
  });
});

