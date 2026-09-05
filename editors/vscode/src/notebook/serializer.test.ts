import { describe, it, expect } from "vitest";
import { FsqnbSerializer } from "./serializer";

const enc = (s: string) => new TextEncoder().encode(s);
const dec = (u: Uint8Array) => new TextDecoder().decode(u);

describe("FsqnbSerializer.deserializeNotebook", () => {
  it("maps code and markdown cells to NotebookCellData", () => {
    const data = new FsqnbSerializer().deserializeNotebook(
      enc(
        JSON.stringify({
          version: 1,
          cells: [
            { kind: "markdown", value: "# H" },
            { kind: "code", language: "fsharp", value: "1 + 1" },
          ],
        }),
      ),
    );
    expect(data.cells.map((c) => c.kind)).toEqual([1, 2]); // Markup, Code
    expect(data.cells[1].value).toBe("1 + 1");
    expect(data.cells[1].languageId).toBe("fsharp");
    expect(data.cells[0].languageId).toBe("markdown");
  });

  it("accepts an empty file", () => {
    const data = new FsqnbSerializer().deserializeNotebook(enc(""));
    expect(data.cells).toEqual([]);
  });
});

describe("FsqnbSerializer.serializeNotebook", () => {
  it("round-trips content through a deserialize/serialize/deserialize cycle", () => {
    const s = new FsqnbSerializer();
    const original = enc(
      JSON.stringify({
        version: 1,
        cells: [{ kind: "code", language: "fsharp", value: "let x = 1" }],
        metadata: {},
      }),
    );
    const back = s.serializeNotebook(s.deserializeNotebook(original));
    expect(dec(back)).toContain('"let x = 1"');
    const reparsed = s.deserializeNotebook(back);
    expect(reparsed.cells[0].value).toBe("let x = 1");
    expect(reparsed.cells[0].kind).toBe(2);
  });

  it("writes a version-1 envelope", () => {
    const s = new FsqnbSerializer();
    const bytes = s.serializeNotebook(s.deserializeNotebook(enc("")));
    expect(JSON.parse(dec(bytes)).version).toBe(1);
  });
});
