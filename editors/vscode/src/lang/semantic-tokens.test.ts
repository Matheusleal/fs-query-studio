import { describe, it, expect } from "vitest";
import {
  FSharpQuerySemanticTokensProvider,
  QUERY_KEYWORDS,
  querySemanticTokensLegend,
} from "./semantic-tokens";
import * as vscode from "vscode";

describe("FSharpQuerySemanticTokensProvider", () => {
  it("recognizes LINQ and fsql query keywords", () => {
    expect(QUERY_KEYWORDS.has("query")).toBe(true);
    expect(QUERY_KEYWORDS.has("fsql")).toBe(true);
    expect(QUERY_KEYWORDS.has("whereExists")).toBe(true);
    expect(QUERY_KEYWORDS.has("whereNotExists")).toBe(true);
    expect(QUERY_KEYWORDS.has("having")).toBe(true);
    expect(QUERY_KEYWORDS.has("crossApply")).toBe(true);
    expect(QUERY_KEYWORDS.has("lateral")).toBe(true);
    expect(QUERY_KEYWORDS.has("select")).toBe(true);
    expect(QUERY_KEYWORDS.has("where")).toBe(true);
    expect(QUERY_KEYWORDS.has("join")).toBe(true);
    expect(QUERY_KEYWORDS.has("on")).toBe(true);
    expect(QUERY_KEYWORDS.has("equals")).toBe(true);
    expect(QUERY_KEYWORDS.has("sortBy")).toBe(true);
  });

  it("builds tokens for query and fsql expressions", () => {
    const provider = new FSharpQuerySemanticTokensProvider();
    const fakeDoc = {
      getText: () => `fsql {
    for c in conn.core.tenants do
    whereExists (fsql { for s in connCsv.test1.Rows do where (c.id = s.id) })
    select (c)
}`,
    } as unknown as vscode.TextDocument;

    const tokens = provider.provideDocumentSemanticTokens(fakeDoc);
    expect(tokens).toBeDefined();
    // SemanticTokens data is an array of 5 integers per token
    expect((tokens as vscode.SemanticTokens).data.length).toBeGreaterThan(0);
  });
});
