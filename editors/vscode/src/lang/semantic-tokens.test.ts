import { describe, it, expect } from "vitest";
import {
  FSharpQuerySemanticTokensProvider,
  QUERY_KEYWORDS,
  querySemanticTokensLegend,
} from "./semantic-tokens";
import * as vscode from "vscode";

describe("FSharpQuerySemanticTokensProvider", () => {
  it("recognizes LINQ query keywords", () => {
    expect(QUERY_KEYWORDS.has("query")).toBe(true);
    expect(QUERY_KEYWORDS.has("select")).toBe(true);
    expect(QUERY_KEYWORDS.has("where")).toBe(true);
    expect(QUERY_KEYWORDS.has("join")).toBe(true);
    expect(QUERY_KEYWORDS.has("on")).toBe(true);
    expect(QUERY_KEYWORDS.has("equals")).toBe(true);
    expect(QUERY_KEYWORDS.has("sortBy")).toBe(true);
  });

  it("builds tokens for query expressions", () => {
    const provider = new FSharpQuerySemanticTokensProvider();
    const fakeDoc = {
      getText: () => `query {
    for c in conn.core.tenants do
    join s in connCsv.test1.Rows on (c.id = s.id)
    select (c, s)
}`,
    } as unknown as vscode.TextDocument;

    const tokens = provider.provideDocumentSemanticTokens(fakeDoc);
    expect(tokens).toBeDefined();
    // SemanticTokens data is an array of 5 integers per token
    expect((tokens as vscode.SemanticTokens).data.length).toBeGreaterThan(0);
  });
});
