// @vitest-environment jsdom
import { describe, it, expect, beforeEach } from "vitest";
import { activate, type RendererContextLike, type OutputItemLike } from "./index";
import type { HostToRendererMessage, RendererToHostMessage, TablePayload } from "./types";

function samplePayload(overrides: Partial<TablePayload> = {}): TablePayload {
  return {
    documentUri: "file:///workspace/query.fsqnb",
    columns: [
      { name: "id", displayType: "int" },
      { name: "title", displayType: "string" },
      { name: "price", displayType: "decimal" },
      { name: "in_stock", displayType: "boolean" },
      { name: "notes", displayType: "string" },
    ],
    rows: [
      [101, "Widget A", 19.99, true, "Standard item"],
      [102, "Gadget B", 49.5, false, null],
      [103, "Tool C", 9.0, true, "Discounted"],
    ],
    truncated: false,
    continuationToken: null,
    elapsedMs: 12,
    ...overrides,
  };
}

function createMockContext() {
  const sentMessages: RendererToHostMessage[] = [];
  let messageHandler: ((m: HostToRendererMessage) => void) | undefined;
  const context: RendererContextLike = {
    postMessage: (m) => sentMessages.push(m),
    onDidReceiveMessage: (fn) => {
      messageHandler = fn;
      return { dispose: () => (messageHandler = undefined) };
    },
  };
  return {
    context,
    sentMessages,
    receiveFromHost: (m: HostToRendererMessage) => messageHandler!(m),
  };
}

const createItem = (id: string, payload: TablePayload): OutputItemLike => ({
  id,
  json: () => payload,
});

describe("Results Grid UI Elements & Interactivity", () => {
  let container: HTMLElement;
  let mock: ReturnType<typeof createMockContext>;
  let host: ReturnType<typeof activate>;

  beforeEach(() => {
    container = document.createElement("div");
    document.body.appendChild(container);
    mock = createMockContext();
    host = activate(mock.context);
  });

  describe("Layout and Container Elements", () => {
    it("renders the root .fsq-grid and .fsq-panel containers", () => {
      host.renderOutputItem(createItem("out-1", samplePayload()), container);

      const grid = container.querySelector(".fsq-grid");
      const panel = container.querySelector(".fsq-panel");
      expect(grid).toBeTruthy();
      expect(panel).toBeTruthy();
      expect(grid?.contains(panel)).toBe(true);
    });

    it("injects component stylesheet into element", () => {
      host.renderOutputItem(createItem("out-1", samplePayload()), container);
      const style = container.querySelector("style");
      expect(style).toBeTruthy();
      expect(style?.textContent).toContain(".fsq-grid");
      expect(style?.textContent).toContain(".fsq-toolbar");
    });
  });

  describe("Toolbar UI Elements", () => {
    it("renders segmented view buttons (Data and Schema)", () => {
      host.renderOutputItem(createItem("out-1", samplePayload()), container);

      const toolbar = container.querySelector(".fsq-toolbar");
      expect(toolbar).toBeTruthy();

      const seg = toolbar?.querySelector(".fsq-seg");
      expect(seg).toBeTruthy();

      const dataBtn = seg?.querySelector('button[data-view="data"]') as HTMLButtonElement;
      const schemaBtn = seg?.querySelector('button[data-view="schema"]') as HTMLButtonElement;

      expect(dataBtn).toBeTruthy();
      expect(dataBtn.textContent).toBe("Data");
      expect(dataBtn.classList.contains("active")).toBe(true);

      expect(schemaBtn).toBeTruthy();
      expect(schemaBtn.textContent).toBe("Schema");
      expect(schemaBtn.classList.contains("active")).toBe(false);
    });

    it("renders row count indicator with correct pluralization and numbers", () => {
      // 3 rows
      host.renderOutputItem(createItem("out-1", samplePayload()), container);
      const countEl = container.querySelector(".fsq-count");
      expect(countEl?.textContent).toBe("3 rows");

      // 1 row
      const singleRowContainer = document.createElement("div");
      host.renderOutputItem(
        createItem("out-2", samplePayload({ rows: [[1, "Solo", 10.0, true, null]] })),
        singleRowContainer,
      );
      expect(singleRowContainer.querySelector(".fsq-count")?.textContent).toBe("1 row");

      // 0 rows
      const emptyContainer = document.createElement("div");
      host.renderOutputItem(createItem("out-3", samplePayload({ rows: [] })), emptyContainer);
      expect(emptyContainer.querySelector(".fsq-count")?.textContent).toBe("0 rows");
    });

    it("renders Export CSV button and dispatches exportCsv on click", () => {
      host.renderOutputItem(createItem("out-1", samplePayload()), container);

      const exportBtn = container.querySelector(".fsq-export") as HTMLButtonElement;
      expect(exportBtn).toBeTruthy();
      expect(exportBtn.textContent).toBe("Export CSV");

      exportBtn.click();
      expect(mock.sentMessages).toHaveLength(1);
      expect(mock.sentMessages[0]).toEqual({
        type: "exportCsv",
        outputId: "out-1",
        documentUri: "file:///workspace/query.fsqnb",
      });
    });
  });

  describe("Sticky Table Header Elements", () => {
    it("renders column headers with names, tooltips, and type indicators", () => {
      host.renderOutputItem(createItem("out-1", samplePayload()), container);

      const head = container.querySelector(".fsq-head");
      expect(head).toBeTruthy();

      const cells = head?.querySelectorAll(".fsq-cell");
      expect(cells).toHaveLength(5);

      expect(cells?.[0].textContent).toBe("id");
      expect(cells?.[0].getAttribute("title")).toBe("id · int");

      expect(cells?.[1].textContent).toBe("title");
      expect(cells?.[1].getAttribute("title")).toBe("title · string");

      expect(cells?.[2].textContent).toBe("price");
      expect(cells?.[2].getAttribute("title")).toBe("price · decimal");
    });

    it("applies .fsq-num class to numeric column headers and preserves text headers", () => {
      host.renderOutputItem(createItem("out-1", samplePayload()), container);

      const cells = container.querySelectorAll(".fsq-head .fsq-cell");
      expect(cells[0].classList.contains("fsq-num")).toBe(true); // id
      expect(cells[1].classList.contains("fsq-num")).toBe(false); // title
      expect(cells[2].classList.contains("fsq-num")).toBe(true); // price
      expect(cells[3].classList.contains("fsq-num")).toBe(false); // in_stock
      expect(cells[4].classList.contains("fsq-num")).toBe(false); // notes
    });
  });

  describe("Data Viewport & Cell Formatting", () => {
    it("renders virtualized viewport and sizer with calculated height", () => {
      host.renderOutputItem(createItem("out-1", samplePayload()), container);

      const viewport = container.querySelector(".fsq-viewport") as HTMLElement;
      const sizer = container.querySelector(".fsq-sizer") as HTMLElement;
      const rowsHost = container.querySelector(".fsq-rows") as HTMLElement;

      expect(viewport).toBeTruthy();
      expect(sizer).toBeTruthy();
      expect(rowsHost).toBeTruthy();

      // 3 rows * 24px = 72px
      expect(sizer.style.height).toBe("72px");
    });

    it("renders alternating zebra rows with .fsq-odd class", () => {
      host.renderOutputItem(createItem("out-1", samplePayload()), container);

      const rows = container.querySelectorAll(".fsq-rows .fsq-row");
      expect(rows).toHaveLength(3);

      expect(rows[0].classList.contains("fsq-odd")).toBe(false);
      expect(rows[1].classList.contains("fsq-odd")).toBe(true);
      expect(rows[2].classList.contains("fsq-odd")).toBe(false);
    });

    it("formats null values with .fsq-null class and displays 'NULL'", () => {
      host.renderOutputItem(createItem("out-1", samplePayload()), container);

      const rows = container.querySelectorAll(".fsq-rows .fsq-row");
      const row2Cells = rows[1].querySelectorAll(".fsq-cell");

      // Row 2: [102, "Gadget B", 49.5, false, null]
      const notesCell = row2Cells[4];
      expect(notesCell.textContent).toBe("NULL");
      expect(notesCell.classList.contains("fsq-null")).toBe(true);
    });

    it("formats boolean, number, and string cell values correctly", () => {
      host.renderOutputItem(createItem("out-1", samplePayload()), container);

      const row1Cells = container.querySelectorAll(".fsq-rows .fsq-row")[0].querySelectorAll(".fsq-cell");
      expect(row1Cells[0].textContent).toBe("101");
      expect(row1Cells[0].classList.contains("fsq-num")).toBe(true);

      expect(row1Cells[1].textContent).toBe("Widget A");
      expect(row1Cells[2].textContent).toBe("19.99");
      expect(row1Cells[3].textContent).toBe("true");
      expect(row1Cells[4].textContent).toBe("Standard item");
    });

    it("renders UTF-8 accented strings in grid headers and cells without mojibake", () => {
      host.renderOutputItem(
        createItem(
          "out-utf8",
          samplePayload({
            columns: [
              { name: "EntidadePrincipal", displayType: "string" },
              { name: "Fonte", displayType: "string" },
              { name: "Tipo", displayType: "string" },
              { name: "TotalRegistros", displayType: "int" },
            ],
            rows: [
              ["Transações", "SalesFeed", "CSV (Arquivo Plano)", 10],
              ["Perfis de Fidelidade", "CustomersFeed", "CSV (Arquivo Plano)", 6],
              ["Operação São Paulo — Ação", "InventoryStore", "SQLite (Relacional)", 4],
            ],
          }),
        ),
        container,
      );

      const rows = container.querySelectorAll(".fsq-rows .fsq-row");
      expect(rows).toHaveLength(3);
      expect(rows[0].querySelectorAll(".fsq-cell")[0].textContent).toBe("Transações");
      expect(rows[1].querySelectorAll(".fsq-cell")[0].textContent).toBe("Perfis de Fidelidade");
      expect(rows[2].querySelectorAll(".fsq-cell")[0].textContent).toBe("Operação São Paulo — Ação");
    });

    it("renders .fsq-empty container with 'No rows' when result set is empty", () => {
      host.renderOutputItem(createItem("out-empty", samplePayload({ rows: [] })), container);

      const emptyEl = container.querySelector(".fsq-empty");
      expect(emptyEl).toBeTruthy();
      expect(emptyEl?.textContent).toBe("No rows");
    });
  });

  describe("Schema View Toggle UI", () => {
    it("switches to schema table when clicking Schema button and hides data viewport", () => {
      host.renderOutputItem(createItem("out-1", samplePayload()), container);

      const schemaBtn = container.querySelector('button[data-view="schema"]') as HTMLButtonElement;
      const dataBtn = container.querySelector('button[data-view="data"]') as HTMLButtonElement;
      const head = container.querySelector(".fsq-head") as HTMLElement;
      const viewport = container.querySelector(".fsq-viewport") as HTMLElement;
      const schema = container.querySelector(".fsq-schema") as HTMLElement;

      // Click Schema
      schemaBtn.click();

      expect(schemaBtn.classList.contains("active")).toBe(true);
      expect(dataBtn.classList.contains("active")).toBe(false);
      expect(head.hidden).toBe(true);
      expect(viewport.hidden).toBe(true);
      expect(schema.hidden).toBe(false);

      // Verify schema content
      const schemaRows = schema.querySelectorAll(".fsq-row");
      expect(schemaRows.length).toBe(6); // 1 header + 5 columns

      const schemaHeader = schemaRows[0].textContent;
      expect(schemaHeader).toContain("name");
      expect(schemaHeader).toContain("displayType");

      expect(schemaRows[1].textContent).toContain("id");
      expect(schemaRows[1].textContent).toContain("int");

      expect(schemaRows[2].textContent).toContain("title");
      expect(schemaRows[2].textContent).toContain("string");

      // Click Data back
      dataBtn.click();
      expect(dataBtn.classList.contains("active")).toBe(true);
      expect(schemaBtn.classList.contains("active")).toBe(false);
      expect(head.hidden).toBe(false);
      expect(viewport.hidden).toBe(false);
      expect(schema.hidden).toBe(true);
    });
  });

  describe("Pagination and Load More Button UI", () => {
    it("shows .fsq-loadmore button when continuationToken is present", () => {
      host.renderOutputItem(
        createItem("out-more", samplePayload({ truncated: true, continuationToken: "token-abc" })),
        container,
      );

      const loadMoreBtn = container.querySelector(".fsq-loadmore") as HTMLButtonElement;
      expect(loadMoreBtn).toBeTruthy();
      expect(loadMoreBtn.hidden).toBe(false);
      expect(loadMoreBtn.disabled).toBe(false);
      expect(loadMoreBtn.textContent).toBe("Load more");
    });

    it("hides .fsq-loadmore button when continuationToken is null", () => {
      host.renderOutputItem(
        createItem("out-nomore", samplePayload({ truncated: false, continuationToken: null })),
        container,
      );

      const loadMoreBtn = container.querySelector(".fsq-loadmore") as HTMLButtonElement;
      expect(loadMoreBtn.hidden).toBe(true);
    });

    it("disables Load more button and sends loadMore message when clicked", () => {
      host.renderOutputItem(
        createItem("out-more", samplePayload({ truncated: true, continuationToken: "token-abc" })),
        container,
      );

      const loadMoreBtn = container.querySelector(".fsq-loadmore") as HTMLButtonElement;
      loadMoreBtn.click();

      expect(loadMoreBtn.disabled).toBe(true);
      expect(mock.sentMessages).toHaveLength(1);
      expect(mock.sentMessages[0]).toEqual({
        type: "loadMore",
        outputId: "out-more",
        documentUri: "file:///workspace/query.fsqnb",
        token: "token-abc",
      });
    });

    it("re-enables button and appends rows when host sends rows message", () => {
      host.renderOutputItem(
        createItem("out-more", samplePayload({ truncated: true, continuationToken: "token-abc" })),
        container,
      );

      const loadMoreBtn = container.querySelector(".fsq-loadmore") as HTMLButtonElement;
      loadMoreBtn.click();
      expect(loadMoreBtn.disabled).toBe(true);

      // Simulate incoming rows from host
      mock.receiveFromHost({
        type: "rows",
        outputId: "out-more",
        rows: [[104, "New Device", 199.0, true, "Extra"]],
        truncated: false,
        nextToken: null,
      });

      expect(loadMoreBtn.disabled).toBe(false);
      expect(loadMoreBtn.hidden).toBe(true); // nextToken is null, so hidden now
      expect(container.querySelector(".fsq-count")?.textContent).toBe("4 rows");

      // Verify row appended to DOM
      const rows = container.querySelectorAll(".fsq-rows .fsq-row");
      expect(rows).toHaveLength(4);
      expect(rows[3].textContent).toContain("New Device");
    });
  });

  describe("Notice Banners and Notification UI", () => {
    it("renders materialized truncation notice with plus in row count", () => {
      host.renderOutputItem(
        createItem("out-mat", samplePayload({ truncated: true, continuationToken: null })),
        container,
      );

      const notice = container.querySelector(".fsq-notice") as HTMLElement;
      expect(notice.hidden).toBe(false);
      expect(notice.textContent).toContain("Showing the first 3 rows");
      expect(notice.textContent).toContain("Export CSV writes the full result");

      const count = container.querySelector(".fsq-count");
      expect(count?.textContent).toBe("3+ rows");
    });

    it("displays error notice when host sends error message", () => {
      host.renderOutputItem(
        createItem("out-err", samplePayload({ truncated: true, continuationToken: "tok" })),
        container,
      );

      mock.receiveFromHost({
        type: "error",
        outputId: "out-err",
        scope: "loadMore",
        message: "Session expired: please re-run cell",
      });

      const notice = container.querySelector(".fsq-notice") as HTMLElement;
      const loadMore = container.querySelector(".fsq-loadmore") as HTMLButtonElement;

      expect(notice.hidden).toBe(false);
      expect(notice.textContent).toContain("Session expired: please re-run cell");
      expect(loadMore.hidden).toBe(true);
    });

    it("displays export success notice when host sends exportDone message", () => {
      host.renderOutputItem(createItem("out-exp", samplePayload()), container);

      mock.receiveFromHost({
        type: "exportDone",
        outputId: "out-exp",
        path: "/exports/results_2026.csv",
      });

      const notice = container.querySelector(".fsq-notice") as HTMLElement;
      expect(notice.hidden).toBe(false);
      expect(notice.textContent).toBe("Exported to /exports/results_2026.csv");
    });
  });
});
