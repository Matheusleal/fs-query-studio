// @vitest-environment jsdom
import { describe, it, expect, beforeEach } from "vitest";
import { activate, type RendererContextLike, type OutputItemLike } from "./index";
import type { HostToRendererMessage, RendererToHostMessage, TablePayload } from "./types";

function payload(overrides: Partial<TablePayload> = {}): TablePayload {
  return {
    documentUri: "file:///q.fsqnb",
    columns: [
      { name: "id", displayType: "int" },
      { name: "name", displayType: "string" },
    ],
    rows: [
      [1, "ada"],
      [2, "grace"],
      [3, "lin"],
    ],
    truncated: false,
    continuationToken: null,
    elapsedMs: 4,
    ...overrides,
  };
}

function ctx() {
  const sent: RendererToHostMessage[] = [];
  let route: ((m: HostToRendererMessage) => void) | undefined;
  const context: RendererContextLike = {
    postMessage: (m) => sent.push(m),
    onDidReceiveMessage: (fn) => {
      route = fn;
      return { dispose: () => (route = undefined) };
    },
  };
  return { context, sent, route: (m: HostToRendererMessage) => route!(m) };
}

const item = (id: string, p: TablePayload): OutputItemLike => ({ id, json: () => p });

let host: ReturnType<typeof activate>;
let el: HTMLElement;

beforeEach(() => {
  el = document.createElement("div");
  document.body.appendChild(el);
});

describe("grid renderer", () => {
  it("renders the column headers and some rows", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload()), el);
    expect(el.textContent).toContain("id");
    expect(el.textContent).toContain("name");
    expect(el.textContent).toContain("ada");
    expect(el.querySelector(".fsq-count")?.textContent).toBe("3 rows");
  });

  it("hides Load more when there is no token", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload()), el);
    const btn = el.querySelector(".fsq-loadmore") as HTMLButtonElement;
    expect(btn.hidden).toBe(true);
  });

  it("shows Load more and posts loadMore on click when truncated with a token", () => {
    const c = ctx();
    host = activate(c.context);
    host.renderOutputItem(
      item("o1", payload({ truncated: true, continuationToken: "t0" })),
      el,
    );
    const btn = el.querySelector(".fsq-loadmore") as HTMLButtonElement;
    expect(btn.hidden).toBe(false);
    btn.click();
    expect(c.sent[0]).toEqual({
      type: "loadMore",
      outputId: "o1",
      documentUri: "file:///q.fsqnb",
      token: "t0",
    });
  });

  it("notes materialized truncation and marks the count with + when truncated without a token", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload({ truncated: true, continuationToken: null })), el);
    expect((el.querySelector(".fsq-loadmore") as HTMLButtonElement).hidden).toBe(true);
    const notice = el.querySelector(".fsq-notice") as HTMLElement;
    expect(notice.hidden).toBe(false);
    expect(notice.textContent).toContain("Showing the first 3 rows");
    expect(notice.textContent).toContain("Export CSV writes the full result");
    expect(el.querySelector(".fsq-count")?.textContent).toBe("3+ rows");
  });

  it("appends rows and updates the count when a rows message arrives", () => {
    const c = ctx();
    host = activate(c.context);
    host.renderOutputItem(
      item("o1", payload({ truncated: true, continuationToken: "t0" })),
      el,
    );
    c.route({ type: "rows", outputId: "o1", rows: [[4, "kay"]], truncated: false, nextToken: null });
    expect(el.querySelector(".fsq-count")?.textContent).toBe("4 rows");
    expect((el.querySelector(".fsq-loadmore") as HTMLButtonElement).hidden).toBe(true);
  });

  it("routes a rows message only to the matching outputId", () => {
    const c = ctx();
    host = activate(c.context);
    const elA = document.createElement("div");
    const elB = document.createElement("div");
    host.renderOutputItem(item("a", payload({ truncated: true, continuationToken: "t" })), elA);
    host.renderOutputItem(item("b", payload()), elB);
    c.route({ type: "rows", outputId: "a", rows: [[9, "z"]], truncated: false, nextToken: null });
    expect(elA.querySelector(".fsq-count")?.textContent).toBe("4 rows");
    expect(elB.querySelector(".fsq-count")?.textContent).toBe("3 rows");
  });

  it("toggles to the schema view and back", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload()), el);
    (el.querySelector('[data-view="schema"]') as HTMLButtonElement).click();
    const schema = el.querySelector(".fsq-schema") as HTMLElement;
    expect(schema.hidden).toBe(false);
    expect(schema.textContent).toContain("displayType");
    expect(schema.textContent).toContain("int");
    expect((el.querySelector(".fsq-head") as HTMLElement).hidden).toBe(true);
    (el.querySelector('[data-view="data"]') as HTMLButtonElement).click();
    expect((el.querySelector(".fsq-schema") as HTMLElement).hidden).toBe(true);
    expect((el.querySelector(".fsq-head") as HTMLElement).hidden).toBe(false);
  });

  it("posts exportCsv on Export click", () => {
    const c = ctx();
    host = activate(c.context);
    host.renderOutputItem(item("o1", payload()), el);
    (el.querySelector(".fsq-export") as HTMLButtonElement).click();
    expect(c.sent[0]).toEqual({ type: "exportCsv", outputId: "o1", documentUri: "file:///q.fsqnb" });
  });

  it("shows an error notice and hides Load more on a loadMore error", () => {
    const c = ctx();
    host = activate(c.context);
    host.renderOutputItem(
      item("o1", payload({ truncated: true, continuationToken: "t0" })),
      el,
    );
    c.route({ type: "error", outputId: "o1", scope: "loadMore", message: "re-run the cell" });
    const notice = el.querySelector(".fsq-notice") as HTMLElement;
    expect(notice.hidden).toBe(false);
    expect(notice.textContent).toContain("re-run the cell");
    expect((el.querySelector(".fsq-loadmore") as HTMLButtonElement).hidden).toBe(true);
  });

  it("uses a singular label for one row", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload({ rows: [[1, "solo"]] })), el);
    expect(el.querySelector(".fsq-count")?.textContent).toBe("1 row");
  });

  it("right-aligns numeric columns (header + body) and leaves text columns alone", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload()), el);
    const headCells = el.querySelectorAll(".fsq-head .fsq-cell");
    expect(headCells[0].classList.contains("fsq-num")).toBe(true); // id
    expect(headCells[1].classList.contains("fsq-num")).toBe(false); // name
    const firstRow = el.querySelector(".fsq-rows .fsq-row")!;
    const bodyCells = firstRow.querySelectorAll(".fsq-cell");
    expect(bodyCells[0].classList.contains("fsq-num")).toBe(true);
    expect(bodyCells[1].classList.contains("fsq-num")).toBe(false);
  });

  it("renders a null cell as a muted NULL", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload({ rows: [[1, null]] })), el);
    const cell = el.querySelectorAll(".fsq-rows .fsq-row .fsq-cell")[1];
    expect(cell.textContent).toBe("NULL");
    expect(cell.classList.contains("fsq-null")).toBe(true);
  });

  it("infers a numeric column from the first non-null value", () => {
    host = activate(ctx().context);
    host.renderOutputItem(
      item("o1", payload({ columns: [{ name: "amount", displayType: "decimal" }], rows: [[null], [42]] })),
      el,
    );
    expect(el.querySelector(".fsq-head .fsq-cell")!.classList.contains("fsq-num")).toBe(true);
  });

  it("shows a No rows placeholder for an empty result", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload({ columns: [], rows: [] })), el);
    expect(el.querySelector(".fsq-empty")?.textContent).toBe("No rows");
    expect(el.querySelector(".fsq-count")?.textContent).toBe("0 rows");
  });
});
