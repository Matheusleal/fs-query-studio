import * as vscode from "vscode";

export const QUERY_KEYWORDS = new Set([
  "query",
  "select",
  "where",
  "join",
  "on",
  "equals",
  "into",
  "groupBy",
  "groupValBy",
  "sortBy",
  "sortByDescending",
  "thenBy",
  "thenByDescending",
  "distinct",
  "count",
  "sumBy",
  "minBy",
  "maxBy",
  "averageBy",
  "all",
  "head",
  "nth",
  "exists",
  "contains",
  "take",
  "takeWhile",
  "skip",
  "skipWhile",
  "find",
]);

export const querySemanticTokensLegend = new vscode.SemanticTokensLegend(
  ["keyword", "variable", "property"],
  ["control", "query"],
);

export class FSharpQuerySemanticTokensProvider
  implements vscode.DocumentSemanticTokensProvider
{
  provideDocumentSemanticTokens(
    document: vscode.TextDocument,
  ): vscode.ProviderResult<vscode.SemanticTokens> {
    const builder = new vscode.SemanticTokensBuilder(querySemanticTokensLegend);
    const text = document.getText();
    const lines = text.split(/\r?\n/);

    for (let lineIndex = 0; lineIndex < lines.length; lineIndex++) {
      const line = lines[lineIndex];
      const trimmed = line.trim();
      if (trimmed.startsWith("//") || trimmed.startsWith("(*")) {
        continue;
      }

      const wordRegex = /\b([a-zA-Z_][a-zA-Z0-9_]*)\b/g;
      let match: RegExpExecArray | null;
      while ((match = wordRegex.exec(line)) !== null) {
        const word = match[1];
        if (QUERY_KEYWORDS.has(word)) {
          // Token index 0 is "keyword", modifier 1 (bitmask: 1 << 0) is "control"
          builder.push(lineIndex, match.index, word.length, 0, 1);
        }
      }
    }

    return builder.build();
  }
}
