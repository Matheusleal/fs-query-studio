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
    expect(QUERY_KEYWORDS.has("fquery")).toBe(true);
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
    // New CTE & Set Operations keywords
    expect(QUERY_KEYWORDS.has("withCte")).toBe(true);
    expect(QUERY_KEYWORDS.has("withCteSql")).toBe(true);
    expect(QUERY_KEYWORDS.has("withRecursiveCte")).toBe(true);
    expect(QUERY_KEYWORDS.has("withRecursiveCteSql")).toBe(true);
    expect(QUERY_KEYWORDS.has("union")).toBe(true);
    expect(QUERY_KEYWORDS.has("unionAll")).toBe(true);
    expect(QUERY_KEYWORDS.has("intersect")).toBe(true);
    expect(QUERY_KEYWORDS.has("except")).toBe(true);
    expect(QUERY_KEYWORDS.has("fullOuterJoin")).toBe(true);
    expect(QUERY_KEYWORDS.has("rightOuterJoin")).toBe(true);
    expect(QUERY_KEYWORDS.has("crossJoin")).toBe(true);
    expect(QUERY_KEYWORDS.has("partitionBy")).toBe(true);
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

  it("verifies TextMate grammar file includes all QUERY_KEYWORDS and new functions", async () => {
    const fs = await import("fs");
    const path = await import("path");
    const grammarPath = path.resolve(__dirname, "../../syntaxes/fsharp-query.tmLanguage.json");
    expect(fs.existsSync(grammarPath)).toBe(true);

    const grammar = JSON.parse(fs.readFileSync(grammarPath, "utf8"));
    const keywordsRegex = new RegExp(grammar.repository.keywords.patterns[0].match);

    for (const kw of QUERY_KEYWORDS) {
      expect(keywordsRegex.test(kw)).toBe(true);
    }

    const functionsRegex = new RegExp(grammar.repository.functions.patterns[0].match);
    const newFunctions = [
      "explainQuery",
      "recursive",
      "jsonValue",
      "window",
      "rowNumber",
      "rank",
      "denseRank",
      "ntile",
      "lag",
      "lead",
      "firstValue",
      "lastValue",
      "withCte",
      "withRecursiveCte",
      "union",
      "unionAll",
      "intersect",
      "except",
    ];
    for (const fn of newFunctions) {
      expect(functionsRegex.test(fn)).toBe(true);
    }
  });
});
