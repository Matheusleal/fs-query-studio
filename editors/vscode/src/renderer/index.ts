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
.fsq-grid { font: 12px var(--vscode-editor-font-family, monospace); color: var(--vscode-foreground); }
.fsq-toolbar { display: flex; gap: 6px; align-items: center; padding: 4px 0; }
.fsq-toolbar button { background: var(--vscode-button-secondaryBackground, transparent);
  color: var(--vscode-button-secondaryForeground, var(--vscode-foreground));
  border: 1px solid var(--vscode-panel-border); border-radius: 2px; padding: 2px 8px; cursor: pointer; }
.fsq-toolbar button.active { background: var(--vscode-button-background); color: var(--vscode-button-foreground); }
.fsq-count { margin-left: auto; opacity: 0.8; }
.fsq-viewport { overflow: auto; max-height: 360px; border: 1px solid var(--vscode-panel-border); }
.fsq-sizer { position: relative; }
.fsq-rows { position: absolute; left: 0; right: 0; }
.fsq-row { display: flex; height: ${GRID_ROW_HEIGHT_PX}px; line-height: ${GRID_ROW_HEIGHT_PX}px; }
.fsq-row:hover { background: var(--vscode-list-hoverBackground); }
.fsq-head { display: flex; height: ${GRID_ROW_HEIGHT_PX}px; line-height: ${GRID_ROW_HEIGHT_PX}px;
  position: sticky; top: 0; background: var(--vscode-editor-background);
  border-bottom: 1px solid var(--vscode-panel-border); font-weight: 600; }
.fsq-cell { flex: 1 1 0; min-width: 80px; padding: 0 8px; overflow: hidden;
  text-overflow: ellipsis; white-space: nowrap; }
.fsq-schema { border: 1px solid var(--vscode-panel-border); }
.fsq-schema .fsq-row { padding: 0 8px; }
.fsq-loadmore { margin: 6px 0; padding: 2px 10px; cursor: pointer;
  background: var(--vscode-button-background); color: var(--vscode-button-foreground);
  border: none; border-radius: 2px; }
.fsq-notice { margin: 6px 0; padding: 4px 8px; border-radius: 2px;
  background: var(--vscode-inputValidation-warningBackground, var(--vscode-editorWidget-background));
  border: 1px solid var(--vscode-inputValidation-warningBorder, var(--vscode-panel-border)); }
`;

interface Grid {
  id: string;
  payload: TablePayload;
  rows: unknown[][];
  token: string | null;
  view: "data" | "schema";
  els: {
    count: HTMLElement;
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

function countLabel(n: number): string {
  return `${n} ${n === 1 ? "row" : "rows"}`;
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
    for (let i = start; i < end; i++) {
      const row = document.createElement("div");
      row.className = "fsq-row";
      const values = grid.rows[i] ?? [];
      const width = Math.max(grid.payload.columns.length, values.length, 1);
      for (let c = 0; c < width; c++) {
        const cell = document.createElement("div");
        cell.className = "fsq-cell";
        cell.textContent = formatCell(values[c]);
        row.appendChild(cell);
      }
      rowsHost.appendChild(row);
    }
  }

  function setView(grid: Grid, view: "data" | "schema"): void {
    grid.view = view;
    grid.els.dataBtn.classList.toggle("active", view === "data");
    grid.els.schemaBtn.classList.toggle("active", view === "schema");
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

    const toolbar = document.createElement("div");
    toolbar.className = "fsq-toolbar";
    const dataBtn = document.createElement("button");
    dataBtn.dataset.view = "data";
    dataBtn.textContent = "Data";
    const schemaBtn = document.createElement("button");
    schemaBtn.dataset.view = "schema";
    schemaBtn.textContent = "Schema";
    const count = document.createElement("span");
    count.className = "fsq-count";
    const exportBtn = document.createElement("button");
    exportBtn.className = "fsq-export";
    exportBtn.textContent = "Export CSV";
    toolbar.append(dataBtn, schemaBtn, count, exportBtn);
    root.appendChild(toolbar);

    const head = document.createElement("div");
    head.className = "fsq-head";
    for (const col of payload.columns) {
      const cell = document.createElement("div");
      cell.className = "fsq-cell";
      cell.textContent = col.name;
      head.appendChild(cell);
    }
    root.appendChild(head);

    const viewport = document.createElement("div");
    viewport.className = "fsq-viewport";
    const sizer = document.createElement("div");
    sizer.className = "fsq-sizer";
    const rowsHost = document.createElement("div");
    rowsHost.className = "fsq-rows";
    sizer.appendChild(rowsHost);
    viewport.appendChild(sizer);
    root.appendChild(viewport);

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
    root.appendChild(schema);

    const notice = document.createElement("div");
    notice.className = "fsq-notice";
    notice.hidden = true;
    root.appendChild(notice);

    const loadMore = document.createElement("button");
    loadMore.className = "fsq-loadmore";
    loadMore.textContent = "Load more";
    loadMore.hidden = !payload.continuationToken;
    root.appendChild(loadMore);

    const grid: Grid = {
      id: item.id,
      payload,
      rows: payload.rows.slice(),
      token: payload.continuationToken,
      view: "data",
      els: { count, viewport, sizer, rowsHost, schema, loadMore, notice, dataBtn, schemaBtn },
    };

    count.textContent = countLabel(grid.rows.length);
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
