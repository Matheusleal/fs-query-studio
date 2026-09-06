import { describe, it, expect } from "vitest";
import { SchemaCache } from "./schema-cache";

describe("SchemaCache", () => {
  it("returns undefined for an unknown id", () => {
    expect(new SchemaCache().get("x")).toBeUndefined();
  });
  it("returns what was set", () => {
    const c = new SchemaCache();
    const tables = [{ name: "t", columns: [{ name: "c", dataType: "int" }] }];
    c.set("id1", tables);
    expect(c.get("id1")).toBe(tables);
  });
  it("clear drops every entry", () => {
    const c = new SchemaCache();
    c.set("id1", []);
    c.clear();
    expect(c.get("id1")).toBeUndefined();
  });
});
