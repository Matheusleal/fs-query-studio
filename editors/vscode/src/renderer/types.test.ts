import { describe, it, expect } from "vitest";
import {
  FSQUERY_TABLE_MIME,
  FSQUERY_RENDERER_ID,
  GRID_ROW_HEIGHT_PX,
  isRendererToHostMessage,
} from "./types";

describe("renderer wire constants", () => {
  it("pins the MIME type and renderer id the package.json contribution depends on", () => {
    expect(FSQUERY_TABLE_MIME).toBe("application/x-fsquery-table+json");
    expect(FSQUERY_RENDERER_ID).toBe("fs-query-studio-table");
    expect(GRID_ROW_HEIGHT_PX).toBeGreaterThan(0);
  });
});

describe("isRendererToHostMessage", () => {
  it("accepts loadMore and exportCsv shapes", () => {
    expect(
      isRendererToHostMessage({ type: "loadMore", outputId: "o", documentUri: "u", token: "t" }),
    ).toBe(true);
    expect(isRendererToHostMessage({ type: "exportCsv", outputId: "o", documentUri: "u" })).toBe(true);
  });

  it("rejects anything else", () => {
    expect(isRendererToHostMessage(null)).toBe(false);
    expect(isRendererToHostMessage(undefined)).toBe(false);
    expect(isRendererToHostMessage("loadMore")).toBe(false);
    expect(isRendererToHostMessage({ type: "rows" })).toBe(false);
    expect(isRendererToHostMessage({})).toBe(false);
  });
});
