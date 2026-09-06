import { newRequestId, type WorkerResponseFrame } from "../protocol";
import {
  isRendererToHostMessage,
  type HostToRendererMessage,
  type RendererToHostMessage,
} from "../renderer/types";

/** The one `WorkerClient` method this module needs — injectable for tests. */
export interface WorkerSendLike {
  send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame>;
}

/**
 * The slice of `vscode.NotebookRendererMessaging` used here. `postMessage`
 * takes the `editor` from the incoming event so a reply lands on the
 * webview that asked.
 */
export interface RendererMessagingLike {
  onDidReceiveMessage(
    listener: (e: { editor: unknown; message: unknown }) => unknown,
  ): { dispose(): void };
  postMessage(
    message: HostToRendererMessage,
    editor?: unknown,
  ): Promise<boolean> | Thenable<boolean>;
}

export interface RendererHostDeps {
  messaging: RendererMessagingLike;
  /** Resolves the shared worker; may reject with a `userMessage`-carrying error. */
  getWorker: () => Promise<WorkerSendLike>;
  /** Opens a save dialog; resolves to the chosen path, or `undefined` if cancelled. */
  pickSaveFile: (defaultName: string, documentUri: string) => Promise<string | undefined>;
  log: (line: string) => void;
}

const LOAD_MORE_STALE =
  "This result is no longer available for paging — re-run the cell to load more rows.";
const EXPORT_UNAVAILABLE =
  "CSV export is only available for tables produced by Results.table.";

function errorMessage(err: unknown): string {
  return (err as { userMessage?: string }).userMessage ?? (err as Error).message;
}

export function registerRendererHost(deps: RendererHostDeps): { dispose(): void } {
  const subscription = deps.messaging.onDidReceiveMessage(async ({ editor, message }) => {
    if (!isRendererToHostMessage(message)) {
      return;
    }
    const msg: RendererToHostMessage = message;
    try {
      if (msg.type === "loadMore") {
        await handleLoadMore(deps, msg, editor);
      } else {
        await handleExportCsv(deps, msg, editor);
      }
    } catch (err) {
      deps.log(`renderer-host: ${msg.type} failed: ${errorMessage(err)}`);
      void deps.messaging.postMessage(
        {
          type: "error",
          outputId: msg.outputId,
          scope: msg.type === "loadMore" ? "loadMore" : "exportCsv",
          message: errorMessage(err),
        },
        editor,
      );
    }
  });

  return { dispose: () => subscription.dispose() };
}

async function handleLoadMore(
  deps: RendererHostDeps,
  msg: Extract<RendererToHostMessage, { type: "loadMore" }>,
  editor: unknown,
): Promise<void> {
  const worker = await deps.getWorker();
  const response = await worker.send({
    id: newRequestId(),
    method: "results/page",
    params: { sessionId: msg.documentUri, continuationToken: msg.token },
  });

  if (response.error) {
    const text =
      response.error.code === "results/unknownToken" ? LOAD_MORE_STALE : response.error.message;
    void deps.messaging.postMessage(
      { type: "error", outputId: msg.outputId, scope: "loadMore", message: text },
      editor,
    );
    return;
  }

  const page = (response.result ?? {}) as {
    rows?: unknown[][];
    isTruncated?: boolean;
    continuationToken?: string | null;
  };
  void deps.messaging.postMessage(
    {
      type: "rows",
      outputId: msg.outputId,
      rows: page.rows ?? [],
      truncated: page.isTruncated === true,
      nextToken: page.continuationToken ?? null,
    },
    editor,
  );
}

async function handleExportCsv(
  deps: RendererHostDeps,
  msg: Extract<RendererToHostMessage, { type: "exportCsv" }>,
  editor: unknown,
): Promise<void> {
  const target = await deps.pickSaveFile("results.csv", msg.documentUri);
  if (!target) {
    return; // cancelled — nothing to report
  }

  const worker = await deps.getWorker();
  const response = await worker.send({
    id: newRequestId(),
    method: "results/exportCsv",
    params: { documentUri: msg.documentUri, filePath: target },
  });

  if (response.error) {
    const text =
      response.error.code === "results/nothingToExport"
        ? EXPORT_UNAVAILABLE
        : response.error.message;
    void deps.messaging.postMessage(
      { type: "error", outputId: msg.outputId, scope: "exportCsv", message: text },
      editor,
    );
    return;
  }

  void deps.messaging.postMessage(
    { type: "exportDone", outputId: msg.outputId, path: target },
    editor,
  );
}
