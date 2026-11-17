import { describe, it, expect } from "vitest";
import { ConnectorSharedUtils } from "./connector-shared";
import type { ConnectorDescriptorDto } from "../protocol";

describe("ConnectorSharedUtils", () => {
  describe("Port validation", () => {
    it("accepts valid TCP ports as numbers and strings", () => {
      expect(ConnectorSharedUtils.isValidPort(1)).toBe(true);
      expect(ConnectorSharedUtils.isValidPort(80)).toBe(true);
      expect(ConnectorSharedUtils.isValidPort(443)).toBe(true);
      expect(ConnectorSharedUtils.isValidPort(1433)).toBe(true);
      expect(ConnectorSharedUtils.isValidPort(1521)).toBe(true);
      expect(ConnectorSharedUtils.isValidPort(3306)).toBe(true);
      expect(ConnectorSharedUtils.isValidPort(5432)).toBe(true);
      expect(ConnectorSharedUtils.isValidPort(8080)).toBe(true);
      expect(ConnectorSharedUtils.isValidPort(65535)).toBe(true);

      // String representations
      expect(ConnectorSharedUtils.isValidPort("1")).toBe(true);
      expect(ConnectorSharedUtils.isValidPort("1433")).toBe(true);
      expect(ConnectorSharedUtils.isValidPort("  5432  ")).toBe(true);
      expect(ConnectorSharedUtils.isValidPort("65535")).toBe(true);

      expect(ConnectorSharedUtils.validatePort(1433)).toBeUndefined();
      expect(ConnectorSharedUtils.validatePort("5432")).toBeUndefined();
    });

    it("rejects port numbers strictly less than 1", () => {
      expect(ConnectorSharedUtils.isValidPort(0)).toBe(false);
      expect(ConnectorSharedUtils.isValidPort(-1)).toBe(false);
      expect(ConnectorSharedUtils.isValidPort(-5432)).toBe(false);
      expect(ConnectorSharedUtils.isValidPort("0")).toBe(false);
      expect(ConnectorSharedUtils.isValidPort("-1433")).toBe(false);

      expect(ConnectorSharedUtils.validatePort(0)).toContain("between 1 and 65535");
      expect(ConnectorSharedUtils.validatePort(-1)).toContain("between 1 and 65535");
    });

    it("rejects port numbers strictly greater than 65535", () => {
      expect(ConnectorSharedUtils.isValidPort(65536)).toBe(false);
      expect(ConnectorSharedUtils.isValidPort(70000)).toBe(false);
      expect(ConnectorSharedUtils.isValidPort(99999)).toBe(false);
      expect(ConnectorSharedUtils.isValidPort("65536")).toBe(false);
      expect(ConnectorSharedUtils.isValidPort("100000")).toBe(false);

      expect(ConnectorSharedUtils.validatePort(65536)).toContain("between 1 and 65535");
    });

    it("rejects non-integer, NaN, empty, or non-numeric values", () => {
      expect(ConnectorSharedUtils.isValidPort(1433.5)).toBe(false);
      expect(ConnectorSharedUtils.isValidPort("1433.5")).toBe(false);
      expect(ConnectorSharedUtils.isValidPort("")).toBe(false);
      expect(ConnectorSharedUtils.isValidPort("   ")).toBe(false);
      expect(ConnectorSharedUtils.isValidPort("abc")).toBe(false);
      expect(ConnectorSharedUtils.isValidPort("1433abc")).toBe(false);
      expect(ConnectorSharedUtils.isValidPort(null)).toBe(false);
      expect(ConnectorSharedUtils.isValidPort(undefined)).toBe(false);
      expect(ConnectorSharedUtils.isValidPort(Number.NaN)).toBe(false);
      expect(ConnectorSharedUtils.isValidPort(Infinity)).toBe(false);

      expect(ConnectorSharedUtils.validatePort("abc")).toContain("between 1 and 65535");
      expect(ConnectorSharedUtils.validatePort(null)).toContain("between 1 and 65535");
    });
  });

  describe("Host / server validation", () => {
    it("accepts localhost, IP addresses (IPv4 & IPv6), and standard hostnames", () => {
      expect(ConnectorSharedUtils.isValidHost("localhost")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("127.0.0.1")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("192.168.1.100")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("10.0.0.1")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("::1")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("[::1]")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("fe80::1")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("db.example.com")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("mydb.corp.internal")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("postgres-server-01")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("aws-rds.cluster-xyz.us-east-1.rds.amazonaws.com")).toBe(true);

      expect(ConnectorSharedUtils.validateHost("localhost")).toBeUndefined();
      expect(ConnectorSharedUtils.validateHost("127.0.0.1")).toBeUndefined();
    });

    it("accepts database instance notations", () => {
      expect(ConnectorSharedUtils.isValidHost(".")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("(local)")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("(localdb)\\MSSQLLocalDB")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("localhost\\SQLEXPRESS")).toBe(true);
      expect(ConnectorSharedUtils.isValidHost("db-host:1433")).toBe(true);
    });

    it("rejects empty, whitespace-only, or non-string hosts", () => {
      expect(ConnectorSharedUtils.isValidHost("")).toBe(false);
      expect(ConnectorSharedUtils.isValidHost("   ")).toBe(false);
      expect(ConnectorSharedUtils.isValidHost(null)).toBe(false);
      expect(ConnectorSharedUtils.isValidHost(undefined)).toBe(false);
      expect(ConnectorSharedUtils.isValidHost(123)).toBe(false);

      expect(ConnectorSharedUtils.validateHost("")).toContain("cannot be empty");
      expect(ConnectorSharedUtils.validateHost("   ")).toContain("cannot be empty");
      expect(ConnectorSharedUtils.validateHost(undefined)).toContain("cannot be empty");
    });
  });

  describe("File path cleaning and extension validation", () => {
    it("cleans file paths by trimming whitespace and enclosing quotes", () => {
      expect(ConnectorSharedUtils.cleanFilePath("  /path/to/data.csv  ")).toBe("/path/to/data.csv");
      expect(ConnectorSharedUtils.cleanFilePath("\"C:\\data\\file.xlsx\"")).toBe("C:\\data\\file.xlsx");
      expect(ConnectorSharedUtils.cleanFilePath("'C:/data/db.sqlite'")).toBe("C:/data/db.sqlite");
      expect(ConnectorSharedUtils.cleanFilePath(undefined)).toBe("");
    });

    it("validates file paths with expected extensions (.csv, .xlsx, .db, .sqlite, .json, .xml)", () => {
      const allowedExts = [".csv", ".xlsx", ".db", ".sqlite", ".json", ".xml"] as const;

      expect(ConnectorSharedUtils.isValidFilePath("/data/sales.csv", allowedExts)).toBe(true);
      expect(ConnectorSharedUtils.isValidFilePath("C:\\data\\reports.XLSX", allowedExts)).toBe(true);
      expect(ConnectorSharedUtils.isValidFilePath("app.db", allowedExts)).toBe(true);
      expect(ConnectorSharedUtils.isValidFilePath("/var/data/prod.sqlite", allowedExts)).toBe(true);
      expect(ConnectorSharedUtils.isValidFilePath("\"/home/user/items.json\"", allowedExts)).toBe(true);
      expect(ConnectorSharedUtils.isValidFilePath("orders.xml", allowedExts)).toBe(true);

      // Extensions specified without leading dot
      expect(ConnectorSharedUtils.isValidFilePath("data.csv", ["csv", "xlsx"])).toBe(true);

      expect(ConnectorSharedUtils.validateFilePath("data.csv", allowedExts)).toBeUndefined();
    });

    it("rejects paths with non-matching extensions or empty paths", () => {
      const allowedExts = [".csv", ".xlsx", ".db", ".sqlite", ".json", ".xml"] as const;

      expect(ConnectorSharedUtils.isValidFilePath("/data/archive.zip", allowedExts)).toBe(false);
      expect(ConnectorSharedUtils.isValidFilePath("script.py", allowedExts)).toBe(false);
      expect(ConnectorSharedUtils.isValidFilePath("", allowedExts)).toBe(false);
      expect(ConnectorSharedUtils.isValidFilePath("   ", allowedExts)).toBe(false);
      expect(ConnectorSharedUtils.isValidFilePath(null, allowedExts)).toBe(false);

      expect(ConnectorSharedUtils.validateFilePath("archive.zip", allowedExts)).toContain("must end with one of");
      expect(ConnectorSharedUtils.validateFilePath("", allowedExts)).toContain("cannot be empty");
    });
  });

  describe("URL validation (REST API: https/http)", () => {
    it("accepts valid http and https URLs", () => {
      expect(ConnectorSharedUtils.isValidUrl("https://api.example.com/v1/users")).toBe(true);
      expect(ConnectorSharedUtils.isValidUrl("http://localhost:8080/data")).toBe(true);
      expect(ConnectorSharedUtils.isValidUrl("https://api.github.com/repos/test?page=1&limit=50#hash")).toBe(true);
      expect(ConnectorSharedUtils.isValidUrl("http://127.0.0.1:5000/health")).toBe(true);

      expect(ConnectorSharedUtils.validateUrl("https://api.example.com")).toBeUndefined();
      expect(ConnectorSharedUtils.validateUrl("http://localhost:3000")).toBeUndefined();
    });

    it("rejects invalid, malformed, or unsupported protocols", () => {
      expect(ConnectorSharedUtils.isValidUrl("ftp://ftp.example.com/file.zip")).toBe(false);
      expect(ConnectorSharedUtils.isValidUrl("file:///home/user/test.json")).toBe(false);
      expect(ConnectorSharedUtils.isValidUrl("javascript:alert(1)")).toBe(false);
      expect(ConnectorSharedUtils.isValidUrl("not-a-valid-url")).toBe(false);
      expect(ConnectorSharedUtils.isValidUrl("http//missing-colon.com")).toBe(false);
      expect(ConnectorSharedUtils.isValidUrl("")).toBe(false);
      expect(ConnectorSharedUtils.isValidUrl("   ")).toBe(false);
      expect(ConnectorSharedUtils.isValidUrl(null)).toBe(false);

      expect(ConnectorSharedUtils.validateUrl("ftp://example.com")).toContain("valid absolute URL with protocol");
      expect(ConnectorSharedUtils.validateUrl("invalid")).toContain("valid absolute URL with protocol");
    });
  });

  describe("Connection string construction and sanitization with {password} placeholder", () => {
    it("builds key-value connection strings from dictionary, skipping empty or undefined values", () => {
      const connStr = ConnectorSharedUtils.buildKeyValueConnectionString({
        Server: "localhost",
        Port: 1433,
        Database: "AppDb",
        "User Id": "sa",
        Password: "{password}",
        EmptyField: "",
        NullField: undefined,
      });

      expect(connStr).toBe("Server=localhost;Port=1433;Database=AppDb;User Id=sa;Password={password};");
    });

    it("sanitizes key-value connection string and extracts password into isolated credential", () => {
      const res = ConnectorSharedUtils.sanitizeKeyValueConnectionString(
        "Server=dbserver;Database=master;User Id=admin;Password=SuperSecret123!;",
      );
      expect(res.password).toBe("SuperSecret123!");
      expect(res.connectionString).toBe("Server=dbserver;Database=master;User Id=admin;Password={password};");
    });

    it("supports 'Pwd=' alias in key-value connection string", () => {
      const res = ConnectorSharedUtils.sanitizeKeyValueConnectionString(
        "Data Source=localhost;Initial Catalog=app;Uid=sa;Pwd=Pass456;",
      );
      expect(res.password).toBe("Pass456");
      expect(res.connectionString).toBe("Data Source=localhost;Initial Catalog=app;Uid=sa;Pwd={password};");
    });

    it("preserves existing {password} placeholder and keeps explicit password argument", () => {
      const res = ConnectorSharedUtils.sanitizeKeyValueConnectionString(
        "Server=localhost;Database=app;User Id=sa;Password={password};",
        "ExplicitPass789",
      );
      expect(res.password).toBe("ExplicitPass789");
      expect(res.connectionString).toBe("Server=localhost;Database=app;User Id=sa;Password={password};");
    });

    it("appends Password={password}; when password is provided but connection string has no password parameter", () => {
      const res = ConnectorSharedUtils.sanitizeKeyValueConnectionString(
        "Server=localhost;Database=app;User Id=sa",
        "AttachedSecret",
      );
      expect(res.password).toBe("AttachedSecret");
      expect(res.connectionString).toBe("Server=localhost;Database=app;User Id=sa;Password={password};");
    });

    it("sanitizes RFC 3986 URI connection string and extracts credentials", () => {
      const res = ConnectorSharedUtils.sanitizeUriConnectionString(
        "postgresql://postgres:MyPgSecret@localhost:5432/mydb",
      );
      expect(res.password).toBe("MyPgSecret");
      expect(res.connectionString).toBe("postgresql://postgres:{password}@localhost:5432/mydb");
    });

    it("sanitizes URI connection string with URL-encoded special characters in password", () => {
      const res = ConnectorSharedUtils.sanitizeUriConnectionString(
        "mysql://admin:P%40ssw0rd%21@127.0.0.1:3306/shop",
      );
      expect(res.password).toBe("P@ssw0rd!");
      expect(res.connectionString).toBe("mysql://admin:{password}@127.0.0.1:3306/shop");
    });

    it("preserves existing {password} placeholder in URI connection strings", () => {
      const res = ConnectorSharedUtils.sanitizeUriConnectionString(
        "postgresql://postgres:{password}@localhost:5432/mydb",
        "ProvidedSecret",
      );
      expect(res.password).toBe("ProvidedSecret");
      expect(res.connectionString).toBe("postgresql://postgres:{password}@localhost:5432/mydb");
    });

    it("injects {password} into URI connection string without password when rawPassword is provided", () => {
      const res = ConnectorSharedUtils.sanitizeUriConnectionString(
        "postgresql://postgres@localhost:5432/mydb",
        "InjectedSecret",
      );
      expect(res.password).toBe("InjectedSecret");
      expect(res.connectionString).toBe("postgresql://postgres:{password}@localhost:5432/mydb");
    });
  });

  describe("Required fields validation against descriptor", () => {
    const dummyDescriptor: ConnectorDescriptorDto = {
      kindId: "TestKind",
      displayName: "Test Connector",
      category: "test",
      icon: "symbol",
      formFields: [
        {
          name: "mandatoryHost",
          label: "Host Name",
          type: "string",
          required: true,
          secretHandling: "none",
        },
        {
          name: "optionalPort",
          label: "Port Number",
          type: "string",
          required: false,
          secretHandling: "none",
        },
        {
          name: "password",
          label: "Secret Password",
          type: "password",
          required: true,
          secretHandling: "isolated",
        },
      ],
    };

    it("returns error when required non-password field is missing or empty", () => {
      expect(ConnectorSharedUtils.validateRequiredFields(dummyDescriptor, {})).toContain("mandatoryHost");
      expect(ConnectorSharedUtils.validateRequiredFields(dummyDescriptor, { mandatoryHost: "   " })).toContain("mandatoryHost");
      expect(ConnectorSharedUtils.validateRequiredFields(dummyDescriptor, { mandatoryHost: null })).toContain("mandatoryHost");
    });

    it("passes validation when required non-password fields are provided (ignoring password field)", () => {
      const result = ConnectorSharedUtils.validateRequiredFields(dummyDescriptor, {
        mandatoryHost: "localhost",
      });
      expect(result).toBeUndefined();
    });
  });
});
