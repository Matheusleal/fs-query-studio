import { visibleRange, formatCell, computeColWidths, computeGridColumnsCss } from "./grid-core";
import {
  GRID_ROW_HEIGHT_PX,
  type HostToRendererMessage,
  type RendererToHostMessage,
  type TablePayload,
} from "./types";

/** The bits of `vscode-notebook-renderer`'s `OutputItem` used here. */
export interface OutputItemLike {
  id: string;
  json(): unknown;
}

/** The bits of the renderer `RendererContext` used here (messaging is optional). */
export interface RendererContextLike {
  postMessage?(message: RendererToHostMessage): void;
  onDidReceiveMessage?(listener: (message: HostToRendererMessage) => void): { dispose(): void };
}

const STYLE = `
.fsq-grid {
  font-family: var(--vscode-font-family, system-ui, sans-serif);
  font-size: var(--vscode-font-size, 13px);
  color: var(--vscode-foreground);
  --fsq-border: var(--vscode-widget-border, var(--vscode-panel-border));
}
.fsq-panel { border: 1px solid var(--fsq-border); border-radius: 4px; overflow: hidden; max-width: 100%; }

.fsq-toolbar { display: flex; align-items: center; gap: 4px; padding: 3px 5px;
  background: var(--vscode-editorWidget-background, transparent);
  border-bottom: 1px solid var(--fsq-border); }
.fsq-seg { display: inline-flex; gap: 2px; }
.fsq-toolbar button { font: inherit; line-height: 18px; padding: 1px 8px; cursor: pointer;
  background: transparent; color: var(--vscode-foreground);
  border: 1px solid transparent; border-radius: 3px; }
.fsq-toolbar button:hover { background: var(--vscode-toolbar-hoverBackground, rgba(128, 128, 128, 0.15)); }
.fsq-toolbar button.active { background: var(--vscode-button-background); color: var(--vscode-button-foreground);
  border-color: var(--vscode-button-background); }
.fsq-toolbar button:focus-visible { outline: 1px solid var(--vscode-focusBorder); outline-offset: -1px; }
.fsq-count { margin-left: auto; padding: 0 4px; opacity: 0.7; font-variant-numeric: tabular-nums; }

.fsq-viewport { overflow: auto; max-height: 420px; }
.fsq-sizer { position: relative; width: max(100%, var(--fsq-grid-width, max-content)); min-width: 100%; }
.fsq-rows { position: absolute; left: 0; right: 0; width: 100%; z-index: 1; }
.fsq-row { display: grid; grid-template-columns: var(--fsq-grid-cols, 1fr); height: ${GRID_ROW_HEIGHT_PX}px; line-height: ${GRID_ROW_HEIGHT_PX}px; width: 100%; }
.fsq-rows .fsq-row.fsq-odd { background: var(--vscode-tree-tableOddRowsBackground, rgba(128, 128, 128, 0.04)); }
.fsq-rows .fsq-row:hover { background: var(--vscode-list-hoverBackground); color: var(--vscode-list-hoverForeground, inherit); }
.fsq-empty { padding: 8px 10px; opacity: 0.6; }

.fsq-head { display: grid; grid-template-columns: var(--fsq-grid-cols, 1fr); height: ${GRID_ROW_HEIGHT_PX}px; line-height: ${GRID_ROW_HEIGHT_PX}px;
  position: sticky; top: 0; z-index: 10; font-weight: 600;
  width: max(100%, var(--fsq-grid-width, max-content)); min-width: 100%;
  background-color: var(--vscode-editorWidget-background, var(--vscode-editor-background));
  background-image: linear-gradient(var(--vscode-keybindingTable-headerBackground, rgba(128, 128, 128, 0.08)), var(--vscode-keybindingTable-headerBackground, rgba(128, 128, 128, 0.08)));
  border-bottom: 1px solid var(--fsq-border);
  box-shadow: 0 1px 0 var(--fsq-border); }
.fsq-head .fsq-cell { border-right: 1px solid var(--vscode-tree-tableColumnsBorder, transparent); }
.fsq-head .fsq-cell:last-child { border-right: 0; }

.fsq-cell { min-width: 0; padding: 0 10px; overflow: hidden;
  text-overflow: ellipsis; white-space: nowrap; user-select: text; }
.fsq-cell.fsq-num { text-align: right; font-variant-numeric: tabular-nums; font-feature-settings: "tnum"; }
.fsq-cell.fsq-null { opacity: 0.45; font-style: italic; }

.fsq-schema .fsq-row { display: flex; padding: 0 10px; width: 100%; }
.fsq-schema .fsq-row:first-child { font-weight: 600; border-bottom: 1px solid var(--fsq-border); }
.fsq-schema .fsq-cell { flex: 1 1 0; min-width: 96px; }

.fsq-raw-query { padding: 10px 14px; max-height: 420px; overflow: auto; display: flex; flex-direction: column; gap: 12px; }
.fsq-raw-query[hidden],
.fsq-schema[hidden],
.fsq-head[hidden],
.fsq-viewport[hidden] { display: none !important; }
.fsq-raw-query-header { display: flex; align-items: center; justify-content: space-between; }
.fsq-raw-query-title { font-weight: 600; font-size: 11px; text-transform: uppercase; letter-spacing: 0.5px; opacity: 0.8; }
.fsq-raw-query-actions { display: inline-flex; gap: 6px; }
.fsq-raw-query-actions button { font: inherit; font-size: 12px; line-height: 18px; padding: 2px 8px; cursor: pointer;
  background: var(--vscode-button-secondaryBackground, transparent);
  color: var(--vscode-button-secondaryForeground, var(--vscode-foreground));
  border: 1px solid var(--fsq-border); border-radius: 3px; }
.fsq-raw-query-actions button:hover { background: var(--vscode-button-secondaryHoverBackground, rgba(128, 128, 128, 0.15)); }
.fsq-sql-box { margin: 0; padding: 10px 12px; border-radius: 4px; overflow-x: auto;
  background: var(--vscode-editor-background, rgba(0, 0, 0, 0.2));
  border: 1px solid var(--fsq-border);
  font-family: var(--vscode-editor-font-family, monospace);
  font-size: var(--vscode-editor-font-size, 12px);
  line-height: 1.45;
  white-space: pre-wrap;
  word-break: break-word;
  user-select: text; }
.fsq-params-section { display: flex; flex-direction: column; gap: 6px; }
.fsq-params-title { font-weight: 600; font-size: 11px; text-transform: uppercase; letter-spacing: 0.5px; opacity: 0.8; }
.fsq-params-table { border-collapse: collapse; width: 100%; border: 1px solid var(--fsq-border); border-radius: 3px; overflow: hidden; }
.fsq-params-table th, .fsq-params-table td { padding: 4px 10px; text-align: left; border-bottom: 1px solid var(--fsq-border); }
.fsq-params-table th { font-weight: 600; background: var(--vscode-keybindingTable-headerBackground, rgba(128, 128, 128, 0.08)); }
.fsq-params-table tr:last-child td { border-bottom: 0; }
.fsq-param-name { font-family: var(--vscode-editor-font-family, monospace); font-weight: 600; }
.fsq-param-val { font-family: var(--vscode-editor-font-family, monospace); }

.fsq-loadmore { margin: 6px; padding: 2px 10px; cursor: pointer; font: inherit;
  background: var(--vscode-button-secondaryBackground, transparent);
  color: var(--vscode-button-secondaryForeground, var(--vscode-foreground));
  border: 1px solid var(--fsq-border); border-radius: 3px; }
.fsq-loadmore:hover { background: var(--vscode-button-secondaryHoverBackground, var(--vscode-toolbar-hoverBackground, rgba(128, 128, 128, 0.15))); }
.fsq-notice { margin: 6px; padding: 4px 8px; border-radius: 3px;
  background: var(--vscode-inputValidation-warningBackground, var(--vscode-editorWidget-background));
  border: 1px solid var(--vscode-inputValidation-warningBorder, var(--fsq-border)); }
`;

export const STYLE_ID = "fsq-grid-style";

function ensureStyles(targetDocument: Document = document): void {
  if (!targetDocument || typeof targetDocument.getElementById !== "function") {
    return;
  }
  if (!targetDocument.getElementById(STYLE_ID)) {
    const style = targetDocument.createElement("style");
    style.id = STYLE_ID;
    style.textContent = STYLE;
    const parent = targetDocument.head || targetDocument.body || targetDocument.documentElement;
    if (parent) {
      parent.appendChild(style);
    }
  }
}

interface Grid {
  id: string;
  abortController: AbortController;
  payload: TablePayload;
  rows: unknown[][];
  token: string | null;
  view: "data" | "schema" | "rawQuery";
  /** Per-column: does its first non-null value read as a number? Drives
   *  right-alignment + tabular figures on the header and every cell. */
  numericCols: boolean[];
  colWidths: number[];
  gridTemplate: string;
  totalWidth: number;
  els: {
    count: HTMLElement;
    head: HTMLElement;
    viewport: HTMLElement;
    sizer: HTMLElement;
    rowsHost: HTMLElement;
    schema: HTMLElement;
    rawQuery: HTMLElement;
    loadMore: HTMLButtonElement;
    notice: HTMLElement;
    dataBtn: HTMLButtonElement;
    schemaBtn: HTMLButtonElement;
    rawQueryBtn: HTMLButtonElement;
  };
}

function raf(fn: () => void): void {
  if (typeof requestAnimationFrame === "function") {
    requestAnimationFrame(fn);
  } else {
    fn();
  }
}

function countLabel(n: number, more = false): string {
  return `${n}${more ? "+" : ""} ${n === 1 ? "row" : "rows"}`;
}

function isNumeric(value: unknown): boolean {
  return typeof value === "number" || typeof value === "bigint";
}

/** A column is "numeric" if its first non-null value across the rows seen
 *  so far is a number. Refined as more rows stream in. */
function computeNumericCols(rows: unknown[][], colCount: number): boolean[] {
  const cols = new Array(colCount).fill(false);
  for (let c = 0; c < colCount; c++) {
    for (const row of rows) {
      const v = row?.[c];
      if (v !== null && v !== undefined) {
        cols[c] = isNumeric(v);
        break;
      }
    }
  }
  return cols;
}

export function activate(context: RendererContextLike): {
  renderOutputItem(item: OutputItemLike, element: HTMLElement): void;
  disposeOutputItem(id: string): void;
} {
  const grids = new Map<string, Grid>();

  context.onDidReceiveMessage?.((message) => {
    const grid = grids.get(message.outputId);
    if (!grid) {
      return;
    }
    if (message.type === "rows") {
      grid.rows = grid.rows.concat(message.rows);
      grid.token = message.nextToken;
      grid.numericCols = computeNumericCols(grid.rows, grid.payload.columns.length);
      grid.colWidths = computeColWidths(grid.payload.columns, grid.rows);
      const layout = computeGridColumnsCss(grid.colWidths);
      grid.gridTemplate = layout.template;
      grid.totalWidth = layout.totalWidth;
      grid.els.viewport.style.setProperty("--fsq-grid-cols", layout.template);
      grid.els.viewport.style.setProperty("--fsq-grid-width", `${layout.totalWidth}px`);
      grid.els.head.style.gridTemplateColumns = layout.template;
      grid.els.loadMore.disabled = false;
      grid.els.loadMore.hidden = !grid.token;
      grid.els.count.textContent = countLabel(grid.rows.length);
      renderWindow(grid);
    } else if (message.type === "error") {
      showNotice(grid, message.message);
      grid.els.loadMore.disabled = false;
      if (message.scope === "loadMore") {
        grid.els.loadMore.hidden = true;
      }
    } else if (message.type === "exportDone") {
      showNotice(grid, `Exported to ${message.path}`);
    }
  });

  function showNotice(grid: Grid, text: string): void {
    grid.els.notice.textContent = text;
    grid.els.notice.hidden = false;
  }

  function renderWindow(grid: Grid): void {
    const { viewport, sizer, rowsHost } = grid.els;
    const total = grid.rows.length;
    sizer.style.height = `${total * GRID_ROW_HEIGHT_PX}px`;
    const { start, end } = visibleRange(
      viewport.scrollTop,
      GRID_ROW_HEIGHT_PX,
      viewport.clientHeight || 400,
      total,
    );
    rowsHost.style.transform = `translateY(${start * GRID_ROW_HEIGHT_PX}px)`;
    rowsHost.textContent = "";

    if (total === 0) {
      const empty = document.createElement("div");
      empty.className = "fsq-empty";
      empty.textContent = "No rows";
      rowsHost.appendChild(empty);
      return;
    }

    for (let i = start; i < end; i++) {
      const row = document.createElement("div");
      row.className = i % 2 === 1 ? "fsq-row fsq-odd" : "fsq-row";
      row.style.gridTemplateColumns = grid.gridTemplate;
      const values = grid.rows[i] ?? [];
      const width = Math.max(grid.payload.columns.length, values.length, 1);
      for (let c = 0; c < width; c++) {
        const cell = document.createElement("div");
        cell.className = "fsq-cell";
        const v = values[c];
        if (v === null || v === undefined) {
          cell.classList.add("fsq-null");
          cell.textContent = "NULL";
        } else {
          if (grid.numericCols[c] ?? isNumeric(v)) {
            cell.classList.add("fsq-num");
          }
          cell.textContent = formatCell(v);
        }
        row.appendChild(cell);
      }
      rowsHost.appendChild(row);
    }
  }

  function setView(grid: Grid, view: "data" | "schema" | "rawQuery"): void {
    grid.view = view;
    grid.els.dataBtn.classList.toggle("active", view === "data");
    grid.els.schemaBtn.classList.toggle("active", view === "schema");
    grid.els.rawQueryBtn.classList.toggle("active", view === "rawQuery");
    grid.els.head.hidden = view !== "data";
    grid.els.head.style.display = view === "data" ? "" : "none";
    grid.els.viewport.hidden = view !== "data";
    grid.els.viewport.style.display = view === "data" ? "" : "none";
    grid.els.schema.hidden = view !== "schema";
    grid.els.schema.style.display = view === "schema" ? "" : "none";
    grid.els.rawQuery.hidden = view !== "rawQuery";
    grid.els.rawQuery.style.display = view === "rawQuery" ? "flex" : "none";
    grid.els.loadMore.hidden = view !== "data" || !grid.token;
  }

  function build(item: OutputItemLike, element: HTMLElement): Grid {
    const payload = item.json() as TablePayload;
    element.textContent = "";

    ensureStyles(element.ownerDocument || document);

    const root = document.createElement("div");
    root.className = "fsq-grid";
    element.appendChild(root);

    const panel = document.createElement("div");
    panel.className = "fsq-panel";
    root.appendChild(panel);

    const numericCols = computeNumericCols(payload.rows, payload.columns.length);
    const colWidths = computeColWidths(payload.columns, payload.rows);
    const { template: gridTemplate, totalWidth } = computeGridColumnsCss(colWidths);

    const toolbar = document.createElement("div");
    toolbar.className = "fsq-toolbar";
    const seg = document.createElement("div");
    seg.className = "fsq-seg";
    const dataBtn = document.createElement("button");
    dataBtn.dataset.view = "data";
    dataBtn.textContent = "Data";
    const schemaBtn = document.createElement("button");
    schemaBtn.dataset.view = "schema";
    schemaBtn.textContent = "Schema";
    const rawQueryBtn = document.createElement("button");
    rawQueryBtn.dataset.view = "rawQuery";
    rawQueryBtn.textContent = "Raw Query";
    seg.append(dataBtn, schemaBtn, rawQueryBtn);
    const count = document.createElement("span");
    count.className = "fsq-count";
    const exportBtn = document.createElement("button");
    exportBtn.className = "fsq-export";
    exportBtn.textContent = "Export CSV";
    toolbar.append(seg, count, exportBtn);
    panel.appendChild(toolbar);

    const head = document.createElement("div");
    head.className = "fsq-head";
    head.style.gridTemplateColumns = gridTemplate;
    payload.columns.forEach((col, c) => {
      const cell = document.createElement("div");
      cell.className = numericCols[c] ? "fsq-cell fsq-num" : "fsq-cell";
      cell.textContent = col.name;
      cell.title = `${col.name} · ${col.displayType}`;
      head.appendChild(cell);
    });

    const viewport = document.createElement("div");
    viewport.className = "fsq-viewport";
    viewport.style.setProperty("--fsq-grid-cols", gridTemplate);
    viewport.style.setProperty("--fsq-grid-width", `${totalWidth}px`);
    viewport.appendChild(head);

    const sizer = document.createElement("div");
    sizer.className = "fsq-sizer";
    const rowsHost = document.createElement("div");
    rowsHost.className = "fsq-rows";
    sizer.appendChild(rowsHost);
    viewport.appendChild(sizer);
    panel.appendChild(viewport);

    const schema = document.createElement("div");
    schema.className = "fsq-schema";
    schema.hidden = true;
    schema.style.display = "none";
    const schemaHead = document.createElement("div");
    schemaHead.className = "fsq-row";
    schemaHead.innerHTML = `<div class="fsq-cell">name</div><div class="fsq-cell">displayType</div>`;
    schema.appendChild(schemaHead);
    for (const col of payload.columns) {
      const r = document.createElement("div");
      r.className = "fsq-row";
      const n = document.createElement("div");
      n.className = "fsq-cell";
      n.textContent = col.name;
      const t = document.createElement("div");
      t.className = "fsq-cell";
      t.textContent = col.displayType;
      r.append(n, t);
      schema.appendChild(r);
    }
    panel.appendChild(schema);

    const abortController = new AbortController();
    const { signal } = abortController;

    const rawQuery = document.createElement("div");
    rawQuery.className = "fsq-raw-query";
    rawQuery.hidden = true;
    rawQuery.style.display = "none";

    if (payload.rawQuery) {
      const header = document.createElement("div");
      header.className = "fsq-raw-query-header";

      const title = document.createElement("span");
      title.className = "fsq-raw-query-title";
      title.textContent = "SQL Query";

      const actions = document.createElement("div");
      actions.className = "fsq-raw-query-actions";

      const copyBtn = document.createElement("button");
      copyBtn.className = "fsq-copy-query";
      copyBtn.textContent = "Copy";
      copyBtn.addEventListener(
        "click",
        () => {
          const textToCopy = payload.rawQuery || "";
          if (typeof navigator !== "undefined" && navigator.clipboard?.writeText) {
            navigator.clipboard.writeText(textToCopy).catch(() => {});
          }
          copyBtn.textContent = "Copied!";
          setTimeout(() => {
            copyBtn.textContent = "Copy";
          }, 1500);
        },
        { signal },
      );

      actions.appendChild(copyBtn);
      header.append(title, actions);
      rawQuery.appendChild(header);

      const sqlBox = document.createElement("pre");
      sqlBox.className = "fsq-sql-box";
      const sqlCode = document.createElement("code");
      sqlCode.textContent = payload.rawQuery;
      sqlBox.appendChild(sqlCode);
      rawQuery.appendChild(sqlBox);

      if (payload.parameters && payload.parameters.length > 0) {
        const paramsSec = document.createElement("div");
        paramsSec.className = "fsq-params-section";

        const paramsTitle = document.createElement("div");
        paramsTitle.className = "fsq-params-title";
        paramsTitle.textContent = `Parameters (${payload.parameters.length})`;
        paramsSec.appendChild(paramsTitle);

        const table = document.createElement("table");
        table.className = "fsq-params-table";
        table.innerHTML = `<thead><tr><th>Parameter</th><th>Value</th></tr></thead>`;
        const tbody = document.createElement("tbody");

        for (const p of payload.parameters) {
          const tr = document.createElement("tr");
          const tdName = document.createElement("td");
          tdName.className = "fsq-param-name";
          tdName.textContent = p.name;
          const tdVal = document.createElement("td");
          tdVal.className = "fsq-param-val";
          tdVal.textContent = p.value === null || p.value === undefined ? "NULL" : String(p.value);
          tr.append(tdName, tdVal);
          tbody.appendChild(tr);
        }
        table.appendChild(tbody);
        paramsSec.appendChild(table);
        rawQuery.appendChild(paramsSec);
      }
    } else {
      const empty = document.createElement("div");
      empty.className = "fsq-empty";
      empty.textContent = "No raw SQL query was executed for this result.";
      rawQuery.appendChild(empty);
    }
    panel.appendChild(rawQuery);

    const notice = document.createElement("div");
    notice.className = "fsq-notice";
    notice.hidden = true;
    panel.appendChild(notice);

    const loadMore = document.createElement("button");
    loadMore.className = "fsq-loadmore";
    loadMore.textContent = "Load more";
    loadMore.hidden = !payload.continuationToken;
    panel.appendChild(loadMore);

    // A materialized table can be truncated to the preview limit with no
    // continuation token — the full set is kept only for Export CSV and there
    // is nothing to stream. Without this note the count ("500 rows") would
    // silently under-report a 100k-row result.
    const materializedTruncated = payload.truncated && !payload.continuationToken;
    if (materializedTruncated) {
      notice.textContent =
        `Showing the first ${payload.rows.length} rows. Export CSV writes the full result.`;
      notice.hidden = false;
    }

    const grid: Grid = {
      id: item.id,
      abortController,
      payload,
      rows: payload.rows.slice(),
      token: payload.continuationToken,
      view: "data",
      numericCols,
      colWidths,
      gridTemplate,
      totalWidth,
      els: { count, head, viewport, sizer, rowsHost, schema, rawQuery, loadMore, notice, dataBtn, schemaBtn, rawQueryBtn },
    };

    count.textContent = countLabel(grid.rows.length, materializedTruncated);
    dataBtn.classList.add("active");
    dataBtn.addEventListener("click", () => setView(grid, "data"), { signal });
    schemaBtn.addEventListener("click", () => setView(grid, "schema"), { signal });
    rawQueryBtn.addEventListener("click", () => setView(grid, "rawQuery"), { signal });
    exportBtn.addEventListener("click", () => {
      context.postMessage?.({
        type: "exportCsv",
        outputId: grid.id,
        documentUri: grid.payload.documentUri,
      });
    }, { signal });
    loadMore.addEventListener("click", () => {
      if (!grid.token) {
        return;
      }
      loadMore.disabled = true;
      context.postMessage?.({
        type: "loadMore",
        outputId: grid.id,
        documentUri: grid.payload.documentUri,
        token: grid.token,
      });
    }, { signal });

    viewport.addEventListener("scroll", () => {
      raf(() => renderWindow(grid));
    }, { signal });

    renderWindow(grid);
    return grid;
  }

  return {
    renderOutputItem(item, element) {
      const existing = grids.get(item.id);
      if (existing) {
        existing.abortController.abort();
      }
      grids.set(item.id, build(item, element));
    },
    disposeOutputItem(id) {
      const grid = grids.get(id);
      if (grid) {
        grid.abortController.abort();
        grids.delete(id);
      }
    },
  };
}
