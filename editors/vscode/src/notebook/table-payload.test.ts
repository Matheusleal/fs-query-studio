import { describe, it, expect } from "vitest";
import { buildTablePayload } from "./table-payload";
import type { ResultEnvelope } from "../protocol";

function tableEnv(overrides: Partial<ResultEnvelope>): ResultEnvelope {
  return {
    kind: { Case: "Table" },
    displayText: null,
    schema: [{ name: "id", displayType: "int" }],
    rows: [[1], [2]],
    isTruncated: false,
    continuationToken: null,
    elapsedMs: 9,
    output: [],
    ...overrides,
  };
}

describe("buildTablePayload", () => {
  it("maps a Results.table-style envelope (real columns, no token)", () => {
    const payload = buildTablePayload(
      tableEnv({
        schema: [
          { name: "name", displayType: "string" },
          { name: "age", displayType: "int" },
        ],
        rows: [
          ["ada", 36],
          ["grace", 45],
        ],
      }),
      "file:///q.fsqnb",
    );
    expect(payload).toEqual({
      documentUri: "file:///q.fsqnb",
      columns: [
        { name: "name", displayType: "string" },
        { name: "age", displayType: "int" },
      ],
      rows: [
        ["ada", 36],
        ["grace", 45],
      ],
      truncated: false,
      continuationToken: null,
      elapsedMs: 9,
    });
  });

  it("carries the continuation token and truncated flag from a paged tail-value envelope", () => {
    const payload = buildTablePayload(
      tableEnv({
        schema: [{ name: "Value", displayType: "obj" }],
        rows: [[1], [2], [3]],
        isTruncated: true,
        continuationToken: "abc123",
      }),
      "file:///q.fsqnb",
    );
    expect(payload.truncated).toBe(true);
    expect(payload.continuationToken).toBe("abc123");
    expect(payload.columns).toEqual([{ name: "Value", displayType: "obj" }]);
  });

  it("tolerates missing schema/rows", () => {
    const payload = buildTablePayload(
      { ...tableEnv({}), schema: undefined as never, rows: undefined as never },
      "u",
    );
    expect(payload.columns).toEqual([]);
    expect(payload.rows).toEqual([]);
  });
});
