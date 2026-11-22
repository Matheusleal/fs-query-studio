import { describe, it, expect, vi, beforeEach } from "vitest";
import * as path from "node:path";
import type * as vscode from "vscode";
import * as vscodeStub from "../../test/vscode-stub";
import {
  isDataSourcesCandidate,
  resolveTargetDirFromUri,
  DataSourcesWatcher,
  type DataSourcesWatcherDeps,
} from "./data-sources-watcher";

describe("data-sources-watcher", () => {
  describe("isDataSourcesCandidate", () => {
    it("accepts valid data source extensions", () => {
      expect(isDataSourcesCandidate("data.csv")).toBe(true);
      expect(isDataSourcesCandidate("folder/sheet.xlsx")).toBe(true);
      expect(isDataSourcesCandidate("data.xls")).toBe(true);
      expect(isDataSourcesCandidate("data.json")).toBe(true);
      expect(isDataSourcesCandidate("data.xml")).toBe(true);
      expect(isDataSourcesCandidate("data.sqlite")).toBe(true);
      expect(isDataSourcesCandidate("data.sqlite3")).toBe(true);
      expect(isDataSourcesCandidate("data.db")).toBe(true);
    });

    it("rejects dotfiles, office locks, readme, and unsupported extensions", () => {
      expect(isDataSourcesCandidate(".DS_Store")).toBe(false);
      expect(isDataSourcesCandidate(".gitkeep")).toBe(false);
      expect(isDataSourcesCandidate("~$temp.xlsx")).toBe(false);
      expect(isDataSourcesCandidate("README.txt")).toBe(false);
      expect(isDataSourcesCandidate("readme.txt")).toBe(false);
      expect(isDataSourcesCandidate("notes.txt")).toBe(false);
      expect(isDataSourcesCandidate("image.png")).toBe(false);
    });
  });

  describe("resolveTargetDirFromUri", () => {
    it("returns workspace folder path when Uri is part of workspace", () => {
      const origWorkspace = (vscodeStub.workspace as any).getWorkspaceFolder;
      (vscodeStub.workspace as any).getWorkspaceFolder = () => ({
        uri: vscodeStub.Uri.file("/workspace/root"),
      });

      try {
        const uri = vscodeStub.Uri.file("/workspace/root/data_sources/test.csv") as unknown as vscode.Uri;
        expect(resolveTargetDirFromUri(uri)).toBe("/workspace/root");
      } finally {
        (vscodeStub.workspace as any).getWorkspaceFolder = origWorkspace;
      }
    });

    it("extracts parent of data_sources when workspace folder is not found", () => {
      const origWorkspace = (vscodeStub.workspace as any).getWorkspaceFolder;
      (vscodeStub.workspace as any).getWorkspaceFolder = () => undefined;

      try {
        const uri = vscodeStub.Uri.file("/custom/path/data_sources/sub/test.csv") as unknown as vscode.Uri;
        expect(path.normalize(resolveTargetDirFromUri(uri))).toBe(path.normalize("/custom/path"));
      } finally {
        (vscodeStub.workspace as any).getWorkspaceFolder = origWorkspace;
      }
    });

    it("falls back to dirname when data_sources is not in path", () => {
      const origWorkspace = (vscodeStub.workspace as any).getWorkspaceFolder;
      (vscodeStub.workspace as any).getWorkspaceFolder = () => undefined;

      try {
        const uri = vscodeStub.Uri.file("/custom/path/test.csv") as unknown as vscode.Uri;
        expect(path.normalize(resolveTargetDirFromUri(uri))).toBe(path.normalize("/custom/path"));
      } finally {
        (vscodeStub.workspace as any).getWorkspaceFolder = origWorkspace;
      }
    });
  });

  describe("DataSourcesWatcher lifecycle and events", () => {
    let createdWatchers: any[] = [];
    let origCreateWatcher: any;

    beforeEach(() => {
      createdWatchers = [];
      origCreateWatcher = (vscodeStub.workspace as any).createFileSystemWatcher;
      (vscodeStub.workspace as any).createFileSystemWatcher = (pattern: string) => {
        const listeners = {
          create: [] as Function[],
          change: [] as Function[],
          delete: [] as Function[],
        };
        const watcher = {
          pattern,
          listeners,
          disposed: false,
          onDidCreate: (fn: Function) => {
            listeners.create.push(fn);
            return {
              dispose: () => {
                const idx = listeners.create.indexOf(fn);
                if (idx >= 0) listeners.create.splice(idx, 1);
              },
            };
          },
          onDidChange: (fn: Function) => {
            listeners.change.push(fn);
            return {
              dispose: () => {
                const idx = listeners.change.indexOf(fn);
                if (idx >= 0) listeners.change.splice(idx, 1);
              },
            };
          },
          onDidDelete: (fn: Function) => {
            listeners.delete.push(fn);
            return {
              dispose: () => {
                const idx = listeners.delete.indexOf(fn);
                if (idx >= 0) listeners.delete.splice(idx, 1);
              },
            };
          },
          dispose: () => {
            watcher.disposed = true;
          },
        };
        createdWatchers.push(watcher);
        return watcher;
      };
    });

    it("starts and stops the watcher cleanly", () => {
      const logs: string[] = [];
      const deps: DataSourcesWatcherDeps = {
        getWorker: async () => ({ send: async () => ({ id: "1", result: {} }) }),
        log: (l) => logs.push(l),
        isPaused: () => false,
      };

      const watcher = new DataSourcesWatcher(deps);
      expect(watcher.isActive()).toBe(false);

      watcher.start();
      expect(watcher.isActive()).toBe(true);
      expect(createdWatchers.length).toBe(1);

      // Idempotent start
      watcher.start();
      expect(createdWatchers.length).toBe(1);

      watcher.stop();
      expect(createdWatchers[0].disposed).toBe(true);

      watcher.dispose();
      expect(watcher.isActive()).toBe(false);
    });

    it("debounces rapid file changes and ignores changes when paused", async () => {
      vi.useFakeTimers();
      try {
        let isPausedState = false;
        const sentCalls: any[] = [];
        const logs: string[] = [];
        const deps: DataSourcesWatcherDeps = {
          getWorker: async () => ({
            send: async (req) => {
              sentCalls.push(req);
              return {
                id: req.id,
                result: {
                  importedCount: 1,
                  dataSourcesDir: "/ws/data_sources",
                  imported: [{ name: "orders", kind: "csv", filePath: "/ws/data_sources/orders.csv", relativePath: "orders.csv" }],
                  skippedCount: 0,
                  stagedCount: 1,
                  total: 1,
                  elapsedMs: 12,
                },
              };
            },
          }),
          log: (l) => logs.push(l),
          isPaused: () => isPausedState,
          debounceMs: 100,
        };

        const watcher = new DataSourcesWatcher(deps);
        watcher.start();

        const fsWatcher = createdWatchers[0];
        const testUri = vscodeStub.Uri.file("/ws/data_sources/orders.csv") as unknown as vscode.Uri;

        // Non-candidate file should be ignored
        const nonCandidateUri = vscodeStub.Uri.file("/ws/data_sources/notes.txt") as unknown as vscode.Uri;
        fsWatcher.listeners.change[0](nonCandidateUri);
        expect(sentCalls.length).toBe(0);

        // Fire multiple changes rapidly
        fsWatcher.listeners.change[0](testUri);
        fsWatcher.listeners.create[0](testUri);
        fsWatcher.listeners.change[0](testUri);

        // Before debounce timeout, no worker call
        expect(sentCalls.length).toBe(0);

        // Advance timers by 100ms
        await vi.advanceTimersByTimeAsync(110);
        expect(sentCalls.length).toBeGreaterThan(0);

        // Now test paused: should not trigger
        isPausedState = true;
        sentCalls.length = 0;
        fsWatcher.listeners.change[0](testUri);
        await vi.advanceTimersByTimeAsync(110);
        expect(sentCalls.length).toBe(0);

        watcher.dispose();
      } finally {
        vi.useRealTimers();
      }
    });

    it("clears debounce timer when stop() is called while debouncing", async () => {
      vi.useFakeTimers();
      try {
        let called = false;
        const deps: DataSourcesWatcherDeps = {
          getWorker: async () => ({
            send: async (req) => {
              called = true;
              return { id: req.id, result: {} };
            },
          }),
          log: () => {},
          isPaused: () => false,
          debounceMs: 100,
        };

        const watcher = new DataSourcesWatcher(deps);
        watcher.start();

        const fsWatcher = createdWatchers[0];
        const testUri = vscodeStub.Uri.file("/ws/data_sources/orders.csv") as unknown as vscode.Uri;
        fsWatcher.listeners.change[0](testUri);

        // Call stop before debounce fires
        watcher.stop();
        await vi.advanceTimersByTimeAsync(150);
        expect(called).toBe(false);

        watcher.dispose();
      } finally {
        vi.useRealTimers();
      }
    });

    it("triggers onRefreshConnections and shows message when silent is false", async () => {
      let customRefreshed = false;
      const messages: string[] = [];

      const origShowInfo = vscodeStub.window.showInformationMessage;
      (vscodeStub.window as any).showInformationMessage = async (msg: string) => {
        messages.push(msg);
      };

      const deps: DataSourcesWatcherDeps = {
        getWorker: async () => ({
          send: async (req) => ({
            id: req.id,
            result: {
              targetDir: "/ws",
              dataSourcesDir: "/ws/data_sources",
              folderCreated: false,
              foundFilesCount: 1,
              importedCount: 1,
              imported: [{ name: "customers", kind: "csv", filePath: "/ws/data_sources/customers.csv", relativePath: "customers.csv" }],
              skippedCount: 0,
              stagedCount: 1,
              total: 1,
              elapsedMs: 10,
            },
          }),
        }),
        log: () => {},
        isPaused: () => false,
        onRefreshConnections: async () => {
          customRefreshed = true;
        },
      };

      const watcher = new DataSourcesWatcher(deps);
      try {
        await watcher.autoImportAndSyncDataSources("/ws", false);
        expect(customRefreshed).toBe(true);
        expect(messages.some((m) => m.includes("Auto-imported 1 data source(s)"))).toBe(true);
      } finally {
        (vscodeStub.window as any).showInformationMessage = origShowInfo;
        watcher.dispose();
      }
    });

    it("handles autoImport error gracefully without throwing", async () => {
      const logs: string[] = [];
      const deps: DataSourcesWatcherDeps = {
        getWorker: async () => ({
          send: async (req) => ({
            id: req.id,
            error: { code: "Err", message: "Auto-import failed" },
          }),
        }),
        log: (l) => logs.push(l),
        isPaused: () => false,
      };

      const watcher = new DataSourcesWatcher(deps);
      await watcher.autoImportAndSyncDataSources("/ws", true);
      expect(logs.some((l) => l.includes("Auto-import error: Auto-import failed"))).toBe(true);
      watcher.dispose();
    });

    it("catches exception thrown during autoImport and logs it", async () => {
      const logs: string[] = [];
      const deps: DataSourcesWatcherDeps = {
        getWorker: async () => {
          throw new Error("Worker crash");
        },
        log: (l) => logs.push(l),
        isPaused: () => false,
      };

      const watcher = new DataSourcesWatcher(deps);
      await watcher.autoImportAndSyncDataSources("/ws", true);
      expect(logs.some((l) => l.includes("Error auto-syncing data sources: Worker crash"))).toBe(true);
      watcher.dispose();
    });

    it("updates status bar during runSyncDataSources and logs summary", async () => {
      const logs: string[] = [];
      const statusBar: any = {
        text: "",
        tooltip: "",
        show: () => {},
      };

      const deps: DataSourcesWatcherDeps = {
        getWorker: async () => ({
          send: async (req) => ({
            id: req.id,
            result: {
              targetDir: "/ws",
              stagedCount: 2,
              skippedCount: 1,
              total: 3,
              elapsedMs: 25,
            },
          }),
        }),
        log: (l) => logs.push(l),
        isPaused: () => false,
        statusBar,
      };

      const watcher = new DataSourcesWatcher(deps);
      await watcher.runSyncDataSources("/ws", true);

      expect(statusBar.text).toBe("$(database) Data sources ready");
      expect(statusBar.tooltip).toContain("3 sources synced");
      expect(logs.some((l) => l.includes("Data sources synced: 2 staged"))).toBe(true);
      watcher.dispose();
    });

    it("shows information message on runSyncDataSources when silent is false", async () => {
      const messages: string[] = [];
      const origShowInfo = vscodeStub.window.showInformationMessage;
      (vscodeStub.window as any).showInformationMessage = async (msg: string) => {
        messages.push(msg);
      };

      const deps: DataSourcesWatcherDeps = {
        getWorker: async () => ({
          send: async (req) => ({
            id: req.id,
            result: {
              targetDir: "/ws",
              stagedCount: 2,
              skippedCount: 1,
              total: 3,
              elapsedMs: 25,
            },
          }),
        }),
        log: () => {},
        isPaused: () => false,
      };

      const watcher = new DataSourcesWatcher(deps);
      try {
        await watcher.runSyncDataSources("/ws", false);
        expect(messages.some((m) => m.includes("Sync completed: 2 file(s) staged"))).toBe(true);
      } finally {
        (vscodeStub.window as any).showInformationMessage = origShowInfo;
        watcher.dispose();
      }
    });

    it("handles runSyncDataSources error and updates status bar to failed", async () => {
      const logs: string[] = [];
      const statusBar: any = {
        text: "",
        tooltip: "",
        show: () => {},
      };

      const deps: DataSourcesWatcherDeps = {
        getWorker: async () => ({
          send: async (req) => ({
            id: req.id,
            error: { code: "Fail", message: "Cache locked" },
          }),
        }),
        log: (l) => logs.push(l),
        isPaused: () => false,
        statusBar,
      };

      const watcher = new DataSourcesWatcher(deps);
      await watcher.runSyncDataSources("/ws", true);

      expect(statusBar.text).toBe("$(error) Data sync failed");
      expect(logs.some((l) => l.includes("Sync data sources failed: Cache locked"))).toBe(true);
      watcher.dispose();
    });

    it("catches exception thrown during runSyncDataSources and logs error", async () => {
      const logs: string[] = [];
      const statusBar: any = {
        text: "",
        tooltip: "",
        show: () => {},
      };

      const deps: DataSourcesWatcherDeps = {
        getWorker: async () => {
          throw new Error("Disk disconnected");
        },
        log: (l) => logs.push(l),
        isPaused: () => false,
        statusBar,
      };

      const watcher = new DataSourcesWatcher(deps);
      await watcher.runSyncDataSources("/ws", true);

      expect(statusBar.text).toBe("$(error) Data sync error");
      expect(logs.some((l) => l.includes("Error during sync: Disk disconnected"))).toBe(true);
      watcher.dispose();
    });
  });
});
