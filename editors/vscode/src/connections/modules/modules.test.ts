import { describe, it, expect } from "vitest";
import { BUILTIN_FRONTEND_CONNECTORS } from "./index";
import { csvFrontendModule } from "./csv";
import { excelFrontendModule } from "./excel";
import { jsonFrontendModule } from "./json";
import { xmlFrontendModule } from "./xml";
import { sqliteFrontendModule } from "./sqlite";
import { sqlServerFrontendModule } from "./sqlserver";
import { postgreSqlFrontendModule } from "./postgresql";
import { mySqlFrontendModule } from "./mysql";
import { mariaDbFrontendModule } from "./mariadb";
import { oracleFrontendModule } from "./oracle";
import { restFrontendModule } from "./rest";
import { buildConnectionDefinition } from "../definition";

describe("Frontend Connection Modules (11 Built-in Connectors)", () => {
  it("registers all 11 built-in connectors in BUILTIN_FRONTEND_CONNECTORS", () => {
    expect(BUILTIN_FRONTEND_CONNECTORS).toHaveLength(11);
    const kinds = BUILTIN_FRONTEND_CONNECTORS.map((m) => m.descriptor.kindId);
    expect(kinds).toEqual([
      "SqlServer",
      "PostgreSql",
      "MySql",
      "MariaDb",
      "Oracle",
      "Sqlite",
      "Csv",
      "Excel",
      "Json",
      "Xml",
      "RestJson",
    ]);
  });

  describe("1. CSV Module (csvFrontendModule)", () => {
    it("has correct descriptor properties", () => {
      expect(csvFrontendModule.descriptor.kindId).toBe("Csv");
      expect(csvFrontendModule.descriptor.displayName).toBe("CSV");
      expect(csvFrontendModule.descriptor.category).toBe("csv");
      expect(csvFrontendModule.descriptor.defaultPort).toBeUndefined();
      expect(csvFrontendModule.autocompleteCategory).toBe("Csv");
      expect(csvFrontendModule.completionTriggerMembers).toContain("csv");
    });

    it("builds configuration correctly from inputs (with path cleaning and optional fields)", () => {
      const config = csvFrontendModule.buildConfiguration({
        kind: "Csv",
        name: "MyCsv",
        filePath: '  "/data/sales.csv"  ',
        delimiter: ";",
        columnTypes: "id=int, price=decimal",
        columnTransforms: "price=replace(',', '.')",
      }) as Record<string, unknown>;

      expect(config.filePath).toBe("/data/sales.csv");
      expect(config.delimiter).toBe(";");
      expect(config.columnTypes).toBe("id=int, price=decimal");
      expect(config.columnTransforms).toBe("price=replace(',', '.')");
    });

    it("assembles full ConnectionDefinition via buildConnectionDefinition", () => {
      const built = buildConnectionDefinition({
        kind: "Csv",
        name: "Sales",
        filePath: "data/sales.csv",
      });
      expect(built.definition.kind).toEqual({ Case: "Csv" });
      expect(built.definition.name).toBe("Sales");
      expect(built.definition.configuration).toEqual({ filePath: "data/sales.csv" });
      expect(built.secretReference).toBeUndefined();
    });

    it("validates missing or empty required fields", () => {
      expect(csvFrontendModule.validateConfiguration({})).toContain("filePath");
      expect(csvFrontendModule.validateConfiguration({ filePath: "   " })).toContain("filePath");
      expect(csvFrontendModule.validateConfiguration({ filePath: "/valid/path.csv" })).toBeUndefined();
    });
  });

  describe("2. Excel Module (excelFrontendModule)", () => {
    it("has correct descriptor properties", () => {
      expect(excelFrontendModule.descriptor.kindId).toBe("Excel");
      expect(excelFrontendModule.descriptor.displayName).toBe("Excel");
      expect(excelFrontendModule.descriptor.category).toBe("excel");
      expect(excelFrontendModule.descriptor.defaultPort).toBeUndefined();
      expect(excelFrontendModule.autocompleteCategory).toBe("Excel");
      expect(excelFrontendModule.completionTriggerMembers).toContain("excel");
    });

    it("builds configuration correctly with sheetName and column overrides", () => {
      const config = excelFrontendModule.buildConfiguration({
        kind: "Excel",
        name: "MySpreadsheet",
        filePath: "C:\\data\\sheets.xlsx",
        sheetName: "Sheet1",
        columnTypes: "Amount=decimal",
      }) as Record<string, unknown>;

      expect(config.filePath).toBe("C:\\data\\sheets.xlsx");
      expect(config.sheetName).toBe("Sheet1");
      expect(config.columnTypes).toBe("Amount=decimal");
    });

    it("assembles full ConnectionDefinition via buildConnectionDefinition", () => {
      const built = buildConnectionDefinition({
        kind: "Excel",
        name: "Financials",
        filePath: "fin.xlsx",
      });
      expect(built.definition.kind).toEqual({ Case: "Excel" });
      expect(built.definition.configuration).toEqual({ filePath: "fin.xlsx" });
    });

    it("validates missing or empty required fields", () => {
      expect(excelFrontendModule.validateConfiguration({})).toContain("filePath");
      expect(excelFrontendModule.validateConfiguration({ filePath: "fin.xlsx" })).toBeUndefined();
    });
  });

  describe("3. JSON Module (jsonFrontendModule)", () => {
    it("has correct descriptor properties", () => {
      expect(jsonFrontendModule.descriptor.kindId).toBe("Json");
      expect(jsonFrontendModule.descriptor.displayName).toBe("JSON File");
      expect(jsonFrontendModule.descriptor.category).toBe("json");
      expect(jsonFrontendModule.descriptor.defaultPort).toBeUndefined();
      expect(jsonFrontendModule.autocompleteCategory).toBe("Json");
      expect(jsonFrontendModule.completionTriggerMembers).toContain("json");
    });

    it("builds configuration correctly with jsonPath", () => {
      const config = jsonFrontendModule.buildConfiguration({
        kind: "Json",
        name: "MyJson",
        filePath: "data/store.json",
        jsonPath: "catalog.products",
      }) as Record<string, unknown>;

      expect(config.filePath).toBe("data/store.json");
      expect(config.jsonPath).toBe("catalog.products");
    });

    it("assembles full ConnectionDefinition via buildConnectionDefinition", () => {
      const built = buildConnectionDefinition({
        kind: "Json",
        name: "Catalog",
        filePath: "data/catalog.json",
      });
      expect(built.definition.kind).toEqual({ Case: "Json" });
      expect(built.definition.configuration).toEqual({ filePath: "data/catalog.json" });
    });

    it("validates missing or empty required fields", () => {
      expect(jsonFrontendModule.validateConfiguration({})).toContain("filePath");
      expect(jsonFrontendModule.validateConfiguration({ filePath: "data.json" })).toBeUndefined();
    });
  });

  describe("4. XML Module (xmlFrontendModule)", () => {
    it("has correct descriptor properties", () => {
      expect(xmlFrontendModule.descriptor.kindId).toBe("Xml");
      expect(xmlFrontendModule.descriptor.displayName).toBe("XML File");
      expect(xmlFrontendModule.descriptor.category).toBe("xml");
      expect(xmlFrontendModule.descriptor.defaultPort).toBeUndefined();
      expect(xmlFrontendModule.autocompleteCategory).toBe("Xml");
      expect(xmlFrontendModule.completionTriggerMembers).toContain("xml");
    });

    it("builds configuration correctly with rowElement", () => {
      const config = xmlFrontendModule.buildConfiguration({
        kind: "Xml",
        name: "MyXml",
        filePath: "data/orders.xml",
        rowElement: "Order",
      }) as Record<string, unknown>;

      expect(config.filePath).toBe("data/orders.xml");
      expect(config.rowElement).toBe("Order");
    });

    it("assembles full ConnectionDefinition via buildConnectionDefinition", () => {
      const built = buildConnectionDefinition({
        kind: "Xml",
        name: "Orders",
        filePath: "orders.xml",
      });
      expect(built.definition.kind).toEqual({ Case: "Xml" });
      expect(built.definition.configuration).toEqual({ filePath: "orders.xml" });
    });

    it("validates missing or empty required fields", () => {
      expect(xmlFrontendModule.validateConfiguration({})).toContain("filePath");
      expect(xmlFrontendModule.validateConfiguration({ filePath: "orders.xml" })).toBeUndefined();
    });
  });

  describe("5. SQLite Module (sqliteFrontendModule)", () => {
    it("has correct descriptor properties", () => {
      expect(sqliteFrontendModule.descriptor.kindId).toBe("Sqlite");
      expect(sqliteFrontendModule.descriptor.displayName).toBe("SQLite");
      expect(sqliteFrontendModule.descriptor.category).toBe("sql");
      expect(sqliteFrontendModule.descriptor.defaultPort).toBeUndefined();
      expect(sqliteFrontendModule.autocompleteCategory).toBe("Database");
      expect(sqliteFrontendModule.completionTriggerMembers).toContain("sql");
    });

    it("builds configuration correctly with cleaned filePath", () => {
      const config = sqliteFrontendModule.buildConfiguration({
        kind: "Sqlite",
        name: "MyDb",
        filePath: ' "C:/sqlite/test.db" ',
      }) as Record<string, unknown>;

      expect(config.filePath).toBe("C:/sqlite/test.db");
    });

    it("assembles full ConnectionDefinition via buildConnectionDefinition", () => {
      const built = buildConnectionDefinition({
        kind: "Sqlite",
        name: "SqliteLocal",
        filePath: "app.db",
      });
      expect(built.definition.kind).toEqual({ Case: "Sqlite" });
      expect(built.definition.configuration).toEqual({ filePath: "app.db" });
    });

    it("validates missing or empty required fields", () => {
      expect(sqliteFrontendModule.validateConfiguration({})).toContain("filePath");
      expect(sqliteFrontendModule.validateConfiguration({ filePath: "app.db" })).toBeUndefined();
    });
  });

  describe("6. SQL Server Module (sqlServerFrontendModule)", () => {
    it("has correct descriptor properties and default port 1433", () => {
      expect(sqlServerFrontendModule.descriptor.kindId).toBe("SqlServer");
      expect(sqlServerFrontendModule.descriptor.displayName).toBe("SQL Server");
      expect(sqlServerFrontendModule.descriptor.category).toBe("sql");
      expect(sqlServerFrontendModule.descriptor.defaultPort).toBe(1433);
      expect(sqlServerFrontendModule.defaultPort).toBe(1433);
      expect(sqlServerFrontendModule.autocompleteCategory).toBe("Database");
    });

    it("builds configuration and sanitizes password into secret reference", () => {
      const built = buildConnectionDefinition({
        kind: "SqlServer",
        name: "MssqlProd",
        connectionString: "Server=localhost,1433;Database=master;User Id=sa;Password=SecretPassword123!;",
      });

      expect(built.definition.kind).toEqual({ Case: "SqlServer" });
      expect(built.secret).toBe("SecretPassword123!");
      expect(built.secretReference).toMatch(/^fqs-conn-[0-9a-f]+$/);
      const cfg = built.definition.configuration as { connectionString: string };
      expect(cfg.connectionString).toBe("Server=localhost,1433;Database=master;User Id=sa;Password={password};");
    });

    it("handles Windows Authentication without minting a secret reference", () => {
      const built = buildConnectionDefinition({
        kind: "SqlServer",
        name: "MssqlWinAuth",
        connectionString: "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;",
      });

      expect(built.secret).toBeUndefined();
      expect(built.secretReference).toBeUndefined();
      expect(built.definition.secretReference).toBeNull();
    });

    it("validates missing or empty required connectionString", () => {
      expect(sqlServerFrontendModule.validateConfiguration({})).toContain("connectionString");
      expect(sqlServerFrontendModule.validateConfiguration({ connectionString: "   " })).toContain("connectionString");
      expect(
        sqlServerFrontendModule.validateConfiguration({ connectionString: "Server=localhost;Database=master;" }),
      ).toBeUndefined();
    });
  });

  describe("7. PostgreSQL Module (postgreSqlFrontendModule)", () => {
    it("has correct descriptor properties and default port 5432", () => {
      expect(postgreSqlFrontendModule.descriptor.kindId).toBe("PostgreSql");
      expect(postgreSqlFrontendModule.descriptor.displayName).toBe("PostgreSQL");
      expect(postgreSqlFrontendModule.descriptor.category).toBe("sql");
      expect(postgreSqlFrontendModule.descriptor.defaultPort).toBe(5432);
      expect(postgreSqlFrontendModule.defaultPort).toBe(5432);
      expect(postgreSqlFrontendModule.autocompleteCategory).toBe("Database");
    });

    it("builds configuration and sanitizes URI connection string with port 5432", () => {
      const built = buildConnectionDefinition({
        kind: "PostgreSql",
        name: "PgLocal",
        connectionString: "postgresql://postgres:pgSecret123@localhost:5432/orders_db",
      });

      expect(built.definition.kind).toEqual({ Case: "PostgreSql" });
      expect(built.secret).toBe("pgSecret123");
      expect(built.secretReference).toBeDefined();
      const cfg = built.definition.configuration as { connectionString: string };
      expect(cfg.connectionString).toBe("postgresql://postgres:{password}@localhost:5432/orders_db");
    });

    it("validates missing or empty required connectionString", () => {
      expect(postgreSqlFrontendModule.validateConfiguration({})).toContain("connectionString");
      expect(
        postgreSqlFrontendModule.validateConfiguration({ connectionString: "Host=localhost;Database=orders_db" }),
      ).toBeUndefined();
    });
  });

  describe("8. MySQL Module (mySqlFrontendModule)", () => {
    it("has correct descriptor properties and default port 3306", () => {
      expect(mySqlFrontendModule.descriptor.kindId).toBe("MySql");
      expect(mySqlFrontendModule.descriptor.displayName).toBe("MySQL");
      expect(mySqlFrontendModule.descriptor.category).toBe("sql");
      expect(mySqlFrontendModule.descriptor.defaultPort).toBe(3306);
      expect(mySqlFrontendModule.defaultPort).toBe(3306);
      expect(mySqlFrontendModule.autocompleteCategory).toBe("Database");
    });

    it("builds configuration and sanitizes key-value connection string with port 3306", () => {
      const built = buildConnectionDefinition({
        kind: "MySql",
        name: "MySqlStore",
        connectionString: "Server=localhost;Port=3306;Database=store;User ID=root;Password=MySqlPass456;",
      });

      expect(built.definition.kind).toEqual({ Case: "MySql" });
      expect(built.secret).toBe("MySqlPass456");
      expect(built.secretReference).toBeDefined();
      const cfg = built.definition.configuration as { connectionString: string };
      expect(cfg.connectionString).toBe("Server=localhost;Port=3306;Database=store;User ID=root;Password={password};");
    });

    it("validates missing or empty required connectionString", () => {
      expect(mySqlFrontendModule.validateConfiguration({})).toContain("connectionString");
      expect(
        mySqlFrontendModule.validateConfiguration({ connectionString: "Server=localhost;Database=store;" }),
      ).toBeUndefined();
    });
  });

  describe("9. MariaDB Module (mariaDbFrontendModule)", () => {
    it("has correct descriptor properties and default port 3306", () => {
      expect(mariaDbFrontendModule.descriptor.kindId).toBe("MariaDb");
      expect(mariaDbFrontendModule.descriptor.displayName).toBe("MariaDB");
      expect(mariaDbFrontendModule.descriptor.category).toBe("sql");
      expect(mariaDbFrontendModule.descriptor.defaultPort).toBe(3306);
      expect(mariaDbFrontendModule.defaultPort).toBe(3306);
      expect(mariaDbFrontendModule.autocompleteCategory).toBe("Database");
    });

    it("builds configuration and sanitizes connection string", () => {
      const built = buildConnectionDefinition({
        kind: "MariaDb",
        name: "MariaDbWarehouse",
        connectionString: "Server=localhost;Port=3306;Database=warehouse;User ID=root;Password=MariaSecret789;",
      });

      expect(built.definition.kind).toEqual({ Case: "MariaDb" });
      expect(built.secret).toBe("MariaSecret789");
      expect(built.secretReference).toBeDefined();
      const cfg = built.definition.configuration as { connectionString: string };
      expect(cfg.connectionString).toBe("Server=localhost;Port=3306;Database=warehouse;User ID=root;Password={password};");
    });

    it("validates missing or empty required connectionString", () => {
      expect(mariaDbFrontendModule.validateConfiguration({})).toContain("connectionString");
      expect(
        mariaDbFrontendModule.validateConfiguration({ connectionString: "Server=localhost;Database=warehouse;" }),
      ).toBeUndefined();
    });
  });

  describe("10. Oracle Module (oracleFrontendModule)", () => {
    it("has correct descriptor properties and default port 1521", () => {
      expect(oracleFrontendModule.descriptor.kindId).toBe("Oracle");
      expect(oracleFrontendModule.descriptor.displayName).toBe("Oracle");
      expect(oracleFrontendModule.descriptor.category).toBe("sql");
      expect(oracleFrontendModule.descriptor.defaultPort).toBe(1521);
      expect(oracleFrontendModule.defaultPort).toBe(1521);
      expect(oracleFrontendModule.autocompleteCategory).toBe("Database");
    });

    it("builds configuration and sanitizes Oracle Data Source connection string with port 1521", () => {
      const built = buildConnectionDefinition({
        kind: "Oracle",
        name: "OracleFree",
        connectionString: "Data Source=localhost:1521/FREEPDB1;User Id=system;Password=OraclePass123;",
      });

      expect(built.definition.kind).toEqual({ Case: "Oracle" });
      expect(built.secret).toBe("OraclePass123");
      expect(built.secretReference).toBeDefined();
      const cfg = built.definition.configuration as { connectionString: string };
      expect(cfg.connectionString).toBe("Data Source=localhost:1521/FREEPDB1;User Id=system;Password={password};");
    });

    it("validates missing or empty required connectionString", () => {
      expect(oracleFrontendModule.validateConfiguration({})).toContain("connectionString");
      expect(
        oracleFrontendModule.validateConfiguration({ connectionString: "Data Source=localhost:1521/FREEPDB1;User Id=system;" }),
      ).toBeUndefined();
    });
  });

  describe("11. REST API Module (restFrontendModule)", () => {
    it("has correct descriptor properties", () => {
      expect(restFrontendModule.descriptor.kindId).toBe("RestJson");
      expect(restFrontendModule.descriptor.displayName).toBe("REST API (JSON)");
      expect(restFrontendModule.descriptor.category).toBe("rest");
      expect(restFrontendModule.descriptor.defaultPort).toBeUndefined();
      expect(restFrontendModule.autocompleteCategory).toBe("RestJson");
      expect(restFrontendModule.completionTriggerMembers).toContain("rest");
    });

    it("builds configuration with defaults (GET method, empty headers/queryParams, null body)", () => {
      const config = restFrontendModule.buildConfiguration({
        kind: "RestJson",
        name: "GitHubApi",
        url: "  https://api.github.com/events  ",
      }) as Record<string, unknown>;

      expect(config.url).toBe("https://api.github.com/events");
      expect(config.method).toBe("GET");
      expect(config.headers).toEqual([]);
      expect(config.queryParams).toEqual([]);
      expect(config.body).toBeNull();
    });

    it("assembles full ConnectionDefinition via buildConnectionDefinition", () => {
      const built = buildConnectionDefinition({
        kind: "RestJson",
        name: "RestFeed",
        url: "https://api.example.com/items",
      });
      expect(built.definition.kind).toEqual({ Case: "RestJson" });
      expect(built.definition.configuration).toEqual({
        url: "https://api.example.com/items",
        method: "GET",
        headers: [],
        queryParams: [],
        body: null,
      });
    });

    it("validates missing or empty required url", () => {
      expect(restFrontendModule.validateConfiguration({})).toContain("url");
      expect(restFrontendModule.validateConfiguration({ url: "   " })).toContain("url");
      expect(restFrontendModule.validateConfiguration({ url: "https://api.example.com" })).toBeUndefined();
    });
  });
});
