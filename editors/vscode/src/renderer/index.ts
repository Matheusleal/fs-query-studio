import { visibleRange, formatCell } from "./grid-core";
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
.fsq-sizer { position: relative; width: max-content; min-width: 100%; }
.fsq-rows { position: absolute; left: 0; right: 0; }
.fsq-row { display: flex; height: ${GRID_ROW_HEIGHT_PX}px; line-height: ${GRID_ROW_HEIGHT_PX}px; width: max-content; min-width: 100%; }
.fsq-rows .fsq-row.fsq-odd { background: var(--vscode-tree-tableOddRowsBackground, rgba(128, 128, 128, 0.04)); }
.fsq-rows .fsq-row:hover { background: var(--vscode-list-hoverBackground); color: var(--vscode-list-hoverForeground, inherit); }
.fsq-empty { padding: 8px 10px; opacity: 0.6; }

.fsq-head { display: flex; height: ${GRID_ROW_HEIGHT_PX}px; line-height: ${GRID_ROW_HEIGHT_PX}px;
  position: sticky; top: 0; z-index: 2; font-weight: 600;
  width: max-content; min-width: 100%;
  background: var(--vscode-keybindingTable-headerBackground, var(--vscode-editorWidget-background, var(--vscode-editor-background)));
  border-bottom: 1px solid var(--fsq-border);
  box-shadow: 0 1px 0 var(--fsq-border); }
.fsq-head .fsq-cell { border-right: 1px solid var(--vscode-tree-tableColumnsBorder, transparent); }
.fsq-head .fsq-cell:last-child { border-right: 0; }

.fsq-cell { flex: 1 1 0; min-width: 96px; padding: 0 10px; overflow: hidden;
  text-overflow: ellipsis; white-space: nowrap; user-select: text; }
.fsq-cell.fsq-num { text-align: right; font-variant-numeric: tabular-nums; font-feature-settings: "tnum"; }
.fsq-cell.fsq-null { opacity: 0.45; font-style: italic; }

.fsq-schema .fsq-row { padding: 0 10px; }
.fsq-schema .fsq-row:first-child { font-weight: 600; border-bottom: 1px solid var(--fsq-border); }

.fsq-loadmore { margin: 6px; padding: 2px 10px; cursor: pointer; font: inherit;
  background: var(--vscode-button-secondaryBackground, transparent);
  color: var(--vscode-button-secondaryForeground, var(--vscode-foreground));
  border: 1px solid var(--fsq-border); border-radius: 3px; }
.fsq-loadmore:hover { background: var(--vscode-button-secondaryHoverBackground, var(--vscode-toolbar-hoverBackground, rgba(128, 128, 128, 0.15))); }
.fsq-notice { margin: 6px; padding: 4px 8px; border-radius: 3px;
  background: var(--vscode-inputValidation-warningBackground, var(--vscode-editorWidget-background));
  border: 1px solid var(--vscode-inputValidation-warningBorder, var(--fsq-border)); }
`;

interface Grid {
  id: string;
  payload: TablePayload;
  rows: unknown[][];
  token: string | null;
  view: "data" | "schema";
  /** Per-column: does its first non-null value read as a number? Drives
   *  right-alignment + tabular figures on the header and every cell. */
  numericCols: boolean[];
  els: {
    count: HTMLElement;
    head: HTMLElement;
    viewport: HTMLElement;
    sizer: HTMLElement;
    rowsHost: HTMLElement;
    schema: HTMLElement;
    loadMore: HTMLButtonElement;
    notice: HTMLElement;
    dataBtn: HTMLButtonElement;
    schemaBtn: HTMLButtonElement;
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
      const v = row[c];
      if (v === null || v === undefined) {
        continue;
      }
      cols[c] = isNumeric(v);
      break;
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

  function setView(grid: Grid, view: "data" | "schema"): void {
    grid.view = view;
    grid.els.dataBtn.classList.toggle("active", view === "data");
    grid.els.schemaBtn.classList.toggle("active", view === "schema");
    grid.els.head.hidden = view !== "data";
    grid.els.viewport.hidden = view !== "data";
    grid.els.schema.hidden = view !== "schema";
  }

  function build(item: OutputItemLike, element: HTMLElement): Grid {
    const payload = item.json() as TablePayload;
    element.textContent = "";

    const style = document.createElement("style");
    style.textContent = STYLE;
    element.appendChild(style);

    const root = document.createElement("div");
    root.className = "fsq-grid";
    element.appendChild(root);

    const panel = document.createElement("div");
    panel.className = "fsq-panel";
    root.appendChild(panel);

    const numericCols = computeNumericCols(payload.rows, payload.columns.length);

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
    seg.append(dataBtn, schemaBtn);
    const count = document.createElement("span");
    count.className = "fsq-count";
    const exportBtn = document.createElement("button");
    exportBtn.className = "fsq-export";
    exportBtn.textContent = "Export CSV";
    toolbar.append(seg, count, exportBtn);
    panel.appendChild(toolbar);

    const head = document.createElement("div");
    head.className = "fsq-head";
    payload.columns.forEach((col, c) => {
      const cell = document.createElement("div");
      cell.className = numericCols[c] ? "fsq-cell fsq-num" : "fsq-cell";
      cell.textContent = col.name;
      cell.title = `${col.name} · ${col.displayType}`;
      head.appendChild(cell);
    });

    const viewport = document.createElement("div");
    viewport.className = "fsq-viewport";
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
      payload,
      rows: payload.rows.slice(),
      token: payload.continuationToken,
      view: "data",
      numericCols,
      els: { count, head, viewport, sizer, rowsHost, schema, loadMore, notice, dataBtn, schemaBtn },
    };

    count.textContent = countLabel(grid.rows.length, materializedTruncated);
    dataBtn.classList.add("active");
    dataBtn.addEventListener("click", () => setView(grid, "data"));
    schemaBtn.addEventListener("click", () => setView(grid, "schema"));
    exportBtn.addEventListener("click", () => {
      context.postMessage?.({
        type: "exportCsv",
        outputId: grid.id,
        documentUri: grid.payload.documentUri,
      });
    });
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
    });
    viewport.addEventListener("scroll", () => raf(() => renderWindow(grid)));

    renderWindow(grid);
    return grid;
  }

  return {
    renderOutputItem(item, element) {
      const grid = build(item, element);
      grids.set(item.id, grid);
    },
    disposeOutputItem(id) {
      grids.delete(id);
    },
  };
}
