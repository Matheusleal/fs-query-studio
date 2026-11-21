import { describe, it, expect, vi, beforeEach } from "vitest";
import * as vscodeStub from "../../test/vscode-stub";
import {
  getDemoOptions,
  getLanguageOptions,
  registerSampleDataCommands,
  executeCreateSampleData,
  type SampleDataDeps,
} from "./sample-data";

describe("sample-data commands", () => {
  beforeEach(() => {
    vscodeStub.commands._handlers.clear();
  });

  describe("getDemoOptions", () => {
    it("returns options with English text when isPt is false", () => {
      const options = getDemoOptions(false);
      expect(options.length).toBe(6);
      expect(options[0].id).toBe("standard");
      expect(options[0].description).toContain("Complete demo in English");
      expect(options[1].id).toBe("postgresql");
      expect(options[1].description).toContain("Docker container");
    });

    it("returns options with Portuguese text when isPt is true", () => {
      const options = getDemoOptions(true);
      expect(options.length).toBe(6);
      expect(options[0].description).toContain("Demonstração completa em inglês");
      expect(options[1].description).toContain("Container Docker");
    });
  });

  describe("getLanguageOptions", () => {
    it("returns options with English descriptions when isPt is false", () => {
      const options = getLanguageOptions(false);
      expect(options.length).toBe(3);
      expect(options[0].lang).toBe("en");
      expect(options[0].description).toContain("Generate demo notebooks in English");
      expect(options[1].lang).toBe("pt");
      expect(options[2].lang).toBe("all");
    });

    it("returns options with Portuguese descriptions when isPt is true", () => {
      const options = getLanguageOptions(true);
      expect(options.length).toBe(3);
      expect(options[0].description).toContain("Gerar cadernos de demonstração em inglês");
      expect(options[1].description).toContain("Gerar cadernos de demonstração em português");
      expect(options[2].description).toContain("Gerar demonstrações em inglês e português");
    });
  });

  describe("registerSampleDataCommands", () => {
    it("registers both createSampleData and createAdvancedSamples commands", () => {
      const fakeContext = { subscriptions: [] } as never;
      const deps: SampleDataDeps = {
        getWorker: async () => ({ send: async () => ({ id: "1", result: {} }) }),
        log: () => {},
      };

      const disposables = registerSampleDataCommands(fakeContext, deps);
      expect(disposables.length).toBe(2);
      expect(vscodeStub.commands._handlers.has("fs-query-studio.createSampleData")).toBe(true);
      expect(vscodeStub.commands._handlers.has("fs-query-studio.createAdvancedSamples")).toBe(true);

      for (const d of disposables) {
        d.dispose();
      }
    });

    it("createAdvancedSamples executes createSampleData command", async () => {
      const fakeContext = { subscriptions: [] } as never;
      let executed = false;
      const deps: SampleDataDeps = {
        getWorker: async () => ({ send: async () => ({ id: "1", result: {} }) }),
        log: () => {},
      };

      const disposables = registerSampleDataCommands(fakeContext, deps);

      // Override createSampleData handler to verify delegation
      vscodeStub.commands._handlers.set("fs-query-studio.createSampleData", async () => {
        executed = true;
      });

      await vscodeStub.commands.executeCommand("fs-query-studio.createAdvancedSamples");
      expect(executed).toBe(true);

      for (const d of disposables) {
        d.dispose();
      }
    });
  });

  describe("executeCreateSampleData", () => {
    it("exits early when demo selection is cancelled", async () => {
      const sentRequests: unknown[] = [];
      const deps: SampleDataDeps = {
        getWorker: async () => ({
          send: async (req) => {
            sentRequests.push(req);
            return { id: req.id, result: {} };
          },
        }),
        log: () => {},
      };

      const origWorkspaceFolders = (vscodeStub.workspace as any).workspaceFolders;
      (vscodeStub.workspace as any).workspaceFolders = [
        { name: "ws1", uri: vscodeStub.Uri.file("/test/ws1") },
      ];

      const origShowQuickPick = vscodeStub.window.showQuickPick;
      (vscodeStub.window as any).showQuickPick = async () => undefined;

      try {
        await executeCreateSampleData(deps);
        expect(sentRequests.length).toBe(0);
      } finally {
        (vscodeStub.workspace as any).workspaceFolders = origWorkspaceFolders;
        (vscodeStub.window as any).showQuickPick = origShowQuickPick;
      }
    });

    it("handles multiple workspace folders and picks one via showQuickPick", async () => {
      const origWorkspaceFolders = (vscodeStub.workspace as any).workspaceFolders;
      (vscodeStub.workspace as any).workspaceFolders = [
        { name: "ws1", uri: vscodeStub.Uri.file("/test/ws1") },
        { name: "ws2", uri: vscodeStub.Uri.file("/test/ws2") },
      ];

      const origShowQuickPick = vscodeStub.window.showQuickPick;
      let pickedFolder: string | undefined;

      (vscodeStub.window as any).showQuickPick = async (items: any[], opts: any) => {
        if (opts?.placeHolder?.includes("Select a workspace folder")) {
          return items[1]; // ws2
        }
        if (opts?.title?.includes("Passo 2") || opts?.title?.includes("Step 2")) {
          return { lang: "en" };
        }
        return [items[0]]; // standard
      };

      const deps: SampleDataDeps = {
        getWorker: async () => ({
          send: async (req) => {
            pickedFolder = (req.params as any)?.targetDir;
            return {
              id: req.id,
              result: {
                sqlitePath: "/test/ws2/sample.db",
                csvPath: "/test/ws2/sample.csv",
                notebookPath: "/test/ws2/sample_demo.fsqnb",
              },
            };
          },
        }),
        log: () => {},
      };

      try {
        await executeCreateSampleData(deps);
        expect(pickedFolder).toBe("/test/ws2");
      } finally {
        (vscodeStub.workspace as any).workspaceFolders = origWorkspaceFolders;
        (vscodeStub.window as any).showQuickPick = origShowQuickPick;
      }
    });

    it("exits early when workspace folder pick is cancelled in multi-root workspace", async () => {
      const origWorkspaceFolders = (vscodeStub.workspace as any).workspaceFolders;
      (vscodeStub.workspace as any).workspaceFolders = [
        { name: "ws1", uri: vscodeStub.Uri.file("/test/ws1") },
        { name: "ws2", uri: vscodeStub.Uri.file("/test/ws2") },
      ];

      const origShowQuickPick = vscodeStub.window.showQuickPick;
      (vscodeStub.window as any).showQuickPick = async () => undefined;

      let workerCalled = false;
      const deps: SampleDataDeps = {
        getWorker: async () => ({
          send: async (req) => {
            workerCalled = true;
            return { id: req.id, result: {} };
          },
        }),
        log: () => {},
      };

      try {
        await executeCreateSampleData(deps);
        expect(workerCalled).toBe(false);
      } finally {
        (vscodeStub.workspace as any).workspaceFolders = origWorkspaceFolders;
        (vscodeStub.window as any).showQuickPick = origShowQuickPick;
      }
    });

    it("exits early when showOpenDialog is cancelled when no workspace folder is open", async () => {
      const origWorkspaceFolders = (vscodeStub.workspace as any).workspaceFolders;
      (vscodeStub.workspace as any).workspaceFolders = undefined;

      const origShowOpenDialog = (vscodeStub.window as any).showOpenDialog;
      (vscodeStub.window as any).showOpenDialog = async () => undefined;

      let workerCalled = false;
      const deps: SampleDataDeps = {
        getWorker: async () => ({
          send: async (req) => {
            workerCalled = true;
            return { id: req.id, result: {} };
          },
        }),
        log: () => {},
      };

      try {
        await executeCreateSampleData(deps);
        expect(workerCalled).toBe(false);
      } finally {
        (vscodeStub.workspace as any).workspaceFolders = origWorkspaceFolders;
        (vscodeStub.window as any).showOpenDialog = origShowOpenDialog;
      }
    });

    it("creates standard sample data in a single workspaceFolder successfully", async () => {
      const sentRequests: Array<{ method: string; params: any }> = [];
      let watcherStarted = false;
      let syncCalledWith: string | undefined;
      const openedNotebooks: string[] = [];

      const origWorkspaceFolders = (vscodeStub.workspace as any).workspaceFolders;
      (vscodeStub.workspace as any).workspaceFolders = [
        { name: "ws1", uri: vscodeStub.Uri.file("/test/ws1") },
      ];

      const origShowQuickPick = vscodeStub.window.showQuickPick;
      (vscodeStub.window as any).showQuickPick = async (items: any[], opts: any) => {
        if (opts?.title?.includes("Passo 2") || opts?.title?.includes("Step 2")) {
          return { lang: "en" };
        }
        return [items[0]]; // standard
      };

      const origOpenNotebook = (vscodeStub.workspace as any).openNotebookDocument;
      (vscodeStub.workspace as any).openNotebookDocument = async (uri: any) => {
        openedNotebooks.push(uri.fsPath);
        return { uri };
      };

      const origShowNotebook = (vscodeStub.window as any).showNotebookDocument;
      (vscodeStub.window as any).showNotebookDocument = async () => {};

      const deps: SampleDataDeps = {
        getWorker: async () => ({
          send: async (req) => {
            sentRequests.push(req as any);
            return {
              id: req.id,
              result: {
                sqlitePath: "/test/ws1/sample.db",
                csvPath: "/test/ws1/sample.csv",
                notebookPath: "/test/ws1/sample_demo.fsqnb",
              },
            };
          },
        }),
        log: () => {},
        startDataSourcesWatcher: () => {
          watcherStarted = true;
        },
        runSyncDataSources: async (targetDir) => {
          syncCalledWith = targetDir;
        },
      };

      try {
        await executeCreateSampleData(deps);
        expect(sentRequests.some((r) => r.method === "workspace/createSampleData")).toBe(true);
        expect(watcherStarted).toBe(true);
        expect(syncCalledWith).toBe("/test/ws1");
        expect(openedNotebooks).toContain("/test/ws1/sample_demo.fsqnb");
      } finally {
        (vscodeStub.workspace as any).workspaceFolders = origWorkspaceFolders;
        (vscodeStub.window as any).showQuickPick = origShowQuickPick;
        (vscodeStub.workspace as any).openNotebookDocument = origOpenNotebook;
        (vscodeStub.window as any).showNotebookDocument = origShowNotebook;
      }
    });

    it("prefers Portuguese notebook when pt language is selected", async () => {
      const openedNotebooks: string[] = [];

      const origWorkspaceFolders = (vscodeStub.workspace as any).workspaceFolders;
      (vscodeStub.workspace as any).workspaceFolders = [
        { name: "ws1", uri: vscodeStub.Uri.file("/test/ws1") },
      ];

      const origShowQuickPick = vscodeStub.window.showQuickPick;
      (vscodeStub.window as any).showQuickPick = async (items: any[], opts: any) => {
        if (opts?.title?.includes("Passo 2") || opts?.title?.includes("Step 2")) {
          return { lang: "pt" };
        }
        return [items[0]]; // standard
      };

      const origOpenNotebook = (vscodeStub.workspace as any).openNotebookDocument;
      (vscodeStub.workspace as any).openNotebookDocument = async (uri: any) => {
        openedNotebooks.push(uri.fsPath);
        return { uri };
      };

      const origShowNotebook = (vscodeStub.window as any).showNotebookDocument;
      (vscodeStub.window as any).showNotebookDocument = async () => {};

      const deps: SampleDataDeps = {
        getWorker: async () => ({
          send: async (req) => ({
            id: req.id,
            result: {
              sqlitePath: "/test/ws1/sample.db",
              csvPath: "/test/ws1/sample.csv",
              notebookPath: "/test/ws1/sample_demo.fsqnb",
              notebookPtPath: "/test/ws1/sample_demo_pt.fsqnb",
            },
          }),
        }),
        log: () => {},
      };

      try {
        await executeCreateSampleData(deps);
        expect(openedNotebooks).toContain("/test/ws1/sample_demo_pt.fsqnb");
      } finally {
        (vscodeStub.workspace as any).workspaceFolders = origWorkspaceFolders;
        (vscodeStub.window as any).showQuickPick = origShowQuickPick;
        (vscodeStub.workspace as any).openNotebookDocument = origOpenNotebook;
        (vscodeStub.window as any).showNotebookDocument = origShowNotebook;
      }
    });

    it("creates advanced database samples when selected with docker notice", async () => {
      const sentRequests: Array<{ method: string; params: any }> = [];
      const messages: string[] = [];

      const origWorkspaceFolders = (vscodeStub.workspace as any).workspaceFolders;
      (vscodeStub.workspace as any).workspaceFolders = [
        { name: "ws1", uri: vscodeStub.Uri.file("/test/ws1") },
      ];

      const origShowQuickPick = vscodeStub.window.showQuickPick;
      (vscodeStub.window as any).showQuickPick = async (items: any[], opts: any) => {
        if (opts?.title?.includes("Passo 2") || opts?.title?.includes("Step 2")) {
          return { lang: "all" };
        }
        return [items[1]]; // postgresql
      };

      const origShowInfo = vscodeStub.window.showInformationMessage;
      (vscodeStub.window as any).showInformationMessage = async (msg: string) => {
        messages.push(msg);
      };

      const deps: SampleDataDeps = {
        getWorker: async () => ({
          send: async (req) => {
            sentRequests.push(req as any);
            return {
              id: req.id,
              result: {
                targetDir: "/test/ws1",
                selectedEngines: ["postgresql"],
                dockerComposePath: "/test/ws1/docker-compose.postgresql.yml",
                initScripts: [],
                notebooksGenerated: ["/test/ws1/sample_postgresql.fsqnb"],
                registeredConnections: ["postgresql_demo"],
              },
            };
          },
        }),
        log: () => {},
      };

      try {
        await executeCreateSampleData(deps);
        expect(sentRequests.some((r) => r.method === "workspace/createAdvancedSamples")).toBe(true);
        expect(messages.some((m) => m.includes("docker-compose.postgresql.yml"))).toBe(true);
      } finally {
        (vscodeStub.workspace as any).workspaceFolders = origWorkspaceFolders;
        (vscodeStub.window as any).showQuickPick = origShowQuickPick;
        (vscodeStub.window as any).showInformationMessage = origShowInfo;
      }
    });

    it("handles error during sample data creation and shows error message with logs option", async () => {
      let outputShown = false;
      let errorShown = false;

      const origWorkspaceFolders = (vscodeStub.workspace as any).workspaceFolders;
      (vscodeStub.workspace as any).workspaceFolders = [
        { name: "ws1", uri: vscodeStub.Uri.file("/test/ws1") },
      ];

      const origShowQuickPick = vscodeStub.window.showQuickPick;
      (vscodeStub.window as any).showQuickPick = async (items: any[]) => [items[0]];

      const origShowError = vscodeStub.window.showErrorMessage;
      (vscodeStub.window as any).showErrorMessage = async (_msg: string, ...choices: string[]) => {
        errorShown = true;
        return choices.includes("Show Logs") ? "Show Logs" : undefined;
      };

      const deps: SampleDataDeps = {
        getWorker: async () => ({
          send: async (req) => ({
            id: req.id,
            error: { code: "Fail", message: "Disk full" },
          }),
        }),
        log: () => {},
        outputChannel: {
          show: () => {
            outputShown = true;
          },
        } as any,
      };

      try {
        await executeCreateSampleData(deps);
        expect(errorShown).toBe(true);
        expect(outputShown).toBe(true);
      } finally {
        (vscodeStub.workspace as any).workspaceFolders = origWorkspaceFolders;
        (vscodeStub.window as any).showQuickPick = origShowQuickPick;
        (vscodeStub.window as any).showErrorMessage = origShowError;
      }
    });

    it("triggers showOutput command if outputChannel is not provided when clicking Show Logs", async () => {
      let commandExecuted = "";
      const origExecute = vscodeStub.commands.executeCommand;
      vscodeStub.commands.executeCommand = async (cmd: string) => {
        commandExecuted = cmd;
        return undefined;
      };

      const origWorkspaceFolders = (vscodeStub.workspace as any).workspaceFolders;
      (vscodeStub.workspace as any).workspaceFolders = [
        { name: "ws1", uri: vscodeStub.Uri.file("/test/ws1") },
      ];

      const origShowQuickPick = vscodeStub.window.showQuickPick;
      (vscodeStub.window as any).showQuickPick = async (items: any[]) => [items[0]];

      const origShowError = vscodeStub.window.showErrorMessage;
      (vscodeStub.window as any).showErrorMessage = async (_msg: string, ...choices: string[]) => {
        return choices.includes("Show Logs") ? "Show Logs" : undefined;
      };

      const deps: SampleDataDeps = {
        getWorker: async () => ({
          send: async (req) => ({
            id: req.id,
            error: { code: "Fail", message: "Disk error" },
          }),
        }),
        log: () => {},
      };

      try {
        await executeCreateSampleData(deps);
        expect(commandExecuted).toBe("fs-query-studio.showOutput");
      } finally {
        vscodeStub.commands.executeCommand = origExecute;
        (vscodeStub.workspace as any).workspaceFolders = origWorkspaceFolders;
        (vscodeStub.window as any).showQuickPick = origShowQuickPick;
        (vscodeStub.window as any).showErrorMessage = origShowError;
      }
    });

    it("selects targetDir from showOpenDialog when no workspace folder is open", async () => {
      const origWorkspaceFolders = (vscodeStub.workspace as any).workspaceFolders;
      (vscodeStub.workspace as any).workspaceFolders = undefined;

      const origShowOpenDialog = (vscodeStub.window as any).showOpenDialog;
      (vscodeStub.window as any).showOpenDialog = async () => [vscodeStub.Uri.file("/chosen/folder")];

      const origShowQuickPick = vscodeStub.window.showQuickPick;
      (vscodeStub.window as any).showQuickPick = async (items: any[]) => [items[0]];

      let capturedTargetDir: string | undefined;
      const deps: SampleDataDeps = {
        getWorker: async () => ({
          send: async (req) => {
            capturedTargetDir = (req.params as any)?.targetDir;
            return {
              id: req.id,
              result: {
                sqlitePath: "/chosen/folder/sample.db",
                csvPath: "/chosen/folder/sample.csv",
                notebookPath: "/chosen/folder/sample_demo.fsqnb",
              },
            };
          },
        }),
        log: () => {},
      };

      try {
        await executeCreateSampleData(deps);
        expect(capturedTargetDir).toBe("/chosen/folder");
      } finally {
        (vscodeStub.workspace as any).workspaceFolders = origWorkspaceFolders;
        (vscodeStub.window as any).showOpenDialog = origShowOpenDialog;
        (vscodeStub.window as any).showQuickPick = origShowQuickPick;
      }
    });
  });
});
