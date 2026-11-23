// @vitest-environment jsdom
import { describe, it, expect, beforeEach, vi } from "vitest";
import { activate, STYLE_ID, type RendererContextLike, type OutputItemLike } from "./index";
import type { HostToRendererMessage, RendererToHostMessage, TablePayload } from "./types";

function createPayload(overrides: Partial<TablePayload> = {}): TablePayload {
  return {
    documentUri: "file:///test-notebook.fsqnb",
    columns: [
      { name: "id", displayType: "int" },
      { name: "title", displayType: "string" },
    ],
    rows: [
      [1, "First row"],
      [2, "Second row"],
    ],
    truncated: false,
    continuationToken: null,
    elapsedMs: 5,
    ...overrides,
  };
}

function createMockContext() {
  const sent: RendererToHostMessage[] = [];
  let route: ((m: HostToRendererMessage) => void) | undefined;
  const context: RendererContextLike = {
    postMessage: (m) => sent.push(m),
    onDidReceiveMessage: (fn) => {
      route = fn;
      return { dispose: () => (route = undefined) };
    },
  };
  return { context, sent, route: (m: HostToRendererMessage) => route?.(m) };
}

const createItem = (id: string, payload: TablePayload): OutputItemLike => ({
  id,
  json: () => payload,
});

describe("Renderer Optimization: Single Style Injection & AbortController (TASK-010)", () => {
  let container1: HTMLElement;
  let container2: HTMLElement;

  beforeEach(() => {
    // Clean up any existing style tag from previous test runs
    const existingStyle = document.getElementById(STYLE_ID);
    if (existingStyle) {
      existingStyle.remove();
    }
    container1 = document.createElement("div");
    container2 = document.createElement("div");
    document.body.appendChild(container1);
    document.body.appendChild(container2);
  });

  // --- Scenario 1: Single Stylesheet Injection ---
  describe("Scenario 1: Single Stylesheet Injection", () => {
    it("1.1: injects the CSS stylesheet with ID fsq-grid-style exactly once on first render", () => {
      const { context } = createMockContext();
      const host = activate(context);

      expect(document.getElementById(STYLE_ID)).toBeNull();

      host.renderOutputItem(createItem("out-1", createPayload()), container1);

      const style = document.getElementById(STYLE_ID);
      expect(style).not.toBeNull();
      expect(style?.tagName.toLowerCase()).toBe("style");
      expect(style?.textContent).toContain(".fsq-grid");
      expect(style?.textContent).toContain(".fsq-panel");

      // Verify style is NOT duplicated inside container1 itself
      expect(container1.querySelector(`style`)).toBeNull();
    });

    it("1.2: reuses existing stylesheet without injecting duplicate <style> tags on subsequent renders", () => {
      const { context } = createMockContext();
      const host = activate(context);

      host.renderOutputItem(createItem("out-1", createPayload()), container1);
      host.renderOutputItem(createItem("out-2", createPayload()), container2);

      const matchingStyles = document.querySelectorAll(`#${STYLE_ID}`);
      expect(matchingStyles).toHaveLength(1);

      // Total style elements in head/body should be exactly 1
      const allStyles = document.querySelectorAll("style");
      const fsqStyles = Array.from(allStyles).filter((s) => s.id === STYLE_ID);
      expect(fsqStyles).toHaveLength(1);
    });
  });

  // --- Scenario 2: AbortController Association & Listener Cleanup ---
  describe("Scenario 2: AbortController Association and Listener Cleanup", () => {
    it("2.1: triggers event listeners (Export CSV, Load More) while output item is active", () => {
      const mock = createMockContext();
      const host = activate(mock.context);

      host.renderOutputItem(
        createItem("out-active", createPayload({ truncated: true, continuationToken: "tok-1" })),
        container1,
      );

      const exportBtn = container1.querySelector(".fsq-export") as HTMLButtonElement;
      const loadMoreBtn = container1.querySelector(".fsq-loadmore") as HTMLButtonElement;

      exportBtn.click();
      expect(mock.sent).toHaveLength(1);
      expect(mock.sent[0]).toEqual({
        type: "exportCsv",
        outputId: "out-active",
        documentUri: "file:///test-notebook.fsqnb",
      });

      loadMoreBtn.click();
      expect(mock.sent).toHaveLength(2);
      expect(mock.sent[1]).toEqual({
        type: "loadMore",
        outputId: "out-active",
        documentUri: "file:///test-notebook.fsqnb",
        token: "tok-1",
      });
    });

    it("2.2: calling disposeOutputItem aborts listener signals and prevents orphaned handlers from executing", () => {
      const mock = createMockContext();
      const host = activate(mock.context);

      host.renderOutputItem(
        createItem("out-dispose", createPayload({ truncated: true, continuationToken: "tok-2" })),
        container1,
      );

      const exportBtn = container1.querySelector(".fsq-export") as HTMLButtonElement;
      const dataBtn = container1.querySelector('button[data-view="data"]') as HTMLButtonElement;
      const schemaBtn = container1.querySelector('button[data-view="schema"]') as HTMLButtonElement;
      const rawQueryBtn = container1.querySelector('button[data-view="rawQuery"]') as HTMLButtonElement;
      const viewport = container1.querySelector(".fsq-viewport") as HTMLElement;

      // Verify active works
      exportBtn.click();
      expect(mock.sent).toHaveLength(1);

      schemaBtn.click();
      expect(container1.querySelector(".fsq-schema")?.getAttribute("hidden")).toBeNull();

      rawQueryBtn.click();
      expect(container1.querySelector(".fsq-raw-query")?.getAttribute("hidden")).toBeNull();

      // Dispose the output item
      host.disposeOutputItem("out-dispose");

      // Now click buttons again: signal was aborted, listener must not fire
      exportBtn.click();
      expect(mock.sent).toHaveLength(1); // Still 1, didn't increase

      dataBtn.click();
      // Should remain on raw query view because click listener was aborted
      expect(container1.querySelector(".fsq-raw-query")?.getAttribute("hidden")).toBeNull();

      // Viewport scroll should not throw or trigger
      viewport.dispatchEvent(new Event("scroll"));
    });

    it("2.3: re-rendering the same outputId aborts previous instance before replacing it", () => {
      const mock = createMockContext();
      const host = activate(mock.context);

      host.renderOutputItem(createItem("out-dup", createPayload()), container1);
      const oldExportBtn = container1.querySelector(".fsq-export") as HTMLButtonElement;

      // Re-render into container2 with the same ID
      host.renderOutputItem(createItem("out-dup", createPayload()), container2);

      // Old button from previous render was aborted
      oldExportBtn.click();
      expect(mock.sent).toHaveLength(0);

      // New button works
      const newExportBtn = container2.querySelector(".fsq-export") as HTMLButtonElement;
      newExportBtn.click();
      expect(mock.sent).toHaveLength(1);
    });
  });

  // --- Scenario 3: Edge Cases ---
  describe("Scenario 3: Edge Cases", () => {
    it("3.1: disposeOutputItem safely handles non-existent or already-disposed ID", () => {
      const { context } = createMockContext();
      const host = activate(context);

      expect(() => {
        host.disposeOutputItem("unknown-id");
        host.disposeOutputItem("");
      }).not.toThrow();
    });

    it("3.2: injects into document.body or documentElement when document.head is missing", () => {
      const { context } = createMockContext();
      const host = activate(context);

      // Create a mock document without head
      const doc = document.implementation.createHTMLDocument("test");
      if (doc.head) {
        doc.head.remove();
      }
      const el = doc.createElement("div");
      doc.body.appendChild(el);

      host.renderOutputItem(createItem("out-nohead", createPayload()), el);

      const injected = doc.getElementById(STYLE_ID);
      expect(injected).not.toBeNull();
      expect(injected?.parentElement).toBe(doc.body);
    });
  });
});
